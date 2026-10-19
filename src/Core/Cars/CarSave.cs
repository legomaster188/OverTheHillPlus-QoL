using System;
using System.Collections.Generic;
using System.IO;
using BepInEx;
using OverTheHillPlus.Core.Shared;

namespace OverTheHillPlus.Cars;

/// <summary>
/// Keeps a modded car out of the game's save, so a save written with the mod loads in a game without it (it then has
/// the fallback car in use, and nothing unknown in its inventory):
/// <list type="bullet">
/// <item>Writing: right before the game serializes the inventory, the car, its parts and anything else keyed by its
///   id come out of the item list (<see cref="CarSavePlan"/>), the fallback car is marked in use if the modded car was,
///   and what came out goes to a small sidecar file (BepInEx/config/oth.&lt;car id&gt;.&lt;profile&gt;.sidecar). Right
///   after, the inventory is put back exactly as it was. With several modded cars each takes its own out in turn and
///   they go back in the reverse order (<see cref="ModdedCars"/> runs them).</item>
/// <item>Loading: after the game reads a profile, the sidecar's car, odometer and parts are added back (once the car
///   is registered, if the profile loads before that).</item>
/// </list>
/// </summary>
internal sealed class CarSave
{
    internal sealed class Undo
    {
        public Il2CppSystem.Collections.Generic.List<BaseInventoryItem> Items;
        public readonly List<(int index, BaseInventoryItem item)> RemovedItems = new();
        public readonly List<(Il2CppSystem.Collections.Generic.List<VehicleState> list, int index, VehicleState state)> RemovedStates = new();
        public InventoryItem FallbackMarked;
    }

    private readonly RuntimeCar _car;
    private (PlayerInventory inv, CarSidecar sidecar)? _toRestore;
    private bool _disabled;
    /// <summary>The profile (file key) whose sidecar could not be read nor set aside: never written over this session.</summary>
    private string _unreadable;

    /// <summary>The car shown in use instead of the modded car: the last stock car the player drove (or the garage base).</summary>
    public uint FallbackCar;

    public CarSave(RuntimeCar car) { _car = car; }

    private uint ItemId => _car.Def.ItemId;

    // ---------------------------------------------------------------- writing

    /// <summary>Takes the car out of <paramref name="items"/> before the game writes them; the undo to put it back, or
    /// null when nothing came out.</summary>
    public Undo BeforeWrite(Il2CppSystem.Collections.Generic.List<BaseInventoryItem> items)
    {
        if (items == null) return null;
        var model = Model(items, out var own);
        var plan = CarSavePlan.Make(model, ItemId, FallbackCar != 0 ? FallbackCar : _car.Catalog.Base?.ItemId ?? 0, ModdedCars.OtherItemIds(_car),
            _car.Liveries.ItemIds);
        WriteSidecar(plan, own, model);
        if (plan.IsEmpty) return null;

        var undo = new Undo { Items = items };
        try
        {
            Apply(items, plan, undo);
            return undo;
        }
        catch (Exception e)
        {
            DebugLog.Error("core", e, "CarSave.BeforeWrite");
            AfterWrite(undo);
            _car.LogOnce("save-filter", $"could not keep the {_car.Def.CarName} out of this save: " + e.Message);
            return null;
        }
    }

    /// <summary>The inventory as <see cref="CarSavePlan"/> sees it (and the car's own item, when owned).</summary>
    private List<CarSaveItem> Model(Il2CppSystem.Collections.Generic.List<BaseInventoryItem> items, out VehicleItem own)
    {
        var model = new List<CarSaveItem>(items.Count);
        own = null;
        for (int i = 0; i < items.Count; i++)
        {
            var b = items[i];
            var it = new CarSaveItem { ItemId = b != null ? b.ItemId : 0 };
            if (b != null)
            {
                var vi = b.TryCast<VehicleItem>();
                var ii = b.TryCast<InventoryItem>();
                var vs = b.TryCast<VehicleSpecificItem>();
                it.IsVehicle = vi != null;
                it.InUse = ii != null && ii.InUse;
                if (vi != null && vi.ItemId == ItemId) own = vi;
                if (vs?.VehicleStates != null)
                {
                    it.States = new List<CarSaveState>(vs.VehicleStates.Count);
                    foreach (var st in vs.VehicleStates)
                        it.States.Add(st == null ? null : new CarSaveState { VehicleItemId = st.VehicleItemId, Count = st.Count, InUse = st.InUse });
                }
            }
            model.Add(it);
        }
        return model;
    }

