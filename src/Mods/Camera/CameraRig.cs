using System;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;
using UnityEngine.InputSystem;
using VehiclePhysics;
using Object = UnityEngine.Object;

namespace OverTheHillPlus.CameraPlus;

/// <summary>
/// Hood, bumper, chase-far and drone cameras, on top of the game's own camera rig. Purely local (your view only).
///
/// How it stays out of Cinemachine's way (from the decompiled game, see also Photo &amp; Spectate's notes):
/// - CinemachineManualUpdateDriver.LateUpdate runs the game's camera once per frame: it moves the followed point
///   (CameraFollowUpdater.ActiveCameraUpdater.UpdateCameraTarget) and then calls CinemachineBrain.ManualUpdate, which
///   writes the main CinemachineFreeLook's result into the Unity camera.
/// - A postfix on that LateUpdate (no parameters at all, so none of the struct-parameter trampolines that crashed the
///   game before) runs right after, in the same frame and before rendering. While a Camera+ camera is on, it only
///   overwrites the Unity camera's position, rotation, field of view and near plane. Cinemachine keeps running the
///   chase camera underneath, unchanged, so switching back to the game camera is instant and nothing needs restoring
///   in the rig (the brain writes its own values again on its next update).
/// - Nothing Cinemachine owns is touched: no lens, rig, priority or follow target changes, and no per-frame patch on a
///   Cinemachine method.
/// It only works while you drive (core's CameraView.Driving: the game's Driving input map is the topmost one apart from
/// menus and the MODS page), so the game's camera shows during cutscenes (ScenicPoint.StartCutscene pushes the
/// CutscenePlaying map), loading screens, photo mode and the free camera. It also steps aside while you watch another
/// player (the chase camera follows someone else's car then) and while there is no car or driving camera.
/// </summary>
internal static class CameraRig
{
    private const float NearPlane = 0.05f;
    private const float CastRadius = 0.25f;
    private const int MaxHits = 32;
    private static Il2CppStructArray<RaycastHit> _hits;

    public static string Status { get; private set; } = "game camera";
    public static bool Overriding { get; private set; }
    /// <summary>Unscaled time of the last camera change (the on-screen name shows for a moment after it).</summary>
    public static float ChangedAt { get; private set; } = -100f;

    private static bool _hookSeen;
    private static float _firstNeededAt = -1f;
    private static int _errors;
    private static bool _loggedError;

    // The Unity camera the brain drives, and the game's values we replaced on it.
    private static Camera _cam;
    private static IntPtr _brainPtr;
    private static float _gameFov, _gameNear, _setFov = -1f, _setNear = -1f;

    // The car's size, per car.
    private static IntPtr _boxCar;
    private static CameraMath.CarBox _box = CameraMath.CarBox.Default;
    private static float _boxRetryAt;

    // Collision layers the game's own camera collides with.
    private static int _mask;
    private static float _maskAt = -100f;

    // Drone state.
    private static bool _droneReady;
    private static float _yaw, _pitch, _dist;
    private static Vector3 _droneTarget;

    /// <summary>Crash-trail steps per mode (check, place, done), made once so the per-frame marks allocate nothing.</summary>
    private static readonly string[] StepNames = MakeStepNames();

    private static string[] MakeStepNames()
    {
        var n = new string[CameraMath.ModeCount * 3];
        for (int i = 0; i < CameraMath.ModeCount; i++)
        {
            string m = CameraMath.Name((CamMode)i).ToLowerInvariant();
            n[i * 3] = "camera+: " + m + " check";
            n[i * 3 + 1] = "camera+: " + m + " place";
            n[i * 3 + 2] = "camera+: " + m + " done";
        }
        return n;
    }

    // ---- switching ----

