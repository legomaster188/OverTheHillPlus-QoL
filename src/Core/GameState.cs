using System;
#if MELON
using Il2CppFusion;
#else
using Fusion;
#endif
using Il2CppInterop.Runtime;
using UnityEngine;
#if MELON
using Il2CppVehiclePhysics;
#else
using VehiclePhysics;
#endif
using Object = UnityEngine.Object;

namespace OverTheHillPlus;

/// <summary>
/// Cached lookups of the network session and the local player's car, refreshed once per second. The session comes from the
/// game's own runner holder, and the car stays the car it was while it is still yours: searching the world for them
/// every second cost 40 ms a time, and a search now only runs when the world changed (<see cref="WorldScan"/>).
/// </summary>
internal static partial class GameState
{
    public static NetworkRunner Runner { get; private set; }
    public static VPVehicleController LocalCar { get; private set; }
    public static IntPtr LocalCarPtr { get; private set; }

    /// <summary>Your own player in the session, or null when there is no session.</summary>
    public static PlayerRef? LocalPlayer
    {
        get
        {
            try { return Runner != null ? Runner.LocalPlayer : null; }
            catch (Exception ex) { DebugLog.Error("core", ex, "GameState.LocalPlayer"); return null; }
        }
    }

    private static float _nextRefresh;

    private static FusionPlayer[] _fusionPlayers = Array.Empty<FusionPlayer>();
    private static readonly Core.Shared.ScanPacer PlayersPace = WorldScan.Pace("cars", 1f);
    // Searches for what is missing: with no session or no car of yours nobody is driving, so these cost nothing anyone
    // feels, and whatever the game's holder or the car hooks didn't tell still turns up.
    private static float _nextRunnerSearch, _nextCarSearch;

    /// <summary>
    /// Every FusionPlayer (player car object) in the scene, from one shared scan, so features don't each walk all loaded
    /// objects, and that scan only runs after the world changed (<see cref="WorldScan"/>). Entries can be destroyed
    /// already (check with the Unity null test, e.g. <c>fp == null</c>) and a car spawned in the last second can be
    /// missing. Main thread only.
    /// </summary>
    public static FusionPlayer[] FusionPlayers
    {
        get
        {
            if (!WorldScan.Due(PlayersPace, Time.unscaledTime)) return _fusionPlayers;
            try
            {
                var found = Object.FindObjectsOfType(Il2CppType.Of<FusionPlayer>());
                var list = new System.Collections.Generic.List<FusionPlayer>(found.Length);
                foreach (var o in found)
                {
                    var fp = o?.TryCast<FusionPlayer>();
                    if (fp != null) list.Add(fp);
                }
                _fusionPlayers = list.ToArray();
            }
            catch (Exception e)
            {
                DebugLog.Error("core", e, "GameState.FusionPlayers");
                Core.CorePlugin.Logger?.LogDebug("FusionPlayers: " + e.Message);
                _fusionPlayers = Array.Empty<FusionPlayer>();
            }
            return _fusionPlayers;
        }
    }

    /// <summary>A session property as text (string properties as-is, others via ToString); null and found = false when absent.</summary>
    public static string SessionString(SessionInfo session, string key, out bool found)
    {
        found = false;
        var props = session?.Properties;
        if (props == null || !props.ContainsKey(key)) return null;
        var prop = props[key];
        found = true;
        if (prop == null) return null;
        return prop.IsString ? (string)prop : prop.PropertyValue?.ToString();
    }

    /// <summary>True when the joined session is private (room code / friends only).</summary>
    public static bool JoinedPrivateSession { get; private set; }

    /// <summary>Offline, single-player, or hosting: this game runs the physics for your car.</summary>
    public static bool IsAuthority
    {
        get
        {
            // Asked many times per frame (every physics step of the local car, per menu widget, ...): each answer
            // costs several interop calls on the runner, so it's computed once per frame.
            int frame = Time.frameCount;
            if (frame == _authorityFrame) return _authority;
            _authority = ComputeAuthority();
            _authorityFrame = frame;
            return _authority;
        }
    }

