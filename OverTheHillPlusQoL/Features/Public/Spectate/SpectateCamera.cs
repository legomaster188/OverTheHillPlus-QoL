using System;
using System.Collections.Generic;
using Fusion;
using Il2CppInterop.Runtime;
using OverTheHillPlus.Features.Public.PlayerList;
using UnityEngine;
using Object = UnityEngine.Object;

namespace OverTheHillPlus.Features.Public.Spectate;

/// <summary>
/// Follow another player's car with your own chase camera. Purely local: nothing is sent to anyone, so it works the
/// same as host and as guest.
///
/// How the game's chase camera works (decoded from Cpp2IL):
/// - Each player car has a CameraFollowUpdater. For the local car, SetupCameraOnPlayerSpawned stores the updater's
///   prefab parent in <c>parent</c>, unparents the updater and makes it CameraFollowUpdater.ActiveCameraUpdater.
/// - CinemachineManualUpdateDriver.LateUpdate calls ActiveCameraUpdater.UpdateCameraTarget(), then
///   CinemachineBrain.ManualUpdate(). UpdateCameraTarget moves the updater to parent.position + parent.up * yOffset
///   + offset (and turns it with parent.forward while <c>vehicle</c> has a grounded wheel); the main CinemachineFreeLook
///   follows the updater. Mouse orbit (CustomCinemachineInputProvider reads Mouse.delta directly) and zoom work on top.
/// - CameraFollowUpdater.Debug_OverrideVehicleToTrack(Transform) only sets <c>parent</c>: the game's own hook for
///   pointing the chase camera at another car. We use it and leave <c>vehicle</c> and <c>networkRigidbody</c> on your
///   own car, because StopCameraOnPlayerDespawned matches on <c>vehicle</c> (pointing it at someone else's car would
///   let their despawn clear your camera, and yours would no longer clean up).
/// - Clearing <c>hasRenderedTeleportKey</c> makes the next UpdateCameraTarget treat the frame as a teleport and cut
///   the camera (it invalidates the virtual camera's previous state) instead of gliding across the map.
///
/// Your car while you watch:
/// - GameInput.PushMap(UIWithCameraZoomAndOrbit) switches input to the map the game uses for menus that keep camera
///   orbit and zoom: driving actions stop. GameInput.MapShouldTriggerAutoHandbrake is true for that map, and
///   VehicleInput.OnInputMapChanged then engages the auto handbrake (if the player's control settings allow it).
/// - On top, VehicleInput.EngageExternalHandbrake() (the call photo mode uses) when the handbrake is off. That state is
///   released by the game itself the moment you give throttle again, exactly like after photo mode.
/// </summary>
internal static class SpectateCamera
{
    internal struct Candidate
    {
        public int Raw;
        public PlayerRef Ref;
        public FusionPlayer Player;
        public string Name;
    }

    private const int MapUiWithCameraOrbit = 3; // GameInput.Map.UIWithCameraZoomAndOrbit
    private static readonly HashSet<int> PhotoMaps = new() { 7, 8, 9 }; // FreeCamera, PhotoModeFreeCamera, PhotoModeUI

    public static bool Active { get; private set; }
    public static string Status { get; private set; } = "off";

    // what we watch
    private static int _targetRaw;
    public static int TargetRaw => _targetRaw;
    private static FusionPlayer _target;
    private static Transform _follow;
    private static IntPtr _followPtr;
    public static string TargetName { get; private set; } = "";

    // the local chase camera we redirected, and what it followed before
    private static CameraFollowUpdater _updater;
    private static IntPtr _updaterPtr;
    private static Transform _savedParent;

    private static Il2CppSystem.Guid? _map;
    private static int _mapRepushes;
    private static float _repushWindowAt = -100f;

    private static VehicleInput _input;
    private static float _inputAt = -10f;

    private static float _nextCheck;
    private static float _sessionLostAt = -1f;
    private static int _errors;

    // speed readout
    private static Vector3 _lastPos;
    private static float _lastPosAt = -1f;
    public static float SpeedKmh { get; private set; }
    public static float DistanceToOwnCar { get; private set; } = -1f;
    public static int Index { get; private set; }
    public static int Count { get; private set; }

    private static readonly List<Candidate> Cands = new();
    private static float _candsAt = -10f;
    private static readonly HashSet<string> Logged = new();

    private static void LogOnce(string key, string message)
    {
        if (Logged.Count < 64 && Logged.Add(key)) Plugin.Logger.LogWarning("[Spectate] " + message);
    }

    // ---- who can be watched ----

