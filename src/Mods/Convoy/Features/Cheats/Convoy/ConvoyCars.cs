using System;
using System.Collections.Generic;
using Fusion;
using Il2CppInterop.Runtime;
using OverTheHillPlus.Convoy;
using UnityEngine;

namespace OverTheHillPlus.Features.Cheats.Convoy;

/// <summary>
/// Car models the convoy may spawn, and the spawn itself. A copy of the model list and spawn of Host Powers' track
/// builder (BuildCars): mods don't reference each other, so each keeps its own. Spawning goes through the game's own
/// CheatsManager.SpawnCar (loads "Prefabs/Cars/Car_&lt;Name&gt;", Runner.Spawn, stock parts, engine off). SpawnCar keeps
/// one car per model (it despawns an already spawned car of the same name first, found through its SpawnedCars list),
/// so each convoy car is taken off that list right away: several cars of one model can coexist and the convoy
/// despawns its own cars itself.
/// </summary>
internal static class ConvoyCars
{
    private static readonly List<ItemCatalog.Item> Models = new();
    private static readonly Dictionary<uint, string> ResolvedName = new();
    private static readonly HashSet<uint> Unspawnable = new();
    private static float _nextRefresh;

    /// <summary>Car models you may use: the catalog's cars without the ones locked behind a DLC you don't own.</summary>
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
                catch (Exception ex) { DebugLog.Error("convoy", ex, "ConvoyCars.Refresh"); allowed = false; } // unknown: leave it out rather than show a paid car
                if (allowed) Models.Add(car);
            }
        }
        catch (Exception e) { DebugLog.Error("convoy", e, "ConvoyCars.Refresh"); ConvoyPlugin.Logger.LogDebug("[Convoy] car list: " + e.Message); }
    }

    /// <summary>Prefab name the game accepts for this car ("Car_" + name under Resources/Prefabs/Cars), or null.</summary>
    private static string PrefabName(ItemCatalog.Item car)
    {
        if (ResolvedName.TryGetValue(car.Id, out var known)) return known;
        static string Cap(string s) => string.IsNullOrEmpty(s) ? s : char.ToUpperInvariant(s[0]) + s.Substring(1);
        string raw = car.RawName ?? "", pretty = car.Name ?? "";
        var candidates = new[] { Cap(pretty.Replace(" ", "")), Cap(raw.Replace(" ", "")), raw, pretty.Replace(" ", "") };
        foreach (var name in candidates)
        {
            if (string.IsNullOrEmpty(name)) continue;
            try
            {
                // Checked here first: a wrong name makes SpawnCar print a console error and throw.
                if (Resources.Load("Prefabs/Cars/Car_" + name, Il2CppType.Of<GameObject>()) != null)
                    return ResolvedName[car.Id] = name;
            }
            catch (Exception ex) { DebugLog.Error("convoy", ex, "ConvoyCars.PrefabName"); }
        }
        Unspawnable.Add(car.Id);
        Models.Remove(car);
        ConvoyPlugin.Logger.LogWarning($"[Convoy] No car prefab found for '{raw}', left out of the model list.");
        return null;
    }

    /// <summary>Spawns a driverless car. Host only (callers check). Returns the network object or null + reason.</summary>
    public static NetworkObject Spawn(ItemCatalog.Item car, Vector3 pos, float yaw, out string error)
    {
        error = null;
        var cm = CheatsManager.Instance;
        if (cm == null) { error = "the game's cheat manager isn't loaded in this session"; return null; }
        if (car == null) { error = "no car model available"; return null; }
        string name = PrefabName(car);
        if (name == null) { error = $"the game has no prefab for {car.Name}"; return null; }
        NetworkObject obj;
        try { obj = cm.SpawnCar(name, pos, new Vector3(0f, yaw, 0f)); }
        catch (Exception e) { DebugLog.Error("convoy", e, "ConvoyCars.Spawn"); error = e.Message; return null; }
        if (obj == null) { error = "the game didn't spawn it"; return null; }
        try { CheatsManager.SpawnedCars?.Remove(obj); } catch (Exception ex) { DebugLog.Error("convoy", ex, "ConvoyCars.Spawn"); }
        return obj;
    }
}
