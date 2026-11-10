using System;
using System.Collections.Generic;

namespace OverTheHillPlus.CustomCosmetics;

/// <summary>Where a part sits on one car: the part's origin in the car's root frame (metres, degrees, uniform scale).</summary>
public sealed class PartFit
{
    public float[] Pos = new float[3];
    public float[] Rot = new float[3];
    public float Scale = 1f;
    /// <summary>Per-axis factors on top of <see cref="Scale"/> (the part's own x, y, z): a rim as wide as this car's tyre.</summary>
    public float[] Scale3 = { 1f, 1f, 1f };

    /// <summary>The scale on one of the part's axes.</summary>
    public float AxisScale(int axis) => Scale * Scale3[axis];
}

/// <summary>Finds a car's fit among a part's <c>fits</c> (keys are car names or mod ids, any case). Pure (unit-tested).</summary>
public static class FitLookup
{
    /// <summary>The fit for the first of <paramref name="names"/> (the car's CarInfo name, its mod id, ...) that has one;
    /// null when the part doesn't fit the car.</summary>
    public static PartFit Find(IReadOnlyDictionary<string, PartFit> fits, params string[] names)
    {
        if (fits == null || names == null) return null;
        foreach (var n in names)
        {
            if (string.IsNullOrEmpty(n)) continue;
            foreach (var kv in fits)
                if (string.Equals(kv.Key, n, StringComparison.OrdinalIgnoreCase)) return kv.Value;
        }
        return null;
    }
}