    private static int _authorityFrame = -1;
    private static bool _authority;

    private static bool ComputeAuthority()
    {
        var r = Runner;
        if (r == null) return true;
        try
        {
            if (r.IsSinglePlayer || r.IsServer) return true;
            if (r.GameMode == GameMode.Shared && r.IsSharedModeMasterClient) return true;
        }
        catch (Exception ex) { DebugLog.Error("core", ex, "GameState.ComputeAuthority"); }
        return false;
    }

    /// <summary>A guest in a public lobby: commands that affect other players are blocked here.</summary>
    public static bool IsPublicGuest => !IsAuthority && !JoinedPrivateSession;

    /// <summary>Short badge for the menu header.</summary>
    public static string Badge
    {
        get
        {
            var r = Runner;
            if (r == null) return "MENU";
            try
            {
                if (r.IsSinglePlayer) return "SOLO";
                if (IsAuthority) return "HOST";
                return JoinedPrivateSession ? "FRIENDS" : "PUBLIC";
            }
            catch (Exception ex) { DebugLog.Error("core", ex, "GameState.Badge"); return "?"; }
        }
    }

    /// <summary>
    /// Whether this game simulates car physics (solo or hosting). The host simulates every car, so on a joined
    /// client any local physics change would just be overwritten by the host's result.
    /// </summary>
    public static bool PhysicsAllowed => IsAuthority;

    public static string SessionDescription
    {
        get
        {
            var r = Runner;
            if (r == null) return "offline / menu";
            try
            {
                if (r.IsSinglePlayer) return "single-player";
                if (r.IsServer) return "hosting";
                if (r.GameMode == GameMode.Shared && r.IsSharedModeMasterClient) return "hosting (shared)";
                return JoinedPrivateSession ? "joined a private (friends) session" : "joined a public session";
            }
            catch (Exception ex) { DebugLog.Error("core", ex, "GameState.SessionDescription"); return "unknown"; }
        }
    }

    public static void Tick()
    {
        if (Time.unscaledTime < _nextRefresh) return;
        _nextRefresh = Time.unscaledTime + 1f;
        Runner = FindRunner();
        _authorityFrame = -1; // runner may have changed: recompute IsAuthority on next use
        JoinedPrivateSession = Runner != null && IsPrivate(Runner);
        WorldScan.Poll(Runner);
        DropDeadCars();
        LocalCar = FindLocalCar();
        LocalCarPtr = LocalCar == null ? IntPtr.Zero : LocalCar.Pointer;
        if (LocalCarPtr != IntPtr.Zero) WorldScan.SawLocalCar();
    }

    private static readonly System.Collections.Generic.List<FusionPlayer> Left = new();

    /// <summary>A car on the list is gone: it leaves the list, and the searches may look again. A handful of cars at most,
    /// each one native check.</summary>
    private static void DropDeadCars()
    {
        var cars = _fusionPlayers;
        if (cars.Length == 0) return;
        Left.Clear();
        foreach (var fp in cars)
        {
            try { if (fp != null) Left.Add(fp); }
            catch (Exception ex) { DebugLog.Error("core", ex, "GameState.DropDeadCars"); }
        }
        if (Left.Count == cars.Length) return;
        _fusionPlayers = Left.ToArray();
        WorldScan.Changed();
    }

    /// <summary>False before Fusion spawns this behaviour and once it has let it go: its networked properties throw then.</summary>
    public static bool IsSpawned(NetworkBehaviour b)
    {
        try { return b != null && b.Object != null && b.Object.IsValid; }
        catch (Exception) { return false; }
    }

    private static bool IsPrivate(NetworkRunner r)
    {
        try
        {
            var props = r.SessionInfo?.Properties;
            return props != null && new OverlandRoomProperties(props).PrivateServer;
        }
        catch (Exception ex) { DebugLog.Error("core", ex, "GameState.IsPrivate"); return false; }
    }

