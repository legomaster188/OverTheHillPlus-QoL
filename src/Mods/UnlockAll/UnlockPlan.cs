using System;
using System.Collections.Generic;
using System.Globalization;

namespace OverTheHillPlus.UnlockAll;

/// <summary>
/// Pure planning for Unlock All's per-car, per-map and item unlocks: given catalog-like data and what the profile
/// already holds, which ids to add and how. Nothing here touches the game (the tests compile this file on its own);
/// CarUnlocks, MapUnlocks and ItemUnlocks read the game into these shapes and carry the plan out.
///
/// How the game stores what is planned here (Cpp2IL of the updated game):
/// - A car is a VehicleItem, added with PlayerInventory.AddVehicleItem.
/// - An upgrade or cosmetic is a VehicleSpecificItem holding one VehicleState per car. Count 0 = unlocked in the shop
///   (buyable), Count 1 or more = owned. PlayerInventory.AddItem(partId, state, isNew) adds the state when the car has
///   none yet, and otherwise adds 1 to the existing state's Count (whatever Count the passed state has). So one call
///   turns a buyable upgrade into an owned one, and a second call would give a second copy: owned parts are left alone.
/// - A livery is a plain InventoryItem (owned when present), added with AddItem(id, 1, false, false).
/// - An area is an AreaItem virtual item. AddVirtualItem refuses an id that is already there, even a locked one, so a
///   locked area gets its IsUnlocked flag set instead.
/// </summary>
internal static class UnlockPlan
{
    internal enum PartKind { Upgrade, Cosmetic, Livery, Other }

    [Flags]
    internal enum CarScope
    {
        Car = 1,
        Upgrades = 2,
        Cosmetics = 4,
        Liveries = 8,
        Everything = Car | Upgrades | Cosmetics | Liveries,
    }

    /// <summary>One entry of a car's parts list. Dlc: its catalog entry needs a DLC (never added).</summary>
    internal sealed class Part
    {
        public readonly uint Id;
        public readonly PartKind Kind;
        public readonly bool Dlc;

        public Part(uint id, PartKind kind, bool dlc) { Id = id; Kind = kind; Dlc = dlc; }
    }

    /// <summary>A car with its parts (upgrades, cosmetics, liveries). Dlc: the car needs a DLC.</summary>
    internal sealed class CarEntry
    {
        public readonly uint Id;
        public readonly string Name;
        public readonly bool Dlc;
        public readonly IReadOnlyList<Part> Parts;

        public CarEntry(uint id, string name, bool dlc, IReadOnlyList<Part> parts)
        {
            Id = id;
            Name = name;
            Dlc = dlc;
            Parts = parts ?? Array.Empty<Part>();
        }
    }

    /// <summary>One PlayerInventory.AddItem(Id, VehicleState { car, Count }, false) call.</summary>
    internal readonly struct PartAdd
    {
        public readonly uint Id;
        public readonly PartKind Kind;
        public readonly int Count;

        public PartAdd(uint id, PartKind kind, int count) { Id = id; Kind = kind; Count = count; }
    }

    internal sealed class CarPlan
    {
        public bool AddCar;
        public readonly List<PartAdd> VehicleParts = new();
        /// <summary>Plain items to add once each (liveries).</summary>
        public readonly List<uint> Items = new();
        /// <summary>Distinct DLC entries left out (the car itself counts as one when it is DLC).</summary>
        public int DlcSkipped;

        public bool IsEmpty => !AddCar && VehicleParts.Count == 0 && Items.Count == 0;
    }