    /// <summary>Other players' cars, sorted by player id. Cached for a second unless forced.</summary>
    public static List<Candidate> Candidates(bool force = false)
    {
        if (!force && Time.unscaledTime - _candsAt < 1f && Time.unscaledTime >= _candsAt) return Cands;
        _candsAt = Time.unscaledTime;
        Cands.Clear();
        try
        {
            var runner = PlayerListRoster.Runner;
            if (runner == null) return Cands;
            int local = 0;
            try { local = runner.LocalPlayer.RawEncoded; } catch { }
            var sp = OverlandSessionPlayers.Instance;
            var seen = new HashSet<int>();
            foreach (var o in GameState.FusionPlayers)
            {
                try
                {
                    var fp = o;
                    if (fp == null) continue;
                    var obj = fp.Object;
                    if (obj == null) continue;
                    if (obj.HasInputAuthority) continue;
                    var p = obj.InputAuthority;
                    if (!p.IsRealPlayer || p.RawEncoded == local) continue;
                    if (GameState.LocalCarPtr != IntPtr.Zero && fp.vehicleBase != null && fp.vehicleBase.Pointer == GameState.LocalCarPtr) continue;
                    if (!seen.Add(p.RawEncoded)) continue;
                    Cands.Add(new Candidate { Raw = p.RawEncoded, Ref = p, Player = fp, Name = PlayerListRoster.NameOf(sp, p) });
                }
                catch { /* car despawning mid-scan */ }
            }
            Cands.Sort((a, b) => a.Raw.CompareTo(b.Raw));
        }
        catch (Exception e) { LogOnce("cands", "listing players: " + e.Message); }
        return Cands;
    }

    /// <summary>Why spectating can't start right now (null = it can).</summary>
    public static string BlockReason()
    {
        if (PlayerListRoster.Runner == null) return "Join or host a session first";
        if (GameState.LocalCar == null) return "Waiting for your car";
        CameraFollowUpdater u = null;
        try { u = CameraFollowUpdater.ActiveCameraUpdater; } catch { }
        if (u == null) return "Waiting for the driving camera";
        if (PhotoOrFreeCamera()) return "Close photo mode / the free camera first";
        if (Candidates().Count == 0) return "Nobody else is driving here";
        return null;
    }

    // ---- start / stop / switch ----

    public static void Toggle()
    {
        if (Active) Stop("back to your car");
        else Start(0);
    }

    /// <summary>Starts watching (or switches to) a player; raw 0 = the first one, or the last one watched if still here.</summary>
    public static void Start(int raw)
    {
        try
        {
            string why = BlockReason();
            if (why != null) { Status = why; Menu.SetStatus("Spectate: " + why); return; }
            var list = Candidates(true);
            int idx = list.FindIndex(c => c.Raw == (raw != 0 ? raw : _targetRaw));
            if (idx < 0) idx = 0;

            if (!Active)
            {
                Active = true;
                _errors = 0;
                _mapRepushes = 0;
                _sessionLostAt = -1f;
                PushMap();
                EngageHandbrake();
            }
            if (!Watch(list[idx]))
            {
                Stop("could not follow that car");
                return;
            }
            Status = "watching";
        }
        catch (Exception e)
        {
            LogOnce("start", "start failed: " + e);
            Stop("error: " + e.Message);
        }
    }

    public static void Cycle(int dir)
    {
        if (!Active) { Start(0); return; }
        try
        {
            var list = Candidates(true);
            if (list.Count == 0) { Stop("nobody left to watch"); return; }
            int idx = list.FindIndex(c => c.Raw == _targetRaw);
            idx = idx < 0 ? 0 : ((idx + dir) % list.Count + list.Count) % list.Count;
            if (!Watch(list[idx])) Stop("could not follow that car");
        }
        catch (Exception e)
        {
            LogOnce("cycle", "switching players: " + e);
            Stop("error: " + e.Message);
        }
    }

    public static void Stop(string why)
    {
        bool was = Active;
        Active = false;
        try { RestoreCamera(); }
        catch (Exception e) { LogOnce("restore", "restoring the camera: " + e.Message); }
        try { PopMap(); }
        catch (Exception e) { LogOnce("popmap", "input map: " + e.Message); }
        _target = null;
        _follow = null;
        _followPtr = IntPtr.Zero;
        _targetRaw = 0;
        TargetName = "";
        _lastPosAt = -1f;
        SpeedKmh = 0f;
        DistanceToOwnCar = -1f;
        _input = null;
        _inputAt = -10f;
        Status = why ?? "off";
        if (was && why != null) Menu.SetStatus("Spectate: " + why);
    }

