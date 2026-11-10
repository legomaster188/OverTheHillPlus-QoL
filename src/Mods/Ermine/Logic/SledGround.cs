using System;

namespace OverTheHillPlus.Ermine;

/// <summary>What the sled is on, as far as its skis and track care.</summary>
public enum SledSurface : byte
{
    /// <summary>Tarmac, concrete, bricks, wood: steel runners and rubber lugs scrape and slide.</summary>
    Hard,
    /// <summary>Packed dirt, gravel, rock, loose rock: worse still for the carbides, and it grinds the sled to a halt.</summary>
    Rough,
    /// <summary>Grass: the lugs bite a little, the skis slide.</summary>
    Grass,
    /// <summary>Mud and sand: soft but sticky; the track paddles, the skis push.</summary>
    Mud,
    /// <summary>Snow of any depth: what it is built for.</summary>
    Snow,
    /// <summary>Ice: the studded track and the carbide runners hold well.</summary>
    Ice,
    /// <summary>Water (a shallow ford): a sled can skim it at speed, but it bogs when slow.</summary>
    Water,
}

/// <summary>One wheel's numbers on its surface: the grip factor its friction multiplier gets, the drag (a fraction of g
/// on the share of the sled that wheel carries) and how much it floats (Terrain+'s sinkage and bulldozing go down by it).</summary>
public readonly struct SledFeel
{
    public readonly float Grip, Drag, Flotation;
    public SledFeel(float grip, float drag, float flotation) { Grip = grip; Drag = drag; Flotation = flotation; }
}

/// <summary>
/// The Ermine's grip on each ground (the game's MaterialLabel flags, decoded the way Terrain+ does): a snowmobile has lots
/// of grip and floats on snow and ice, and is very poor on dirt, rock and tarmac. The numbers are multipliers on the
/// game's own wheel friction for that ground (the game already makes snow and ice slippery for tyres: Snow 0.72, Ice 0.30,
/// Dirt 0.88), so a sled ends up gripping like a good tyre on snow, holding on ice and sliding on dirt. Pure (unit-tested).
/// </summary>
public static class SledGround
{
    // The game's MaterialLabel values (one flag each), as Terrain+ decodes them.
    public const int Grass = 2, Gravel = 4, Mud = 8, DeepMud = 16, Dirt = 32, Water = 64, Rock = 128, LooseRock = 256,
        WoodPlank = 512, WoodTrunk = 1024, Snow = 2048, DeepSnow = 4096, Ice = 8192, Sand = 16384, DeepSand = 32768,
        QuickSand = 65536, Bricks = 131072, ShallowSnow = 262144;

    /// <summary>The surface for a label; a combined label takes the most sled-friendly flag it has.</summary>
    public static SledSurface Of(int label)
    {
        if (label <= 0) return SledSurface.Hard;
        if ((label & (Snow | DeepSnow | ShallowSnow)) != 0) return SledSurface.Snow;
        if ((label & Ice) != 0) return SledSurface.Ice;
        if ((label & Water) != 0) return SledSurface.Water;
        if ((label & (Mud | DeepMud | Sand | DeepSand | QuickSand)) != 0) return SledSurface.Mud;
        if ((label & Grass) != 0) return SledSurface.Grass;
        if ((label & (Gravel | Dirt | Rock | LooseRock)) != 0) return SledSurface.Rough;
        return SledSurface.Hard;
    }

    /// <summary>Snow: the game gives tyres 0.72 (deep snow 0.75); the sled's track bites into it (x1.45: about 1.05 in
    /// all) and it rides on top (Terrain+'s sinkage cut by 85 %).</summary>
    public const float SnowGrip = 1.45f, SnowFloat = 0.85f;
    /// <summary>Ice: the game gives tyres 0.30; studs and carbides hold (x2.6: about 0.78).</summary>
    public const float IceGrip = 2.6f;

    /// <summary>The wheel's grip, drag and flotation on <paramref name="s"/> at <paramref name="speed"/> m/s.</summary>
    public static SledFeel Feel(SledSurface s, float speed)
    {
        float v = float.IsFinite(speed) ? Math.Abs(speed) : 0f;
        switch (s)
        {
            case SledSurface.Snow: return new SledFeel(SnowGrip, 0f, SnowFloat);
            case SledSurface.Ice: return new SledFeel(IceGrip, 0f, 0.5f);
            case SledSurface.Grass: return new SledFeel(0.55f, 0.10f, 0.3f);
            case SledSurface.Mud: return new SledFeel(0.50f, 0.14f, 0.5f);
            // a ford: skimmed at speed (little drag over 8 m/s), a bog when slow
            case SledSurface.Water: return new SledFeel(0.60f, 0.30f * (1f - Smooth(v, 3f, 8f)) + 0.04f, 0.6f);
            case SledSurface.Rough: return new SledFeel(0.32f, 0.22f, 0f);
            default: return new SledFeel(0.28f, 0.18f, 0f);
        }
    }

    /// <summary>
    /// The friction multiplier the sled writes on a wheel: its own grip for the surface times the ground's factor that
    /// Terrain+ published for that wheel (packed ruts, polished snow, glazed ice), of which a sled feels only part
    /// (<see cref="TerrainShare"/>: the factor is worked out for a tyre that sinks in). Capped like every grip writer.
    /// </summary>
    public static float Write(float baseGrip, SledFeel feel, float terrainFactor)
    {
        float b = float.IsFinite(baseGrip) && baseGrip > 0f ? baseGrip : 1f;
        float t = float.IsFinite(terrainFactor) ? Math.Max(0.3f, Math.Min(1.5f, terrainFactor)) : 1f;
        float g = b * feel.Grip * (1f + (t - 1f) * TerrainShare);
        return Math.Max(MinWrite, Math.Min(MaxWrite, g));
    }

    /// <summary>How much of Terrain+'s factor a sled feels.</summary>
    public const float TerrainShare = 0.35f;
    public const float MinWrite = 0.15f, MaxWrite = 2.8f;

    /// <summary>
    /// The deceleration (m/s²) the scraping skis and dragging track add to the sled moving at <paramref name="speed"/>:
    /// the drag fraction of g, faded in from walking pace (a sled at rest isn't pushed back), never more than stops it
    /// in this step.
    /// </summary>
    public static float DragDecel(float dragG, float speed, float dt)
    {
        float v = float.IsFinite(speed) ? Math.Abs(speed) : 0f;
        if (!(dragG > 0f) || !(dt > 0f) || v < 0.05f) return 0f;
        float d = 9.81f * dragG * Smooth(v, 0.2f, 1.5f);
        return Math.Min(d, v / dt);
    }

    internal static float Smooth(float x, float a, float b)
    {
        if (b <= a) return x >= b ? 1f : 0f;
        float t = Math.Max(0f, Math.Min(1f, (x - a) / (b - a)));
        return t * t * (3f - 2f * t);
    }
}
