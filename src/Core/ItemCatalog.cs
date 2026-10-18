using System;
using System.Collections.Generic;
using ItemType = InventoryItemScriptableObject.ItemType;

namespace OverTheHillPlus;

/// <summary>
/// Every item in the game's catalog (cars, upgrades, cosmetics, liveries, usable items), read once from
/// <c>PlayerInventoryManager.InventoryItemsCatalog</c> with readable names. Read only: nothing here changes an inventory.
/// </summary>
internal static class ItemCatalog
{
    public sealed class Item
    {
        public uint Id;
        public string Name;
        public string RawName;
        public ItemType Type;
        public int Price;
    }

    private static List<Item> _all;

    /// <summary>Every item except the money item (cached after the first successful read; empty before the catalog loads).</summary>
    public static IReadOnlyList<Item> All
    {
        get
        {
            if (_all != null) return _all;
            var m = PlayerInventoryManager.Instance;
            var cat = PlayerInventoryManager.InventoryItemsCatalog;
            if (cat?.IdToSO == null || m == null) return Array.Empty<Item>();
            var list = new List<Item>();
            foreach (var kv in cat.IdToSO)
            {
                var so = kv.Value;
                if (so == null || kv.Key == m.MoneyItemId) continue;
                string name = null;
                try { if (cat.IdToName.ContainsKey(kv.Key)) name = cat.IdToName[kv.Key]; } catch (Exception ex) { DebugLog.Error("core", ex, "ItemCatalog.All"); }
                list.Add(new Item { Id = kv.Key, Name = Pretty(name ?? so.name), RawName = name ?? so.name, Type = so.Type, Price = so.Price });
            }
            list.Sort((a, b) => a.Type != b.Type ? a.Type.CompareTo(b.Type) : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            Core.CorePlugin.Logger?.LogInfo($"Item catalog: {list.Count} items");
            return _all = list;
        }
    }

    /// <summary>All cars the game knows about.</summary>
    public static IEnumerable<Item> Cars()
    {
        foreach (var item in All)
            if (item.Type == ItemType.Vehicle) yield return item;
    }

    /// <summary>"VehicleUpgrade_RoofRack_T2" -> "Vehicle Upgrade Roof Rack T2".</summary>
    private static string Pretty(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return "?";
        var s = raw.Replace('_', ' ').Trim();
        var sb = new System.Text.StringBuilder(s.Length + 8);
        for (int i = 0; i < s.Length; i++)
        {
            if (i > 0 && char.IsUpper(s[i]) && char.IsLower(s[i - 1])) sb.Append(' ');
            sb.Append(s[i]);
        }
        return sb.ToString();
    }
}
