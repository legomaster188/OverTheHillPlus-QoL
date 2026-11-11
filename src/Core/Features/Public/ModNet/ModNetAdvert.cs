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

/// <summary>A hello's / welcome's payload, and how the Mod network card lists a peer's channels (pure, unit tested).</summary>
internal static class ModNetAdvert
{
    /// <summary>
    /// A hello's / welcome's payload: the mod version, the channels with their versions, then the sender's own player
    /// (PlayerRef.RawEncoded). The player comes last: a reader from before it existed stops after the channels and reads
    /// the rest as it always did.
    /// </summary>
    public static byte[] Write(string version, IReadOnlyList<(ushort id, byte version)> channels, int senderRaw)
    {
        var w = new NetWriter(64).String(version, 24);
        int n = Math.Min(channels?.Count ?? 0, 255);
        w.Byte((byte)n);
        for (int i = 0; i < n; i++) w.UShort(channels[i].id).Byte(channels[i].version);
        w.Int(senderRaw);
        return w.ToArray();
    }

    /// <summary>
    /// Reads <see cref="Write"/>'s payload into <paramref name="into"/>: at most <paramref name="max"/> channels, never
    /// <paramref name="skip"/> (the control channel). Returns the mod version ("?" when missing).
    /// <paramref name="senderRaw"/> is 0 for a payload from before the sender's player was added, and for a cut-off one.
    /// </summary>
    public static string Read(byte[] bytes, Dictionary<ushort, byte> into, int max, ushort skip, out int senderRaw)
    {
        var rd = new NetReader(bytes);
        string version = rd.String(24);
        int n = rd.Byte();
        into.Clear();
        for (int i = 0; i < n && rd.Ok; i++)
        {
            ushort ch = rd.UShort();
            byte v = rd.Byte();
            if (rd.Ok && i < max && ch != skip && v > 0) into[ch] = v;
        }
        senderRaw = rd.Ok && rd.Remaining >= 4 ? rd.Int() : 0;
        return string.IsNullOrEmpty(version) ? "?" : version;
    }

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