    /// <summary>
    /// What to add for <paramref name="car"/>. <paramref name="upgradesOwned"/>: new upgrades are owned (Count 1) and
    /// buyable ones become owned; otherwise new ones are only unlocked in the shop (Count 0) and existing ones are left
    /// as they are. Cosmetics are always owned, like the game's own unlock. <paramref name="states"/>: part id -> Count
    /// of this car's existing state (missing = no state yet). <paramref name="ownedItems"/>: plain items held.
    /// </summary>
    internal static CarPlan ForCar(CarEntry car, CarScope scope, bool upgradesOwned, bool carOwned,
        IReadOnlyDictionary<uint, int> states, ISet<uint> ownedItems)
    {
        var plan = new CarPlan();
        if (car.Dlc)
        {
            plan.DlcSkipped = 1;
            return plan;
        }
        plan.AddCar = (scope & CarScope.Car) != 0 && !carOwned;

        var seen = new HashSet<uint>();
        var dlc = new HashSet<uint>();
        foreach (var part in car.Parts)
        {
            if (part == null || !Wanted(part.Kind, scope)) continue;
            if (part.Dlc)
            {
                dlc.Add(part.Id);
                continue;
            }
            if (!seen.Add(part.Id)) continue;
            bool has = states.TryGetValue(part.Id, out int count);
            switch (part.Kind)
            {
                case PartKind.Livery:
                    if (!ownedItems.Contains(part.Id)) plan.Items.Add(part.Id);
                    break;
                case PartKind.Upgrade when !upgradesOwned:
                    if (!has) plan.VehicleParts.Add(new PartAdd(part.Id, part.Kind, 0));
                    break;
                default: // owned upgrades, cosmetics
                    if (!has || count <= 0) plan.VehicleParts.Add(new PartAdd(part.Id, part.Kind, 1));
                    break;
            }
        }
        plan.DlcSkipped = dlc.Count;
        return plan;
    }

    private static bool Wanted(PartKind kind, CarScope scope) => kind switch
    {
        PartKind.Upgrade => (scope & CarScope.Upgrades) != 0,
        PartKind.Cosmetic => (scope & CarScope.Cosmetics) != 0,
        PartKind.Livery => (scope & CarScope.Liveries) != 0,
        _ => false,
    };

    // ---------------------------------------------------------------- maps

    internal sealed class Area
    {
        public readonly uint Id;
        public readonly string Name;

        public Area(uint id, string name) { Id = id; Name = name; }
    }

    /// <summary>
    /// A map as the page lists it: one of the game's regions (Data/Game/Regions/RegionsList) with its areas. Each
    /// area is a scene with its own map texture, fog and cabins.
    /// </summary>
    internal sealed class Region
    {
        public readonly string Name;
        public readonly IReadOnlyList<Area> Areas;

        public Region(string name, IReadOnlyList<Area> areas)
        {
            Name = name;
            Areas = areas ?? Array.Empty<Area>();
        }

        /// <summary>The region's name, with the area names when it has more than one ("Alps (Valley, Peak)").</summary>
        public string Label
        {
            get
            {
                var names = new List<string>();
                foreach (var a in Areas ?? Array.Empty<Area>())
                    if (a != null && !string.IsNullOrEmpty(a.Name) && !string.Equals(a.Name, Name, StringComparison.OrdinalIgnoreCase))
                        names.Add(a.Name);
                return (Areas?.Count ?? 0) > 1 && names.Count > 0 ? $"{Name} ({string.Join(", ", names)})" : Name;
            }
        }
    }

    /// <summary>
    /// The regions to list, in the game's order: a region without areas is dropped, and an area listed in two regions
    /// stays only in the first, so each area (and each loaded map) belongs to exactly one entry.
    /// </summary>
    internal static List<Region> Regions(IEnumerable<Region> regions)
    {
        var list = new List<Region>();
        var seen = new HashSet<uint>();
        foreach (var region in regions ?? Array.Empty<Region>())
        {
            if (region?.Areas == null) continue;
            var areas = new List<Area>();
            foreach (var area in region.Areas)
                if (area != null && seen.Add(area.Id)) areas.Add(area);
            if (areas.Count > 0) list.Add(new Region(region.Name, areas));
        }
        return list;
    }

    /// <summary>Index of the region holding area <paramref name="areaId"/> (the loaded map), or -1.</summary>
    internal static int RegionOf(IReadOnlyList<Region> regions, uint? areaId)
    {
        if (areaId == null || regions == null) return -1;
        for (int i = 0; i < regions.Count; i++)
            foreach (var area in regions[i]?.Areas ?? Array.Empty<Area>())
                if (area != null && area.Id == areaId.Value) return i;
        return -1;
    }

    internal static List<uint> AreaIds(Region region)
    {
        var ids = new List<uint>();
        if (region?.Areas == null) return ids;
        foreach (var area in region.Areas)
            if (area != null && !ids.Contains(area.Id)) ids.Add(area.Id);
        return ids;
    }

