using System;
using System.Collections.Generic;
using Fusion;
using Il2CppInterop.Runtime;
using OverTheHillPlus.CheatsPlus;
using UnityEngine;
using VehiclePhysics;
using Object = UnityEngine.Object;

namespace OverTheHillPlus;

/// <summary>Car switching, dummy cars, towing and other per-car tools.</summary>
internal static class Vehicles
{
    private static readonly List<string> SpawnedDummies = new();
    private static readonly Dictionary<IntPtr, float> OriginalWinchRange = new();

    /// <summary>Swaps your car in place, the same way the garage does it (stock upgrades).</summary>
    public static string SwitchCar(ItemCatalog.Item car)
    {
        var sm = SpawnManager.Instance;
        var me = GameState.LocalPlayer;
        if (sm == null || me == null) return "Failed: load into a world first.";
        try
        {
            AdvantageFlag.MarkUsed();
            sm.RPC_ChangeCarAtCurrentPosition(me.Value, car.Id, new BitSet256(), 0, 0);
            return $"Switching to {car.Name}…";
        }
        catch (Exception e) { DebugLog.Error("cheats", e, "Vehicles.SwitchCar"); return "Failed: " + e.Message; }
    }

    /// <summary>Spawns a driverless copy of a car in front of you (host/solo).</summary>
    public static string SpawnDummy(ItemCatalog.Item car)
    {
        if (!GameState.IsAuthority) return "Blocked: only the host can spawn cars.";
        var cm = CheatsManager.Instance;
        var me = GameState.LocalCar;
        if (cm == null) return "Failed: the game's cheat manager isn't loaded in this session.";
        if (me == null) return "Failed: no car to spawn next to.";
        var t = me.cachedTransform != null ? me.cachedTransform : me.transform;
        var pos = t.position + t.forward * 9f + Vector3.up * 1.5f;
        var rot = new Vector3(0f, t.eulerAngles.y, 0f);
        AdvantageFlag.MarkUsed();
        // The game's prefabs are "Car_<Name>" with a capitalised name (e.g. Car_Lynx, Car_RustOx), while catalog names
        // can be lowercase: try the capitalised forms first.
        static string Cap(string s) => string.IsNullOrEmpty(s) ? s : char.ToUpperInvariant(s[0]) + s.Substring(1);
        var candidates = new[] { Cap(car.Name.Replace(" ", "")), Cap(car.RawName.Replace(" ", "")), car.RawName, car.Name.Replace(" ", "") };
        foreach (var name in System.Linq.Enumerable.Distinct(candidates))
        {
            try
            {
                var obj = cm.SpawnCar(name, pos, rot);
                if (obj == null) continue;
                SpawnedDummies.Add(name);
                return $"Spawned {car.Name}";
            }
            catch (Exception e) { DebugLog.Error("cheats", e, "Vehicles.SpawnDummy"); CheatsPlugin.Logger.LogDebug($"SpawnCar('{name}'): {e.Message}"); }
        }
        return $"Failed: game didn't accept '{car.RawName}' as a car name (see log).";
    }

    public static string DespawnDummies()
    {
        var cm = CheatsManager.Instance;
        if (cm == null || SpawnedDummies.Count == 0) return "Nothing to remove.";
        int n = 0;
        foreach (var name in SpawnedDummies)
        {
            try { cm.DespawnCar(name); n++; } catch (Exception ex) { DebugLog.Error("cheats", ex, "Vehicles.DespawnDummies"); }
        }
        SpawnedDummies.Clear();
        return $"Removed {n} spawned car(s).";
    }

    public static int DummyCount => SpawnedDummies.Count;

    // ---- winch / towing ----

    /// <summary>Hooks your winch onto the nearest other player's car.</summary>
    public static string TowNearestPlayer()
    {
        var me = GameState.LocalCar;
        var myWinch = me?.GetComponent<VehicleWinchSystem>();
        if (myWinch == null) return "Failed: your car has no winch.";

        VehicleBase best = null;
        PlayerRef bestRef = default;
        float bestDist = float.MaxValue;
        foreach (var o in Object.FindObjectsOfType(Il2CppType.Of<FusionPlayer>()))
        {
            var fp = o.TryCast<FusionPlayer>();
            var car = fp?.vehicleBase;
            if (car == null || GameState.IsLocalCar(car)) continue;
            float d = Vector3.Distance(car.transform.position, me.transform.position);
            if (d < bestDist) { bestDist = d; best = car; bestRef = fp.Object.InputAuthority; }
        }
        if (best == null) return "No other player's car found.";

        var theirWinch = best.GetComponent<VehicleWinchSystem>();
        var mine = ClosestPoint(myWinch.winchPoints, best.transform.position, me.transform);
        var theirs = theirWinch != null ? ClosestPoint(theirWinch.winchPoints, me.transform.position, best.transform) : Vector3.zero;
        try
        {
            AdvantageFlag.MarkUsed();
            myWinch.RPC_AttachWinchToPlayerCar(mine, bestRef, theirs);
            return $"Winched to the car {bestDist:0} m away.";
        }
        catch (Exception e) { DebugLog.Error("cheats", e, "Vehicles.TowNearestPlayer"); return "Failed: " + e.Message; }
    }

    private static Vector3 ClosestPoint(Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<Transform> points, Vector3 towards, Transform owner)
    {
        Transform best = null;
        float bestD = float.MaxValue;
        if (points != null)
            foreach (var p in points)
            {
                if (p == null) continue;
                float d = Vector3.Distance(p.position, towards);
                if (d < bestD) { bestD = d; best = p; }
            }
        return best != null ? owner.InverseTransformPoint(best.position) : Vector3.zero;
    }

    public static string DetachWinches()
    {
        var w = GameState.LocalCar?.GetComponent<VehicleWinchSystem>();
        if (w == null) return "Failed: no winch.";
        try { w.DetachAllWinches(); return "Winch detached."; }
        catch (Exception e) { DebugLog.Error("cheats", e, "Vehicles.DetachWinches"); return "Failed: " + e.Message; }
    }

    /// <summary>
    /// Applied every second: longer attach range for your winch. Only where this game simulates the winch (solo/host):
    /// winch errors showed up on a guest with it on, and the rope's networked length is left to the game.
    /// </summary>
    public static void TickWinch()
    {
        var w = GameState.LocalCar?.GetComponent<VehicleWinchSystem>();
        if (w == null) return;
        if (!OriginalWinchRange.TryGetValue(w.Pointer, out var orig)) OriginalWinchRange[w.Pointer] = orig = w.maxWinchRange;
        float range = CheatsConfig.LongWinch.Value && GameState.IsAuthority ? Mathf.Max(orig, 120f) : orig;
        if (Math.Abs(w.maxWinchRange - range) > 0.01f) w.maxWinchRange = range;
    }

    // ---- small tools ----

    public static string KickPush()
    {
        var k = GameState.LocalCar?.GetComponent<CarKicker>();
        if (k == null) return "Failed: no push on this car.";
        try { k.Kick(); return "Push!"; }
        catch (Exception e) { DebugLog.Error("cheats", e, "Vehicles.KickPush"); return "Failed: " + e.Message; }
    }

    public static string RefillTools(VehicleBase car = null)
    {
        var inv = (car ?? GameState.LocalCar)?.GetComponent<CarInventory>();
        if (inv == null) return "Failed: no tool inventory found.";
        try { AdvantageFlag.MarkUsed(); inv.RPC_RefillItems(); return "Tools refilled."; }
        catch (Exception e) { DebugLog.Error("cheats", e, "Vehicles.RefillTools"); return "Failed: " + e.Message; }
    }
}
