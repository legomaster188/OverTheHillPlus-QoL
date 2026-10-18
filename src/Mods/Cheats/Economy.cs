using System;
using System.Collections.Generic;
using HarmonyLib;
using OverTheHillPlus.CheatsPlus;
using ItemType = InventoryItemScriptableObject.ItemType;

namespace OverTheHillPlus;

/// <summary>Pebbles (the game's money) and items. Inventory is per-player profile data, so this works in any session.</summary>
internal static class Economy
{
    /// <summary>True while we're granting something, so achievement hooks can ignore it.</summary>
    public static bool Granting;

    private static PlayerInventoryManager Manager => PlayerInventoryManager.Instance;

    public static int Balance
    {
        get
        {
            try { var m = Manager; return m == null || m.Inventory == null ? -1 : m.Inventory.GetItemsCount(m.MoneyItemId); }
            catch (Exception ex) { DebugLog.Error("cheats", ex, "Economy.Balance"); return -1; }
        }
    }

    public static string AddPebbles(int amount)
    {
        var m = Manager;
        if (m?.Inventory == null) return "Failed: inventory not loaded yet (load into a world or the garage).";
        if (amount == 0) return "";
        Grant(() =>
        {
            if (amount > 0) m.Inventory.AddItem(m.MoneyItemId, amount, false, false);
            else m.Inventory.TryRemoveItem(m.MoneyItemId, Math.Min(-amount, Balance));
        });
        DebugLog.Info(CheatsPlugin.ModId, $"pebbles {(amount > 0 ? "+" : "")}{amount:N0}: balance now {Balance:N0}");
        return $"Pebbles: {Balance:N0}";
    }

    public static string SetPebbles(int target) => AddPebbles(Math.Max(0, target) - Math.Max(0, Balance));

    public static string Give(ItemCatalog.Item item, int count = 1)
    {
        var m = Manager;
        if (m?.Inventory == null) return "Failed: inventory not loaded yet.";
        Grant(() =>
        {
            if (item.Type == ItemType.Vehicle) m.Inventory.AddVehicleItem(item.Id, 0, count, false, true);
            else m.Inventory.AddItem(item.Id, count, false, true);
        });
        DebugLog.Info(CheatsPlugin.ModId, $"item added: {item.Name} x{count} (id 0x{item.Id:X8}, {item.Type})");
        return $"Added {item.Name}";
    }

    public static bool Owns(ItemCatalog.Item item)
    {
        try { return Manager?.Inventory != null && Manager.Inventory.ContainsItem(item.Id); }
        catch (Exception ex) { DebugLog.Error("cheats", ex, "Economy.Owns"); return false; }
    }

    private static void Grant(Action action)
    {
        AdvantageFlag.MarkUsed();
        Granting = true;
        try { action(); }
        catch (Exception e) { CheatsPlugin.Logger.LogError("Inventory change failed: " + e); }
        finally { Granting = false; }
    }
}

[HarmonyPatch(typeof(AchievementManager), nameof(AchievementManager.OnInventoryItemAdded))]
internal static class AchievementInventoryGuard
{
    // Pebble/collection stats shouldn't count what the mod handed out.
    private static bool Prefix() => !Economy.Granting;
}