    /// <summary>
    /// Called from physics-step patches for every car (several times per tick with client prediction), so the cheap
    /// pointer test runs first and the Unity null check (a native call) only when there is a local car at all.
    /// </summary>
    public static bool IsLocalCar(VehicleBase v) =>
        LocalCarPtr != IntPtr.Zero && v is not null && v.Pointer == LocalCarPtr && v != null;

    private static NetworkRunner FindRunner()
    {
        try
        {
            var held = OverlandNetworkRunner.Instance?.NetworkRunner;
            if (held != null && held.IsRunning) return held;
        }
        catch (Exception e) { DebugLog.Error("core", e, "GameState.FindRunner held"); }
        // Not in the game's own session: the menus, or a runner it doesn't hold, which stays while it runs. A search now and then.
        try
        {
            var known = Runner;
            if (known != null && known.IsRunning) return known;
        }
        catch (Exception e) { DebugLog.Error("core", e, "GameState.FindRunner known"); }
        float now = Time.unscaledTime;
        if (now < _nextRunnerSearch || !WorldScan.Turn()) return null;
        _nextRunnerSearch = now + 5f;
        try
        {
            foreach (var o in Object.FindObjectsOfType(Il2CppType.Of<NetworkRunner>()))
            {
                var r = o.TryCast<NetworkRunner>();
                if (r != null && r.IsRunning) return r;
            }
        }
        catch (Exception e) { DebugLog.Error("core", e, "GameState.FindRunner"); Core.CorePlugin.Logger?.LogDebug("FindRunner: " + e.Message); }
        return null;
    }

    /// <summary>The car already known is still the one this player drives: no search needed.</summary>
    private static bool StillMine(VPVehicleController v)
    {
        try
        {
            if (v == null || !v.isActiveAndEnabled) return false;
            if (v.Object != null && v.HasInputAuthority) return true;
            return _onlyCar && Runner.IsSinglePlayer;
        }
        catch (Exception ex) { DebugLog.Error("core", ex, "GameState.StillMine"); return false; }
    }

    // The car came from the single-player rule below: it stays the one until it is gone.
    private static bool _onlyCar;

    private static VPVehicleController FindLocalCar()
    {
        if (Runner == null) { _onlyCar = false; return null; } // no session, no car of ours
        if (StillMine(LocalCar)) return LocalCar;
        _onlyCar = false;
        // Among the cars that are listed anyway, then a search of its own.
        foreach (var fp in FusionPlayers)
        {
            try
            {
                if (fp == null) continue;
                var v = fp.GetComponent<VPVehicleController>();
                if (v != null && v.isActiveAndEnabled && v.Object != null && v.HasInputAuthority) return v;
            }
            catch (Exception ex) { DebugLog.Error("core", ex, "GameState.FindLocalCar list"); }
        }
        float now = Time.unscaledTime;
        if (now < _nextCarSearch || !WorldScan.Turn()) return null;
        _nextCarSearch = now + 2f;
        try
        {
            VPVehicleController only = null;
            int count = 0;
            foreach (var o in Object.FindObjectsOfType(Il2CppType.Of<VPVehicleController>()))
            {
                var v = o.TryCast<VPVehicleController>();
                if (v == null || !v.isActiveAndEnabled) continue;
                count++;
                only = v;
                try { if (v.Object != null && v.HasInputAuthority) return v; } catch (Exception ex) { DebugLog.Error("core", ex, "GameState.FindLocalCar"); }
            }
            // Single-player fallback: the only car around is ours.
            if (count == 1 && Runner != null && Runner.IsSinglePlayer) { _onlyCar = true; return only; }
        }
        catch (Exception e) { DebugLog.Error("core", e, "GameState.FindLocalCar"); Core.CorePlugin.Logger?.LogDebug("FindLocalCar: " + e.Message); }
        return null;
    }
}
