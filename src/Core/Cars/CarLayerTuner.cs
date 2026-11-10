using System;
using System.Collections.Generic;
using UnityEngine;
#if MELON
using Il2CppVehiclePhysics;
#else
using VehiclePhysics;
#endif

namespace OverTheHillPlus.Cars;

/// <summary>
/// Puts upgrade layers (<see cref="CarTuneLayers"/>) on the stock cars nobody else tunes: a stock car with a fresh layer
/// gets a <see cref="TunedCar"/> that writes only the layer on top of the game's own values (engine, revs, top speed,
/// gearing, weight, brakes, lift, winch). Modded cars are left to their own tuning (<see cref="CarTuning"/>, which
/// multiplies the layers in), and so are cars Realistic Handling tunes (<see cref="CarTuneLayers.TunedElsewhere"/>) or a
/// mod tunes by hand (<see cref="CarTuneClaims"/>). A car whose layer lapses gets the game's values back. Ticks once a
/// second, from the first layer on (nothing runs before any mod sets one). Main thread only.
/// </summary>
internal static class CarLayerTuner
{
    private static readonly Dictionary<IntPtr, TunedCar> Tuned = new();
    private static readonly CarTuningProfile Identity = new();
    private static readonly HashSet<string> Logged = new();
    private static bool _ticking;
    private static float _next;

    /// <summary>Starts the tick (called when a layer is set).</summary>
    internal static void EnsureTicking()
    {
        if (_ticking) return;
        _ticking = true;
        Features.Registry.OnTick(Tick, "core");
    }

    /// <summary>How many stock cars carry a layer now (for the log and the MODS page).</summary>
    internal static int Count => Tuned.Count;

    private static void Tick()
    {
        float now = Time.unscaledTime;
        if (now < _next && now >= _next - 5f) return;
        _next = now + 1f;
        try { Refresh(); }
        catch (Exception e)
        {
            DebugLog.Error("core", e, "CarLayerTuner.Tick");
            if (Logged.Add("tick")) Core.CorePlugin.Logger?.LogWarning("[Cars] upgrade layers: " + e.Message + " (logged once)");
        }
    }

    private static void Refresh()
    {
        var layered = CarTuneLayers.Cars();
        if (layered.Count == 0 && Tuned.Count == 0) return;
        var want = new HashSet<long>(layered);
        var seen = new HashSet<IntPtr>();
        foreach (var fp in GameState.FusionPlayers)
        {
            if (fp == null) continue;
            VPVehicleController car;
            try { car = fp.GetComponent<VPVehicleController>(); }
            catch (Exception e) { DebugLog.Error("core", e, "CarLayerTuner.Refresh"); continue; }
            if (car == null) continue;
            var ptr = car.Pointer;
            bool has = want.Contains(ptr.ToInt64());
            if (!has && !Tuned.ContainsKey(ptr)) continue;
            if (!has || !car.isActiveAndEnabled || CarTuning.IsModdedTuned(ptr) || CarTuneLayers.IsTunedElsewhere(ptr) || CarTuneClaims.IsHeld(ptr))
            {
                // No layer any more, or another tuner has the car: the game's values go back (only those still ours).
                if (Tuned.TryGetValue(ptr, out var old)) { old.Restore(); Tuned.Remove(ptr); }
                continue;
            }
            seen.Add(ptr);
            if (!Tuned.TryGetValue(ptr, out var rec))
            {
                Tuned[ptr] = rec = new TunedCar(car, TunedCar.Parts.Layer)
                {
                    OnShared = what => { if (Logged.Add("shared " + what)) Core.CorePlugin.Logger?.LogInfo($"[Cars] upgrade layers: a car's {what} is shared with its class: left alone."); },
                };
            }
            rec.Apply(Identity, 1f, 1f, null, CarTuneLayers.For(ptr));
        }
        // Cars that left the scene: nothing to restore (the object is gone or no longer a player's car).
        List<IntPtr> gone = null;
        foreach (var k in Tuned.Keys)
            if (!seen.Contains(k)) (gone ??= new List<IntPtr>()).Add(k);
        if (gone != null) foreach (var k in gone) Tuned.Remove(k);
    }
}
