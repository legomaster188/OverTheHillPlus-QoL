using System;
using System.Collections.Generic;
using Fusion;
using OverTheHillPlus.Core.Shared;
using OverTheHillPlus.Shared;
using UnityEngine;

namespace OverTheHillPlus.Traffic;

/// <summary>
/// Car models AI Traffic may spawn, and the spawn itself. A copy of Convoy AI's ConvoyCars (mods don't reference
/// each other). Spawning goes through the game's own CheatsManager.SpawnCar (loads "Prefabs/Cars/Car_&lt;Name&gt;",
/// Runner.Spawn, stock parts, engine off); SpawnCar keeps one car per model (it despawns an earlier one of the same
/// name through its SpawnedCars list), so each traffic car is taken off that list right away and despawned by us.
/// Cars locked behind a DLC you don't own are left out.
/// </summary>
internal static class TrafficCars
{
    private static readonly List<ItemCatalog.Item> Models = new();
    private static readonly Dictionary<uint, string> ResolvedName = new();
    private static readonly HashSet<uint> Unspawnable = new();
    private static float _nextRefresh;

    public static IReadOnlyList<ItemCatalog.Item> All
    {
        get
        {
            if (Models.Count == 0 && Time.unscaledTime >= _nextRefresh) Refresh();
            return Models;
        }
    }

    private static void Refresh()
    {
        _nextRefresh = Time.unscaledTime + 5f;
        Models.Clear();
        try
        {
            var cat = PlayerInventoryManager.InventoryItemsCatalog;
            foreach (var car in ItemCatalog.Cars())
            {
                if (Unspawnable.Contains(car.Id)) continue;
                bool allowed = true;
                try
                {
                    if (cat?.IdToSO != null && cat.IdToSO.ContainsKey(car.Id))
                    {
                        var so = cat.IdToSO[car.Id];
                        if (so != null) allowed = DlcManager.IsAllowed(so);
                    }
                }
                catch (Exception ex) { DebugLog.Error("traffic", ex, "TrafficCars.Refresh"); allowed = false; } // unknown: leave it out rather than show a paid car
                if (allowed) Models.Add(car);
            }
        }
        catch (Exception e) { DebugLog.Error("traffic", e, "TrafficCars.Refresh"); TrafficPlugin.Logger?.LogDebug("[Traffic] car list: " + e.Message); }
    }

    private static string PrefabName(ItemCatalog.Item car)
    {
        if (ResolvedName.TryGetValue(car.Id, out var known)) return known;
        string raw = car.RawName ?? "";
        // Checked first: a wrong name makes SpawnCar print a console error and throw.
        string name = CarPrefabs.Find(car.Id, raw, car.Name, NonStockCars.Contains(car.Id));
        if (name != null) return ResolvedName[car.Id] = name;
        Unspawnable.Add(car.Id);
        Models.Remove(car);
        TrafficPlugin.Logger?.LogWarning($"[Traffic] No car prefab found for '{raw}', left out of the model list.");
        return null;
    }

    /// <summary>Spawns a driverless car. Host only (callers check). Returns the network object or null + reason.</summary>
    public static NetworkObject Spawn(ItemCatalog.Item car, Vector3 pos, float yaw, out string error)
    {
        error = null;
        var cm = CheatsManager.Instance;
        if (cm == null) { error = "the game's spawn manager isn't loaded in this session"; return null; }
        if (car == null) { error = "no car model available"; return null; }
        string name = PrefabName(car);
        if (name == null) { error = $"the game has no prefab for {car.Name}"; return null; }
        // SpawnCar first despawns a car of that model it spawned before (GetAlreadySpawnedCar / DespawnCar): one the
        // host spawned with the game's own command or another mod would vanish. Skip the model this time instead.
        try
        {
            if (cm.GetAlreadySpawnedCar(name) != null) { error = $"a {car.Name} spawned by the host is out (left alone)"; return null; }
        }
        catch (Exception ex) { DebugLog.Error("traffic", ex, "TrafficCars.Spawn"); }
        NetworkObject obj;
        try { obj = cm.SpawnCar(name, pos, new Vector3(0f, yaw, 0f)); }
        catch (Exception e) { DebugLog.Error("traffic", e, "TrafficCars.Spawn"); error = e.Message; return null; }
        if (obj == null) { error = "the game didn't spawn it"; return null; }
        try { CheatsManager.SpawnedCars?.Remove(obj); } catch (Exception ex) { DebugLog.Error("traffic", ex, "TrafficCars.Spawn"); }
        return obj;
    }
}
