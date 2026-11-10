using System;
using System.Collections.Generic;

namespace OverTheHillPlus.Grayling;

/// <summary>A small vector in the boat's own frame (Unity axes: x right, y up, z forward), metres.</summary>
public readonly struct HullVec
{
    public readonly float X, Y, Z;
    public HullVec(float x, float y, float z) { X = x; Y = y; Z = z; }
    public static HullVec operator +(HullVec a, HullVec b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    public static HullVec operator -(HullVec a, HullVec b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    public static HullVec operator *(HullVec a, float s) => new(a.X * s, a.Y * s, a.Z * s);
    public float Dot(HullVec b) => X * b.X + Y * b.Y + Z * b.Z;
    public HullVec Cross(HullVec b) => new(Y * b.Z - Z * b.Y, Z * b.X - X * b.Z, X * b.Y - Y * b.X);
    public float Length => MathF.Sqrt(X * X + Y * Y + Z * Z);
    public override string ToString() => $"({X:0.###}, {Y:0.###}, {Z:0.###})";
}

/// <summary>One buoyancy sample: a point on the hull's bottom (boat frame), the waterplane area it stands for and how deep
/// it can go before the water reaches the gunwale above it.</summary>
public readonly struct HullPoint
{
    public readonly HullVec At;
    public readonly float Area;
    public readonly float MaxDepth;
    public HullPoint(HullVec at, float area, float maxDepth) { At = at; Area = area; MaxDepth = maxDepth; }
}

/// <summary>
/// The Grayling's hull as the water sees it: a 14 ft riveted semi-V aluminium boat (4.30 m long, 1.72 m beam, a flat
/// keel line aft that rises into the bow, deadrise from about 12 degrees at the transom to 30 at the bow) sampled by
/// points on its bottom, plus its drag and its outboard's thrust. The game has no buoyancy, so this is the boat's water
/// physics, run every physics step where the game simulates the boat (the host, solo, and a guest's own boat, which it
/// predicts): each point under the water surface pushes up by the weight of the water it displaces (ρ g A d, held at its
/// depth to the gunwale) with a damper on its vertical speed; the hull drags little lengthwise and a lot sideways (its keel),
/// and the motor pushes at the propeller along the way it is turned. The same constants everywhere, so nothing is sent.
/// Pure (unit-tested). Positions are in the model's frame (car-root space before the layout offset).
/// </summary>
public static class BoatHull
{
    public const float WaterDensity = 1000f, G = 9.81f;

    /// <summary>The boat as afloat with its rider: hull 135 kg, a 15 hp outboard 45 kg, fuel 15 kg, the rider 85 kg.</summary>
    public const float Mass = 280f;

    // ---------------------------------------------------------------- the hull (model metres, Unity axes)

    /// <summary>The keel line's height over the model's ground plane (the hidden wheels' contact plane): the boat sits
    /// 2 cm over the ground on its wheels when beached.</summary>
    public const float KeelY = 0.02f;
    /// <summary>The transom's and the bow's z (the hull is 4.30 m long overall).</summary>
    public const float SternZ = -2.05f, BowZ = 2.25f;
    /// <summary>Half the beam at the chine (where the bottom meets the flared side) and at the gunwale.</summary>
    public const float ChineHalf = 0.62f, GunwaleHalf = 0.86f;

    /// <summary>The length of the forefoot: the keel line's last stretch, curving up into the raked stem.</summary>
    public const float Forefoot = 0.42f;

    /// <summary>The keel's rise toward the bow: flat to 0.6 m ahead of the middle, a gentle 0.30 m rise to the bow, and the
    /// forefoot curving up another 0.40 m into the raked stem (about 22 degrees off vertical at the stem head).</summary>
    public static float KeelRise(float z)
    {
        const float flatTo = 0.6f;
        float r = 0f;
        if (z > flatTo)
        {
            float t = Math.Min(1f, (z - flatTo) / (BowZ - flatTo));
            r = 0.30f * t * t;
        }
        if (z > BowZ - Forefoot)
        {
            float u = Math.Min(1f, (z - (BowZ - Forefoot)) / Forefoot);
            r += 0.40f * MathF.Pow(u, 2.2f);
        }
        return r;
    }

    /// <summary>Deadrise (degrees) along the hull: 12 at the transom to 30 at the bow.</summary>
    public static float Deadrise(float z)
    {
        float t = Math.Max(0f, Math.Min(1f, (z - SternZ) / (BowZ - SternZ)));
        return 12f + 18f * t * t;
    }

    /// <summary>The gunwale's height along the hull (the sheer: 0.47 m at the transom, 0.60 m amidships, 0.74 m at the bow).</summary>
    public static float GunwaleY(float z)
    {
        float t = Math.Max(0f, Math.Min(1f, (z - SternZ) / (BowZ - SternZ)));
        return KeelY + 0.47f + 0.08f * t + 0.19f * t * t;
    }

    /// <summary>The half width of the bottom (to the chine) along the hull: full aft of 0.2 m, closing into the forefoot
    /// (it meets the keel 6 cm short of the bow).</summary>
    public static float ChineHalfAt(float z)
    {
        if (z <= 0.2f) return ChineHalf;
        float s = Math.Max(0f, Math.Min(1f, (BowZ - 0.06f - z) / (BowZ - 0.06f - 0.2f)));
        return ChineHalf * (1f - MathF.Pow(1f - s, 1.6f));
    }

    /// <summary>The bottom's height at (x, z): the keel line plus the deadrise out to the chine.</summary>
    public static float BottomY(float x, float z) =>
        KeelY + KeelRise(z) + Math.Abs(x) * MathF.Tan(Deadrise(z) * MathF.PI / 180f);

    /// <summary>The buoyancy points: <paramref name="stations"/> along the hull by <paramref name="across"/> across the
    /// bottom, each standing for its share of the bottom's plan area.</summary>
    public static List<HullPoint> Points(int stations = 6, int across = 3)
    {
        var l = new List<HullPoint>();
        float z0 = SternZ + 0.15f, z1 = BowZ - 0.35f;
        float dz = (z1 - z0) / (stations - 1);
        for (int i = 0; i < stations; i++)
        {
            float z = z0 + dz * i;
            float half = ChineHalfAt(z);
            float width = 2f * half;
            for (int j = 0; j < across; j++)
            {
                float u = across == 1 ? 0f : -1f + 2f * j / (across - 1);
                float x = u * half * 0.8f;
                float y = BottomY(x, z);
                float area = width / across * dz;
                l.Add(new HullPoint(new HullVec(x, y, z), area, Math.Max(0.05f, GunwaleY(z) - y)));
            }
        }
        return l;
    }

    // ---------------------------------------------------------------- buoyancy

    /// <summary>Damping of each point's vertical speed, as a fraction of the critical damping of the whole boat's heave.</summary>
    public const float HeaveDamping = 0.35f;

    /// <summary>The upward push (newtons) of one point <paramref name="depth"/> metres under the water surface, moving up at
    /// <paramref name="upSpeed"/> m/s, on a boat whose points cover <paramref name="totalArea"/> m² and that weighs
    /// <paramref name="mass"/> kg: the displaced water's weight (to the gunwale at most) and a damper on its vertical
    /// speed, never pulling the boat down.</summary>
    public static float Lift(HullPoint p, float depth, float upSpeed, float totalArea, float mass)
    {
        if (!(depth > 0f) || !float.IsFinite(depth) || !float.IsFinite(upSpeed)) return 0f;
        float d = Math.Min(depth, p.MaxDepth);
        float k = WaterDensity * G * p.Area;
        float kTotal = WaterDensity * G * Math.Max(totalArea, 0.1f);
        float cTotal = 2f * HeaveDamping * MathF.Sqrt(kTotal * Math.Max(mass, 1f));
        float c = cTotal * p.Area / Math.Max(totalArea, 0.1f);
        // the damper only acts on the wet part (fades in over the first 5 cm)
        float wet = Math.Min(1f, depth / 0.05f);
        float f = k * d - c * upSpeed * wet;
        return f > 0f ? f : 0f;
    }

    /// <summary>
    /// The depth (metres, below the keel line's ground plane: how far the keel is under the water surface) at which the
    /// boat floats level with <paramref name="mass"/> kg aboard: the depth where the points' lift equals the weight
    /// (bisection). The boat is built to float with about 15 cm of keel under the water.
    /// </summary>
    public static float RestDraft(float mass, IReadOnlyList<HullPoint> points)
    {
        float total = 0f;
        foreach (var p in points) total += p.Area;
        float lo = 0f, hi = 1.0f;
        for (int it = 0; it < 60; it++)
        {
            float mid = (lo + hi) * 0.5f;
            float water = KeelY + mid;
            float f = 0f;
            foreach (var p in points) f += Lift(p, water - p.At.Y, 0f, total, mass);
            if (f > mass * G) hi = mid; else lo = mid;
        }
        return (lo + hi) * 0.5f;
    }

    // ---------------------------------------------------------------- drag

    /// <summary>Quadratic drag along the boat and across it (N per (m/s)²), and the linear part (N per m/s) that keeps a
    /// slow boat from drifting for ever.</summary>
    public const float DragAlong = 3.5f, DragAcross = 160f, LinearAlong = 12f, LinearAcross = 140f;
    /// <summary>Yaw damping (N·m per rad/s) and roll / pitch damping (N·m per rad/s) of the hull in the water.</summary>
    public const float YawDamping = 650f, RollDamping = 420f;

    /// <summary>The water's drag on the hull (boat frame, newtons) for its velocity through the water (boat frame,
    /// m/s), scaled by how much of the hull is wet (0 to 1).</summary>
    public static HullVec Drag(HullVec v, float wet)
    {
        if (!(wet > 0f)) return default;
        float w = Math.Min(1f, wet);
        float fz = -(DragAlong * Math.Abs(v.Z) + LinearAlong) * v.Z;
        float fx = -(DragAcross * Math.Abs(v.X) + LinearAcross) * v.X;
        return new HullVec(fx * w, 0f, fz * w);
    }

    // ---------------------------------------------------------------- the outboard

    /// <summary>The propeller's static thrust (N) at full throttle, the speed it can no longer push past (m/s) and the share
    /// of it left in reverse. A 15 hp tiller outboard on a 280 kg boat: about 1.5 m/s² off the line, a 6 m/s top speed (the
    /// game's speeds are a third of the real ones, like its cars' limiters).</summary>
    public const float Thrust = 430f, PropSpeed = 11.0f, Reverse = 0.45f;
    /// <summary>How far the motor swings either way on its swivel, degrees.</summary>
    public const float MaxSteer = 32f;

    /// <summary>The propeller's push (newtons, + forward along the motor) for the throttle (0..1), the reverse pull (0..1,
    /// the brake: astern), and the boat's speed along its length (m/s); 0 when the propeller is out of the water.</summary>
    public static float PropThrust(float throttle, float reverse, float speedAlong, bool propWet)
    {
        if (!propWet) return 0f;
        float t = Clamp01(throttle), r = Clamp01(reverse);
        float v = float.IsFinite(speedAlong) ? speedAlong : 0f;
        float ahead = t * Thrust * Math.Max(0f, 1f - Math.Max(0f, v) / PropSpeed);
        float astern = r * Thrust * Reverse * Math.Max(0f, 1f - Math.Max(0f, -v) / (PropSpeed * 0.5f));
        return ahead - astern;
    }

    /// <summary>The motor's swing (degrees, + = the propeller pushes the stern left so the bow turns right) for the steering
    /// input (-1..1). A tiller's own throw is the other way: this is the motor, not the handle.</summary>
    public static float MotorAngle(float steer) => Math.Max(-1f, Math.Min(1f, float.IsFinite(steer) ? steer : 0f)) * MaxSteer;

    /// <summary>The thrust as a force in the boat's frame (newtons) for a push along the motor turned by
    /// <paramref name="motorDeg"/> degrees.</summary>
    public static HullVec ThrustVector(float push, float motorDeg)
    {
        float a = motorDeg * MathF.PI / 180f;
        return new HullVec(-MathF.Sin(a) * push, 0f, MathF.Cos(a) * push);
    }

    /// <summary>How much of the hull is wet (0 to 1): the share of the points under the surface.</summary>
    public static float WetShare(int under, int count) => count > 0 ? Clamp01((float)under / count) : 0f;

    /// <summary>The boat floats (rather than sitting on the bottom or the shore): at least this share of its weight is
    /// carried by the water.</summary>
    public const float FloatingShare = 0.6f;

    internal static float Clamp01(float v) => float.IsFinite(v) ? Math.Max(0f, Math.Min(1f, v)) : 0f;
}
