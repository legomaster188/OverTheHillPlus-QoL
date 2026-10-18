using System;
using System.Collections.Generic;

namespace OverTheHillPlus.CustomCosmetics;

/// <summary>One part to add to a car's list.</summary>
public readonly struct PartToAdd
{
    public readonly string PartId;
    public readonly uint ItemId;
    public PartToAdd(string partId, uint itemId) { PartId = partId; ItemId = itemId; }
}

/// <summary>
/// How the custom parts are appended to a car's part list. The game's mounted-parts bitset (BitSet256) has one bit per
/// entry of the car's <c>UpgradesLists.Parts</c>, so players see each other's parts through the index: the custom parts go
/// at the END of the list (the stock indices never move), in the order of their part ids (the same on every PC with the
/// same set of parts), and never past the 256 bits the set holds. Pure code (unit-tested).
/// </summary>
public static class PartLists
{
    public const int MaxBits = 256;

    /// <summary>
    /// The parts to append to a list that already holds <paramref name="existing"/>, in the order to add them. A part
    /// whose item id is already in the list is not added twice; parts beyond the bitset's capacity are left out and named in
    /// <paramref name="skipped"/>.
    /// </summary>
    public static List<PartToAdd> Plan(IReadOnlyList<uint> existing, IEnumerable<PartToAdd> candidates, List<string> skipped)
    {
        var have = new HashSet<uint>(existing ?? (IReadOnlyList<uint>)Array.Empty<uint>());
        var sorted = new List<PartToAdd>(candidates ?? Array.Empty<PartToAdd>());
        sorted.Sort((a, b) => string.CompareOrdinal(a.PartId, b.PartId));
        var add = new List<PartToAdd>();
        int count = existing?.Count ?? 0;
        foreach (var c in sorted)
        {
            if (have.Contains(c.ItemId)) continue;
            if (count >= MaxBits)
            {
                skipped?.Add($"{c.PartId}: the car's list is full ({MaxBits} bits)");
                continue;
            }
            have.Add(c.ItemId);
            add.Add(c);
            count++;
        }
        return add;
    }

    /// <summary>The bit a part takes in a list: its index, or -1 when it isn't in it.</summary>
    public static int BitOf(IReadOnlyList<uint> list, uint itemId)
    {
        if (list == null) return -1;
        for (int i = 0; i < list.Count; i++)
            if (list[i] == itemId) return i < MaxBits ? i : -1;
        return -1;
    }
}
