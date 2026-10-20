using System;
using System.Collections.Generic;
using System.IO;
using BepInEx;
using UnityEngine;

namespace OverTheHillPlus.Unreleased;

/// <summary>
/// Keeps the unreleased cars out of the game's save file, so a profile still loads after the mod is removed.
///
/// Why not leave them in: the save's inventory is read back by type with no catalog check
/// (BaseInventoryItemJsonConverter), but without this mod the catalog doesn't know the ids, and
/// OverlandNetworkRunner.SetupCar / SpawnPlayer then log "Failed to find car inventory item with id ..." and spawn no
/// car at all (RuntimePlayerInjector.SendRuntimePlayer even throws on IdToSO[id]). So the saved file must never have
/// one of ours, or one of their parts, in it.
///
/// How: OverlandSaver.SaveGame serialises PlayerInventoryManager.Instance.Inventory on the main thread
/// (JsonConvert.SerializeObject, then SaveJson). Around it (prefix / postfix) every entry with one of our ids, and
/// every part state that belongs to one of our cars, is taken out of the live inventory; when one of ours was in use,
/// a stock car the player owns is marked in use instead. The game writes that clean inventory, then everything is
/// put back exactly as it was. What was taken out goes to BepInEx/config/oth.unreleased.stash.txt (per profile) and
/// comes back into the inventory when the profile is loaded with the mod. If the postfix never runs (the save
/// threw), the next tick puts things back.
/// </summary>
internal static class SaveGuard
{
    private const string FileName = "oth.unreleased.stash.txt";

    private sealed class Held
    {
        public PlayerInventory Inventory;
        public readonly List<(int index, BaseInventoryItem item)> Items = new();
        public readonly List<(VehicleSpecificItem owner, VehicleState state)> States = new();
        public VehicleItem Fallback;
    }

    private static Held _held;
    /// <summary>Marked only by a finished restore: until then the stash file is never written (see RestoreGate).</summary>
    private static readonly RestoreGate Gate = new();
    private static float _nextTick;
    private static uint _lastStockInUse;

    private static string StashPath => Path.Combine(Paths.ConfigPath, FileName);

    // ---------------------------------------------------------------- around SaveGame

    internal static void BeforeSave()
    {
        try
        {
            PutBack(); // a previous save that never finished
            EnsureRestored(); // a save right after a load: read the stash back first, or it would be overwritten
            var inv = Inventory();
            if (inv?.Items == null) return;
            string profile = ProfileKey();
            // Held before anything changes: whatever TakeOut managed to change is undone by PutBack, even if it throws.
            var held = new Held { Inventory = inv };
            _held = held;
            var stash = new ProfileStash();
            TakeOut(inv, held, stash);
            // Only with the cars set up (the Brigand's and Cordillera's ids are known then) and the stash read back.
            if (UnreleasedCars.Ready && profile != null && Gate.MayWrite((long)inv.Pointer, profile))
                WriteStash(profile, stash);
            if (held.Items.Count == 0 && held.States.Count == 0 && held.Fallback == null)
            {
                _held = null;
                return;
            }
            DebugLog.Info(UnreleasedPlugin.ModId, $"save: kept {held.Items.Count} items and {held.States.Count} part states out of the file" + (held.Fallback != null ? $", fallback car {Describe(held.Fallback.ItemId)}" : ""));
            UnreleasedPlugin.Logger.LogInfo($"[Unreleased] save: kept {held.Items.Count} items and {held.States.Count} part states out of the file" +
                                            (held.Fallback != null ? $", the {Describe(held.Fallback.ItemId)} stands in as the car in use" : ""));
        }
        catch (Exception e)
        {
            // Not put back here: the save runs next and should still get whatever was taken out so far. AfterSave
            // (or the next tick, if the save throws too) puts it all back.
            UnreleasedPlugin.Logger.LogError("[Unreleased] could not fully clean the save (some of it may reach the file): " + e);
        }
    }

    internal static void AfterSave() => PutBack();

