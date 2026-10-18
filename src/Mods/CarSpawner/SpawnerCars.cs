using System;
using System.Collections.Generic;
using Fusion;
using Il2CppInterop.Runtime;
using OverTheHillPlus.Cars;
using OverTheHillPlus.Core.Shared;
using OverTheHillPlus.Shared;
using UnityEngine;

namespace OverTheHillPlus.CarSpawner;

/// <summary>
/// The cars the menu offers, and spawning, driving and removing them. Stock cars come from the game's item catalog
/// (cars locked behind a DLC you don't own are left out); modded cars from core's car framework
/// (<see cref="ModdedCars"/>), so a car registered there shows up without this mod knowing it. Spawning is the game's
/// own CheatsManager.SpawnCar (loads "Prefabs/Cars/Car_&lt;Name&gt;", Runner.Spawn, stock parts, engine off): the
/// framework serves a modded car's prefab through Addressables under that same name. SpawnCar keeps one car per model
/// (it despawns an earlier one of the same name through its SpawnedCars list), so each car we spawn is taken off that
/// list right away and despawned by us; a car of that model the host spawned some other way steps off the list while
/// we spawn and goes back after. "Drive it" is the garage's own "change car where it stands" request.
/// </summary>
internal static class SpawnerCars
{
    private const float MaxSpeedToSwap = 1.5f;

    private static List<CarChoice> _stock;
    private static readonly HashSet<uint> Unspawnable = new();
    private static List<CarChoice> _all = new();
    private static float _nextRefresh;
    private static readonly List<NetworkObject> Spawned = new();

    /// <summary>Every car the menu can offer now (modded first); refreshed every couple of seconds.</summary>
    public static IReadOnlyList<CarChoice> All
    {
        get
        {
            if (Time.unscaledTime >= _nextRefresh) Refresh();
            return _all;
        }
    }

    /// <summary>Changes whenever <see cref="All"/> got another list (the menu's filtered copy is rebuilt then).</summary>
    public static int Version { get; private set; }

    private static void Refresh()
    {
        _nextRefresh = Time.unscaledTime + 2f;
        try
        {
            var modded = new List<CarChoice>();
            foreach (var c in ModdedCars.All)
                if (c.Ready) modded.Add(new CarChoice { ItemId = c.Def.ItemId, Display = c.Def.DisplayName, Prefab = c.Def.CarName, Modded = true });
            _stock ??= BuildStock();
            var merged = SpawnerLogic.Merge(modded, _stock);
            if (SameList(merged, _all)) return;
            _all = merged;
            Version++;
            DebugLog.Info(CarSpawnerPlugin.ModId, $"car list: {merged.Count} car(s), {modded.Count} modded");
        }
        catch (Exception e) { DebugLog.Error(CarSpawnerPlugin.ModId, e, "SpawnerCars.Refresh"); }
    }

    private static bool SameList(List<CarChoice> a, List<CarChoice> b)
    {
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
            if (a[i].Prefab != b[i].Prefab || a[i].Modded != b[i].Modded) return false;
        return true;
    }

    /// <summary>The game's catalog cars that have a prefab (null while the catalog isn't loaded: tried again later).</summary>
    private static List<CarChoice> BuildStock()
    {
        var list = new List<CarChoice>();
        var cat = PlayerInventoryManager.InventoryItemsCatalog;
        int seen = 0;
        foreach (var car in ItemCatalog.Cars())
        {
            seen++;
            if (ModdedCars.IsModdedItem(car.Id) || Unspawnable.Contains(car.Id)) continue; // modded ones come from the framework
            bool allowed = true;
            try
            {
                if (cat?.IdToSO != null && cat.IdToSO.ContainsKey(car.Id))
                {
                    var so = cat.IdToSO[car.Id];
                    if (so != null) allowed = DlcManager.IsAllowed(so);
                }
            }
            catch (Exception ex) { DebugLog.Error(CarSpawnerPlugin.ModId, ex, "SpawnerCars.BuildStock"); allowed = false; } // unknown: leave it out rather than offer a paid car
            if (!allowed) continue;
            string name = PrefabName(car);
            if (name == null) continue;
            list.Add(new CarChoice { ItemId = car.Id, Display = car.Name, Prefab = name, Modded = NonStockCars.Contains(car.Id) });
        }
        // Not loaded yet: don't keep an empty list for the session.
        return seen == 0 ? null : list;
    }

    private static string PrefabName(ItemCatalog.Item car)
    {
        // Checked first: a wrong name makes SpawnCar print a console error and throw. The unreleased stand-ins are
        // given to the game's cache by their mod when it is needed, so they are not checked.
        string name = CarPrefabs.Find(car.Id, car.RawName, car.Name, NonStockCars.Contains(car.Id));
        if (name != null) return name;
        Unspawnable.Add(car.Id);
        CarSpawnerPlugin.Logger?.LogWarning($"[CarSpawner] No car prefab found for '{car.RawName}', left out of the list.");
        return null;
    }

