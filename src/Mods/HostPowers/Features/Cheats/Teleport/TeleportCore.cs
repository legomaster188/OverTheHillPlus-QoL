using System;
using System.Collections.Generic;
using Fusion;
using Il2CppInterop.Runtime;
using OverTheHillPlus.HostPowers;
using UnityEngine;
using UnityEngine.SceneManagement;
using VehiclePhysics;
using Object = UnityEngine.Object;

namespace OverTheHillPlus.Features.Cheats.Teleport;

/// <summary>
/// The teleport engine: ground snapping, moving a car (VehicleBase.Reposition, like the game's own
/// CheatsManager.RPC_TeleportPlayer: detach winches first), a "back" history, the current area key and the map pin.
/// Moving a car only works where this game simulates it (solo / host), see <see cref="GameState.PhysicsAllowed"/>.
/// </summary>
internal static class TeleportCore
{
    private const int MaxHistory = 12;

    /// <summary>Where your car was before each teleport (most recent last).</summary>
    private static readonly List<(Vector3 pos, float yaw, string area)> History = new();

    private static readonly HashSet<string> LoggedOnce = new();

    private static string _areaKey = "", _areaName = "";
    private static float _nextAreaRefresh;

    public static int HistoryCount => History.Count;

    public static string NotAllowedReason =>
        GameState.PhysicsAllowed ? null : "Blocked: teleporting needs solo or hosting (the host's game simulates your car).";

    internal static void LogOnce(string key, string message)
    {
        if (LoggedOnce.Add(key)) HostPowersPlugin.Logger.LogWarning("[Teleport] " + message);
    }

    // ---- area ----

    /// <summary>Stable id of the loaded map (AreaInfo asset name), used to file waypoints per area.</summary>
    public static string AreaKey { get { RefreshArea(); return _areaKey; } }

    /// <summary>Display name of the loaded map.</summary>
    public static string AreaName { get { RefreshArea(); return _areaName; } }

    private static void RefreshArea()
    {
        if (Time.unscaledTime < _nextAreaRefresh) return;
        _nextAreaRefresh = Time.unscaledTime + 2f;
        string key = null, name = null;
        try
        {
            var area = SceneReferences.Instance != null ? SceneReferences.Instance.AreaInfo : null;
            if (area != null)
            {
                key = area.name;
                try { name = area.Name?.GetLocalizedString(); } catch (Exception ex) { DebugLog.Error("hostpowers", ex, "TeleportCore.RefreshArea"); }
            }
        }
        catch (Exception e) { DebugLog.Error("hostpowers", e, "TeleportCore.RefreshArea"); LogOnce("area", "area lookup: " + e.Message); }
        if (string.IsNullOrEmpty(key))
        {
            try { key = AnalyticsManager.GetCurrentAreaName(); } catch (Exception ex) { DebugLog.Error("hostpowers", ex, "TeleportCore.RefreshArea"); }
        }
        if (string.IsNullOrEmpty(key))
        {
            try { key = SceneManager.GetActiveScene().name; } catch (Exception ex) { DebugLog.Error("hostpowers", ex, "TeleportCore.RefreshArea"); }
        }
        _areaKey = string.IsNullOrEmpty(key) ? "unknown" : key;
        _areaName = string.IsNullOrEmpty(name) ? _areaKey : name;
    }

    // ---- ground ----

    /// <summary>Terrain height under a point, if a terrain covers it.</summary>
    public static bool TryTerrainHeight(Vector3 p, out float height)
    {
        height = 0f;
        try
        {
            foreach (var t in Terrain.activeTerrains)
            {
                if (t == null || t.terrainData == null) continue;
                var o = t.GetPosition();
                var size = t.terrainData.size;
                if (p.x < o.x || p.z < o.z || p.x > o.x + size.x || p.z > o.z + size.z) continue;
                height = t.SampleHeight(p) + o.y;
                return true;
            }
        }
        catch (Exception e) { DebugLog.Error("hostpowers", e, "TeleportCore.TryTerrainHeight"); LogOnce("terrain", "terrain height: " + e.Message); }
        return false;
    }