    public static void Set(CamMode m)
    {
        ExtraCameras.StopAll(); // another mod's camera (the Cockpit view) gives way to a choice made here
        if (CameraConfig.Mode.Value == m) return;
        var from = CameraConfig.Mode.Value;
        CameraConfig.Mode.Value = m;
        _droneReady = false;
        ChangedAt = Time.unscaledTime;
        CameraPlugin.Logger.LogInfo($"Camera+: {CameraMath.Name(from)} -> {CameraMath.Name(m)}");
        DebugLog.Info(CameraPlugin.ModId, $"camera {CameraMath.Name(from)} -> {CameraMath.Name(m)} (status: {Status})");
    }

    /// <summary>The cycle: Game, our cameras, then the cameras other mods added (core's ExtraCameras), then Game again.</summary>
    public static void Cycle(int dir)
    {
        var extras = ExtraCameras.InCycle();
        var active = ExtraCameras.Active;
        if (active != null)
        {
            int i = extras.IndexOf(active);
            ExtraCameras.StopAll();
            ChangedAt = Time.unscaledTime;
            if (dir >= 0) { if (i >= 0 && i + 1 < extras.Count) ExtraCameras.TurnOn(extras[i + 1]); }
            else if (i > 0) ExtraCameras.TurnOn(extras[i - 1]);
            else Set(CameraMath.Previous(CamMode.Game, CameraConfig.InCycle));
            return;
        }
        var cur = CameraConfig.Mode.Value;
        if (dir >= 0)
        {
            var next = CameraMath.Next(cur, CameraConfig.InCycle);
            if (next == CamMode.Game && extras.Count > 0)
            {
                Set(CamMode.Game);
                ExtraCameras.TurnOn(extras[0]);
            }
            else Set(next);
        }
        else if (cur == CamMode.Game && extras.Count > 0) ExtraCameras.TurnOn(extras[extras.Count - 1]);
        else Set(CameraMath.Previous(cur, CameraConfig.InCycle));
    }

    /// <summary>Another mod's camera is about to turn on (core's ExtraCameras): back to the game's camera under it.</summary>
    internal static void ExtraTurningOn(ExtraCamera camera)
    {
        if (CameraConfig.Mode.Value != CamMode.Game)
        {
            CameraConfig.Mode.Value = CamMode.Game;
            _droneReady = false;
        }
        ChangedAt = Time.unscaledTime;
    }

    // ---- once per frame, after the game's camera ----

    internal static void AfterGameCamera()
    {
        _hookSeen = true;
        var mode = CameraConfig.Mode.Value;
        if (mode == CamMode.Game)
        {
            if (Overriding)
            {
                CrashTrail.Mark("camera+: back to the game camera");
                Stop();
                CrashTrail.Mark("camera+: game camera restored");
            }
            Status = "game camera";
            return;
        }
        try
        {
            CrashTrail.Mark(StepNames[(int)mode * 3]);
            string why = Blocked(out var cam, out var updater, out var follow, out var car);
            if (why != null)
            {
                if (Overriding) Stop();
                // The reasons are fixed strings: a new one is a change of state (Verbose debug log only).
                if (DebugLog.IsVerbose && !ReferenceEquals(Status, why)) DebugLog.Verbose(CameraPlugin.ModId, CameraMath.Name(mode) + " camera waits: " + why);
                Status = why;
                _droneReady = false;
                return;
            }
            Begin(cam);
            CrashTrail.Mark(StepNames[(int)mode * 3 + 1]);
            switch (mode)
            {
                case CamMode.ChaseFar: ChaseFar(cam, updater, car); break;
                case CamMode.Hood: Mounted(cam, follow, car, true); break;
                case CamMode.Bumper: Mounted(cam, follow, car, false); break;
                case CamMode.Drone: Drone(cam, follow, car); break;
            }
            float fov = CameraConfig.FovFor(mode);
            if (fov >= 10f) SetFov(cam, Mathf.Clamp(fov, 10f, 150f));
            if (DebugLog.IsVerbose && Status != "active") DebugLog.Verbose(CameraPlugin.ModId, CameraMath.Name(mode) + " camera active");
            Status = "active";
            _errors = 0;
            CrashTrail.Mark(StepNames[(int)mode * 3 + 2]);
        }
        catch (Exception e)
        {
            // Every distinct error once in the debug log, with repeat counts (the BepInEx log gets the first one only).
            DebugLog.Error(CameraPlugin.ModId, e, "camera update (" + CameraMath.Name(mode) + ")");
            if (!_loggedError)
            {
                _loggedError = true;
                CameraPlugin.Logger.LogError("Camera+: " + e);
            }
            Status = "error: " + e.Message;
            // A camera that keeps failing goes back to the game's own.
            if (++_errors > 30)
            {
                _errors = 0;
                Set(CamMode.Game);
                Menu.SetStatus("Camera+: back to the game camera after an error");
            }
        }
    }