    /// <summary>Carries out a plan on the live list; with <paramref name="undo"/>, records how to put it back.</summary>
    private static void Apply(Il2CppSystem.Collections.Generic.List<BaseInventoryItem> items, CarSavePlan plan, Undo undo)
    {
        // states first (their item indices are still valid), highest index first
        for (int k = plan.RemoveStates.Count - 1; k >= 0; k--)
        {
            var (ii, si) = plan.RemoveStates[k];
            var list = items[ii].TryCast<VehicleSpecificItem>().VehicleStates;
            undo?.RemovedStates.Add((list, si, list[si]));
            list.RemoveAt(si);
        }
        if (plan.FallbackInUse >= 0)
        {
            var fb = items[plan.FallbackInUse].TryCast<InventoryItem>();
            if (fb != null && !fb.InUse)
            {
                fb.InUse = true;
                if (undo != null) undo.FallbackMarked = fb;
            }
        }
        for (int k = plan.RemoveItems.Count - 1; k >= 0; k--)
        {
            int idx = plan.RemoveItems[k];
            undo?.RemovedItems.Add((idx, items[idx]));
            items.RemoveAt(idx);
        }
    }

    /// <summary>Puts the inventory back exactly as it was before <see cref="BeforeWrite"/>.</summary>
    public void AfterWrite(Undo u)
    {
        if (u == null) return;
        try
        {
            // removed highest index first: re-insert lowest first
            for (int k = u.RemovedItems.Count - 1; k >= 0; k--)
            {
                var (idx, item) = u.RemovedItems[k];
                u.Items.Insert(Math.Min(idx, u.Items.Count), item);
            }
            if (u.FallbackMarked != null) u.FallbackMarked.InUse = false;
            for (int k = u.RemovedStates.Count - 1; k >= 0; k--)
            {
                var (list, idx, st) = u.RemovedStates[k];
                list.Insert(Math.Min(idx, list.Count), st);
            }
        }
        catch (Exception e) { DebugLog.Error("core", e, "CarSave.AfterWrite"); _car.LogOnce("save-restore", "could not put the inventory back after saving: " + e.Message); }
    }

    private void WriteSidecar(CarSavePlan plan, VehicleItem own, List<CarSaveItem> model)
    {
        // A loaded sidecar not applied yet (the car isn't registered), or the car is off this session: this
        // inventory lacks the car only for now, so the file is kept as it is.
        if (_toRestore != null || _disabled) return;
        try
        {
            string profile = OverlandSaver.CurrentProfileSave?.Profile?.Name;
            var s = new CarSidecar
            {
                Owned = plan.CarOwned,
                InUse = plan.CarInUse,
                MetersTravelled = own != null ? own.MetersTravelled : 0,
                FallbackId = plan.FallbackInUse >= 0 ? model[plan.FallbackInUse].ItemId : FallbackCar,
            };
            s.States.AddRange(plan.Removed);
            if (_unreadable != null && _unreadable == CarSidecar.FileKey(profile)) return; // keep what may still be recovered
            string path = SidecarPath(profile);
            if (!s.Owned && s.States.Count == 0)
            {
                if (File.Exists(path)) File.Delete(path); // sold, or never owned: nothing to bring back
                return;
            }
            // Written at every save (autosaves too): tmp then replace, so a crash mid-write never cuts the only copy
            // of the car's odometer and parts.
            AtomicFile.WriteAllText(path, s.Encode());
        }
        catch (Exception e) { DebugLog.Error("core", e, "CarSave.WriteSidecar"); _car.LogOnce("sidecar-write", "could not write the save sidecar: " + e.Message); }
    }

    private string SidecarPath(string profile) =>
        Path.Combine(Paths.ConfigPath, "oth." + _car.Id + "." + CarSidecar.FileKey(profile) + ".sidecar");

    // ---------------------------------------------------------------- loading

