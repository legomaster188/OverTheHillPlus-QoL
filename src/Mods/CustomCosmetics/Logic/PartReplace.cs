using System;
using System.Collections.Generic;

namespace OverTheHillPlus.CustomCosmetics;

/// <summary>
/// A part that takes the look of one of the game's own parts (<see cref="PartManifest.Replaces"/>): the bush cables are one
/// mesh the game only moves per car, so on most cars they float or sink. Where the car's list has the game's part, ours is
/// drawn instead of its mesh and adds nothing to the list (no garage entry, no bit: players without the mod see the
/// game's own); where it hasn't, ours is a part of its own. Pure code (unit-tested).
/// </summary>
public static class PartReplace
{
    /// <summary>A game part's asset name as a manifest may give it.</summary>
    public static bool ValidName(string name)
    {
        if (string.IsNullOrEmpty(name) || name.Length > 60) return false;
        foreach (char c in name)
            if (!(c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '_')) return false;
        return true;
    }

    /// <summary>The index of the game's part <paramref name="replaces"/> in a car's list (the part names in list order,
    /// null for our own parts), or -1: then the part is one of its own on that car.</summary>
    public static int IndexOf(IReadOnlyList<string> names, string replaces)
    {
        if (names == null || string.IsNullOrEmpty(replaces)) return -1;
        for (int i = 0; i < names.Count; i++)
            if (names[i] != null && string.Equals(names[i], replaces, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    /// <summary>Does this part go on the car's list as a part of its own (false: it only draws the game's part)?</summary>
    public static bool OwnEntry(PartManifest m, IReadOnlyList<string> names) =>
        m == null || m.Replaces.Length == 0 || IndexOf(names, m.Replaces) < 0;
}
