using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Rendering;
using VehiclePhysics;
using Object = UnityEngine.Object;

namespace OverTheHillPlus.Cockpit;

/// <summary>
/// The driver's-seat camera, on top of the game's own camera rig. Purely local (your view only).
///
/// It hooks the game's camera exactly as Camera+ does (see CameraRig): a postfix on
/// CinemachineManualUpdateDriver.LateUpdate runs right after the game's camera and, while the cockpit is on, overwrites
/// only the Unity camera's position, rotation, field of view and near plane. Cinemachine keeps running the chase camera
/// underneath, so leaving is instant. Ours runs last (HarmonyPriority.Last), after Camera+'s. The two cooperate through
/// core's ExtraCameras: Camera+'s cycle key goes through the Cockpit after its drone camera, and turning either on turns
/// the other off.
///
/// Per car it measures the car's renderers once (the cabin box), seats the driver from that (SeatMath), builds the
/// cockpit model there (CockpitModel) as a child of the car, and hides the car's own parts that surround the eyes by
/// switching them to shadow-only (put back on leaving). It only works while you drive on your own car (core's
/// CameraView.Driving), like Camera+; during cutscenes, photo mode, spectating and loading screens the game's camera
/// shows and the model is hidden.
/// </summary>
internal static class CockpitRig
{
    public static bool On { get; private set; }
    public static string Status { get; private set; } = "off";

    private static readonly ExtraCamera Extra = new()
    {
        Name = "Cockpit",
        InCycle = () => CockpitConfig.InCycle != null && CockpitConfig.InCycle.Value,
        IsOn = () => On,
        SetOn = SetOn,
    };

    // The Unity camera the brain drives.
    private static Camera _cam;
    private static IntPtr _brainPtr;

    // Per car.
    private static IntPtr _carPtr;
    private static CockpitModel _model;
    private static SeatPlan _plan;
    private static string _builtFor;
    private static NetworkedVehicleStatus _status;
    private static float _statusRetryAt;
    private static int _statusTries;
    private static bool _suspended;
    private static bool _modelDrawable = true;

    // Parts of the car hidden while we sit in it, with the shadow mode they had.
    private static readonly List<Renderer> HiddenParts = new();
    private static readonly List<ShadowCastingMode> HiddenModes = new();

    // The head.
    private static readonly LookSpring Look = new();
    private static readonly HeadSway Sway = new();
    private static readonly ZoomGate Zoom = new();
    private static float _leavePush;
    private static Vector3 _lastVelocity;
    private static bool _hasVelocity;

    // What the dials show.
    private static float _speedNeedle, _rpmNeedle, _wheel, _topSpeed = 100f, _peakRpm = 4000f;

    private static int _errors;
    private static bool _loggedError;

    internal static void Install()
    {
        CoreBehaviour.Hotkeys += Hotkeys;
        ExtraCameras.Register(Extra);
    }

    // ---- switching ----

    private static void Hotkeys(Keyboard kb)
    {
        if (!CoreBehaviour.Pressed(kb, CockpitConfig.ToggleKey.Value)) return;
        Toggle();
    }

    public static void Toggle()
    {
        if (On)
        {
            SetOn(false);
            return;
        }
        if (GameState.LocalCar == null)
        {
            Menu.SetStatus("Cockpit: get in a car first");
            return;
        }
        ExtraCameras.TurnOn(Extra);
    }

    private static void SetOn(bool on)
    {
        if (On == on) return;
        On = on;
        Status = on ? "starting" : "off";
        Look.Reset();
        Sway.Reset();
        Zoom.Reset();
        _leavePush = 0f;
        _hasVelocity = false;
        if (!on) Leave();
        CockpitPlugin.Logger.LogInfo("Cockpit: " + (on ? "on" : "off"));
        DebugLog.Info(CockpitPlugin.ModId, on ? "cockpit on" : "cockpit off");
    }

    /// <summary>Takes everything back: the car's parts, the model.</summary>
    private static void Leave()
    {
        RestoreParts();
        if (_model != null) _model.Destroy();
        _model = null;
        _carPtr = IntPtr.Zero;
        _builtFor = null;
    }

