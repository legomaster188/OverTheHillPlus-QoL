using System;
using System.Collections.Generic;
using System.Text;

namespace OverTheHillPlus.Cars;

/// <summary>A livery a car mod bundles: the PNG "&lt;asset&gt;_livery_&lt;id&gt;.png" (and its garage icon
/// "&lt;asset&gt;_livery_&lt;id&gt;_icon.png") in the mod's embedded assets, shown in the garage's Liveries tab.</summary>
public sealed class LiveryPreset
{
    /// <summary>1 to 24 characters from a-z, 0-9 and '_'. Part of the item id and the network layout: never changes.</summary>
    public string Id;
    /// <summary>The garage name, e.g. "Bronze Two-Tone".</summary>
    public string Name;
    /// <summary>One plain sentence for the garage.</summary>
    public string Description;

    public LiveryPreset() { }
    public LiveryPreset(string id, string name, string description) { Id = id; Name = name; Description = description; }
}

/// <summary>
/// A modded car's liveries as the game's own livery parts. Entry 0 is always the car's own paint ("Paint": your paint
/// and trim colours), then the car's presets in their order. Each entry is a runtime <c>Livery</c> part appended to the
/// car's <c>UpgradesLists.Parts</c> right after the base car's kept parts, so the garage's Liveries tab lists, buys and
/// mounts them, and the game sends the choice to other players in its mounted-parts bitset (one bit per list entry,
/// 256 bits). Every peer builds the same list from the same car mod, and the liveries are part of the car's network
/// layout (<see cref="LayoutPart"/>), so peers with another set are refused at connect instead of seeing the wrong part.
/// Pure (unit-tested).
/// </summary>
public static class CarLiveries
{
    public const int MaxLiveries = 16, MaxId = 24, MaxName = 40, MaxDescription = 200, MaxBits = 256;
    /// <summary>The id of entry 0, the car's own paint.</summary>
    public const string PaintId = "paint";
    public const string PaintName = "Paint";
    public const string PaintDescription = "Your own paint and trim colours, set on the MODS page.";

    public static bool IsValidId(string id)
    {
        if (string.IsNullOrEmpty(id) || id.Length > MaxId) return false;
        foreach (char c in id)
            if (!(c >= 'a' && c <= 'z' || c >= '0' && c <= '9' || c == '_')) return false;
        return true;
    }

    /// <summary>Null when the list can be used (null and empty are fine: no liveries), otherwise the first problem.</summary>
    public static string Check(IReadOnlyList<LiveryPreset> list)
    {
        if (list == null || list.Count == 0) return null;
        if (list.Count > MaxLiveries) return $"at most {MaxLiveries} liveries";
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { PaintName };
        foreach (var l in list)
        {
            if (l == null) return "a livery is missing";
            if (!IsValidId(l.Id)) return $"the livery id \"{l.Id}\" must be 1 to {MaxId} characters from a-z, 0-9 and '_'";
            if (l.Id == PaintId) return $"\"{PaintId}\" is the car's own paint, not a livery id";
            if (!ids.Add(l.Id)) return $"the livery id \"{l.Id}\" is listed twice";
            if (string.IsNullOrWhiteSpace(l.Name) || l.Name.Length > MaxName) return $"the livery \"{l.Id}\" needs a name of 1 to {MaxName} characters";
            if (!names.Add(l.Name.Trim())) return $"the livery name \"{l.Name}\" is used twice (or is \"{PaintName}\")";
            if (l.Description != null && l.Description.Length > MaxDescription) return $"the livery \"{l.Id}\" has a description over {MaxDescription} characters";
        }
        return null;
    }

    /// <summary>The ids of the entries in list order: the paint, then the presets (empty when there are no presets).</summary>
    public static List<string> EntryIds(IReadOnlyList<LiveryPreset> list)
    {
        var ids = new List<string>();
        if (list == null || list.Count == 0) return ids;
        ids.Add(PaintId);
        foreach (var l in list) ids.Add(l.Id);
        return ids;
    }

    /// <summary>An entry's item id: the FNV-1a hash of "cars:&lt;car id&gt;/livery/&lt;entry id&gt;" (the same on every PC,
    /// never stored anywhere but the save sidecar); never 0.</summary>
    public static uint ItemId(string carId, string entryId)
    {
        uint h = 2166136261;
        foreach (byte b in Encoding.UTF8.GetBytes("cars:" + (carId ?? "") + "/livery/" + (entryId ?? "")))
        {
            h ^= b;
            h *= 16777619;
        }
        return h == 0 ? 1u : h;
    }

    public static string TextureFile(string assetName, string entryId) => assetName + "_livery_" + entryId + ".png";
    public static string IconFile(string assetName, string entryId) => assetName + "_livery_" + entryId + "_icon.png";

    /// <summary>The key a bundled livery has as a skin (MODS page): "*&lt;car id&gt;/&lt;name&gt;". The '*' can't start a
    /// pack folder's name on Windows, so it never clashes with a livery pack's key.</summary>
    public static string SkinKey(string carId, string name) => "*" + carId + "/" + (name ?? "").Trim();

    /// <summary>What the liveries add to the car's network layout: "" without liveries (the layout stays as it was),
    /// otherwise "|liv&lt;n&gt;:&lt;8 hex digits of the FNV-1a hash of the entry ids&gt;".</summary>
    public static string LayoutPart(IReadOnlyList<LiveryPreset> list)
    {
        var ids = EntryIds(list);
        if (ids.Count == 0) return "";
        uint h = 2166136261;
        foreach (byte b in Encoding.UTF8.GetBytes(string.Join(",", ids)))
        {
            h ^= b;
            h *= 16777619;
        }
        return $"|liv{ids.Count}:{h:x8}";
    }

    /// <summary>
    /// The bits the entries take when appended to a list that already holds <paramref name="existing"/> parts: entry i
    /// gets bit existing + i, or -1 when that is past the bitset (the entry is left out). Nothing is added twice: an
    /// entry whose item id is already in the list keeps that index.
    /// </summary>
    public static int[] PlanBits(IReadOnlyList<uint> existing, IReadOnlyList<uint> entryItemIds)
    {
        var bits = new int[entryItemIds?.Count ?? 0];
        int count = existing?.Count ?? 0;
        for (int i = 0; i < bits.Length; i++)
        {
            int have = -1;
            if (existing != null)
                for (int k = 0; k < existing.Count; k++)
                    if (existing[k] == entryItemIds[i]) { have = k; break; }
            if (have >= 0) { bits[i] = have < MaxBits ? have : -1; continue; }
            bits[i] = count < MaxBits ? count : -1;
            count++;
        }
        return bits;
    }

    /// <summary>
    /// The entry a car has mounted, from its bitset: the first preset (entry 1 and up) whose bit is set, otherwise 0
    /// (the paint: nothing, or the paint entry, mounted). <paramref name="bits"/>[i] is entry i's bit (-1: not in the
    /// list).
    /// </summary>
    public static int MountedEntry(IReadOnlyList<int> bits, Func<int, bool> isSet)
    {
        if (bits == null || isSet == null) return 0;
        for (int i = 1; i < bits.Count; i++)
            if (bits[i] >= 0 && isSet(bits[i])) return i;
        return 0;
    }
}