    /// <summary>
    /// Takes our entries out of <paramref name="inv"/>. Every change is recorded in <paramref name="held"/> right as it
    /// is made (never ahead of it), so <see cref="PutBack"/> undoes exactly what happened, also after a throw.
    /// </summary>
    private static void TakeOut(PlayerInventory inv, Held held, ProfileStash stash)
    {
        var items = inv.Items;
        var owned = new List<uint>();
        VehicleItem oursInUse = null, stockInUse = null;
        for (int i = 0; i < items.Count; i++)
        {
            var vi = items[i]?.TryCast<VehicleItem>();
            if (vi == null) continue;
            if (UnreleasedCars.IsOurCar(vi.ItemId)) { if (vi.InUse) oursInUse = vi; continue; }
            // Another mod's car (core's modded cars, in core's NonStockCars): not stock, so never our stand-in. Core's
            // own save filter takes it out of the file and shows its stock fallback in use instead.
            if (Core.Shared.NonStockCars.Contains(vi.ItemId)) continue;
            owned.Add(vi.ItemId);
            if (vi.InUse) stockInUse = vi;
        }
        if (stockInUse != null) _lastStockInUse = stockInUse.ItemId;

        var remove = new List<int>(); // indices of whole entries to take out, ascending
        for (int i = 0; i < items.Count; i++)
        {
            var item = items[i];
            if (item == null) continue;
            uint id = item.ItemId;
            var vehicle = item.TryCast<VehicleItem>();
            if (vehicle != null)
            {
                if (!UnreleasedCars.OurIds.Contains(id)) continue;
                remove.Add(i);
                stash.Items.Add(new StashItem
                {
                    Kind = StashKind.Vehicle, Id = id, Count = vehicle.Count, InUse = vehicle.InUse, IsNew = vehicle.IsNew,
                    Meters = vehicle.MetersTravelled, Tools = Tools(vehicle),
                });
                continue;
            }
            var part = item.TryCast<VehicleSpecificItem>();
            if (part != null)
            {
                if (TakeOutStates(part, held, stash, UnreleasedCars.OurIds.Contains(id))) remove.Add(i);
                continue;
            }
            var plain = item.TryCast<InventoryItem>();
            if (plain != null && UnreleasedCars.OurIds.Contains(id))
            {
                remove.Add(i);
                stash.Items.Add(new StashItem { Kind = StashKind.Plain, Id = id, Count = plain.Count, InUse = plain.InUse, IsNew = plain.IsNew });
            }
        }

        if (oursInUse != null && stockInUse == null)
        {
            uint fallback = UnreleasedRules.ChooseFallback(owned, UnreleasedCars.CarIds, _lastStockInUse);
            stash.FallbackId = fallback;
            if (fallback != 0)
            {
                for (int i = 0; i < items.Count; i++)
                {
                    var vi = items[i]?.TryCast<VehicleItem>();
                    if (vi == null || vi.ItemId != fallback) continue;
                    held.Fallback = vi; // was out of use (no stock car was in use): PutBack sets it back
                    vi.InUse = true;
                    break;
                }
            }
            else UnreleasedPlugin.LogOnce("nofallback", "the save has no stock car to put in use instead (the profile owns none)");
        }

        // From the back, so the indices still point at the right entries; each one is held once it is out.
        for (int k = remove.Count - 1; k >= 0; k--)
        {
            int index = remove[k];
            var item = items[index];
            items.RemoveAt(index);
            held.Items.Add((index, item));
        }
    }

    /// <summary>
    /// A part entry: its states for our cars come out (each held as it is removed). True when the whole entry is to be
    /// taken out instead (the part is ours, or every state is for our cars); its states then stay on it.
    /// </summary>
    private static bool TakeOutStates(VehicleSpecificItem part, Held held, ProfileStash stash, bool wholeItem)
    {
        var states = part.VehicleStates;
        if (states == null || states.Count == 0)
        {
            if (!wholeItem) return false;
            stash.Items.Add(new StashItem { Kind = StashKind.Part, Id = part.ItemId, IsNew = part.IsNew });
            return true;
        }
        var ours = new List<VehicleState>();
        foreach (var s in states)
            if (s != null && (wholeItem || UnreleasedCars.IsOurCar(s.VehicleItemId))) ours.Add(s);
        if (ours.Count == 0) return false;
        foreach (var s in ours)
            stash.Items.Add(new StashItem
            {
                Kind = StashKind.PartState, Id = part.ItemId, IsNew = part.IsNew, VehicleId = s.VehicleItemId, Count = s.Count, InUse = s.InUse,
            });
        if (wholeItem || ours.Count == states.Count) return true;
        foreach (var s in ours)
        {
            states.Remove(s);
            held.States.Add((part, s));
        }
        return false;
    }

    private static List<uint> Tools(VehicleItem v)
    {
        var list = new List<uint>();
        try { if (v.ToolsetIDs != null) foreach (uint t in v.ToolsetIDs) list.Add(t); } catch (Exception ex) { DebugLog.Error("unreleased", ex, "SaveGuard.Tools"); }
        return list;
    }

    /// <summary>Puts back what <see cref="BeforeSave"/> took out of the live inventory.</summary>
    private static void PutBack()
    {
        var held = _held;
        if (held == null) return;
        _held = null;
        try
        {
            var items = held.Inventory.Items;
            foreach (var (owner, state) in held.States) owner.VehicleStates?.Add(state);
            // Ascending original indices put any set of removed entries back in their old order.
            var back = new List<(int index, BaseInventoryItem item)>(held.Items);
            back.Sort((a, b) => a.index.CompareTo(b.index));
            foreach (var (index, item) in back) items.Insert(Math.Min(index, items.Count), item);
            if (held.Fallback != null) held.Fallback.InUse = false;
        }
        catch (Exception e) { UnreleasedPlugin.Logger.LogError("[Unreleased] could not put the cars back after a save: " + e); }
    }

