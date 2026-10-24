using System;

namespace OverTheHillPlus.ControllerFeel;

/// <summary>What a wheel is rolling on, as far as the texture of the rumble goes (the game's material labels are mapped to these in the sampler).</summary>
internal enum Surface
{
    Paved, Bricks, Gravel, Dirt, Grass, Rock, LooseRock, Sand, Snow, Mud, Wood, Ice, Water,
}

/// <summary>
/// How a surface feels through the pad. <see cref="Low"/> is the heavy motor (chunks, thumps), <see cref="High"/> the light
/// one (grit, buzz), both at full speed on that surface. The level wanders about <see cref="Hz"/> times a second by
/// <see cref="Depth"/> (0 = steady, 1 = from nothing to double): gravel is a fast fine buzz, rock slow and chunky, sand a soft
/// low hum.
/// </summary>
internal readonly struct SurfaceProfile
{
    public readonly float Low, High, Hz, Depth;

    public SurfaceProfile(float low, float high, float hz, float depth)
    {
        Low = low;
        High = high;
        Hz = hz;
        Depth = depth;
    }
}

internal static class SurfaceProfiles
{
    public static readonly int Count = Enum.GetValues(typeof(Surface)).Length;

    private static readonly SurfaceProfile[] Table =
    {
        /* Paved     */ new(0.00f, 0.025f, 30f, 0.30f),
        /* Bricks    */ new(0.10f, 0.22f, 18f, 0.60f),
        /* Gravel    */ new(0.10f, 0.42f, 26f, 0.80f),
        /* Dirt      */ new(0.12f, 0.20f, 14f, 0.60f),
        /* Grass     */ new(0.05f, 0.10f, 10f, 0.50f),
        /* Rock      */ new(0.42f, 0.22f, 9f, 0.90f),
        /* LooseRock */ new(0.35f, 0.38f, 14f, 0.90f),
        /* Sand      */ new(0.16f, 0.06f, 6f, 0.40f),
        /* Snow      */ new(0.07f, 0.10f, 5f, 0.40f),
        /* Mud       */ new(0.20f, 0.04f, 4f, 0.60f),
        /* Wood      */ new(0.22f, 0.28f, 12f, 0.90f),
        /* Ice       */ new(0.00f, 0.04f, 20f, 0.30f),
        /* Water     */ new(0.14f, 0.05f, 5f, 0.50f),
    };

    public static SurfaceProfile Get(Surface s)
    {
        int i = (int)s;
        return i >= 0 && i < Table.Length ? Table[i] : Table[0];
    }

    /// <summary>
    /// How much of the texture comes through at this speed (m/s): nothing at a crawl, most of it by a brisk walk's worth of
    /// wheel speed, all of it at a fast pace.
    /// </summary>
    public static float SpeedGain(float speedMs) =>
        FeelMath.Smooth(speedMs, 0.8f, 5f) * (0.55f + 0.45f * FeelMath.Smooth(speedMs, 5f, 24f));
}
