using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace OverTheHillPlus.UnlockAll;

/// <summary>
/// Keeps paid DLC out of the unlocks. While <see cref="Active"/> (only during one of our unlock commands), the four
/// PlayerInventory adders the game's unlock commands use refuse any item whose catalog entry needs a DLC, owned or
/// not; for a vehicle part the vehicle it belongs to is checked too. Outside our unlocks the adders run untouched, so
/// buying and the game's own DLC handling are unaffected. Unknown items count as DLC: nothing paid slips through.
/// </summary>
internal static class DlcGuard
{
    /// <summary>True while one of our unlock commands runs.</summary>
    internal static bool Active { get; private set; }

    /// <summary>All four adders are patched; the unlocks stay off otherwise.</summary>
    internal static bool Installed { get; private set; }

    private static readonly HashSet<uint> Skipped = new();

    internal static bool Install(Harmony harmony)
    {
        int ok = 0;
        ok += Patch(harmony, "AddItem", new[] { typeof(uint), typeof(VehicleState), typeof(bool) }, nameof(PartPrefix));
        ok += Patch(harmony, "AddItem", new[] { typeof(InventoryItemScriptableObject), typeof(int), typeof(bool), typeof(bool) }, nameof(ObjectPrefix));
        ok += Patch(harmony, "AddItem", new[] { typeof(uint), typeof(int), typeof(bool), typeof(bool) }, nameof(IdPrefix));
        ok += Patch(harmony, "AddVehicleItem", new[] { typeof(uint), typeof(int), typeof(int), typeof(bool), typeof(bool) }, nameof(IdPrefix));
        Installed = ok == 4;
        return Installed;
    }

    private static int Patch(Harmony harmony, string name, Type[] args, string prefix)
    {
        try
        {
            var target = AccessTools.Method(typeof(PlayerInventory), name, args);
            if (target == null)
            {
                UnlockAllPlugin.Logger.LogError($"[UnlockAll] DLC guard: PlayerInventory.{name}({args.Length} args) not found.");
                return 0;
            }
            harmony.Patch(target, prefix: new HarmonyMethod(typeof(DlcGuard), prefix));
            return 1;
        }
        catch (Exception e)
        {
            UnlockAllPlugin.Logger.LogError($"[UnlockAll] DLC guard on PlayerInventory.{name} failed: {e.GetBaseException().Message}");
            return 0;
        }
    }

    /// <summary>Runs <paramref name="unlock"/> with the guard on; returns how many distinct DLC items it refused.</summary>
    internal static int Run(Action unlock)
    {
        Skipped.Clear();
        Active = true;
        try { unlock(); }
        finally { Active = false; }
        return Skipped.Count;
    }

    // ---------------------------------------------------------------- prefixes (false = the item is not added)

    private static bool PartPrefix(uint vehicleSpecificItemId, VehicleState state)
    {
        if (!Active) return true;
        if (IsDlc(vehicleSpecificItemId)) return Skip(vehicleSpecificItemId);
        uint vehicle = 0;
        try { vehicle = state?.VehicleItemId ?? 0; } catch (Exception ex) { DebugLog.Error("unlockall", ex, "DlcGuard.PartPrefix"); }
        return vehicle == 0 || !IsDlc(vehicle) || Skip(vehicle);
    }

    private static bool ObjectPrefix(InventoryItemScriptableObject inventoryItem)
    {
        if (!Active) return true;
        if (!IsDlc(inventoryItem)) return true;
        uint id = 0;
        try { id = inventoryItem?.ItemId ?? 0; } catch (Exception ex) { DebugLog.Error("unlockall", ex, "DlcGuard.ObjectPrefix"); }
        return Skip(id);
    }

    private static bool IdPrefix(uint itemId) => !Active || !IsDlc(itemId) || Skip(itemId);

    private static bool Skip(uint id)
    {
        Skipped.Add(id);
        return false;
    }

    // ---------------------------------------------------------------- the check

    internal static bool IsDlc(uint itemId)
    {
        try
        {
            var dict = PlayerInventoryManager.InventoryItemsCatalog?.IdToSO;
            if (dict == null || !dict.ContainsKey(itemId)) return true;
            return IsDlc(dict[itemId]);
        }
        catch (Exception ex) { DebugLog.Error("unlockall", ex, "DlcGuard.IsDlc"); return true; }
    }

    internal static bool IsDlc(InventoryItemScriptableObject so)
    {
        try
        {
            if (so == null) return true;
            return so.RequiredDlc != DlcId.None || DlcManager.GetRequiredDlc(so) != DlcId.None;
        }
        catch (Exception ex) { DebugLog.Error("unlockall", ex, "DlcGuard.IsDlc"); return true; }
    }

    // ---------------------------------------------------------------- startup check

    private static float _nextCheck;

    /// <summary>
    /// Once the item catalog is loaded, counts its DLC-gated entries and logs them, so the log shows the guard is
    /// working before anything is unlocked. Unhooks itself when done.
    /// </summary>
    internal static void StartupCheckTick()
    {
        if (Time.unscaledTime < _nextCheck) return;
        _nextCheck = Time.unscaledTime + 2f;
        try
        {
            var dict = PlayerInventoryManager.InventoryItemsCatalog?.IdToSO;
            if (dict == null || dict.Count == 0) return;
            int dlc = 0;
            foreach (var kv in dict)
                if (IsDlc(kv.Value)) dlc++;
            CoreBehaviour.AfterFeatureTicks -= StartupCheckTick;
            UnlockAllPlugin.Logger.LogInfo($"[UnlockAll] skipped {dlc} DLC items (startup check: {dlc} of {dict.Count} catalog " +
                                           $"entries need a DLC and are never unlocked; guard {(Installed ? "on" : "NOT installed")})");
        }
        catch (Exception e)
        {
            DebugLog.Error("unlockall", e, "DlcGuard.StartupCheckTick");
            CoreBehaviour.AfterFeatureTicks -= StartupCheckTick;
            UnlockAllPlugin.Logger.LogWarning("[UnlockAll] startup DLC check failed: " + e.Message);
        }
    }
}
