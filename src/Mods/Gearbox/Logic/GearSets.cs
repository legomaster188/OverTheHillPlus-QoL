using System;
using System.Collections.Generic;

namespace OverTheHillPlus.GearboxPlus;

/// <summary>How Gearbox+ makes a car's gears: by continuing the game's own step (the car keeps its character), or from a real
/// gearbox's ratios.</summary>
public enum GearMode { Extend = 0, Realistic = 1 }

/// <summary>
/// One kind of gearbox: the ratios of a real one (the shape of its spacing, first to last) and how they sit on the game's
/// numbers. The game has two gears per car, the second of which reaches the speed limiter at about three quarters of the revs,
/// so the real set is laid on that: the gear that is 1.00 in <see cref="Real"/> (the reference, usually the direct gear) is
/// the car's own 2nd, and the others follow from the real ratios (the top ones are overdrives, the lower ones are the gears
/// the game does not have). The first gear is the real one unless that would be more than <see cref="MaxFirst"/> times the
/// game's own 1st (a lower first gear than that spins the wheels in the game's world), and the gears in between keep the real
/// set's spacing on a log scale between the first and the last. Pure code (unit-tested).
/// </summary>
public sealed class GearSet
{
    public readonly string Id;
    public readonly string Name;
    /// <summary>A real box's forward ratios, 1st first, each lower than the one before. Only their proportions count.</summary>
    public readonly float[] Real;
    /// <summary>Index into <see cref="Real"/> of the gear that is the car's own 2nd.</summary>
    public readonly int Reference;
    /// <summary>The most the 1st gear may be of the game's own 1st (1.35 = 35 % lower).</summary>
    public readonly float MaxFirst;

    public GearSet(string id, string name, float[] real, int reference, float maxFirst)
    {
        Id = id; Name = name; Real = real; Reference = reference; MaxFirst = maxFirst;
    }

    public int Count => Real.Length;

    public override string ToString() => Name;
}

public static class GearSets
{
    private static readonly System.Globalization.CultureInfo Inv = System.Globalization.CultureInfo.InvariantCulture;

    /// <summary>A modern road car's close five-speed with an overdrive fifth.</summary>
    public static readonly GearSet Close5 = new("close5", "Close 5-speed", new[] { 3.58f, 2.04f, 1.36f, 1.00f, 0.81f }, 3, 1.35f);
    /// <summary>A pickup or van's five-speed: a wider spread and a lower first.</summary>
    public static readonly GearSet Truck5 = new("truck5", "Truck 5-speed", new[] { 4.45f, 2.45f, 1.50f, 1.00f, 0.82f }, 3, 1.5f);
    /// <summary>A heavy truck's four-speed (a gear is missing at the top: direct drive is the last).</summary>
    public static readonly GearSet Truck4 = new("truck4", "Truck 4-speed", new[] { 4.40f, 2.40f, 1.40f, 1.00f }, 3, 1.55f);
    /// <summary>A vintage truck's three-speed.</summary>
    public static readonly GearSet Vintage3 = new("vintage3", "Vintage 3-speed", new[] { 2.82f, 1.60f, 1.00f }, 2, 1.25f);
    /// <summary>A kei truck or van's five-speed with a very low first gear for loads and hills.</summary>
    public static readonly GearSet Kei5 = new("kei5", "Kei 5-speed, low first", new[] { 3.90f, 2.25f, 1.45f, 1.00f, 0.80f }, 3, 1.9f);
    /// <summary>A heavy tractor's box: five of its many gears, from a crawler first to direct drive.</summary>
    public static readonly GearSet Heavy5 = new("heavy5", "Heavy 5-speed", new[] { 8.00f, 4.40f, 2.50f, 1.45f, 1.00f }, 4, 1.8f);

    public static readonly GearSet[] All = { Close5, Truck5, Truck4, Vintage3, Kei5, Heavy5 };