    // ---------------------------------------------------------------- rules

    /// <summary>Null when cars can be spawned now, otherwise why not.</summary>
    public static string BlockReason()
    {
        if (GameState.Runner == null || GameState.LocalCar == null) return "Load into a world first.";
        if (!GameState.IsAuthority) return "Only solo or the host can spawn cars.";
        if (CheatsManager.Instance == null) return "The game's spawn manager isn't loaded in this session.";
        return null;
    }

    /// <summary>Null when the player can switch cars now (same rules as the garage: authority, stopped).</summary>
    public static string DriveBlockReason()
    {
        var why = BlockReason();
        if (why != null) return why;
        if (GameState.LocalPlayer == null || SpawnManager.Instance == null) return "Load into a world first.";
        try
        {
            var rb = GameState.LocalCar.GetComponent<Rigidbody>();
            if (rb != null && rb.velocity.magnitude > MaxSpeedToSwap) return "Stop the car first.";
        }
        catch (Exception ex) { DebugLog.Error(CarSpawnerPlugin.ModId, ex, "SpawnerCars.DriveBlockReason"); }
        return null;
    }

    public static int Count
    {
        get
        {
            Prune();
            return Spawned.Count;
        }
    }

    private static void Prune()
    {
        for (int i = Spawned.Count - 1; i >= 0; i--)
        {
            bool alive;
            try { alive = Spawned[i] != null && Spawned[i].IsValid; }
            catch (Exception ex) { DebugLog.Error(CarSpawnerPlugin.ModId, ex, "SpawnerCars.Prune"); alive = false; }
            if (!alive) Spawned.RemoveAt(i);
        }
    }

    // ---------------------------------------------------------------- actions

    /// <summary>Spawns the car on the ground in front of the player, facing the same way. Returns a message for the status line.</summary>
    public static string Spawn(CarChoice car)
    {
        var blocked = BlockReason();
        if (blocked != null) return "Blocked: " + blocked;
        if (car == null) return "Failed: no car chosen.";
        try
        {
            var me = GameState.LocalCar;
            var t = me.cachedTransform != null ? me.cachedTransform : me.transform;
            float yaw = t.eulerAngles.y;
            var p = t.position;
            var (wx, wz) = SpawnerLogic.Ahead(p.x, p.z, yaw, SpawnerLogic.DefaultAhead);
            var wanted = new Vector3(wx, p.y, wz);

            // Other cars (players', ours) are kept clear of; SafeSpot also refuses ground with something solid in the way.
            var ctx = new SafeSpot.Context();
            foreach (var fp in GameState.FusionPlayers)
            {
                try
                {
                    var other = fp?.vehicleBase;
                    if (other != null) ctx.Occupied.Add(other.transform.position);
                }
                catch (Exception ex) { DebugLog.Error(CarSpawnerPlugin.ModId, ex, "SpawnerCars.Spawn"); }
            }
            Prune();
            foreach (var o in Spawned)
            {
                try { ctx.Occupied.Add(o.transform.position); } catch (Exception ex) { DebugLog.Error(CarSpawnerPlugin.ModId, ex, "SpawnerCars.Spawn"); }
            }
            var spot = SafeSpot.Find(wanted, yaw, ctx);
            if (!spot.Ok)
            {
                DebugLog.Info(CarSpawnerPlugin.ModId, $"{car.Display}: no safe spot ahead: {spot.Why}");
                return $"Failed: no safe spot in front of you ({spot.Why}). Move to open ground and try again.";
            }

            var obj = SpawnAt(car, spot.Ground + Vector3.up * SpawnerLogic.DropHeight, new Vector3(0f, spot.Yaw, 0f), out string error);
            if (obj == null)
            {
                CarSpawnerPlugin.Logger?.LogWarning($"[CarSpawner] couldn't spawn {car.Display}: {error}");
                return $"Failed: {error}.";
            }
            Spawned.Add(obj);
            AdvantageFlag.MarkUsed();
            DebugLog.Info(CarSpawnerPlugin.ModId, $"spawned {car.Display} ({car.Prefab}{(car.Modded ? ", modded" : "")}) at {spot.Ground.x:0},{spot.Ground.z:0}, {spot.Offset:0.0} m off the wanted spot; {Spawned.Count} out");
            return $"Spawned {car.Display} in front of you.";
        }
        catch (Exception e)
        {
            DebugLog.Error(CarSpawnerPlugin.ModId, e, "SpawnerCars.Spawn");
            CarSpawnerPlugin.Logger?.LogWarning("[CarSpawner] spawn: " + e);
            return "Failed: " + e.Message;
        }
    }