    /// <summary>Marks only the vehicle <paramref name="id"/> in use (like picking it in the garage).</summary>
    internal static void SetOnlyInUse(Il2CppSystem.Collections.Generic.List<BaseInventoryItem> items, uint id)
    {
        for (int i = 0; i < items.Count; i++)
        {
            var vi = items[i]?.TryCast<VehicleItem>();
            if (vi != null) vi.InUse = vi.ItemId == id;
        }
    }

    // ---------------------------------------------------------------- after a load

    /// <summary>Every 0.25 s: put back anything a failed save left out, and read the stash back into a newly loaded profile.</summary>
    internal static void Tick()
    {
        if (_held != null) PutBack();
        if (Time.unscaledTime < _nextTick) return;
        _nextTick = Time.unscaledTime + 0.25f;
        EnsureRestored();
    }

    /// <summary>
    /// Reads the stash back into the loaded profile once (per profile and inventory object). Also called right before
    /// the game sends or spawns the player's car and before a save, so a load in the same frame is never missed.
    /// </summary>
    internal static void EnsureRestored()
    {
        if (!UnreleasedCars.Ready) return;
        try
        {
            var inv = Inventory();
            if (inv?.Items == null) return;
            string profile = ProfileKey();
            if (profile == null) return;
            long key = (long)inv.Pointer;
            if (!Gate.NeedsRestore(key, profile))
            {
                RememberStockInUse();
                return;
            }
            // Retried (every tick) until the file was read and every row applied; saves leave the stash alone till then.
            Gate.Attempt(key, profile, ReadStash, stash =>
            {
                try { Restore(inv, stash); }
                catch (Exception e)
                {
                    UnreleasedPlugin.LogOnce("restorefail", "could not put the stash back (tried again; the stash file is left alone): " + e);
                    throw;
                }
            });
        }
        catch (Exception e) { DebugLog.Error("unreleased", e, "SaveGuard.EnsureRestored"); UnreleasedPlugin.LogOnce("restore", "could not read the stash back: " + e); }
    }

    private static void RememberStockInUse()
    {
        uint id = CurrentVehicleId();
        if (id != 0 && !UnreleasedCars.IsOurCar(id)) _lastStockInUse = id;
    }

    /// <summary>The car in use on the profile (0 = none or no profile).</summary>
    internal static uint CurrentVehicleId()
    {
        try { return InventoryUtility.GetCurrentVehicle()?.ItemId ?? 0; } catch (Exception ex) { DebugLog.Error("unreleased", ex, "SaveGuard.CurrentVehicleId"); return 0; }
    }

    /// <summary>Puts this profile's stash rows back into the inventory (none when <paramref name="stash"/> is null).</summary>
    private static void Restore(PlayerInventory inv, ProfileStash stash)
    {
        if (stash == null || stash.Items.Count == 0) return;
        var items = inv.Items;
        int added = 0, states = 0, skipped = 0;
        uint currentInUse = 0;
        for (int i = 0; i < items.Count; i++)
        {
            var vi = items[i]?.TryCast<VehicleItem>();
            if (vi != null && vi.InUse) { currentInUse = vi.ItemId; break; }
        }
        var cat = PlayerInventoryManager.InventoryItemsCatalog;

        foreach (var s in stash.Items)
        {
            // A car the game now sells itself, or one that couldn't be set up here, stays in the stash (Merge keeps it).
            if (!UnreleasedRules.ShouldRestore(s, CarRegistered, id => cat?.IdToSO != null && cat.IdToSO.ContainsKey(id)))
            {
                skipped++;
                continue;
            }
            switch (s.Kind)
            {
                case StashKind.Vehicle:
                    if (Find(items, s.Id) != null) continue;
                    var v = new VehicleItem { ItemId = s.Id, Count = s.Count, InUse = false, IsNew = s.IsNew, MetersTravelled = s.Meters };
                    var tools = new Il2CppSystem.Collections.Generic.List<uint>();
                    foreach (uint t in s.Tools) tools.Add(t);
                    v.ToolsetIDs = tools;
                    items.Add(v);
                    added++;
                    break;
                case StashKind.Plain:
                    if (Find(items, s.Id) != null) continue;
                    items.Add(new InventoryItem { ItemId = s.Id, Count = s.Count, InUse = s.InUse, IsNew = s.IsNew });
                    added++;
                    break;
                case StashKind.Part:
                    if (Find(items, s.Id) != null) continue;
                    items.Add(new VehicleSpecificItem { ItemId = s.Id, IsNew = s.IsNew, VehicleStates = new Il2CppSystem.Collections.Generic.List<VehicleState>() });
                    added++;
                    break;
                case StashKind.PartState:
                    var part = Find(items, s.Id)?.TryCast<VehicleSpecificItem>();
                    if (part == null)
                    {
                        part = new VehicleSpecificItem { ItemId = s.Id, IsNew = s.IsNew, VehicleStates = new Il2CppSystem.Collections.Generic.List<VehicleState>() };
                        items.Add(part);
                    }
                    part.VehicleStates ??= new Il2CppSystem.Collections.Generic.List<VehicleState>();
                    bool has = false;
                    foreach (var st in part.VehicleStates)
                        if (st != null && st.VehicleItemId == s.VehicleId) { has = true; break; }
                    if (has) continue;
                    part.VehicleStates.Add(new VehicleState { VehicleItemId = s.VehicleId, Count = s.Count, InUse = s.InUse });
                    states++;
                    break;
            }
        }

        uint putInUse = UnreleasedRules.CarToPutInUse(stash, currentInUse, id => UnreleasedCars.Find(id) is { IsOn: true, Registered: true });
        if (putInUse != 0) SetOnlyInUse(items, putInUse);
        UnreleasedPlugin.Logger.LogInfo($"[Unreleased] profile loaded: {added} items and {states} part states back from the stash" +
                                        (skipped > 0 ? $" ({skipped} entries kept there: their car isn't set up here)" : "") +
                                        (putInUse != 0 ? $", the {Describe(putInUse)} is in use again" : ""));
    }