    internal enum AreaStep { Create, SetUnlocked }

    /// <summary>
    /// For each area id: Create when the profile has no AreaItem for it, SetUnlocked when it has a locked one, nothing
    /// when it is unlocked already. <paramref name="known"/>: area id -> IsUnlocked of the existing AreaItem.
    /// </summary>
    internal static List<(uint Id, AreaStep Step)> ForAreas(IEnumerable<uint> ids, IReadOnlyDictionary<uint, bool> known)
    {
        var steps = new List<(uint, AreaStep)>();
        var seen = new HashSet<uint>();
        foreach (uint id in ids)
        {
            if (!seen.Add(id)) continue;
            if (!known.TryGetValue(id, out bool unlocked)) steps.Add((id, AreaStep.Create));
            else if (!unlocked) steps.Add((id, AreaStep.SetUnlocked));
        }
        return steps;
    }

    // ---------------------------------------------------------------- items

    /// <summary>The catalog names of the three tool slots (what the game's UnlockAllToolSlots adds).</summary>
    internal static readonly string[] ToolSlotNames = { "ToolSlot_1", "ToolSlot_2", "ToolSlot_3" };

    /// <summary>Ids of the tool slots not held yet (names matched ignoring case; a slot missing from the catalog is skipped).</summary>
    internal static List<uint> ToolSlots(IReadOnlyDictionary<string, uint> nameToId, ISet<uint> owned)
    {
        var ids = new List<uint>();
        foreach (string name in ToolSlotNames)
        {
            uint? id = null;
            foreach (var kv in nameToId)
                if (string.Equals(kv.Key, name, StringComparison.OrdinalIgnoreCase)) { id = kv.Value; break; }
            if (id is uint v && !owned.Contains(v) && !ids.Contains(v)) ids.Add(v);
        }
        return ids;
    }

    /// <summary>Largest custom money amount in one press.</summary>
    internal const int MaxAmount = 1_000_000_000;

    /// <summary>"5000", "12 500", "1,000", "10k", "2.5k", "1M" -> a whole amount from 1 to <see cref="MaxAmount"/>.</summary>
    internal static bool TryParseAmount(string text, out int amount)
    {
        amount = 0;
        string s = Clean(text);
        if (s.Length == 0) return false;
        decimal scale = 1m;
        char last = char.ToLowerInvariant(s[s.Length - 1]);
        if (last == 'k' || last == 'm')
        {
            scale = last == 'k' ? 1_000m : 1_000_000m;
            s = s.Substring(0, s.Length - 1);
        }
        else if (s.Contains('.')) return false; // no fractions of money
        if (!decimal.TryParse(s, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out decimal value)) return false;
        value *= scale;
        if (value != decimal.Truncate(value) || value < 1m || value > MaxAmount) return false;
        amount = (int)value;
        return true;
    }

    /// <summary>How much of <paramref name="amount"/> fits on top of <paramref name="current"/> (the game's counter is an int and doesn't check).</summary>
    internal static int MoneyToAdd(int current, int amount)
    {
        if (amount <= 0) return 0;
        long room = (long)int.MaxValue - Math.Max(0, current);
        return (int)Math.Max(0, Math.Min(amount, room));
    }

    /// <summary>Highest odometer the game's meter counter (an int) can hold, in km.</summary>
    internal const int MaxKm = int.MaxValue / 1000;

    internal static bool TryParseKm(string text, out int km)
    {
        km = 0;
        string s = Clean(text);
        if (s.Length == 0) return false;
        if (!int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out int v) || v > MaxKm) return false;
        km = v;
        return true;
    }

    /// <summary>VehicleItem.MetersTravelled for <paramref name="km"/> (the game's SetOdometer: km * 1000, capped at int.MaxValue).</summary>
    internal static int OdometerMeters(int km) => (int)Math.Min(Math.Max(0L, km) * 1000L, int.MaxValue);

    private static string Clean(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var sb = new System.Text.StringBuilder(text.Length);
        foreach (char c in text)
            if (c != ' ' && c != ',' && c != '_' && c != ' ' && c != '\'') sb.Append(c);
        return sb.ToString();
    }
}
