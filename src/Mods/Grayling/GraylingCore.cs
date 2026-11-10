using System;
using System.Collections.Generic;
using OverTheHillPlus.Cars;
using OverTheHillPlus.Core.Shared;
using UnityEngine;
#if MELON
using Il2CppVehiclePhysics;
#else
using VehiclePhysics;
#endif

namespace OverTheHillPlus.Grayling;

// After the namespace line on purpose: the game has its own global ModManager (mod.io).
using OverTheHillPlus.Core.Manager;

/// <summary>
/// The Grayling at runtime: every frame each boat in the world gets its outboard moved (<see cref="BoatLive"/>, every
/// peer), and the host (or solo) runs the shore rules (<see cref="BoatLaunch"/>); the water physics runs in the physics
/// step (<see cref="BoatPhysics"/>).
/// </summary>
internal static class GraylingCore
{
    private static readonly Dictionary<IntPtr, BoatLive> Live = new();
    private static readonly HashSet<IntPtr> Seen = new();
    private static readonly List<IntPtr> Gone = new();

    /// <summary>The layout offset (model to car-root space) of the networked boat, once known.</summary>
    public static Vector3 Offset { get; private set; }
    private static bool _ready;
    private static float _draft = float.NaN;

    public static void Tick()
    {
        var car = ModdedCars.ById(GraylingDefinition.Id);
        if (car == null || !car.Ready) return;
        if (!_ready && car.Layout != null)
        {
            Offset = car.Layout.Offset;
            BoatPhysics.Prepare(Offset);
            _ready = true;
        }
        if (!_ready) return;
        // a boat trailer (Trailers+) carries this boat in sessions that run it: it floats off at a ramp, and is moored at
        // the boat's draft
        bool session = GameState.Runner != null && ModManager.ServerModActive(GraylingDefinition.Id);
        WorldSites.TrailerBoatCar = session ? GraylingDefinition.ItemId : 0u;
        if (float.IsNaN(_draft)) _draft = BoatHull.RestDraft(BoatHull.Mass, BoatHull.Points()) - 0.02f;
        WorldSites.TrailerBoatDraft = _draft;
        float dt = Time.deltaTime;
        Seen.Clear();
        foreach (var fp in GameState.FusionPlayers)
        {
            VehicleBase v;
            try
            {
                if (fp == null || fp.Object == null) continue;
                v = fp.vehicleBase;
                if (v == null || !v.isActiveAndEnabled) continue;
                if (VehicleTraits.ModdedId(v) != GraylingDefinition.Id) continue;
            }
            catch (Exception e) { DebugLog.Error(GraylingPlugin.ModId, e, "GraylingCore.Tick player"); continue; }
            var ptr = v.Pointer;
            if (!Live.TryGetValue(ptr, out var live)) Live[ptr] = live = new BoatLive(v, Offset);
            Seen.Add(ptr);
            try { live.Frame(dt, BoatPhysics.Of(ptr)); }
            catch (Exception e) { DebugLog.Error(GraylingPlugin.ModId, e, "BoatLive.Frame"); GraylingPlugin.LogOnce("frame", "a boat's outboard failed: " + e.Message); }
        }
        Gone.Clear();
        foreach (var kv in Live) if (!Seen.Contains(kv.Key)) Gone.Add(kv.Key);
        foreach (var k in Gone)
        {
            Live.Remove(k);
            BoatPhysics.Forget(k);
        }
        BoatLaunch.Tick();
    }

    public static void Clear()
    {
        Live.Clear();
        BoatPhysics.Clear();
        BoatLaunch.Clear();
    }

    private static string _blocker;
    private static float _blockerAt = -10f;

    /// <summary>The Drive button's extra rule: a boat goes in at the water's edge (open water within 40 m of your car).
    /// Worked out at most once a second (it probes the water round you).</summary>
    public static string DriveBlocker()
    {
        float now = Time.unscaledTime;
        if (now - _blockerAt < 1f && now >= _blockerAt) return _blocker;
        _blockerAt = now;
        _blocker = null;
        try
        {
            var car = GameState.LocalCar;
            if (car == null) return _blocker;
            var p = car.transform.position;
            if (!ShoreSearch.NearWater(p.x, p.z, (x, z) => BoatLaunch.Probe(x, z, p.y)))
                _blocker = "Get to a lake or river shore first";
        }
        catch (Exception e) { DebugLog.Error(GraylingPlugin.ModId, e, "GraylingCore.DriveBlocker"); }
        return _blocker;
    }
}