    /// <summary>Called from the frame tick: says so when the hook never ran (the patch did not apply).</summary>
    internal static void CheckHook()
    {
        if (CameraConfig.Mode.Value == CamMode.Game || _hookSeen) { _firstNeededAt = -1f; return; }
        if (_firstNeededAt < 0f) _firstNeededAt = Time.unscaledTime;
        else if (Time.unscaledTime - _firstNeededAt > 3f && GameState.LocalCar != null)
            Status = "the game's camera update could not be hooked (see the log)";
    }

    private static string Blocked(out Camera cam, out CameraFollowUpdater updater, out Transform follow, out VPVehicleController car)
    {
        cam = null;
        updater = null;
        follow = null;
        car = GameState.LocalCar;
        if (CameraView.PhotoOrFreeCamera) return "paused for photo mode / the free camera";
        // Cutscenes (a scenic point's), loading screens, spectating: only while you drive (menus on top are fine).
        if (!CameraView.Driving) return CameraView.Cutscene ? "paused for a cutscene" : "paused (not driving)";
        if (car == null) return "waiting for your car";
        updater = CameraFollowUpdater.ActiveCameraUpdater;
        if (updater == null) return "waiting for the driving camera";
        follow = updater.parent;
        if (follow == null) return "waiting for the driving camera";
        if (!IsOwnCar(follow, car)) return "paused while you watch another player";
        cam = BrainCamera();
        if (cam == null) return "waiting for the main camera";
        return null;
    }

    /// <summary>The chase camera follows your own car (not another player's, as while spectating).</summary>
    private static bool IsOwnCar(Transform follow, VPVehicleController car)
    {
        var ct = car.transform;
        return follow.Pointer == ct.Pointer || follow.IsChildOf(ct) || ct.IsChildOf(follow);
    }

    private static Camera BrainCamera()
    {
        try
        {
            var brain = SceneReferences.Instance?.MainCameraBrain;
            if (brain != null)
            {
                if (_cam == null || brain.Pointer != _brainPtr)
                {
                    _brainPtr = brain.Pointer;
                    _cam = brain.OutputCamera;
                    if (_cam == null) _cam = brain.GetComponent<Camera>();
                }
                if (_cam != null) return _cam;
            }
        }
        catch (Exception ex) { DebugLog.Error("camera", ex, "CameraRig.BrainCamera"); _cam = null; }
        return Camera.main;
    }

    // ---- writing the camera ----

    private static void Begin(Camera cam)
    {
        // Whatever differs from what we set last frame is the game's own (the brain writes its lens every update).
        float fov = cam.fieldOfView, near = cam.nearClipPlane;
        if (!Overriding || Mathf.Abs(fov - _setFov) > 0.01f) _gameFov = fov;
        if (!Overriding || Mathf.Abs(near - _setNear) > 0.0001f) _gameNear = near;
        Overriding = true;
    }

    private static void SetFov(Camera cam, float fov)
    {
        cam.fieldOfView = fov;
        _setFov = fov;
    }

