using System;

namespace OverTheHillPlus.Meerkat;

/// <summary>
/// The tyre meshes the Meerkat switches between: the model's three treads ("tyre" is the all-terrain, "tyre_alt_street"
/// and "tyre_alt_mud"), each also a size bigger for the 4 in lift and on the stock car's 14 in wheels. A bigger tyre is the
/// same tyre with a taller sidewall: every point outside the bead is moved out along its radius so the tread's outer
/// radius goes from the model's to the bigger one, the bead (on the rim's flange) stays where it is, so the tyre stays a
/// closed solid on its rim. The 14 in wheel is the model's rim scaled to size, with the tyre's bead going with it, narrower
/// and on the real car's track. Pure (unit-tested); positions are the part's own (hub frame: the axle along x, the outer
/// face toward -x).
/// </summary>
public static class MeerkatTyres
{
    public const string AllTerrainPart = "tyre", StreetPart = "tyre_alt_street", MudPart = "tyre_alt_mud";

    /// <summary>The radius the tyre's bead sits at (the rim's flange): nothing inside it moves.</summary>
    public const float BeadRadius = 0.197f;

    /// <summary>The 14 in rim's radius against the model's 15 in, the 14 in wheel's width against the model's (185 against
    /// 205 mm), and how far it stands in towards the car (the model's 15 mm less offset each side, gone).</summary>
    public const float RimScale = 14f / 15f, WidthScale = 0.9f, StockInset = 0.015f;

    public static string PartOf(TyreKind t) => t == TyreKind.Street ? StreetPart : t == TyreKind.Mud ? MudPart : AllTerrainPart;

    /// <summary>Hub-frame positions (x, y, z per vertex) with every radius r (in y/z) past the bead taken to
    /// bead + (r - bead) * k, k chosen so <paramref name="fromRadius"/> becomes <paramref name="toRadius"/>.</summary>
    public static float[] Stretch(float[] positions, float fromRadius, float toRadius, float bead = BeadRadius)
    {
        if (positions == null) return null;
        var o = (float[])positions.Clone();
        if (!(fromRadius > bead) || !(toRadius > bead)) return o;
        float k = (toRadius - bead) / (fromRadius - bead);
        for (int i = 0; i + 2 < o.Length; i += 3)
        {
            float y = o[i + 1], z = o[i + 2];
            float r = (float)Math.Sqrt(y * y + z * z);
            if (r <= bead || r < 1e-6f) continue;
            float r2 = bead + (r - bead) * k;
            float s = r2 / r;
            o[i + 1] = y * s;
            o[i + 2] = z * s;
        }
        return o;
    }

    /// <summary>The model's rim as the 14 in one (hub-frame positions).</summary>
    public static float[] RimToStock(float[] positions)
    {
        if (positions == null) return null;
        var o = (float[])positions.Clone();
        for (int i = 0; i + 2 < o.Length; i += 3)
        {
            o[i] = o[i] * WidthScale + StockInset;
            o[i + 1] *= RimScale;
            o[i + 2] *= RimScale;
        }
        return o;
    }

    /// <summary>A tyre of the model's size on the 14 in rim: the bead and all inside it go with the rim, the sidewall and
    /// tread are taken from there out to <paramref name="toRadius"/>.</summary>
    public static float[] TyreToStock(float[] positions, float fromRadius, float toRadius, float bead = BeadRadius)
    {
        if (positions == null) return null;
        var o = (float[])positions.Clone();
        float b2 = bead * RimScale;
        if (!(fromRadius > bead) || !(toRadius > b2)) return o;
        float k = (toRadius - b2) / (fromRadius - bead);
        for (int i = 0; i + 2 < o.Length; i += 3)
        {
            float y = o[i + 1], z = o[i + 2];
            float r = (float)Math.Sqrt(y * y + z * z);
            o[i] = o[i] * WidthScale + StockInset;
            if (r < 1e-6f) continue;
            float s = (r <= bead ? r * RimScale : b2 + (r - bead) * k) / r;
            o[i + 1] = y * s;
            o[i + 2] = z * s;
        }
        return o;
    }

    /// <summary>The radius of hub-frame positions (the largest y / z distance from the axle).</summary>
    public static float RadiusOf(float[] positions)
    {
        float best = 0f;
        if (positions == null) return best;
        for (int i = 0; i + 2 < positions.Length; i += 3)
        {
            float r = (float)Math.Sqrt(positions[i + 1] * positions[i + 1] + positions[i + 2] * positions[i + 2]);
            if (r > best) best = r;
        }
        return best;
    }
}
