using System;

namespace OverTheHillPlus.Grayling;

/// <summary>What the world is like at one spot, for the shore search: is there water, and how deep is it (metres from the
/// surface down to the ground; 0 where there is no water).</summary>
public readonly struct WaterProbe
{
    public readonly bool Water;
    public readonly float Depth;
    public WaterProbe(bool water, float depth) { Water = water; Depth = water && float.IsFinite(depth) ? Math.Max(0f, depth) : 0f; }
    public static readonly WaterProbe Dry = new(false, 0f);
}

/// <summary>
/// Where the boat goes in, and where a car comes out, at a shore: rings of points round a spot (nearest first, 16 to a
/// ring), each checked by the caller's probe of the world (the game's water bodies and the ground under them). A launch
/// spot has water deep enough for the hull and the motor (<see cref="LaunchDepth"/>) under it and round it (a boat's
/// length of open water); a landing spot is dry ground. The boat faces away from where it was asked for (out onto the
/// water). Pure (unit-tested).
/// </summary>
public static class ShoreSearch
{
    /// <summary>Water this deep (metres) takes the hull and the motor's leg; and the clearance round the spot.</summary>
    public const float LaunchDepth = 0.75f, Clearance = 2.6f;
    /// <summary>The search's step and reach, metres: a boat can be taken out within this of the water.</summary>
    public const float Step = 3f, Reach = 40f;

    /// <summary>A found spot: where (world x, z), how far from the asked-for spot, and the heading (degrees, 0 = +z) from
    /// the asked-for spot toward it.</summary>
    public readonly struct Spot
    {
        public readonly float X, Z, Distance, Yaw;
        public Spot(float x, float z, float distance, float yaw) { X = x; Z = z; Distance = distance; Yaw = yaw; }
    }

    /// <summary>The nearest launch spot round (x, z) within <paramref name="reach"/>, or false.</summary>
    public static bool Launch(float x, float z, Func<float, float, WaterProbe> probe, out Spot spot, float reach = Reach)
    {
        spot = default;
        if (probe == null) return false;
        bool Good(float px, float pz)
        {
            var c = probe(px, pz);
            if (!c.Water || c.Depth < LaunchDepth) return false;
            for (int k = 0; k < 4; k++)
            {
                float a = k * MathF.PI / 2f;
                var p = probe(px + MathF.Cos(a) * Clearance, pz + MathF.Sin(a) * Clearance);
                if (!p.Water || p.Depth < LaunchDepth * 0.6f) return false;
            }
            return true;
        }
        return Rings(x, z, reach, Good, out spot);
    }

    /// <summary>The nearest dry spot round (x, z) within <paramref name="reach"/> (a car comes back ashore there), or false.</summary>
    public static bool Landing(float x, float z, Func<float, float, WaterProbe> probe, out Spot spot, float reach = Reach)
    {
        spot = default;
        if (probe == null) return false;
        return Rings(x, z, reach, (px, pz) => !probe(px, pz).Water, out spot);
    }

    /// <summary>Is there water to launch into within reach of (x, z)? (What the boat's "Drive" button asks.)</summary>
    public static bool NearWater(float x, float z, Func<float, float, WaterProbe> probe, float reach = Reach) =>
        Launch(x, z, probe, out _, reach);

    private static bool Rings(float x, float z, float reach, Func<float, float, bool> good, out Spot spot)
    {
        spot = default;
        if (!float.IsFinite(x) || !float.IsFinite(z)) return false;
        if (good(x, z)) { spot = new Spot(x, z, 0f, 0f); return true; }
        for (float r = Step; r <= reach + 1e-3f; r += Step)
        {
            const int n = 16;
            for (int i = 0; i < n; i++)
            {
                float a = 2f * MathF.PI * i / n;
                float px = x + MathF.Sin(a) * r, pz = z + MathF.Cos(a) * r;
                if (!good(px, pz)) continue;
                spot = new Spot(px, pz, r, a * 180f / MathF.PI);
                return true;
            }
        }
        return false;
    }
}
