using System;
using System.Collections.Generic;

namespace OverTheHillPlus.Features.Public.ModNet;

/// <summary>
/// Which channels a hello / welcome lists when there are more than fit: the Mod Manager's own (ids below
/// <see cref="FirstThirdParty"/>) always come first, so other creators' channels can never push them out. Pure logic,
/// unit tested.
/// </summary>
internal static class AdvertOrder
{
    /// <summary>The first channel id of other creators' mods (the public API's range starts here).</summary>
    public const int FirstThirdParty = 1000;

    /// <summary>Our channels, then the others, each in the given order, at most <paramref name="max"/>.</summary>
    public static List<T> Pick<T>(IEnumerable<T> items, Func<T, ushort> id, int max)
    {
        var ours = new List<T>();
        var theirs = new List<T>();
        if (items != null)
            foreach (var it in items)
                (id(it) < FirstThirdParty ? ours : theirs).Add(it);
        var list = new List<T>();
        foreach (var it in ours) { if (list.Count >= max) return list; list.Add(it); }
        foreach (var it in theirs) { if (list.Count >= max) return list; list.Add(it); }
        return list;
    }
}

/// <summary>How the Mod network card lists a peer's channels (pure, unit tested).</summary>
internal static class ModNetAdvert
{
    /// <summary>
    /// The channels this game runs, by name, in id order, and "+N other" for the ones it doesn't know: their numbers
    /// are never shown (a channel this build doesn't run says nothing useful, and may belong to a build it doesn't
    /// have). <paramref name="knownName"/> gives a channel's name, or null when this game doesn't run it.
    /// </summary>
    public static string ChannelSummary(IEnumerable<ushort> channels, Func<ushort, string> knownName)
    {
        var ids = new List<ushort>();
        if (channels != null) ids.AddRange(channels);
        if (ids.Count == 0) return "no features on";
        ids.Sort();
        var names = new List<string>();
        int other = 0;
        foreach (var id in ids)
        {
            string n = null;
            try { n = knownName?.Invoke(id); } catch (Exception ex) { DebugLog.Error("core", ex, "ModNetAdvert.ChannelSummary"); }
            if (string.IsNullOrEmpty(n)) other++;
            else names.Add(n);
        }
        string text = string.Join(", ", names);
        if (other > 0) text += (text.Length > 0 ? " · " : "") + "+" + other + " other";
        return text;
    }
}
