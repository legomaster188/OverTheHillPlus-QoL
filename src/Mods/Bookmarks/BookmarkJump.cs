using System;
using System.Collections.Generic;
using System.IO;
using BepInEx;
using UnityEngine;
using UnityEngine.SceneManagement;
using VehiclePhysics;

namespace OverTheHillPlus.Bookmarks;

/// <summary>
/// The game side of Teleport Bookmarks: which map is loaded (bookmarks are filed per map), where your car is, and the
/// jump itself. A jump looks for safe ground near the bookmark with core's <see cref="SafeSpot"/> (solid ground, not
/// water, not too steep, nothing in the car's way), then moves the car the way the game's own teleport does
/// (detach the winches, VehicleBase.Reposition, stop the car). Jumping only works where this game simulates your car
/// (solo or hosting, <see cref="GameState.PhysicsAllowed"/>) and marks <see cref="AdvantageFlag"/>. With no ground found
/// near the spot (the area not loaded) the jump is refused rather than dropping the car blind.
/// The map key lookup and the move are small copies of Host Powers' teleport (mods don't reference each other).
/// </summary>
internal static class BookmarkJump
{
    private const int MaxHistory = 12;

    private static readonly List<(Vector3 pos, float yaw, string map)> History = new();
    private static readonly HashSet<string> LoggedOnce = new();
    private static string _mapKey = "", _mapName = "";
    private static float _nextMapRefresh;
    private static BookmarkStore _store;

    /// <summary>The bookmarks file (BepInEx/config/oth.bookmarks.json), opened on first use.</summary>
    public static BookmarkStore Store => _store ??= new BookmarkStore(Path.Combine(Paths.ConfigPath, "oth.bookmarks.json"))
    {
        Log = m => BookmarksPlugin.Logger?.LogWarning("[Bookmarks] " + m),
    };

    public static int HistoryCount => History.Count;

    /// <summary>Null when a jump is possible here, else why not.</summary>
    public static string BlockReason
    {
        get
        {
            if (GameState.Runner == null) return "Load into a world first.";
            if (!GameState.PhysicsAllowed) return "Jumping needs solo or hosting (the host's game moves your car).";
            if (GameState.LocalCar == null) return "Get in your car first.";
            return null;
        }
    }

    private static void LogOnce(string key, string message)
    {
        if (LoggedOnce.Add(key)) BookmarksPlugin.Logger?.LogWarning("[Bookmarks] " + message + " (logged once)");
    }

    // ---------------------------------------------------------------- map

    /// <summary>Stable id of the loaded map (its AreaInfo asset name; the scene name as a fallback).</summary>
    public static string MapKey { get { RefreshMap(); return _mapKey; } }

    public static string MapName { get { RefreshMap(); return _mapName; } }

    private static void RefreshMap()
    {
        if (Time.unscaledTime < _nextMapRefresh) return;
        _nextMapRefresh = Time.unscaledTime + 2f;
        string key = null, name = null;
        try
        {
            var area = SceneReferences.Instance != null ? SceneReferences.Instance.AreaInfo : null;
            if (area != null)
            {
                key = area.name;
                try { name = area.Name?.GetLocalizedString(); } catch (Exception ex) { DebugLog.Error("bookmarks", ex, "BookmarkJump.RefreshMap"); }
            }
        }
        catch (Exception e) { DebugLog.Error("bookmarks", e, "BookmarkJump.RefreshMap"); LogOnce("area", "map lookup: " + e.Message); }
        if (string.IsNullOrEmpty(key))
        {
            try { key = AnalyticsManager.GetCurrentAreaName(); } catch (Exception ex) { DebugLog.Error("bookmarks", ex, "BookmarkJump.RefreshMap"); }
        }
        if (string.IsNullOrEmpty(key))
        {
            try { key = SceneManager.GetActiveScene().name; } catch (Exception ex) { DebugLog.Error("bookmarks", ex, "BookmarkJump.RefreshMap"); }
        }
        _mapKey = string.IsNullOrEmpty(key) ? "unknown" : key;
        _mapName = string.IsNullOrEmpty(name) ? _mapKey : name;
    }

    // ---------------------------------------------------------------- your car

    private static float FlatYaw(Transform t)
    {
        var fwd = Vector3.ProjectOnPlane(t.forward, Vector3.up);
        return fwd.sqrMagnitude < 0.01f ? t.eulerAngles.y : Quaternion.LookRotation(fwd.normalized, Vector3.up).eulerAngles.y;
    }

