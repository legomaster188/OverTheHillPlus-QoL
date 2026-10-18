using System;
using System.Collections.Generic;

namespace OverTheHillPlus.UnlockAll;

/// <summary>
/// The Items section: tool slots and money, added with the game's own PlayerInventory.AddItem inside the DLC guard.
///
/// Findings (Cpp2IL of the updated game):
/// - CheatsController.UnlockAllToolSlots runs the AddItem command for "ToolSlot_1", "ToolSlot_2" and "ToolSlot_3";
///   that command looks the name up in InventoryItemsCatalog.NameToId and calls AddItem(id, count, false, false).
/// - Money is the item PlayerInventoryManager.MoneyItemId; AddItem(id, count, ...) adds count to an existing item's
///   Count (an int, without an overflow check), so the amount is capped to what still fits.
/// </summary>
internal static class ItemUnlocks
{
    private static PlayerInventory Inventory => PlayerInventoryManager.Instance?.Inventory;

    internal static string UnlockToolSlots() => Unlocker.Guarded("Tool slots", t =>
    {
        var inv = Inventory;
        var catalog = PlayerInventoryManager.InventoryItemsCatalog?.NameToId;
        if (inv == null || catalog == null) return;
        var names = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in catalog)
            if (kv.Key != null && kv.Key.StartsWith("toolslot", StringComparison.OrdinalIgnoreCase)) names[kv.Key] = kv.Value;
        var owned = new HashSet<uint>();
        foreach (uint id in names.Values)
            if (CarUnlocks.Item(id) != null) owned.Add(id);
        var ids = UnlockPlan.ToolSlots(names, owned);
        if (names.Count == 0) t.Message = "No tool slots found in the game's catalog.";
        else if (ids.Count == 0) t.Message = "All tool slots are unlocked already.";
        foreach (uint id in ids)
        {
            try
            {
                inv.AddItem(id, 1, false, false);
                t.Added++;
            }
            catch (Exception e)
            {
                DebugLog.Error("unlockall", e, "ItemUnlocks.UnlockToolSlots");
                if (!t.Failed.Contains("tool slots")) t.Failed.Add("tool slots");
                UnlockAllPlugin.Logger.LogWarning($"[UnlockAll] tool slot {id} failed: {e.GetBaseException().Message}");
            }
        }
    });

    /// <summary>The money on the profile, or null outside a world.</summary>
    internal static int? Money
    {
        get
        {
            try
            {
                var m = PlayerInventoryManager.Instance;
                var inv = m?.Inventory;
                return inv == null ? null : inv.GetItemsCount(m.MoneyItemId);
            }
            catch (Exception ex) { DebugLog.Error("unlockall", ex, "ItemUnlocks.Money"); return null; }
        }
    }

    internal static string AddMoney(int amount) => Unlocker.Guarded("Money", t =>
    {
        var m = PlayerInventoryManager.Instance;
        var inv = m?.Inventory;
        if (inv == null) return;
        int current = Money ?? 0;
        int add = UnlockPlan.MoneyToAdd(current, amount);
        if (add <= 0)
        {
            t.Message = "Your wallet is full: the game can't hold more money.";
            return;
        }
        inv.AddItem(m.MoneyItemId, add, false, false);
        int now = Money ?? current;
        if (now == current) return; // the DLC guard refused it: the status says so
        t.Added++;
        t.Message = add < amount ? $"Added {add:N0} (the most that fits). You have {now:N0}." : $"Added {add:N0}. You have {now:N0}.";
    });
}