    /// <summary>
    /// Finds the first walkable surface at or just below <paramref name="target"/> (so a cabin's roof or a tree canopy
    /// above the point is skipped), ignoring triggers, moving bodies (cars, props) and <paramref name="ignore"/>'s
    /// colliders; a point under the terrain is lifted onto it. Returns the spot plus <paramref name="clearance"/>.
    /// </summary>
    public static Vector3 Ground(Vector3 target, VehicleBase ignore, float clearance)
    {
        float refY = target.y;
        bool hasTerrain = TryTerrainHeight(target, out float terrainY);
        if (hasTerrain && terrainY > refY) refY = terrainY;

        float best = float.NegativeInfinity;
        try
        {
            Rigidbody own = null;
            try { own = ignore != null ? ignore.cachedRigidbody : null; } catch (Exception ex) { DebugLog.Error("hostpowers", ex, "TeleportCore.Ground"); }
            var origin = new Vector3(target.x, refY + 40f, target.z);
            var hits = Physics.RaycastAll(origin, Vector3.down, 160f, ~0, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < hits.Length; i++)
            {
                var h = hits[i];
                if (h.normal.y < 0.35f) continue; // walls, cliffs
                var rb = h.rigidbody;
                if (rb != null && (own != null && rb.Pointer == own.Pointer || !rb.isKinematic)) continue;
                float y = h.point.y;
                if (y <= refY + 2f && y > best) best = y;
            }
        }
        catch (Exception e) { DebugLog.Error("hostpowers", e, "TeleportCore.Ground"); LogOnce("raycast", "ground raycast: " + e.Message); }

        if (float.IsNegativeInfinity(best)) best = hasTerrain ? terrainY : target.y;
        return new Vector3(target.x, best + clearance, target.z);
    }

    // ---- moving cars ----

    private static float FlatYaw(Transform t)
    {
        var fwd = Vector3.ProjectOnPlane(t.forward, Vector3.up);
        return fwd.sqrMagnitude < 0.01f ? t.eulerAngles.y : Quaternion.LookRotation(fwd.normalized, Vector3.up).eulerAngles.y;
    }

    /// <summary>Moves any car (the host simulates them all). No permission checks here.</summary>
    internal static Vector3 MoveCar(VehicleBase car, Vector3 target, float yaw, bool snap)
    {
        var pos = snap ? Ground(target, car, TeleportFeature.Clearance.Value) : target;
        var rot = Quaternion.Euler(0f, yaw, 0f);
        // Same order as the game's own teleport cheat: let go of winch anchors first, or the rope yanks the car back.
        try
        {
            var winch = car.GetComponentInChildren<VehicleWinchSystem>();
            if (winch != null) winch.DetachAllWinches();
        }
        catch (Exception e) { DebugLog.Error("hostpowers", e, "TeleportCore.MoveCar"); LogOnce("winch", "detach winches: " + e.Message); }

        car.Reposition(pos, rot);

        if (TeleportFeature.StopCar.Value)
        {
            try
            {
                var rb = car.cachedRigidbody;
                if (rb != null && !rb.isKinematic)
                {
                    rb.linearVelocity = Vector3.zero;
                    rb.angularVelocity = Vector3.zero;
                }
            }
            catch (Exception e) { DebugLog.Error("hostpowers", e, "TeleportCore.MoveCar"); LogOnce("stop", "stop car: " + e.Message); }
        }
        return pos;
    }

    /// <summary>Teleports your car. <paramref name="yaw"/> null keeps your current heading.</summary>
    public static string TeleportSelf(Vector3 target, float? yaw, string label, bool snap = true, bool remember = true)
    {
        var blocked = NotAllowedReason;
        if (blocked != null) return blocked;
        var car = GameState.LocalCar;
        if (car == null) return "Failed: no car to move (are you driving?).";
        try
        {
            var t = car.transform;
            var from = t.position;
            float fromYaw = FlatYaw(t);
            var landed = MoveCar(car, target, yaw ?? fromYaw, snap);
            AdvantageFlag.MarkUsed();
            if (remember && (landed - from).sqrMagnitude > 9f)
            {
                History.Add((from, fromYaw, AreaKey));
                if (History.Count > MaxHistory) History.RemoveAt(0);
            }
            return $"Teleported to {label} ({Distance((landed - from).magnitude)} away).";
        }
        catch (Exception e)
        {
            DebugLog.Error("hostpowers", e, "TeleportCore.TeleportSelf");
            HostPowersPlugin.Logger.LogWarning("[Teleport] " + e);
            return "Failed: " + e.Message;
        }
    }