    // ---- once per frame, after the game's camera ----

    internal static void AfterGameCamera()
    {
        try
        {
            var cam = BrainCamera();
            if (!On)
            {
                if (CockpitConfig.ZoomToEnter.Value) TryZoomIn(cam);
                return;
            }
            string why = Blocked(out cam, out _, out var car);
            if (why != null)
            {
                if (_model != null || HiddenParts.Count > 0) Suspend();
                Status = why;
                return;
            }
            CrashTrail.Mark("cockpit: place");
            if (car.Pointer != _carPtr || _builtFor != BuildKey()) Build(car);
            Resume();
            Place(cam, car);
            Status = "active";
            _errors = 0;
            CrashTrail.Mark("cockpit: done");
        }
        catch (Exception e)
        {
            DebugLog.Error(CockpitPlugin.ModId, e, "cockpit update");
            if (!_loggedError)
            {
                _loggedError = true;
                CockpitPlugin.Logger.LogError("Cockpit: " + e);
            }
            Status = "error: " + e.Message;
            // A view that keeps failing goes back to the game's own camera.
            if (++_errors > 30)
            {
                _errors = 0;
                SetOn(false);
                Menu.SetStatus("Cockpit: back to the game camera after an error");
            }
        }
    }

    private static string BuildKey() =>
        (CockpitConfig.LeftHandDrive.Value ? "L" : "R") + (CockpitConfig.ShowCockpit.Value ? "m" : "-") +
        (CockpitConfig.HideBody.Value ? "h" : "-") + CockpitConfig.Brightness.Value.ToString("0.00");

    private static string Blocked(out Camera cam, out CameraFollowUpdater updater, out VPVehicleController car)
    {
        cam = null;
        updater = null;
        car = GameState.LocalCar;
        if (CameraView.PhotoOrFreeCamera) return "paused for photo mode / the free camera";
        if (!CameraView.Driving) return CameraView.Cutscene ? "paused for a cutscene" : "paused (not driving)";
        if (car == null) return "waiting for your car";
        updater = CameraFollowUpdater.ActiveCameraUpdater;
        if (updater == null || updater.parent == null) return "waiting for the driving camera";
        if (!IsOwnCar(updater.parent, car)) return "paused while you watch another player";
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
        catch (Exception ex) { DebugLog.Error(CockpitPlugin.ModId, ex, "CockpitRig.BrainCamera"); _cam = null; }
        return Camera.main;
    }

    // ---- getting in by the mouse wheel ----

    /// <summary>The wheel turned in again while the game's camera was already as close as it goes: sit in the cab.</summary>
    private static void TryZoomIn(Camera cam)
    {
        if (cam == null || !CameraView.DrivingOnTop || CoreBehaviour.MenuOpen)
        {
            Zoom.Reset();
            return;
        }
        if (Blocked(out cam, out var updater, out _) != null)
        {
            Zoom.Reset();
            return;
        }
        var mouse = Mouse.current;
        if (mouse == null) return;
        Zoom.MaxDistance = Mathf.Clamp(CockpitConfig.ZoomDistance.Value, 1f, 30f);
        float dist = (cam.transform.position - updater.transform.position).magnitude;
        if (Zoom.Step(mouse.scroll.ReadValue().y, dist, Time.unscaledDeltaTime))
        {
            DebugLog.Info(CockpitPlugin.ModId, $"zoomed in at {dist:0.0} m: getting in");
            ExtraCameras.TurnOn(Extra);
        }
    }

    // ---- the car ----

