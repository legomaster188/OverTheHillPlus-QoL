using System;
using System.Collections.Generic;

namespace OverTheHillPlus.CustomCosmetics;

/// <summary>One part on a car, as far as the groups care: its cosmetic type (-1 when it is no cosmetic) and group.</summary>
public readonly struct GroupedPart
{
    public readonly int Type;
    public readonly string Group;
    public GroupedPart(int type, string group) { Type = type; Group = group ?? ""; }
}

/// <summary>
/// Groups within a cosmetic type. The game mounts a cosmetic by taking off the first mounted one of the same type; a
/// custom part with a group (wheels in the Fenders tab) must only replace parts of its own group, and a part without one
/// (a mudflap) must not take off a grouped part. So, for the time of the mount, the parts of the same type in another
/// group step aside and come back after. Pure (unit-tested).
/// </summary>
public static class PartGroups
{
    /// <summary>The indices in <paramref name="mounted"/> to set aside while <paramref name="mounting"/> goes on: the
    /// cosmetics of its type in another group. Nothing when no part of that type has a group (the game's own rule).</summary>
    public static List<int> SetAside(IReadOnlyList<GroupedPart> mounted, GroupedPart mounting)
    {
        var list = new List<int>();
        if (mounted == null || mounting.Type < 0) return list;
        for (int i = 0; i < mounted.Count; i++)
        {
            var m = mounted[i];
            if (m.Type == mounting.Type && !string.Equals(m.Group, mounting.Group, StringComparison.Ordinal)) list.Add(i);
        }
        return list;
    }
}

/// <summary>Names of a wheel's mesh objects (the game's and the Mod Manager's cars name them alike). Pure (unit-tested).</summary>
public static class WheelNames
{
    /// <summary>The tyre: "Burro_tyre_LOD0", "..._tire_...".</summary>
    public static bool IsTyre(string name) => name != null && (name.IndexOf("tyre", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                                               name.IndexOf("tire", StringComparison.OrdinalIgnoreCase) >= 0);

    /// <summary>The rim: "Burro_wheel_LOD1", not the tyre, not one of ours ("CC_...").</summary>
    public static bool IsRim(string name) => name != null && !IsTyre(name) && !name.StartsWith("CC_", StringComparison.Ordinal) &&
                                             name.IndexOf("wheel", StringComparison.OrdinalIgnoreCase) >= 0;
}

/// <summary>When a part draws its lower detail mesh. Pure (unit-tested).</summary>
public static class PartLodRule
{
    /// <summary>Beyond this distance from the camera (metres) a part draws its LOD1.</summary>
    public const float Lod1Distance = 30f;
    /// <summary>A part only switches back to full detail this much closer, so it doesn't flicker at the line.</summary>
    public const float Hysteresis = 3f;

    /// <summary>Should the part draw its LOD1 now (it draws it already: <paramref name="lowNow"/>)?</summary>
    public static bool UseLod1(float sqrDistance, bool lowNow)
    {
        float edge = lowNow ? Lod1Distance - Hysteresis : Lod1Distance;
        return sqrDistance > edge * edge;
    }
}