    /// <summary>Goes back to where you were before the last teleport (same map only).</summary>
    public static string Back()
    {
        while (History.Count > 0)
        {
            var last = History[History.Count - 1];
            History.RemoveAt(History.Count - 1);
            if (last.area != AreaKey) continue; // from another map: useless here
            return TeleportSelf(last.pos, last.yaw, "your previous spot", snap: true, remember: false);
        }
        return "Failed: nowhere to go back to on this map.";
    }

    public static void ClearHistory() => History.Clear();

    public static string GoToPlayer(HostTools.Player p)
    {
        if (p == null || p.Car == null) return "Failed: that player has no car yet.";
        if (p.IsMe) return "That's you.";
        try
        {
            var t = p.Car.transform;
            float yaw = FlatYaw(t);
            var behind = t.position - Quaternion.Euler(0f, yaw, 0f) * Vector3.forward * 9f;
            return TeleportSelf(behind, yaw, p.Name);
        }
        catch (Exception e) { DebugLog.Error("hostpowers", e, "TeleportCore.GoToPlayer"); return "Failed: " + e.Message; }
    }

    public static string BringPlayer(HostTools.Player p)
    {
        var blocked = NotAllowedReason;
        if (blocked != null) return blocked;
        if (p == null || p.Car == null) return "Failed: that player has no car yet.";
        if (p.IsMe) return "That's you.";
        var me = GameState.LocalCar;
        if (me == null) return "Failed: no car of yours to bring them to.";
        try
        {
            var t = me.transform;
            float yaw = FlatYaw(t);
            var q = Quaternion.Euler(0f, yaw, 0f);
            var spot = t.position - q * Vector3.forward * 9f + q * Vector3.right * 4f;
            MoveCar(p.Car, spot, yaw, true);
            AdvantageFlag.MarkUsed();
            return $"Brought {p.Name} to you.";
        }
        catch (Exception e) { DebugLog.Error("hostpowers", e, "TeleportCore.BringPlayer"); return "Failed: " + e.Message; }
    }

    // ---- map pin ----

    /// <summary>Your map pin: core's <see cref="Shared.GameMap.TryGetGamePin"/> (the game's pin table, then the flare).</summary>
    public static bool TryGetPin(out Vector3 pin) => global::OverTheHillPlus.Shared.GameMap.TryGetGamePin(out pin);

    private static bool _pinCached;
    private static Vector3 _pinCache;
    private static float _pinCachedAt = -100f;

    /// <summary>Like <see cref="TryGetPin"/> but re-read at most twice a second (for menus and overlays).</summary>
    public static bool TryGetPinCached(out Vector3 pin)
    {
        if (Time.unscaledTime - _pinCachedAt > 0.5f)
        {
            _pinCachedAt = Time.unscaledTime;
            _pinCached = TryGetPin(out _pinCache);
        }
        pin = _pinCache;
        return _pinCached;
    }

    public static string TeleportToPin()
    {
        var blocked = NotAllowedReason;
        if (blocked != null) return blocked;
        if (!TryGetPin(out var pin)) return "Failed: no map pin. Open the map and place a pin first.";
        return TeleportSelf(pin, null, "your map pin");
    }

    // ---- formatting ----

    public static string Distance(float meters) =>
        meters >= 1000f ? (meters / 1000f).ToString("0.0") + " km" : meters.ToString("0") + " m";

    public static bool TryCarPosition(out Vector3 pos, out float yaw)
    {
        pos = default;
        yaw = 0f;
        var car = GameState.LocalCar;
        if (car == null) return false;
        try
        {
            var t = car.transform;
            pos = t.position;
            yaw = FlatYaw(t);
            return true;
        }
        catch (Exception ex) { DebugLog.Error("hostpowers", ex, "TeleportCore.TryCarPosition"); return false; }
    }
}
