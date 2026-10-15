using System;
using Fusion;
using Il2CppInterop.Runtime;
using UnityEngine;
using VehiclePhysics;
using Object = UnityEngine.Object;

namespace OverTheHillPlus;

/// <summary>Cached lookups of the network session and the local player's car, refreshed once per second.</summary>
internal static class GameState
{
    public static NetworkRunner Runner { get; private set; }
    public static VPVehicleController LocalCar { get; private set; }
    public static IntPtr LocalCarPtr { get; private set; }

    private static float _nextRefresh;

    /// <summary>True when the joined session is private (room code / friends only).</summary>
    public static bool JoinedPrivateSession { get; private set; }

    /// <summary>Offline, single-player, or hosting: this game runs the physics for your car.</summary>
    public static bool IsAuthority
    {
        get
        {
            var r = Runner;
            if (r == null) return true;
            try
            {
                if (r.IsSinglePlayer || r.IsServer) return true;
                if (r.GameMode == GameMode.Shared && r.IsSharedModeMasterClient) return true;
            }
            catch { }
            return false;
        }
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
            catch { return "?"; }
        }
    }

    /// <summary>
    /// Car physics changes (assists, no damage, extra acceleration, flip). The host simulates every car,
    /// so on a joined client these would just be overwritten by the host's result.
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
            catch { return "unknown"; }
        }
    }

    public static void Tick()
    {
        if (Time.unscaledTime < _nextRefresh) return;
        _nextRefresh = Time.unscaledTime + 1f;
        Runner = FindRunner();
        JoinedPrivateSession = Runner != null && IsPrivate(Runner);
        LocalCar = FindLocalCar();
        LocalCarPtr = LocalCar == null ? IntPtr.Zero : LocalCar.Pointer;
    }

    private static bool IsPrivate(NetworkRunner r)
    {
        try
        {
            var props = r.SessionInfo?.Properties;
            return props != null && new OverlandRoomProperties(props).PrivateServer;
        }
        catch { return false; }
    }

    public static bool IsLocalCar(VehicleBase v) => v != null && LocalCarPtr != IntPtr.Zero && v.Pointer == LocalCarPtr;

    private static NetworkRunner FindRunner()
    {
        try
        {
            foreach (var o in Object.FindObjectsOfType(Il2CppType.Of<NetworkRunner>()))
            {
                var r = o.TryCast<NetworkRunner>();
                if (r != null && r.IsRunning) return r;
            }
        }
        catch (Exception e) { Plugin.Logger.LogDebug("FindRunner: " + e.Message); }
        return null;
    }

    private static VPVehicleController FindLocalCar()
    {
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
                try { if (v.Object != null && v.HasInputAuthority) return v; } catch { }
            }
            // Single-player fallback: the only car around is ours.
            if (count == 1 && Runner != null && Runner.IsSinglePlayer) return only;
        }
        catch (Exception e) { Plugin.Logger.LogDebug("FindLocalCar: " + e.Message); }
        return null;
    }
}