    public static bool TryCarPose(out Vector3 pos, out float yaw)
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
            return SafeSpot.IsFinite(pos);
        }
        catch (Exception ex) { DebugLog.Error("bookmarks", ex, "BookmarkJump.TryCarPose"); return false; }
    }

    /// <summary>Saves where your car is now. Works anywhere (it only reads your position).</summary>
    public static string SaveHere(string name)
    {
        if (GameState.Runner == null) return "Load into a world first.";
        if (!TryCarPose(out var pos, out float yaw)) return "Failed: get in your car first.";
        string saved = Store.Add(MapKey, name, pos.x, pos.y, pos.z, yaw, DateTime.Now, out _);
        DebugLog.Info(BookmarksPlugin.ModId, $"bookmark saved on '{MapKey}' at {pos.x:0},{pos.z:0}: {saved}");
        return saved;
    }

    public static string MoveHere(BookmarkStore.Bookmark b)
    {
        if (!TryCarPose(out var pos, out float yaw)) return "Failed: get in your car first.";
        return Store.Update(MapKey, b, pos.x, pos.y, pos.z, yaw, DateTime.Now) ? $"\"{b.Name}\" moved to your car." : "Failed: bookmark not found.";
    }

    // ---------------------------------------------------------------- jumping

    public static string Jump(BookmarkStore.Bookmark b)
    {
        if (b == null) return "Failed: no bookmark.";
        return JumpTo(new Vector3(b.X, b.Y, b.Z), b.Yaw, $"\"{b.Name}\"", remember: true);
    }

    /// <summary>Goes back to where you were before the last jump (same map only).</summary>
    public static string Back()
    {
        while (History.Count > 0)
        {
            var last = History[History.Count - 1];
            History.RemoveAt(History.Count - 1);
            if (last.map != MapKey) continue; // from another map: useless here
            return JumpTo(last.pos, last.yaw, "your previous spot", remember: false);
        }
        return "Failed: nowhere to go back to on this map.";
    }

    public static void ForgetHistory() => History.Clear();

    private static string JumpTo(Vector3 wanted, float yaw, string label, bool remember)
    {
        var blocked = BlockReason;
        if (blocked != null) return "Blocked: " + blocked;
        var car = GameState.LocalCar;
        try
        {
            var t = car.transform;
            var from = t.position;
            float fromYaw = FlatYaw(t);

            var ctx = new SafeSpot.Context();
            ctx.Ignore(car);
            foreach (var fp in GameState.FusionPlayers)
            {
                try
                {
                    if (fp == null) continue;
                    var other = fp.vehicleBase;
                    if (other == null || other.Pointer == car.Pointer) continue;
                    ctx.Occupied.Add(other.transform.position);
                }
                catch (Exception ex) { DebugLog.Error("bookmarks", ex, "BookmarkJump.JumpTo"); }
            }
            var spot = SafeSpot.Find(wanted, yaw, ctx);
            if (!spot.Ok)
            {
                // No ground found at all means the area isn't loaded (or the spot is over a hole): dropping the car
                // there blind could put it under the map, so the jump is refused.
                bool noGround = spot.Why != null && spot.Why.StartsWith("no ground", StringComparison.Ordinal);
                DebugLog.Info(BookmarksPlugin.ModId, $"jump to {label} refused: {spot.Why}");
                return noGround
                    ? $"Failed: no ground found at {label} (the area may not be loaded yet). Try again in a moment."
                    : $"Failed: no safe spot near {label} ({spot.Why}).";
            }
            var target = spot.Ground;

            float clearance = Mathf.Clamp(BookmarksConfig.Clearance.Value, 0.3f, 5f);
            Place(car, target + Vector3.up * clearance, spot.Yaw);
            AdvantageFlag.MarkUsed();

            var landed = target;
            if (remember && (landed - from).sqrMagnitude > 9f)
            {
                History.Add((from, fromYaw, MapKey));
                if (History.Count > MaxHistory) History.RemoveAt(0);
            }
            string offset = spot.Offset > 1f ? $", {spot.Offset:0} m from the spot" : "";
            DebugLog.Info(BookmarksPlugin.ModId, $"jumped to {label} on '{MapKey}': {(landed - from).magnitude:0} m, safe spot {spot.Offset:0.0} m off, clearance {clearance:0.0} m, {History.Count} in the way back");
            return $"Jumped to {label} ({Distance((landed - from).magnitude)} away{offset}).";
        }
        catch (Exception e)
        {
            DebugLog.Error("bookmarks", e, "BookmarkJump.JumpTo");
            BookmarksPlugin.Logger?.LogWarning("[Bookmarks] jump: " + e);
            return "Failed: " + e.Message;
        }
    }

    /// <summary>Moves the car like the game's own teleport: winches off first, then Reposition, then stop it.</summary>
    private static void Place(VehicleBase car, Vector3 pos, float yaw)
    {
        try
        {
            var winch = car.GetComponentInChildren<VehicleWinchSystem>();
            if (winch != null) winch.DetachAllWinches();
        }
        catch (Exception e) { DebugLog.Error("bookmarks", e, "BookmarkJump.Place"); LogOnce("winch", "detach winches: " + e.Message); }

        car.Reposition(pos, Quaternion.Euler(0f, yaw, 0f));

        if (!BookmarksConfig.StopCar.Value) return;
        try
        {
            var rb = car.cachedRigidbody;
            if (rb != null && !rb.isKinematic)
            {
                rb.linearVelocity = Vector3.zero;
                rb.angularVelocity = Vector3.zero;
            }
        }
        catch (Exception e) { DebugLog.Error("bookmarks", e, "BookmarkJump.Place"); LogOnce("stop", "stop car: " + e.Message); }
    }

    // ---------------------------------------------------------------- text

    public static string Distance(float meters) =>
        meters >= 1000f ? (meters / 1000f).ToString("0.0") + " km" : meters.ToString("0") + " m";

    /// <summary>How far a bookmark is from your car, or null without a car.</summary>
    public static string DistanceTo(BookmarkStore.Bookmark b)
    {
        if (b == null || !TryCarPose(out var pos, out _)) return null;
        return Distance(Vector3.Distance(pos, new Vector3(b.X, b.Y, b.Z)));
    }
}
