using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using OverTheHillPlus.Features.Public.ModNet;

namespace OverTheHillPlus.CustomCosmetics;

/// <summary>
/// The players' part sets, compared. A custom part reaches another player as a bit of the car's mounted-parts set, and the
/// bit is the part's place in the car's list (<see cref="PartLists"/>): which parts there are and which cars they fit decide
/// it. Two players with another version of the parts would read each other's bits as other parts, so each one sends the
/// other a fingerprint of its set (the part ids in order, their item ids, the cars they fit and the game part they draw in
/// place of) and a player whose set differs from the host's is told, as is the host. Pure code (unit-tested).
/// </summary>
public static class PartsSignature
{
    /// <summary>The wire format of <see cref="Write"/>.</summary>
    public const byte Format = 1;
    public const int MaxPayload = 64;

    /// <summary>One part as it decides the bits: its id, item id, the cars it fits and the game part it draws in place of.</summary>
    public readonly struct Part
    {
        public readonly string Id;
        public readonly uint ItemId;
        public readonly IEnumerable<string> Fits;
        public readonly string Replaces;
        public Part(string id, uint itemId, IEnumerable<string> fits, string replaces)
        {
            Id = id ?? "";
            ItemId = itemId;
            Fits = fits;
            Replaces = replaces ?? "";
        }
    }

    /// <summary>What one game says about its parts.</summary>
    public sealed class Set
    {
        public string ModVersion = "";
        public int Count;
        /// <summary>16 hex digits (<see cref="Of"/>).</summary>
        public string Hash = "";

        public bool SameParts(Set other) => other != null && Count == other.Count && string.Equals(Hash, other.Hash, StringComparison.Ordinal);

        /// <summary>"v1.1, 60 parts".</summary>
        public string Describe() => $"v{(string.IsNullOrEmpty(ModVersion) ? "?" : ModVersion)}, {Count} part{(Count == 1 ? "" : "s")}";
    }

    /// <summary>The fingerprint of a part set: 16 hex digits of a 64-bit FNV-1a hash, the same whatever order the parts come in.</summary>
    public static string Of(IEnumerable<Part> parts)
    {
        var list = new List<Part>(parts ?? Array.Empty<Part>());
        list.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));
        var sb = new StringBuilder("cc1");
        foreach (var p in list)
        {
            var fits = new List<string>();
            if (p.Fits != null)
                foreach (var f in p.Fits)
                    if (!string.IsNullOrEmpty(f)) fits.Add(f.ToLowerInvariant());
            fits.Sort(string.CompareOrdinal);
            sb.Append('|').Append(p.Id).Append('=').Append(p.ItemId.ToString("X8", CultureInfo.InvariantCulture))
              .Append('=').Append(string.Join(",", fits)).Append('=').Append(p.Replaces.ToLowerInvariant());
        }
        ulong h = 14695981039346656037UL;
        foreach (byte b in Encoding.UTF8.GetBytes(sb.ToString()))
        {
            h ^= b;
            h *= 1099511628211UL;
        }
        return h.ToString("x16", CultureInfo.InvariantCulture);
    }

    public static byte[] Write(Set s) =>
        new NetWriter(MaxPayload).Byte(Format).String(s?.ModVersion ?? "", 16).UShort((ushort)Math.Clamp(s?.Count ?? 0, 0, ushort.MaxValue))
            .String(s?.Hash ?? "", 16).ToArray();

    /// <summary>Null when the payload isn't this format (a newer format is read by the fields it starts with).</summary>
    public static Set Read(byte[] payload)
    {
        if (payload == null || payload.Length == 0 || payload.Length > MaxPayload) return null;
        var r = new NetReader(payload);
        if (r.Byte() < Format) return null;
        var s = new Set { ModVersion = r.String(16), Count = r.UShort(), Hash = r.String(16) };
        return r.Ok && s.Hash.Length == 16 ? s : null;
    }

    /// <summary>A guest whose parts differ from the host's.</summary>
    public static string GuestNotice(Set host, Set mine) =>
        $"Your custom parts are another set than the host's (host: {host.Describe()}; you: {mine.Describe()}). " +
        "Parts on other players' cars may show as other parts, or not at all: everyone needs the same version of Custom Cosmetics.";

    /// <summary>The host, about a guest whose parts differ.</summary>
    public static string HostNotice(Set guest, Set mine) =>
        $"A player who joined has another set of custom parts (theirs: {guest.Describe()}; yours: {mine.Describe()}). " +
        "Parts on cars may look wrong between you: everyone needs the same version of Custom Cosmetics.";
}
