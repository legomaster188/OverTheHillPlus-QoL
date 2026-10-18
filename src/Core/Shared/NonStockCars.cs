using System;
using System.Collections.Generic;

namespace OverTheHillPlus.Core.Shared;

/// <summary>
/// Core's one list of car item ids that a stock game can't spawn: core's modded cars (the car framework registers
/// each one) and cars other mods add to the game's catalog (they register through core, never through each other).
/// Whoever picks a stand-in car (core's fallback car for the join data and the save, a mod's own stand-in in its save
/// filter) skips every id here, so one mod's car never stands in for another's. Pure (unit-tested); main thread only.
/// </summary>
internal static class NonStockCars
{
    private sealed class Entry
    {
        public string Owner;
        /// <summary>What others may call the car (e.g. Discord Presence); null: "a car".</summary>
        public string PublicName;
    }

    private static readonly Dictionary<uint, Entry> Cars = new();

    /// <summary>Replaces the cars <paramref name="owner"/> registered with these (id, public name) pairs. A null
    /// public name means the car's name is not to be shown to other people.</summary>
    public static void Set(string owner, IEnumerable<(uint id, string publicName)> cars)
    {
        if (string.IsNullOrEmpty(owner)) return;
        var drop = new List<uint>();
        foreach (var kv in Cars) if (kv.Value.Owner == owner) drop.Add(kv.Key);
        foreach (var id in drop) Cars.Remove(id);
        if (cars == null) return;
        foreach (var (id, name) in cars)
            if (id != 0 && !Cars.ContainsKey(id)) Cars[id] = new Entry { Owner = owner, PublicName = name };
    }

    /// <summary>Adds one car of <paramref name="owner"/> (an id another owner has stays theirs).</summary>
    public static void Add(string owner, uint id, string publicName)
    {
        if (string.IsNullOrEmpty(owner) || id == 0 || Cars.ContainsKey(id)) return;
        Cars[id] = new Entry { Owner = owner, PublicName = publicName };
    }

    /// <summary>The id is a non-stock car: never a stand-in.</summary>
    public static bool Contains(uint id) => id != 0 && Cars.ContainsKey(id);

    /// <summary>Who registered the car (null when it is a stock car).</summary>
    public static string OwnerOf(uint id) => Cars.TryGetValue(id, out var e) ? e.Owner : null;

    /// <summary>Every registered id except <paramref name="except"/>.</summary>
    public static List<uint> AllExcept(uint except)
    {
        var l = new List<uint>();
        foreach (var id in Cars.Keys) if (id != except) l.Add(id);
        l.Sort();
        return l;
    }

    /// <summary>
    /// The car's name as other people may see it: <paramref name="gameName"/> for a stock car, the registered public
    /// name for a non-stock one, and "a car" when it has none.
    /// </summary>
    public static string ShownName(uint id, string gameName, out bool generic)
    {
        generic = false;
        if (!Cars.TryGetValue(id, out var e)) return gameName;
        if (!string.IsNullOrWhiteSpace(e.PublicName)) return e.PublicName;
        generic = true;
        return "a car";
    }

    /// <summary>The first id of <paramref name="candidates"/> that is a stock car, 0 when none is.</summary>
    public static uint FirstStock(IEnumerable<uint> candidates)
    {
        if (candidates == null) return 0;
        foreach (var id in candidates)
            if (id != 0 && !Contains(id)) return id;
        return 0;
    }

    internal static void ClearForTests() => Cars.Clear();
}