    /// <summary>After the game read a profile whose inventory is <paramref name="inv"/>: this car's sidecar, if any.</summary>
    public void AfterProfileLoaded(PlayerInventory inv, ProfileSave save)
    {
        if (inv == null || save == null) return;
        try
        {
            string key = CarSidecar.FileKey(save.Profile?.Name);
            if (_unreadable == key) _unreadable = null; // read again: decided anew below
            string path = SidecarPath(save.Profile?.Name);
            AtomicFile.Recover(path);
            if (!File.Exists(path)) return;
            string text;
            try { text = File.ReadAllText(path); }
            catch (Exception e)
            {
                DebugLog.Error("core", e, "CarSave.AfterProfileLoaded");
                _unreadable = key; // can't tell what is in it: leave it as it is
                _car.LogOnce("sidecar-read", "could not read the save sidecar (it is left as it is): " + e.Message);
                return;
            }
            var s = CarSidecar.Decode(text);
            if (s == null || !s.Usable)
            {
                // Damaged (cut short by a crash before writes were atomic, or edited): kept aside for a manual fix,
                // and the next save writes a fresh one instead of overwriting it.
                string aside = AtomicFile.SetAside(path);
                if (aside == null) _unreadable = key;
                _car.LogOnce("sidecar-bad", $"the save sidecar is damaged and was not restored; " +
                                            (aside != null ? "it was moved to " + Path.GetFileName(aside) : "it is left as it is") + ".");
                return;
            }
            if (s.FallbackId != 0) FallbackCar = s.FallbackId;
            if (!s.Owned && s.States.Count == 0) return;
            _toRestore = (inv, s);
            TryRestore();
        }
        catch (Exception e) { DebugLog.Error("core", e, "CarSave.AfterProfileLoaded"); _car.LogOnce("sidecar-read", "could not read the save sidecar: " + e.Message); }
    }

    /// <summary>Adds the sidecar's car back to the loaded inventory once the car is registered.</summary>
    public void TryRestore()
    {
        if (_toRestore == null || !_car.Ready || _disabled) return;
        var (inv, s) = _toRestore.Value;
        _toRestore = null;
        try
        {
            if (inv.ContainsItem(ItemId)) return; // already there (the game kept it, or restored twice)
            if (s.Owned)
            {
                if (s.InUse)
                    foreach (var b in inv.Items)
                    {
                        var vi = b?.TryCast<VehicleItem>();
                        if (vi != null && vi.InUse) vi.InUse = false;
                    }
                inv.AddVehicleItem(ItemId, s.MetersTravelled, 1, s.InUse, false);
            }
            int inUse = 0;
            foreach (var st in s.States)
            {
                inv.AddItem(st.ItemId, new VehicleState { VehicleItemId = ItemId, Count = st.Count, InUse = st.InUse }, false);
                if (st.InUse) inUse++;
            }
            _car.Info($"restored from the save sidecar: car {(s.Owned ? "owned" : "not owned")}{(s.InUse ? ", in use" : "")}, " +
                      $"{s.States.Count} part(s), {inUse} of them mounted.");
            if (DebugLog.IsVerbose)
                foreach (var st in s.States)
                    DebugLog.Verbose(_car.Id, $"sidecar part 0x{st.ItemId:X8}: count {st.Count}, {(st.InUse ? "mounted" : "not mounted")}");
        }
        catch (Exception e) { DebugLog.Error("core", e, "CarSave.TryRestore"); _car.LogOnce("sidecar-apply", "could not restore the car from the save sidecar: " + e.Message); }
    }

    /// <summary>
    /// The car is free: once it is registered, it is added to the player's inventory (owned, not in use) when it isn't
    /// there. A loaded save's sidecar goes first (it brings the odometer and parts back). Saves never keep it, so
    /// removing the mod loses nothing.
    /// </summary>
    public void Grant()
    {
        if (!_car.Ready || _disabled || _toRestore != null) return;
        try
        {
            var inv = PlayerInventoryManager.Instance?.Inventory;
            if (inv == null || inv.ContainsItem(ItemId)) return;
            inv.AddVehicleItem(ItemId, 0, 1, false, false);
            _car.Info("added to your cars (free).");
        }
        catch (Exception e) { DebugLog.Error("core", e, "CarSave.Grant"); _car.LogOnce("grant", "could not add the car to your inventory: " + e.Message); }
    }

    /// <summary>
    /// The car was switched off after it may have entered the inventory (sidecar restore or the free grant): it and its
    /// parts come out of the inventory again (the car shown in use goes back to the fallback), nothing more is restored
    /// or granted this session, and the sidecar file is left as it is, so a later session with the car working gets
    /// everything back.
    /// </summary>
    public void Disable()
    {
        if (_disabled) return;
        _disabled = true;
        _toRestore = null;
        try
        {
            var items = PlayerInventoryManager.Instance?.Inventory?.Items;
            if (items == null) return;
            // The same plan as for a save (CarSavePlan, unit-tested): the car and anything keyed by it come out, a part
            // item only when it held this car's states and nothing else, and the fallback car takes over "in use".
            var plan = CarSavePlan.Make(Model(items, out _), ItemId, _car.Drive.FallbackId(), ModdedCars.OtherItemIds(_car));
            if (!plan.IsEmpty) Apply(items, plan, undo: null);
        }
        catch (Exception e) { DebugLog.Error("core", e, "CarSave.Disable"); _car.LogOnce("disable", "could not take the car out of the inventory: " + e.Message); }
    }
}