    /// <summary>Spawns the car at a spot the player picked (<see cref="SpawnerPlace"/>). Returns a message for the status line.</summary>
    public static string SpawnPlaced(CarChoice car, Vector3 pos, Vector3 euler)
    {
        var blocked = BlockReason();
        if (blocked != null) return "Blocked: " + blocked;
        try
        {
            var obj = SpawnAt(car, pos, euler, out string error);
            if (obj == null)
            {
                CarSpawnerPlugin.Logger?.LogWarning($"[CarSpawner] couldn't spawn {car.Display}: {error}");
                return $"Failed: {error}.";
            }
            Spawned.Add(obj);
            AdvantageFlag.MarkUsed();
            DebugLog.Info(CarSpawnerPlugin.ModId, $"placed {car.Display} ({car.Prefab}{(car.Modded ? ", modded" : "")}) at {pos.x:0},{pos.z:0}; {Spawned.Count} out");
            return $"Spawned {car.Display}.";
        }
        catch (Exception e)
        {
            DebugLog.Error(CarSpawnerPlugin.ModId, e, "SpawnerCars.SpawnPlaced");
            return "Failed: " + e.Message;
        }
    }

    private static NetworkObject SpawnAt(CarChoice car, Vector3 pos, Vector3 euler, out string error)
    {
        error = null;
        var cm = CheatsManager.Instance;
        // A car of this model that the host spawned some other way: SpawnCar would despawn it, so it steps aside.
        NetworkObject other = null;
        try { other = cm.GetAlreadySpawnedCar(car.Prefab); }
        catch (Exception ex) { DebugLog.Error(CarSpawnerPlugin.ModId, ex, "SpawnerCars.SpawnAt"); }
        bool hidden = false;
        try { if (other != null) hidden = CheatsManager.SpawnedCars.Remove(other); }
        catch (Exception ex) { DebugLog.Error(CarSpawnerPlugin.ModId, ex, "SpawnerCars.SpawnAt"); }
        if (other != null && !hidden) { error = $"a {car.Display} spawned another way is out (left alone)"; return null; }

        NetworkObject obj = null;
        try
        {
            CrashTrail.Mark("carspawner: SpawnCar " + car.Prefab);
            obj = cm.SpawnCar(car.Prefab, pos, euler);
            if (obj == null) error = "the game didn't spawn it";
        }
        catch (Exception e) { DebugLog.Error(CarSpawnerPlugin.ModId, e, "SpawnerCars.SpawnAt"); error = e.Message; }
        finally
        {
            try
            {
                if (obj != null) CheatsManager.SpawnedCars?.Remove(obj);
                if (hidden) CheatsManager.SpawnedCars.Add(other);
            }
            catch (Exception ex) { DebugLog.Error(CarSpawnerPlugin.ModId, ex, "SpawnerCars.SpawnAt"); }
        }
        return obj;
    }

    /// <summary>Switches the player into the car where they stand (the garage's own request). Returns a message.</summary>
    public static string Drive(CarChoice car)
    {
        var blocked = DriveBlockReason();
        if (blocked != null) return "Blocked: " + blocked;
        if (car == null || car.ItemId == 0) return "Failed: no car chosen.";
        try
        {
            AdvantageFlag.MarkUsed();
            var modded = ModdedCars.ByItem(car.ItemId);
            // A modded car has its own path: it remembers the stock car you leave for saves and joins.
            if (modded != null) return modded.Drive.DriveIt();
            var load = CarLoadout.For(car.ItemId);
            CrashTrail.Mark("carspawner: change car");
            DebugLog.Info(CarSpawnerPlugin.ModId, $"change car to {car.Display} 0x{car.ItemId:X8}");
            SpawnManager.Instance.RPC_ChangeCarAtCurrentPosition(GameState.LocalPlayer.Value, car.ItemId, load.Bits, load.LiveryModId, load.LiverySubId);
            return $"Switching to {car.Display}...";
        }
        catch (Exception e)
        {
            DebugLog.Error(CarSpawnerPlugin.ModId, e, "SpawnerCars.Drive");
            return "Failed: " + e.Message;
        }
    }

    /// <summary>Despawns every car this mod spawned (those already gone are skipped).</summary>
    public static string RemoveAll()
    {
        Prune();
        if (Spawned.Count == 0) return "No spawned cars to remove.";
        int n = 0;
        var runner = GameState.Runner;
        foreach (var o in Spawned)
        {
            try
            {
                if (runner != null && o.HasStateAuthority) { runner.Despawn(o); n++; }
            }
            catch (Exception ex) { DebugLog.Error(CarSpawnerPlugin.ModId, ex, "SpawnerCars.RemoveAll"); }
        }
        int total = Spawned.Count;
        Spawned.Clear();
        DebugLog.Info(CarSpawnerPlugin.ModId, $"removed: despawned {n} of {total} spawned car(s)");
        return $"Removed {n} spawned car(s).";
    }
}
