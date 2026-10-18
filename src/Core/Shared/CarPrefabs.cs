using System;

namespace OverTheHillPlus.Shared;

/// <summary>Finds the prefab name the game's SpawnCar accepts for a catalog car.</summary>
public static class CarPrefabs
{
    /// <summary>The name N for which "Prefabs/Cars/Car_N" resolves in Addressables (where the game loads cars from; Resources
    /// doesn't have them), or null. <paramref name="trust"/>: skip the check. The car's CarInfo asset name comes first: it is what the game builds the address from.</summary>
    public static string Find(uint itemId, string rawName, string prettyName, bool trust = false)
    {
        string asset = null;
        try
        {
            var cat = PlayerInventoryManager.InventoryItemsCatalog;
            if (cat?.IdToSO != null && cat.IdToSO.ContainsKey(itemId)) asset = cat.IdToSO[itemId]?.name;
        }
        catch (Exception ex) { DebugLog.Error("core", ex, "CarPrefabs.Find"); }
        var names = CarPrefabNames.Candidates(asset, rawName, prettyName);
        // A car a mod adds to the catalog and serves to the game's cache itself (not through Addressables): its name is taken as it is.
        if (trust) return names.Count > 0 ? names[0] : null;
        foreach (var name in names)
            if (RuntimeAddressables.Resolves("Prefabs/Cars/Car_" + name)) return name;
        return null;
    }
}
