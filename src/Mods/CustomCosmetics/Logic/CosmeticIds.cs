using System;
using System.Collections.Generic;
using System.Text;

namespace OverTheHillPlus.CustomCosmetics;

/// <summary>
/// Item ids of the custom cosmetics. An id is the FNV-1a hash of "customcosmetics:&lt;part id&gt;", so it is the same on
/// every PC and in every session, and never has to be stored anywhere. Pure code (unit-tested).
/// </summary>
public static class CosmeticIds
{
    public const string Namespace = "customcosmetics:";

    /// <summary>The item id of a part (never 0).</summary>
    public static uint For(string partId)
    {
        uint h = 2166136261;
        foreach (byte b in Encoding.UTF8.GetBytes(Namespace + (partId ?? "")))
        {
            h ^= b;
            h *= 16777619;
        }
        return h == 0 ? 1u : h;
    }

    /// <summary>A part id is a folder name: lowercase letters, digits and underscores, 1 to 40 characters, starting with a letter.</summary>
    public static bool ValidPartId(string id)
    {
        if (string.IsNullOrEmpty(id) || id.Length > 40 || id[0] < 'a' || id[0] > 'z') return false;
        foreach (char c in id)
            if (!(c >= 'a' && c <= 'z') && !(c >= '0' && c <= '9') && c != '_') return false;
        return true;
    }

    /// <summary>
    /// Ids for <paramref name="partIds"/> that collide with nothing: not with another of the list, not with an id
    /// <paramref name="taken"/> says is used. A part whose id is taken gets no id (null) and is left out, with the reason in
    /// <paramref name="problems"/>: ids are never changed, since an owned part is found by its id.
    /// </summary>
    public static Dictionary<string, uint> Assign(IEnumerable<string> partIds, Func<uint, bool> taken, List<string> problems)
    {
        var result = new Dictionary<string, uint>(StringComparer.Ordinal);
        var byId = new Dictionary<uint, string>();
        foreach (var part in partIds)
        {
            uint id = For(part);
            if (byId.TryGetValue(id, out var other))
            {
                problems?.Add($"{part}: its item id 0x{id:X8} is the one of {other}");
                continue;
            }
            if (taken != null && taken(id))
            {
                problems?.Add($"{part}: its item id 0x{id:X8} is already used by another item");
                continue;
            }
            byId[id] = part;
            result[part] = id;
        }
        return result;
    }
}