    private static void SetNear(Camera cam, float near)
    {
        cam.nearClipPlane = near;
        _setNear = near;
    }

    /// <summary>Back to the game's camera: the brain moves it again next frame; puts back lens values only we set.</summary>
    private static void Stop()
    {
        Overriding = false;
        var cam = _cam;
        try
        {
            if (cam != null)
            {
                if (_setFov > 0f && Mathf.Abs(cam.fieldOfView - _setFov) < 0.01f) cam.fieldOfView = _gameFov;
                if (_setNear > 0f && Mathf.Abs(cam.nearClipPlane - _setNear) < 0.0001f) cam.nearClipPlane = _gameNear;
            }
        }
        catch (Exception ex) { DebugLog.Error("camera", ex, "CameraRig.Stop"); }
        _setFov = _setNear = -1f;
    }

    // ---- the cameras ----

    private static void ChaseFar(Camera cam, CameraFollowUpdater updater, VPVehicleController car)
    {
        var t = cam.transform;
        Vector3 target = updater.transform.position;
        Vector3 p = t.position;
        Vector3 fwd = t.forward;
        // Where the game's camera aims: the point on its view ray nearest the followed point.
        Vector3 aim = p + fwd * Vector3.Dot(target - p, fwd);

        var pulled = CameraMath.PullBack((target.x, target.y, target.z), (p.x, p.y, p.z),
            Mathf.Clamp(CameraConfig.ChaseFarDistance.Value, 1f, 4f), Mathf.Clamp(CameraConfig.ChaseFarLift.Value, -1f, 6f));
        Vector3 want = new(pulled.x, pulled.y, pulled.z);
        // Never closer than the game's own camera (it has dealt with walls and terrain already).
        want = KeepClear(target, want, car, (p - target).magnitude);
        var look = aim - want;
        if (look.sqrMagnitude < 0.0001f) return;
        t.SetPositionAndRotation(want, Quaternion.LookRotation(look, Vector3.up));
    }

    private static void Mounted(Camera cam, Transform follow, VPVehicleController car, bool hood)
    {
        var box = CarBoxOf(car, follow);
        var local = hood
            ? CameraMath.HoodPoint(box, Mathf.Clamp(CameraConfig.HoodUp.Value, -2f, 3f), Mathf.Clamp(CameraConfig.HoodForward.Value, -6f, 6f))
            : CameraMath.BumperPoint(box, Mathf.Clamp(CameraConfig.BumperUp.Value, -2f, 3f), Mathf.Clamp(CameraConfig.BumperForward.Value, -6f, 6f));
        var world = follow.TransformPoint(new Vector3(local.x, local.y, local.z));
        cam.transform.SetPositionAndRotation(world, follow.rotation);
        SetNear(cam, NearPlane);
    }

    private static void Drone(Camera cam, Transform follow, VPVehicleController car)
    {
        float dt = Mathf.Min(Time.unscaledDeltaTime, 0.1f);
        Vector3 target = follow.position + Vector3.up * 1.2f;
        if (!_droneReady)
        {
            _droneReady = true;
            _droneTarget = target;
            _dist = CameraMath.ClampDistance(CameraConfig.DroneDistance.Value);
            _pitch = CameraMath.ClampPitch(CameraConfig.DronePitch.Value);
            // Start behind the car whichever way it faces (or behind it relative to its heading).
            _yaw = CameraConfig.DroneFollowsHeading.Value ? 0f : CameraMath.WrapYaw(HeadingOf(follow));
        }
        ReadDroneInput(dt);

        _droneTarget = Vector3.Lerp(_droneTarget, target, CameraMath.Damp(Mathf.Clamp(CameraConfig.DroneSmoothing.Value, 0.5f, 30f), dt));
        float yaw = _yaw + (CameraConfig.DroneFollowsHeading.Value ? HeadingOf(follow) : 0f);
        var o = CameraMath.Orbit(yaw, _pitch, _dist);
        Vector3 want = _droneTarget + new Vector3(o.x, o.y, o.z);
        want = KeepClear(_droneTarget, want, car, CameraMath.MinDroneDistance);
        var look = _droneTarget - want;
        if (look.sqrMagnitude < 0.0001f) return;
        cam.transform.SetPositionAndRotation(want, Quaternion.LookRotation(look, Vector3.up));
    }