    private static void Build(VPVehicleController car)
    {
        CrashTrail.Mark("cockpit: build");
        RestoreParts();
        if (_model != null) _model.Destroy();
        _model = null;
        _carPtr = car.Pointer;
        _builtFor = BuildKey();
        _status = null;
        _statusTries = 0;
        _statusRetryAt = 0f;
        _topSpeed = 100f;
        _peakRpm = 4000f;
        _hasVelocity = false;
        Sway.Reset();

        var renderers = new List<Renderer>();
        var box = Measure(car, renderers, out var worldBounds);
        _plan = SeatMath.Plan(box, CockpitConfig.LeftHandDrive.Value);
        _modelDrawable = CockpitModel.CanDraw;
        if (CockpitConfig.ShowCockpit.Value && _modelDrawable) _model = CockpitModel.Build(car.transform, _plan);
        else if (CockpitConfig.ShowCockpit.Value) CockpitPlugin.Logger.LogWarning("Cockpit: no shader to draw the dashboard with; showing the car only.");
        if (CockpitConfig.HideBody.Value) HideParts(car, renderers, worldBounds);
        DebugLog.Info(CockpitPlugin.ModId,
            $"built for the car: box {box.Width:0.00} x {box.Height:0.00} x {box.Length:0.00} m, eyes at ({_plan.EyeX:0.00}, {_plan.EyeY:0.00}, {_plan.EyeZ:0.00}), {HiddenParts.Count} parts hidden");
    }

