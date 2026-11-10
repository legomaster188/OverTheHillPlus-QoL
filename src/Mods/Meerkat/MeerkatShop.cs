using System;

namespace OverTheHillPlus.Meerkat;

/// <summary>Free play with prices on: the game's own pebbles in the player's inventory (outside a career only: a career
/// keeps its own wallet, and its upgrades are bought through it).</summary>
internal static class MeerkatShop
{
    private static PlayerInventory Inv => PlayerInventoryManager.Instance?.Inventory;

    internal static long Pebbles()
    {
        try
        {
            var m = PlayerInventoryManager.Instance;
            var inv = Inv;
            return m == null || inv == null ? 0 : inv.GetItemsCount(m.MoneyItemId);
        }
        catch (Exception e) { DebugLog.Error(MeerkatPlugin.ModId, e, "MeerkatShop.Pebbles"); return 0; }
    }

    /// <summary>Takes <paramref name="price"/> pebbles; false (and nothing taken) when there aren't enough.</summary>
    internal static bool Spend(long price)
    {
        if (price <= 0) return true;
        if (OverTheHillPlus.Api.Career.Active) return false;
        try
        {
            var m = PlayerInventoryManager.Instance;
            var inv = Inv;
            if (m == null || inv == null) return false;
            int have = inv.GetItemsCount(m.MoneyItemId);
            if (have < price || price > int.MaxValue) return false;
            return inv.TryRemoveItem(m.MoneyItemId, (int)price);
        }
        catch (Exception e) { DebugLog.Error(MeerkatPlugin.ModId, e, "MeerkatShop.Spend"); return false; }
    }
}
