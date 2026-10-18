using System;
using System.Collections.Generic;
using ItemType = InventoryItemScriptableObject.ItemType;

namespace OverTheHillPlus.UnlockAll;

/// <summary>
/// The Cars section: one car at a time (or every car for the bulk upgrades), read from the game into
/// <see cref="UnlockPlan"/> shapes and added with the game's own PlayerInventory adders, inside the DLC guard.
///
/// Findings (Cpp2IL of the updated game):
/// - The cars are InventoryUtility.GetAllVehicles() (the catalog's vehicles that DlcManager.IsAllowed lets through);
///   each is a CarInfo whose UpgradesLists.Parts are its shop parts (upgrades, cosmetics, liveries: Livery is a
///   VehiclePartSO too; StateMerchantMenu.InitializePerformanceUpgradesScreen lists exactly CarInfo.UpgradesLists.Parts),
///   plus LinkedParts and DefaultMountedUpgrades. The kind is InventoryItemScriptableObject.Type.
/// - The game's UnlockAllUpgrades adds every upgrade of the whole catalog to every vehicle with
///   VehicleState { Count = its bool argument ? 1 : 0 } (0 by default: "in the shop"), skipping an upgrade as soon as
///   any car has it; UnlockAllCosmetics does the same with Count 1. Here only the car's own parts are added, and a
///   part this car already has is judged by this car's state (see <see cref="UnlockPlan"/>).
/// - SetOdometer sets VehicleItem.MetersTravelled = km * 1000 (capped at int.MaxValue) and saves.
/// </summary>
internal static class CarUnlocks
{
    private static List<UnlockPlan.CarEntry> _all;
    private static List<UnlockPlan.CarEntry> _listed;

    /// <summary>Every car the game offers this profile, DLC ones flagged (empty until the catalog loads).</summary>
    internal static IReadOnlyList<UnlockPlan.CarEntry> All
    {
        get
        {
            if (_all != null) return _all;
            var list = Read();
            if (list.Count == 0) return list;
            _all = list;
            _listed = list.FindAll(c => !c.Dlc);
            UnlockAllPlugin.Logger.LogInfo($"[UnlockAll] {_all.Count} cars, {_listed.Count} without a DLC.");
            return _all;
        }
    }

    /// <summary>The cars the page lists: every car that needs no DLC.</summary>
    internal static IReadOnlyList<UnlockPlan.CarEntry> Listed => All.Count == 0 ? Array.Empty<UnlockPlan.CarEntry>() : _listed;

    private static List<UnlockPlan.CarEntry> Read()
    {
        var list = new List<UnlockPlan.CarEntry>();
        try
        {
            var vehicles = InventoryUtility.GetAllVehicles();
            if (vehicles == null) return list;
            foreach (var so in vehicles)
            {
                var car = so?.TryCast<CarInfo>();
                if (car == null) continue;
                var parts = new List<UnlockPlan.Part>();
                var seen = new HashSet<uint>();
                void Add(InventoryItemScriptableObject part)
                {
                    if (part == null) return;
                    uint id;
                    try { id = part.ItemId; } catch (Exception ex) { DebugLog.Error("unlockall", ex, "CarUnlocks.Read"); return; }
                    if (!seen.Add(id)) return;
                    parts.Add(new UnlockPlan.Part(id, KindOf(part), DlcGuard.IsDlc(part)));
                }
                try
                {
                    var shop = car.UpgradesLists?.Parts;
                    if (shop != null)
                        foreach (var p in shop)
                        {
                            Add(p);
                            var linked = p?.LinkedParts;
                            if (linked != null) foreach (var l in linked) Add(l);
                        }
                    var mounted = car.DefaultMountedUpgrades;
                    if (mounted != null) foreach (var p in mounted) Add(p);
                }
                catch (Exception e)
                {
                    DebugLog.Error("unlockall", e, "CarUnlocks.Read");
                    UnlockAllPlugin.Logger.LogWarning($"[UnlockAll] parts of car {car.ItemId} unreadable: {e.GetBaseException().Message}");
                }
                list.Add(new UnlockPlan.CarEntry(car.ItemId, NameOf(car), DlcGuard.IsDlc(car), parts));
            }
            list.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception e)
        {
            DebugLog.Error("unlockall", e, "CarUnlocks.Read");
            UnlockAllPlugin.Logger.LogWarning("[UnlockAll] car list unreadable: " + e.GetBaseException().Message);
            list.Clear();
        }
        return list;
    }

    private static UnlockPlan.PartKind KindOf(InventoryItemScriptableObject so)
    {
        try
        {
            return so.Type switch
            {
                ItemType.VehicleUpgrade => UnlockPlan.PartKind.Upgrade,
                ItemType.VehicleCosmetic => UnlockPlan.PartKind.Cosmetic,
                ItemType.Livery or ItemType.SpecialLivery => UnlockPlan.PartKind.Livery,
                _ => UnlockPlan.PartKind.Other,
            };
        }
        catch (Exception ex) { DebugLog.Error("unlockall", ex, "CarUnlocks.KindOf"); return UnlockPlan.PartKind.Other; }
    }

    /// <summary>The car's name in the game's language, else its catalog name.</summary>
    internal static string NameOf(InventoryItemScriptableObject so)
    {
        try
        {
            string s = so.Name?.GetLocalizedString();
            if (!string.IsNullOrWhiteSpace(s)) return s.Trim();
        }
        catch (Exception ex) { DebugLog.Error("unlockall", ex, "CarUnlocks.NameOf"); }
        try
        {
            var names = PlayerInventoryManager.InventoryItemsCatalog?.IdToName;
            if (names != null && names.ContainsKey(so.ItemId)) return names[so.ItemId].Replace('_', ' ');
            return so.name;
        }
        catch (Exception ex) { DebugLog.Error("unlockall", ex, "CarUnlocks.NameOf"); return "?"; }
    }