    /// <summary>The car's size in its own space from its renderers (and the renderers, with their union in the world).</summary>
    private static CabinBox Measure(VPVehicleController car, List<Renderer> used, out Bounds world)
    {
        world = new Bounds(car.transform.position, Vector3.zero);
        var ct = car.transform;
        float minX = float.MaxValue, minY = float.MaxValue, minZ = float.MaxValue;
        float maxX = float.MinValue, maxY = float.MinValue, maxZ = float.MinValue;
        bool any = false, first = true;
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
                    var p = ct.InverseTransformPoint(rt.TransformPoint(corner));
                    if (p.x < minX) minX = p.x;
                    if (p.x > maxX) maxX = p.x;
                    if (p.y < minY) minY = p.y;
                    if (p.y > maxY) maxY = p.y;
                    if (p.z < minZ) minZ = p.z;
                    if (p.z > maxZ) maxZ = p.z;
                }
                any = true;
                used.Add(r);
                if (first) { world = r.bounds; first = false; }
                else world.Encapsulate(r.bounds);
            }
        if (!any) return CabinBox.Default;
        var box = new CabinBox(minX, maxX, minY, maxY, minZ, maxZ);
        return box.Plausible ? box : CabinBox.Default;
    }

    // ---- hiding the car's parts that block the view ----

    private static void HideParts(VPVehicleController car, List<Renderer> renderers, Bounds carWorld)
    {
        var eye = car.transform.TransformPoint(new Vector3(_plan.EyeX, _plan.EyeY, _plan.EyeZ));
        var carBox = new CabinBox(carWorld.min.x, carWorld.max.x, carWorld.min.y, carWorld.max.y, carWorld.min.z, carWorld.max.z);
        foreach (var r in renderers)
        {
            if (r == null) continue;
            var b = r.bounds;
            var mn = b.min;
            var mx = b.max;
            bool named = SeatMath.IsSteeringOrDriver(r.name) || SeatMath.IsSteeringOrDriver(r.transform.name);
            if (!SeatMath.ShouldHide(mn.x, mn.y, mn.z, mx.x, mx.y, mx.z, eye.x, eye.y, eye.z, carBox, named)) continue;
            HiddenParts.Add(r);
            HiddenModes.Add(r.shadowCastingMode);
            // Still casts its shadow (onto the dashboard), but is not drawn.
            r.shadowCastingMode = ShadowCastingMode.ShadowsOnly;
        }
    }

    private static void RestoreParts()
    {
        for (int i = 0; i < HiddenParts.Count; i++)
        {
            try { if (HiddenParts[i] != null) HiddenParts[i].shadowCastingMode = HiddenModes[i]; }
            catch (Exception e) { DebugLog.Error(CockpitPlugin.ModId, e, "CockpitRig.RestoreParts"); }
        }
        HiddenParts.Clear();
        HiddenModes.Clear();
    }

    /// <summary>The game's camera shows (a cutscene, photo mode, ...): the model and the hidden parts go back to normal.</summary>
    private static void Suspend()
    {
        if (_suspended) return;
        _suspended = true;
        if (_model != null) _model.SetVisible(false);
        RestoreParts();
        _carPtr = IntPtr.Zero; // rebuilt (parts hidden again) when driving resumes
    }

    private static void Resume()
    {
        if (!_suspended) return;
        _suspended = false;
        Look.Reset();
        Sway.Reset();
        _hasVelocity = false;
        if (_model != null) _model.SetVisible(true);
    }

    // ---- the view ----

    private static void Place(Camera cam, VPVehicleController car)
    {
        float dt = Mathf.Min(Time.unscaledDeltaTime, 0.1f);
        ReadLook(dt);
        ReadCar(car, dt, out float kmh, out float rpm, out int gear, out float steer, out bool low);

        // The head: where it sits (seat adjustments) and how the car throws it about.
        var head = new Vector3(Mathf.Clamp(CockpitConfig.SeatSide.Value, -0.5f, 0.5f), Mathf.Clamp(CockpitConfig.SeatUp.Value, -0.4f, 0.4f),
            Mathf.Clamp(CockpitConfig.SeatForward.Value, -0.6f, 0.6f));
        if (CockpitConfig.Sway.Value)
        {
            float k = Mathf.Clamp(CockpitConfig.SwayAmount.Value, 0.2f, 3f);
            Sway.Gain = 0.006f * k;
            Sway.MaxOffset = 0.09f * Mathf.Min(k, 1.6f);
            head += new Vector3(Sway.X, Sway.Y, Sway.Z);
        }
        var seat = _model != null && _model.Root != null ? _model.Root.transform : null;
        Vector3 pos;
        Quaternion rot;
        if (seat != null)
        {
            pos = seat.TransformPoint(head);
            rot = seat.rotation;
        }
        else
        {
            var ct = car.transform;
            pos = ct.TransformPoint(new Vector3(_plan.EyeX, _plan.EyeY, _plan.EyeZ) + head);
            rot = ct.rotation;
        }
        cam.transform.SetPositionAndRotation(pos, rot * Quaternion.Euler(-Look.Pitch, Look.Yaw, 0f));
        cam.fieldOfView = Mathf.Clamp(CockpitConfig.Fov.Value, 30f, 120f);
        cam.nearClipPlane = Mathf.Clamp(CockpitConfig.NearPlane.Value, 0.01f, 0.5f);

        if (_model != null)
        {
            bool mph = CockpitConfig.Mph.Value;
            float shownSpeed = mph ? kmh * 0.621371f : kmh;
            _topSpeed = Mathf.Max(_topSpeed * 0.9998f, shownSpeed, 100f);
            _peakRpm = Mathf.Max(_peakRpm * 0.9999f, rpm, 4000f);
            _speedNeedle = DialMath.Smooth(_speedNeedle, DialMath.NeedleAngle(shownSpeed, DialMath.SpeedMax(_topSpeed)), 30f, dt);
            _rpmNeedle = DialMath.Smooth(_rpmNeedle, DialMath.NeedleAngle(rpm, DialMath.RpmMax(_peakRpm)), 30f, dt);
            _wheel = DialMath.Smooth(_wheel, DialMath.WheelAngle(steer, Mathf.Clamp(CockpitConfig.WheelTurn.Value, 30f, 900f)), 22f, dt);
            _model.Update(_speedNeedle, _rpmNeedle, _wheel, DialMath.ShownSpeed(kmh, mph), gear, low);
        }
    }

    private static void ReadLook(float dt)
    {
        Look.SpringBack = CockpitConfig.SpringBack.Value;
        Look.ReturnDelay = Mathf.Clamp(CockpitConfig.ReturnDelay.Value, 0f, 5f);
        Look.ReturnSharpness = Mathf.Clamp(CockpitConfig.ReturnSpeed.Value, 0.5f, 30f);
        float dYaw = 0f, dPitch = 0f;
        // Only while the game's Driving map has the input (not under the MODS page, a menu or the map screen).
        if (!CoreBehaviour.MenuOpen && CameraView.DrivingOnTop)
        {
            try
            {
                var mouse = Mouse.current;
                if (mouse != null)
                {
                    var d = mouse.delta.ReadValue();
                    float k = 0.1f * Mathf.Clamp(CockpitConfig.LookMouse.Value, 0.1f, 5f);
                    dYaw += d.x * k;
                    dPitch += d.y * k;
                    if (CockpitConfig.ZoomToLeave.Value && ZoomGate.LeaveStep(ref _leavePush, mouse.scroll.ReadValue().y, 120f))
                    {
                        DebugLog.Info(CockpitPlugin.ModId, "wheel turned out: leaving the cab");
                        SetOn(false);
                        return;
                    }
                }
            }
            catch (Exception ex) { DebugLog.Error(CockpitPlugin.ModId, ex, "CockpitRig.ReadLook"); }
            try
            {
                var pad = Gamepad.current;
                if (pad != null)
                {
                    var s = pad.rightStick.ReadValue();
                    if (s.sqrMagnitude > 0.02f)
                    {
                        float k = 110f * Mathf.Clamp(CockpitConfig.LookStick.Value, 0.1f, 5f) * dt;
                        dYaw += s.x * k;
                        dPitch += s.y * k;
                    }
                }
            }
            catch (Exception ex) { DebugLog.Error(CockpitPlugin.ModId, ex, "CockpitRig.ReadLook"); }
        }
        Look.Step(dYaw, dPitch, dt);
    }

    /// <summary>Speed, RPM, gear, steering and low range from the car (read-only), and the acceleration that moves the head.</summary>
    private static void ReadCar(VPVehicleController car, float dt, out float kmh, out float rpm, out int gear, out float steer, out bool low)
    {
        kmh = rpm = steer = 0f;
        gear = 0;
        low = false;
        var d = car.data;
        if (d != null)
        {
            // Fixed point x1000 (speed, RPM) and x10000 (inputs), as the driving HUD and the AI driver read them.
            kmh = Mathf.Abs(d.Get(Channel.Vehicle, VehicleData.Speed) / 1000f) * 3.6f;
            rpm = d.Get(Channel.Vehicle, VehicleData.EngineRpm) / 1000f;
            gear = d.Get(Channel.Vehicle, VehicleData.GearboxGear);
            steer = d.Get(Channel.Input, InputData.Steer) / 10000f;
        }
        FindStatus(car);
        if (_status != null)
        {
            try { low = _status.GearboxIsLowTransferCase; }
            catch (Exception ex) { DebugLog.Error(CockpitPlugin.ModId, ex, "CockpitRig.ReadCar"); _status = null; }
        }

        float fdt = Time.deltaTime;
        var rb = car.cachedRigidbody;
        if (rb != null && fdt > 0f)
        {
            var v = rb.linearVelocity;
            if (_hasVelocity)
            {
                var local = car.transform.InverseTransformDirection((v - _lastVelocity) / fdt);
                Sway.Step(local.x, local.y, local.z, fdt);
            }
            _lastVelocity = v;
            _hasVelocity = true;
        }
    }

    private static void FindStatus(VPVehicleController car)
    {
        // A car without the component stays without it: stop looking after a few tries.
        if (_status != null || _statusTries >= 5 || Time.unscaledTime < _statusRetryAt) return;
        _statusTries++;
        _statusRetryAt = Time.unscaledTime + 2f;
        try
        {
            _status = car.GetComponent<NetworkedVehicleStatus>();
            if (_status == null) _status = car.GetComponentInChildren<NetworkedVehicleStatus>();
            if (_status == null) _status = car.GetComponentInParent<NetworkedVehicleStatus>();
        }
        catch (Exception ex) { DebugLog.Error(CockpitPlugin.ModId, ex, "CockpitRig.FindStatus"); }
    }
}

/// <summary>
/// Runs <see cref="CockpitRig.AfterGameCamera"/> right after the game's own camera update (and Camera+'s), every frame,
/// through a guard site: it marks the crash trail, is timed, and an exception is caught (never breaks the game's camera
/// update) and noted once in the debug log with repeat counts.
/// </summary>
[HarmonyPatch(typeof(CinemachineManualUpdateDriver), nameof(CinemachineManualUpdateDriver.LateUpdate))]
internal static class AfterGameCameraPatch
{
    private static readonly GuardSite Site = Guard.Site(CockpitPlugin.ModId, "LateUpdate postfix");
    private static readonly Action Body = CockpitRig.AfterGameCamera;

    [HarmonyPriority(Priority.Last)]
    private static void Postfix() => Site.Run(Body);
}
