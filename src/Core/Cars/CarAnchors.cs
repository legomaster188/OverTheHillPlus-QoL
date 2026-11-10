using System;
using UnityEngine;

namespace OverTheHillPlus.Cars;

/// <summary>
/// Named points of a modded car's model (its mesh blob's anchors, e.g. the Longhorn's "fifth_wheel") in the car's root
/// space, where they are on the live car: the model is drawn at the layout's offset from the root
/// (<see cref="CarLayout.Offset"/>). Stock cars have no anchors. Main thread only.
/// </summary>
internal static class CarAnchors
{
    /// <summary>The modded car of this car object, or null (a stock car, or none).</summary>
    public static RuntimeCar Of(GameObject car)
    {
        if (car == null) return null;
        try
        {
            var vcc = car.GetComponent<VehicleCustomizationController>();
            if (vcc == null) vcc = car.GetComponentInChildren<VehicleCustomizationController>();
            return vcc != null ? ModdedCars.ByCar(vcc) : null;
        }
        catch (Exception e) { DebugLog.Error("core", e, "CarAnchors.Of"); return null; }
    }

    /// <summary>True when the car is a modded car whose model has <paramref name="anchor"/>: <paramref name="local"/> is
    /// that point in the car's root space.</summary>
    public static bool Find(GameObject car, string anchor, out Vector3 local)
    {
        local = default;
        var rc = Of(car);
        var a = rc?.Assets?.Blob?.Anchor(anchor);
        if (a == null || rc.Layout == null) return false;
        local = CarAssets.V(a.Position) + rc.Layout.Offset;
        return true;
    }

    /// <summary>Where a modded car's model space sits in its root space (its layout offset); false for a stock car,
    /// whose model space is its root space.</summary>
    public static bool ModelOffset(GameObject car, out Vector3 offset)
    {
        offset = Vector3.zero;
        var rc = Of(car);
        if (rc?.Layout == null) return false;
        offset = rc.Layout.Offset;
        return true;
    }
}
