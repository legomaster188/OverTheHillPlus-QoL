using System;
using System.Collections.Generic;

namespace OverTheHillPlus.Shared;

/// <summary>
/// The names a stock car's prefab may go by ("Prefabs/Cars/Car_&lt;name&gt;"). The game builds the address from the car's
/// CarInfo asset name (SetupCar / SpawnPlayer: Object.GetName), which keeps the game's casing ("DingDing", "TrailBurner");
/// the item catalog's own names are lower case ("dingding") and its display names differ again, so those are only fallbacks.
/// </summary>
public static class CarPrefabNames
{
    /// <summary>Names to try, best first, without duplicates: the CarInfo asset name (a "Car_" prefix dropped), then the
    /// capitalised display / raw names, then them as they are.</summary>
    public static List<string> Candidates(string assetName, string rawName, string prettyName)
    {
        static string Cap(string s) => string.IsNullOrEmpty(s) ? s : char.ToUpperInvariant(s[0]) + s.Substring(1);
        string asset = assetName ?? "", raw = rawName ?? "", pretty = prettyName ?? "";
        if (asset.StartsWith("Car_", StringComparison.Ordinal)) asset = asset.Substring(4);
        var list = new List<string>();
        foreach (var n in new[] { asset, Cap(pretty.Replace(" ", "")), Cap(raw.Replace(" ", "")), raw, pretty.Replace(" ", "") })
            if (!string.IsNullOrEmpty(n) && !list.Contains(n)) list.Add(n);
        return list;
    }
}
