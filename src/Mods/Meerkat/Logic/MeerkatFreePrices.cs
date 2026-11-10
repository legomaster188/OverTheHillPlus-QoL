using System;
using System.Collections.Generic;

namespace OverTheHillPlus.Meerkat;

/// <summary>
/// Free play with prices on (the car's [Prices] PricesInFreePlay, as Custom Cosmetics has it): every choice the car doesn't
/// come with costs its upgrade's base price once, in the game's own pebbles; after that it is the player's to switch
/// freely. The car comes with its safari build (the 3 in lift, all-terrains, the hardtop, the skid plate, the rack and lamps,
/// the flares, flaps and tow hook). Off (the default): everything is free outside a career. Pure (unit-tested).
/// </summary>
public static class MeerkatFreePrices
{
    /// <summary>What the car comes with: never bought.</summary>
    public static readonly HashSet<string> ComesWith = new(StringComparer.Ordinal)
    {
        MeerkatUpgrades.Skid, MeerkatUpgrades.Rack, MeerkatUpgrades.Flares, MeerkatUpgrades.Flaps, MeerkatUpgrades.Hook,
    };

    /// <summary>The key a bought choice is kept under ("meerkat.engine:2" for an engine stage, else the upgrade id).</summary>
    public static string Key(string id, int level = 1) => level > 1 ? id + ":" + level : id;

    /// <summary>The base price of a choice (0 when the car comes with it or it is the stock state).</summary>
    public static long PriceOf(string id, int level = 1)
    {
        if (id == null || ComesWith.Contains(id)) return 0;
        foreach (var d in MeerkatUpgrades.All())
            if (d.Id == id)
                return level >= 1 && level <= d.Levels.Count ? Math.Max(0, d.Levels[level - 1].Price) : 0;
        return 0;
    }

    /// <summary>The owned keys from the config's text ("a,b,c"), and back.</summary>
    public static HashSet<string> Parse(string text)
    {
        var s = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(text)) return s;
        foreach (var p in text.Split(','))
        {
            var t = p.Trim();
            if (t.Length > 0 && t.Length <= 48) s.Add(t);
        }
        return s;
    }

    public static string Format(IEnumerable<string> keys)
    {
        var l = new List<string>(keys);
        l.Sort(StringComparer.Ordinal);
        return string.Join(",", l);
    }

    /// <summary>True when the choice needs no purchase: it is free, the car came with it, or it was bought.</summary>
    public static bool Owned(HashSet<string> owned, string id, int level = 1) =>
        PriceOf(id, level) <= 0 || (owned != null && owned.Contains(Key(id, level)));
}