    private static float HeadingOf(Transform t)
    {
        var f = t.forward;
        f.y = 0f;
        return f.sqrMagnitude < 0.0001f ? 0f : Mathf.Atan2(f.x, f.z) * Mathf.Rad2Deg;
    }

    private static void ReadDroneInput(float dt)
    {
        // Only while the game's Driving map has the input (not under the MODS page, a menu or the map screen).
        if (CoreBehaviour.MenuOpen || !CameraView.DrivingOnTop) return;
        try
        {
            var mouse = Mouse.current;
            if (mouse != null)
            {
                var d = mouse.delta.ReadValue();
                float k = 0.12f * Mathf.Clamp(CameraConfig.DroneMouse.Value, 0.1f, 5f);
                _yaw = CameraMath.WrapYaw(_yaw + d.x * k);
                _pitch = CameraMath.ClampPitch(_pitch - d.y * k);
                float scroll = mouse.scroll.ReadValue().y;
                if (Mathf.Abs(scroll) > 0.01f) _dist = CameraMath.ClampDistance(_dist * (1f - Mathf.Clamp(scroll, -360f, 360f) * 0.0008f));
            }
        }
        catch (Exception ex) { DebugLog.Error("camera", ex, "CameraRig.ReadDroneInput"); }
        try
        {
            var pad = Gamepad.current;
            if (pad != null)
            {
                var s = pad.rightStick.ReadValue();
                if (s.sqrMagnitude > 0.02f)
                {
                    float k = 110f * Mathf.Clamp(CameraConfig.DroneStick.Value, 0.1f, 5f) * dt;
                    _yaw = CameraMath.WrapYaw(_yaw + s.x * k);
                    _pitch = CameraMath.ClampPitch(_pitch - s.y * k);
                }
            }
        }
        catch (Exception ex) { DebugLog.Error("camera", ex, "CameraRig.ReadDroneInput"); }
    }

    /// <summary>
    /// Pulls <paramref name="want"/> in toward <paramref name="from"/> when something solid (terrain, rocks, buildings;
    /// the layers the game's own camera collides with) is in between, ignoring your own car, but never nearer than
    /// <paramref name="minDistance"/>.
    /// </summary>
    private static Vector3 KeepClear(Vector3 from, Vector3 want, VPVehicleController car, float minDistance)
    {
        var dir = want - from;
        float dist = dir.magnitude;
        if (dist < 0.01f) return want;
        dir /= dist;
        float nearest = dist;
        try
        {
            _hits ??= new Il2CppStructArray<RaycastHit>(MaxHits);
            int count = Physics.SphereCastNonAlloc(from, CastRadius, dir, _hits, dist, Mask(), QueryTriggerInteraction.Ignore);
            var hits = _hits;
            Transform carT = car.transform;
            Rigidbody carRb = null;
            try { carRb = car.cachedRigidbody; } catch (Exception ex) { DebugLog.Error("camera", ex, "CameraRig.KeepClear"); }
            IntPtr carRbPtr = carRb == null ? IntPtr.Zero : carRb.Pointer;
            for (int i = 0; i < count && i < MaxHits; i++)
            {
                var h = hits[i];
                var col = h.collider;
                if (col == null) continue;
                // Your own car (its body and wheels) never blocks the view of itself.
                var rb = col.attachedRigidbody;
                if (rb != null && rb.Pointer == carRbPtr) continue;
                if (col.transform.IsChildOf(carT)) continue;
                if (h.distance > 0f && h.distance < nearest) nearest = h.distance;
            }
        }
        catch (Exception ex) { DebugLog.Error("camera", ex, "CameraRig.KeepClear"); return want; }
        float keep = Mathf.Max(Mathf.Min(minDistance, dist), nearest - CastRadius);
        return keep >= dist ? want : from + dir * keep;
    }