    private static bool Watch(Candidate c)
    {
        if (c.Player == null) return false;
        var follow = FollowTransformOf(c.Player);
        if (follow == null) return false;
        var u = CameraFollowUpdater.ActiveCameraUpdater;
        if (u == null) return false;
        Capture(u);

        bool cut = true;
        try
        {
            float d = Vector3.Distance(u.transform.position, follow.position);
            cut = d > Mathf.Max(0f, SpectateFeature.CutDistance.Value);
        }
        catch { }
        u.Debug_OverrideVehicleToTrack(follow);
        if (cut) Cut(u);

        _target = c.Player;
        _follow = follow;
        _followPtr = follow.Pointer;
        _targetRaw = c.Raw;
        TargetName = c.Name ?? "";
        _lastPosAt = -1f;
        SpeedKmh = 0f;
        return true;
    }

    /// <summary>The transform the game would follow for that car: its camera updater's prefab parent, else the car root.</summary>
    private static Transform FollowTransformOf(FusionPlayer fp)
    {
        try
        {
            var cu = fp.cameraFollowUpdater;
            if (cu != null)
            {
                var p = cu.transform.parent;
                if (p != null) return p;
            }
        }
        catch { }
        try { return fp.transform; } catch { return null; }
    }

    /// <summary>Remembers what this updater followed before we touched it (once per updater).</summary>
    private static void Capture(CameraFollowUpdater u)
    {
        if (u.Pointer == _updaterPtr && _updater != null) return;
        _updater = u;
        _updaterPtr = u.Pointer;
        _savedParent = null;
        try { _savedParent = u.parent; } catch { }
    }

    private static void Cut(CameraFollowUpdater u)
    {
        try { u.hasRenderedTeleportKey = false; } catch { }
        try
        {
            var vcam = SceneReferences.Instance?.MainVirtualCamera;
            if (vcam != null) vcam.PreviousStateIsValid = false;
        }
        catch { }
    }

    private static void RestoreCamera()
    {
        var u = _updater;
        var saved = _savedParent;
        _updater = null;
        _updaterPtr = IntPtr.Zero;
        _savedParent = null;
        if (u == null || saved == null) return; // destroyed (car respawned): the new camera follows your car already
        bool cut = true;
        try { cut = Vector3.Distance(u.transform.position, saved.position) > Mathf.Max(0f, SpectateFeature.CutDistance.Value); } catch { }
        u.Debug_OverrideVehicleToTrack(saved);
        if (cut) Cut(u);
    }

    // ---- input map and handbrake ----

    private static void PushMap()
    {
        if (_map != null) return;
        try { _map = GameInput.PushMap(GameInput.Map.UIWithCameraZoomAndOrbit, 0); }
        catch (Exception e) { LogOnce("pushmap", "could not switch the input map: " + e.Message); }
    }

    private static void PopMap()
    {
        if (_map == null) return;
        var id = _map.Value;
        _map = null;
        GameInput.PopMap(id);
    }

    private static bool OurMapInStack()
    {
        try { return GameInput.IsMapActive(GameInput.Map.UIWithCameraZoomAndOrbit); }
        catch { return true; }
    }

    /// <summary>Topmost input map (what the keyboard currently drives), or -1.</summary>
    private static int TopMap()
    {
        try
        {
            var maps = GameInput.GetActiveMaps();
            if (maps != null && maps.Length > 0) return (int)maps[0];
        }
        catch { }
        return -1;
    }

    private static bool PhotoOrFreeCamera()
    {
        try
        {
            var fcm = FreeCameraManager.Instance;
            if (fcm != null && fcm.IsActive) return true;
        }
        catch { }
        try
        {
            var maps = GameInput.GetActiveMaps();
            if (maps != null)
                for (int i = 0; i < maps.Length; i++)
                    if (PhotoMaps.Contains((int)maps[i])) return true;
        }
        catch { }
        return false;
    }

    /// <summary>True when our map is the one receiving input (no game menu, map screen or the mod menu on top).</summary>
    public static bool HasInputFocus => Active && TopMap() == MapUiWithCameraOrbit;

    private static VehicleInput LocalInput()
    {
        if (_input != null) return _input;
        if (Time.unscaledTime - _inputAt < 1f) return null;
        _inputAt = Time.unscaledTime;
        try
        {
            foreach (var o in Object.FindObjectsOfType(Il2CppType.Of<VehicleInput>()))
            {
                var vi = o.TryCast<VehicleInput>();
                if (vi == null) continue;
                try { if (vi.Object != null && vi.HasInputAuthority) { _input = vi; break; } } catch { }
            }
        }
        catch (Exception e) { LogOnce("input", "finding your car's input: " + e.Message); }
        return _input;
    }