    /// <summary>Whether this game could have put a stash row back (else an earlier row is kept, see Merge).</summary>
    private static bool CouldRestore(StashItem item)
    {
        var cat = PlayerInventoryManager.InventoryItemsCatalog;
        return UnreleasedRules.ShouldRestore(item, CarRegistered, id => cat?.IdToSO != null && cat.IdToSO.ContainsKey(id));
    }

    private static bool CarRegistered(uint id) => UnreleasedCars.IsOurCar(id) && UnreleasedCars.Find(id) is { Registered: true };

    private static BaseInventoryItem Find(Il2CppSystem.Collections.Generic.List<BaseInventoryItem> items, uint id)
    {
        for (int i = 0; i < items.Count; i++)
            if (items[i] != null && items[i].ItemId == id) return items[i];
        return null;
    }

    // ---------------------------------------------------------------- helpers

    internal static PlayerInventory Inventory()
    {
        try { return PlayerInventoryManager.Instance?.Inventory; } catch (Exception ex) { DebugLog.Error("unreleased", ex, "SaveGuard.Inventory"); return null; }
    }

    /// <summary>The profile's name (the save file is &lt;name&gt;.json); null when no profile is loaded.</summary>
    private static string ProfileKey()
    {
        try { return OverlandSaver.CurrentProfileSave?.Profile?.Name; } catch (Exception ex) { DebugLog.Error("unreleased", ex, "SaveGuard.ProfileKey"); return null; }
    }

    /// <summary>The last stock car this player drove (the stand-in for saves and joins).</summary>
    internal static uint LastStockInUse => _lastStockInUse;

    private static string Describe(uint id) => UnreleasedCars.Find(id)?.Name ?? ("car " + id);

    /// <summary>All profiles' stashes; null when the file exists but can't be read (see <see cref="StashStore"/>).</summary>
    private static Dictionary<string, ProfileStash> ReadStash()
    {
        var all = StashStore.TryRead(() => File.Exists(StashPath), () => File.ReadAllText(StashPath));
        if (all == null) UnreleasedPlugin.LogOnce("readstash", "could not read " + FileName + ": it is left as it is");
        return all;
    }

    /// <summary>Writes this profile's stash, merged with what the file had; never over a file that can't be read.</summary>
    private static void WriteStash(string profile, ProfileStash now)
    {
        try
        {
            bool ok = StashStore.Update(profile, now, () => File.Exists(StashPath), () => File.ReadAllText(StashPath), text =>
            {
                string tmp = StashPath + ".tmp";
                File.WriteAllText(tmp, text);
                if (File.Exists(StashPath)) File.Replace(tmp, StashPath, null);
                else File.Move(tmp, StashPath);
            }, CouldRestore);
            if (!ok) UnreleasedPlugin.LogOnce("writestash", "could not read " + FileName + ", so it was not written (the cars of this save are only in memory)");
        }
        catch (Exception e) { UnreleasedPlugin.Logger.LogError("[Unreleased] could not write " + FileName + ": " + e.Message); }
    }
}