    private static int Mask()
    {
        if (Time.unscaledTime - _maskAt < 5f && _mask != 0) return _mask;
        _maskAt = Time.unscaledTime;
        int m = 0;
        try
        {
            var clamp = SceneReferences.Instance?.FreeLookCollisionClamp;
            if (clamp != null) m = clamp.collisionLayers.value;
        }
        catch (Exception ex) { DebugLog.Error("camera", ex, "CameraRig.Mask"); }
        _mask = m != 0 ? m : Physics.DefaultRaycastLayers;
        return _mask;
    }

    // ---- the car's size ----

    /// <summary>The car's box in <paramref name="follow"/>'s space, from its mesh renderers (worked out once per car).</summary>
    private static CameraMath.CarBox CarBoxOf(VPVehicleController car, Transform follow)
    {
        if (car.Pointer == _boxCar && (Time.unscaledTime < _boxRetryAt || _boxRetryAt < 0f)) return _box;
        bool newCar = car.Pointer != _boxCar;
        _boxCar = car.Pointer;
        _boxRetryAt = Time.unscaledTime + 2f;
        if (newCar) _box = CameraMath.CarBox.Default;

        float minX = float.MaxValue, minY = float.MaxValue, minZ = float.MaxValue;
        float maxX = float.MinValue, maxY = float.MinValue, maxZ = float.MinValue;
        int used = 0;
        var renderers = car.GetComponentsInChildren<Renderer>();
        if (renderers != null)
            foreach (var r in renderers)
            {
                if (r == null || !r.enabled) continue;
                // Body meshes only: no particles, trails, lines or decals.
                if (r.TryCast<MeshRenderer>() == null && r.TryCast<SkinnedMeshRenderer>() == null) continue;
                var lb = r.localBounds;
                var rt = r.transform;
                var c = lb.center;
                var e = lb.extents;
                for (int i = 0; i < 8; i++)
                {
                    var corner = new Vector3(c.x + ((i & 1) == 0 ? -e.x : e.x), c.y + ((i & 2) == 0 ? -e.y : e.y), c.z + ((i & 4) == 0 ? -e.z : e.z));
                    var p = follow.InverseTransformPoint(rt.TransformPoint(corner));
                    if (p.x < minX) minX = p.x;
                    if (p.x > maxX) maxX = p.x;
                    if (p.y < minY) minY = p.y;
                    if (p.y > maxY) maxY = p.y;
                    if (p.z < minZ) minZ = p.z;
                    if (p.z > maxZ) maxZ = p.z;
                }
                used++;
            }
        if (used == 0) return _box;
        var box = new CameraMath.CarBox(minX, maxX, minY, maxY, minZ, maxZ);
        if (!box.Plausible) return _box;
        _box = box;
        _boxRetryAt = -1f; // found: keep it for this car
        return _box;
    }
}

/// <summary>
/// Runs <see cref="CameraRig.AfterGameCamera"/> right after the game's own camera update, every frame, through a guard
/// site: it marks the crash trail, counts as Camera+'s hook for hangs and the game's errors, is timed, and an exception
/// is caught (never breaks the game's camera update) and noted once in the debug log with repeat counts.
/// </summary>
[HarmonyPatch(typeof(CinemachineManualUpdateDriver), nameof(CinemachineManualUpdateDriver.LateUpdate))]
internal static class AfterGameCameraPatch
{
    private static readonly GuardSite Site = Guard.Site(CameraPlugin.ModId, "LateUpdate postfix");
    private static readonly Action Body = CameraRig.AfterGameCamera;

    private static void Postfix() => Site.Run(Body);
}