    private static void EngageHandbrake()
    {
        if (!SpectateFeature.Handbrake.Value) return;
        var vi = LocalInput();
        if (vi == null) return;
        try
        {
            // Disengaged (0) only: AutoHandbrake (1) is released by the game when driving input comes back,
            // ExternallyEngaged (2) when you give throttle, Engaged (3) is your own parking brake.
            if (vi.handbrakeState == VehicleInput.HandbrakeState.Disengaged) vi.EngageExternalHandbrake();
        }
        catch (Exception e) { LogOnce("handbrake", "handbrake: " + e.Message); _input = null; }
    }

    // ---- per frame ----

    public static void Tick()
    {
        if (!Active) return;
        try
        {
            TickActive();
            _errors = 0;
        }
        catch (Exception e)
        {
            LogOnce("tick:" + e.GetType().Name, "tick: " + e);
            if (++_errors > 30) Stop("stopped after an error");
        }
    }

    private static void TickActive()
    {
        float now = Time.unscaledTime;

        // Keep the chase camera on the target every frame (a respawn or the game re-setting the camera undoes it).
        var u = CameraFollowUpdater.ActiveCameraUpdater;
        if (u != null && (_follow == null || !SameFollow(u)))
        {
            if (u.Pointer != _updaterPtr) { _updater = null; Capture(u); }
            if (_follow != null) { u.Debug_OverrideVehicleToTrack(_follow); Cut(u); }
        }

        KeepHeading(u);
        UpdateReadout(now);

        if (now < _nextCheck) return;
        _nextCheck = now + 0.25f;

        // Session gone or no car of our own: give it a moment (loading hiccups), then stop.
        bool sessionOk = PlayerListRoster.Runner != null && GameState.LocalCar != null && u != null;
        if (!sessionOk)
        {
            if (_sessionLostAt < 0f) _sessionLostAt = now;
            else if (now - _sessionLostAt > 2f) { Stop("session or car gone"); return; }
        }
        else _sessionLostAt = -1f;

        if (PhotoOrFreeCamera()) { Stop("photo mode opened"); return; }

        // Target left or despawned.
        bool targetOk = false;
        try { targetOk = _target != null && _follow != null && _target.Object != null; } catch { }
        if (!targetOk)
        {
            var list = Candidates(true);
            if (SpectateFeature.AutoNext.Value && list.Count > 0)
            {
                Menu.SetStatus("Spectate: player left, watching " + list[0].Name);
                if (!Watch(list[0])) Stop("could not follow that car");
            }
            else Stop(list.Count == 0 ? "nobody left to watch" : "player left");
            return;
        }

        // Something rebuilt the game's input stack (StateGame does that when returning to driving): put our map back,
        // but give up if it keeps getting thrown out (more than 5 times in 10 s).
        if (_map != null && !OurMapInStack())
        {
            if (now - _repushWindowAt > 10f) { _repushWindowAt = now; _mapRepushes = 0; }
            if (_mapRepushes++ < 5) { _map = null; PushMap(); }
            else { Stop("the game keeps resetting the controls"); return; }
        }

        EngageHandbrake();

        var list2 = Candidates();
        Count = list2.Count;
        Index = list2.FindIndex(c => c.Raw == _targetRaw) + 1;
    }

    /// <summary>
    /// UpdateCameraTarget turns the followed point with the car (LookRotation(parent.forward, up), which the camera
    /// recentres behind) only while <c>vehicle</c> (still your own car) has a wheel on the ground. If your car is on
    /// its roof or in the air while you watch, the same rotation is set here so the camera still swings behind the
    /// watched car. Skipped while the watched car is tipped over, as the game does for an airborne car.
    /// </summary>
    private static void KeepHeading(CameraFollowUpdater u)
    {
        if (u == null || _follow == null) return;
        var up = _follow.up;
        if (up.y < 0.5f) return;
        var fwd = _follow.forward;
        if (fwd.sqrMagnitude < 0.01f) return;
        u.transform.rotation = Quaternion.LookRotation(fwd, Vector3.up);
    }

    private static bool SameFollow(CameraFollowUpdater u)
    {
        var p = u.parent;
        return p != null && p.Pointer == _followPtr;
    }

    private static void UpdateReadout(float now)
    {
        if (_follow == null) return;
        Vector3 pos;
        try { pos = _follow.position; }
        catch { return; }
        if (_lastPosAt >= 0f)
        {
            float dt = now - _lastPosAt;
            if (dt > 0.0001f)
            {
                float v = Vector3.Distance(pos, _lastPos) / dt * 3.6f;
                if (v < 600f) SpeedKmh = Mathf.Lerp(SpeedKmh, v, 1f - Mathf.Exp(-dt * 4f));
            }
        }
        _lastPos = pos;
        _lastPosAt = now;
        try
        {
            var car = GameState.LocalCar;
            DistanceToOwnCar = car != null ? Vector3.Distance(car.transform.position, pos) : -1f;
        }
        catch { DistanceToOwnCar = -1f; }
    }
}