    // ---------------------------------------------------------------- reading the profile

    private static PlayerInventory Inventory => PlayerInventoryManager.Instance?.Inventory;

    internal static BaseInventoryItem Item(uint id)
    {
        try
        {
            var inv = Inventory;
            if (inv == null) return null;
            BaseInventoryItem item = null;
            return inv.TryGetItem(id, out item) ? item : null;
        }
        catch (Exception ex) { DebugLog.Error("unlockall", ex, "CarUnlocks.Item"); return null; }
    }

    internal static bool Owns(uint carId) => Item(carId) != null;

    /// <summary>The car's odometer in km, or null when the car isn't in the garage.</summary>
    internal static int? OdometerKm(uint carId)
    {
        try
        {
            var v = Item(carId)?.TryCast<VehicleItem>();
            return v == null ? null : v.MetersTravelled / 1000;
        }
        catch (Exception ex) { DebugLog.Error("unlockall", ex, "CarUnlocks.OdometerKm"); return null; }
    }

    /// <summary>Part id -> Count of this car's state, for the parts that have one.</summary>
    private static Dictionary<uint, int> States(UnlockPlan.CarEntry car)
    {
        var states = new Dictionary<uint, int>();
        foreach (var part in car.Parts)
        {
            if (part.Kind != UnlockPlan.PartKind.Upgrade && part.Kind != UnlockPlan.PartKind.Cosmetic) continue;
            try
            {
                var vs = Item(part.Id)?.TryCast<VehicleSpecificItem>();
                var list = vs?.VehicleStates;
                if (list == null) continue;
                foreach (var st in list)
                    if (st != null && st.VehicleItemId == car.Id)
                    {
                        states[part.Id] = st.Count;
                        break;
                    }
            }
            catch (Exception ex) { DebugLog.Error("unlockall", ex, "CarUnlocks.States"); }
        }
        return states;
    }

    private static HashSet<uint> OwnedItems(UnlockPlan.CarEntry car)
    {
        var owned = new HashSet<uint>();
        foreach (var part in car.Parts)
            if (part.Kind == UnlockPlan.PartKind.Livery && Item(part.Id) != null) owned.Add(part.Id);
        return owned;
    }

    // ---------------------------------------------------------------- unlocking

    /// <summary>
    /// Adds what <paramref name="scope"/> asks for to <paramref name="car"/>. Call inside <see cref="DlcGuard.Run"/>
    /// (through <see cref="Unlocker.Guarded"/>). Returns how many adds were made; DLC parts left out go to the tally.
    /// </summary>
    internal static int Apply(UnlockPlan.CarEntry car, UnlockPlan.CarScope scope, Unlocker.Tally tally)
    {
        var inv = Inventory;
        if (inv == null || car == null) return 0;
        var plan = UnlockPlan.ForCar(car, scope, UnlockAllConfig.Owned, Owns(car.Id), States(car), OwnedItems(car));
        tally.DlcSkipped += plan.DlcSkipped;
        int added = 0;
        if (plan.AddCar && Try(tally, "car", () => inv.AddVehicleItem(car.Id, 0, 1, false, false))) added++;
        foreach (var add in plan.VehicleParts)
        {
            var state = new VehicleState();
            state.VehicleItemId = car.Id;
            state.Count = add.Count;
            state.InUse = false;
            if (Try(tally, add.Kind == UnlockPlan.PartKind.Upgrade ? "upgrades" : "cosmetics", () => inv.AddItem(add.Id, state, false))) added++;
        }
        foreach (uint id in plan.Items)
            if (Try(tally, "liveries", () => inv.AddItem(id, 1, false, false))) added++;
        return added;
    }

    private static bool Try(Unlocker.Tally tally, string what, Action add)
    {
        try
        {
            add();
            return true;
        }
        catch (Exception e)
        {
            DebugLog.Error("unlockall", e, "CarUnlocks.Try");
            if (!tally.Failed.Contains(what)) tally.Failed.Add(what);
            UnlockAllPlugin.Logger.LogWarning($"[UnlockAll] adding {what} failed: {e.GetBaseException().Message}");
            return false;
        }
    }

    internal static string Unlock(UnlockPlan.CarEntry car, UnlockPlan.CarScope scope, string label) =>
        Unlocker.Guarded(label, t => t.Added += Apply(car, scope, t));

    /// <summary>Every car (the bulk upgrades button): DLC cars count as left alone.</summary>
    internal static int ApplyAll(UnlockPlan.CarScope scope, Unlocker.Tally tally)
    {
        int added = 0;
        foreach (var car in All) added += Apply(car, scope, tally);
        return added;
    }

    internal static string SetOdometer(UnlockPlan.CarEntry car, int km) =>
        Unlocker.Guarded($"{car.Name} odometer", t =>
        {
            if (car.Dlc) { t.DlcSkipped++; return; }
            var v = Item(car.Id)?.TryCast<VehicleItem>();
            if (v == null)
            {
                t.Message = $"{car.Name} isn't in your garage: unlock it first.";
                return;
            }
            v.MetersTravelled = UnlockPlan.OdometerMeters(km);
            t.Added++;
            t.Message = $"{car.Name} odometer set to {km:N0} km.";
        });
}