    /// <summary>Which set each stock car gets, by the game's name without "Car_" (the real car's box). A modded car names its own
    /// in its definition (<c>CarDefinition.GearSet</c>); anything else (another creator's car) keeps the game's step:
    /// <see cref="GearMath.Extend"/>.</summary>
    private static readonly Dictionary<string, GearSet> ByCar = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Brick"] = Close5, ["Bull"] = Close5, ["Lynx"] = Close5, ["Polyana"] = Close5, ["Protector"] = Close5, ["Tinny"] = Close5, ["Wombat"] = Close5,
        ["TrailBurner"] = Close5, ["Cumbre"] = Close5, ["Roamer"] = Close5, ["Woodman"] = Close5, ["GrandWoodman"] = Close5,
        ["DingDing"] = Vintage3, ["Trailrat"] = Vintage3, ["RustOx"] = Vintage3,
        ["Burro"] = Truck5, ["Boar"] = Truck5, ["Timberbox"] = Truck5, ["Marshal"] = Truck5,
        ["BlastWagon"] = Truck4, ["Mammoth"] = Truck4,
    };

    /// <summary>The set with this id ("close5"), or null.</summary>
    public static GearSet ById(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        foreach (var s in All) if (string.Equals(s.Id, id.Trim(), StringComparison.OrdinalIgnoreCase)) return s;
        return null;
    }

    /// <summary>"Car_Burro(Clone)" to "Burro".</summary>
    public static string Normalize(string gameName)
    {
        if (string.IsNullOrWhiteSpace(gameName)) return "";
        string n = gameName.Replace("(Clone)", "").Trim();
        if (n.StartsWith("Car_", StringComparison.OrdinalIgnoreCase)) n = n.Substring(4);
        return n.Replace(" ", "");
    }

    /// <summary>The set for a car by its game name, or null. <paramref name="moddedSetId"/> gives a modded car's own set id by its
    /// name (null for a car that is not modded).</summary>
    public static GearSet For(string gameName, Func<string, string> moddedSetId = null)
    {
        string n = Normalize(gameName);
        if (n.Length == 0) return null;
        if (ByCar.TryGetValue(n, out var set)) return set;
        if (n.Equals("Ding", StringComparison.OrdinalIgnoreCase)) return Vintage3;
        if (moddedSetId == null) return null;
        try { return ById(moddedSetId(n)); }
        catch (Exception) { return null; }
    }

    /// <summary>A set is well formed: 3 to <see cref="GearMath.MaxGears"/> falling positive ratios and a reference inside them.</summary>
    public static bool IsValid(GearSet s)
    {
        if (s == null || s.Real == null || s.Real.Length < 3 || s.Real.Length > GearMath.MaxGears) return false;
        if (s.Reference < 0 || s.Reference >= s.Real.Length || !(s.MaxFirst >= 1f)) return false;
        for (int i = 0; i < s.Real.Length; i++)
        {
            if (!(s.Real[i] > 0f) || float.IsInfinity(s.Real[i])) return false;
            if (i > 0 && !(s.Real[i] < s.Real[i - 1])) return false;
        }
        return true;
    }

    /// <summary>
    /// The car's gears with <paramref name="set"/>: <paramref name="stock"/> (the game's table, 1st first) is where the first and
    /// the reference gear are anchored, as the class description says. A copy of <paramref name="stock"/> comes back when the set
    /// does not fit it: the car already has four or more gears, fewer than two, ratios that are zero, negative, not falling or not
    /// numbers, or a last gear that would be taller than <see cref="GearMath.MinRatio"/>. Never throws.
    /// </summary>
    public static float[] Build(GearSet set, float[] stock)
    {
        if (stock == null) return new float[0];
        var keep = (float[])stock.Clone();
        if (!IsValid(set) || stock.Length < 2 || stock.Length >= 4) return keep;
        float g1 = stock[0], g2 = stock[1];
        if (!Ok(g1) || !Ok(g2) || !(g1 > g2)) return keep;

        int n = set.Real.Length;
        float refValue = set.Real[set.Reference];
        float last = g2 * set.Real[n - 1] / refValue;
        float firstReal = g2 * set.Real[0] / refValue;
        float first = Math.Min(firstReal, g1 * set.MaxFirst);
        if (!Ok(last) || last < GearMath.MinRatio) return keep;
        // the first gear is always the lowest and the spread is real: at least a third between the first and the last gear
        if (!(first > last * 1.3f)) return keep;

        var result = new float[n];
        float span = MathF.Log(set.Real[0] / set.Real[n - 1]);
        for (int i = 0; i < n; i++)
        {
            float t = i == 0 ? 0f : i == n - 1 ? 1f : MathF.Log(set.Real[0] / set.Real[i]) / span;
            result[i] = first * MathF.Pow(last / first, t);
        }
        result[0] = first;
        result[n - 1] = last;
        for (int i = 1; i < n; i++)
            if (!Ok(result[i]) || !(result[i] < result[i - 1])) return keep;
        return result;
    }

    private static bool Ok(float ratio) => ratio > 0f && !float.IsNaN(ratio) && !float.IsInfinity(ratio);

    /// <summary>"3.58 : 2.04 : 1.36 : 1.00 : 0.81" for the page.</summary>
    public static string Describe(float[] ratios)
    {
        if (ratios == null || ratios.Length == 0) return "";
        var parts = new string[ratios.Length];
        for (int i = 0; i < parts.Length; i++) parts[i] = ratios[i].ToString("0.00", Inv);
        return string.Join(" : ", parts);
    }
}
