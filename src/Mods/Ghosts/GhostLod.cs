using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace OverTheHillPlus.Ghosts;

/// <summary>
/// Which of a car's meshes a ghost copies when the car has levels of detail ("..._LOD0", "..._LOD1", ...): only the
/// lowest detail of each, since the ghost has no LODGroup and is drawn see-through at any distance (a modded car's
/// LOD0 can be hundreds of thousands of triangles). Meshes without a level in their name are kept. Pure (unit-tested).
/// </summary>
internal static class GhostLod
{
    private static readonly Regex Lod = new(@"LOD(\d{1,2})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>"Car_body_LOD2" → ("Car_body_", 2). False (lod -1) when the name has no level.</summary>
    public static bool TryParse(string name, out string group, out int lod)
    {
        group = name ?? "";
        lod = -1;
        if (string.IsNullOrEmpty(name)) return false;
        var ms = Lod.Matches(name);
        if (ms.Count == 0) return false;
        var m = ms[ms.Count - 1];
        lod = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        group = name.Substring(0, m.Index) + "#" + name.Substring(m.Index + m.Length);
        return true;
    }

    private static bool UnsuffixedGroup(Dictionary<string, int> highest, string name, out int level)
    {
        level = 0;
        foreach (var sep in new[] { "", "_", " ", ".", "-" })
            if (highest.TryGetValue((name ?? "") + sep + "#", out level)) return true;
        return false;
    }

    /// <summary>For each name: true to copy it. Of the names that differ only in their level, the highest level wins
    /// (each one with that level, if there are several); names without a level are always copied.</summary>
    public static bool[] KeepLowestDetail(IReadOnlyList<string> names)
    {
        var keep = new bool[names?.Count ?? 0];
        if (names == null) return keep;
        var highest = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < names.Count; i++)
            if (TryParse(names[i], out var g, out int lod) && (!highest.TryGetValue(g, out int h) || lod > h)) highest[g] = lod;
        for (int i = 0; i < names.Count; i++)
        {
            if (TryParse(names[i], out var g, out int lod)) keep[i] = highest[g] == lod;
            // "Body" next to "Body_LOD1": a name without a level is level 0 of the group it would form with a suffix
            else keep[i] = !UnsuffixedGroup(highest, names[i], out int h) || h == 0;
        }
        return keep;
    }
}
