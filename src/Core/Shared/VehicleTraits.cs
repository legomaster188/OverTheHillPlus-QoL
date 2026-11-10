using System;
using System.Collections.Generic;
using OverTheHillPlus.Cars;
using UnityEngine;
#if MELON
using Il2CppVehiclePhysics;
#else
using VehiclePhysics;
#endif

namespace OverTheHillPlus.Core.Shared;

/// <summary>
/// What a live car is, for the mods that treat vehicles differently (mods never reference each other, so they ask core):
/// a stock car or a modded car on tyres is a <see cref="VehicleKind.Car"/>; a modded snowmobile or boat says so in its
/// definition (<see cref="CarDefinition.Kind"/>). Tyre Pressure leaves vehicles without tyres alone, and the cockpit
/// view seats you at a modded vehicle's own <c>eye</c> anchor (a rider on a snowmobile, in a boat or a side-by-side).
/// Answers are kept per car object for a few seconds (asked every physics step). Main thread only.
/// </summary>
internal static class VehicleTraits
{
    /// <summary>The model anchor of the driver's or rider's eyes (car-root space after the layout offset), optional.</summary>
    public const string EyeAnchor = "eye";

    private sealed class Entry
    {
        public RuntimeCar Car;
        public VehicleKind Kind;
        public float At;
    }

    private static readonly Dictionary<IntPtr, Entry> Cache = new();
    private static float _nextPrune;

    /// <summary>The vehicle's kind (a stock car, or anything not known: <see cref="VehicleKind.Car"/>).</summary>
    public static VehicleKind Kind(VehicleBase v) => Get(v)?.Kind ?? VehicleKind.Car;

    /// <summary>The vehicle stands on tyres you can see (pressures, treads, sizes and chains apply).</summary>
    public static bool HasTyres(VehicleBase v) => VehicleKinds.HasTyres(Kind(v));

    /// <summary>The modded vehicle's definition id ("ermine"), or null for a stock car.</summary>
    public static string ModdedId(VehicleBase v) => Get(v)?.Car?.Id;

    /// <summary>Where the driver's (or rider's) eyes are on a modded vehicle that names them (its model's
    /// <see cref="EyeAnchor"/>), in the car's own space; false for a stock car or a model without one.</summary>
    public static bool Eye(VehicleBase v, out Vector3 local)
    {
        local = default;
        var car = Get(v)?.Car;
        var a = car?.Assets?.Blob?.Anchor(EyeAnchor);
        if (a == null || car.Layout == null) return false;
        local = new Vector3(a.Position.X, a.Position.Y, a.Position.Z) + car.Layout.Offset;
        return true;
    }

    private static Entry Get(VehicleBase v)
    {
        if (v is null) return null;
        IntPtr p;
        try { p = v.Pointer; } catch (Exception ex) { DebugLog.Error("core", ex, "VehicleTraits.Get"); return null; }
        if (p == IntPtr.Zero) return null;
        float now = Time.unscaledTime;
        if (Cache.TryGetValue(p, out var e) && now - e.At < 3f && now >= e.At) return e;
        e = new Entry { At = now, Kind = VehicleKind.Car };
        try
        {
            if (v != null && ModdedCars.All.Count > 0)
            {
                var vcc = v.GetComponent<VehicleCustomizationController>();
                string name = vcc != null && vcc.currentCarInfo != null ? vcc.currentCarInfo.name : null;
                var car = ModdedCars.ByCarName(name);
                if (car != null)
                {
                    e.Car = car;
                    e.Kind = car.Def.Kind;
                }
            }
        }
        catch (Exception ex) { DebugLog.Error("core", ex, "VehicleTraits.Get"); }
        if (now >= _nextPrune || now < _nextPrune - 30f)
        {
            _nextPrune = now + 10f;
            var old = new List<IntPtr>();
            foreach (var kv in Cache) if (now - kv.Value.At > 10f || now < kv.Value.At) old.Add(kv.Key);
            foreach (var k in old) Cache.Remove(k);
        }
        Cache[p] = e;
        return e;
    }
}
