using System;
using Fusion;
using OverTheHillPlus.Core.Shared;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace OverTheHillPlus.Cars;

// After the namespace line on purpose: the game has its own global ModManager (mod.io).
using OverTheHillPlus.Core.Manager;

/// <summary>
/// Getting into a modded car from its MODS page (the garage works too, see <see cref="CarCatalog"/>): the game's own
/// "change car where it stands" request (SpawnManager.RPC_ChangeCarAtCurrentPosition, the garage's path), sent to
/// whoever runs the session. Only while stopped, and as a guest only when the host runs the car too. The stock car you
/// leave is remembered as the fallback (the save shows it in use, guests join in it where the car isn't run).
/// <para>Join data: a modded guest in a session that doesn't run the car never asks for it; its join data names the
/// fallback car with that car's own mounted upgrades and livery (<see cref="BeforeSerialize"/>). The join gate itself is
/// core's (NetPrefabRegistry).</para>
/// </summary>
internal sealed class CarDrive
{
    private const float MaxSpeedToSwap = 1.5f;
    private readonly RuntimeCar _car;
    private float _nextTrack;

    public CarDrive(RuntimeCar car) { _car = car; }

    /// <summary>The garage may list the car here: ready, wanted, and drivable in this session.</summary>
    public bool Listable => _car.Ready && _car.Config.ShowInGarage != null && _car.Config.ShowInGarage.Value && SessionAllows;

    /// <summary>Offline, solo and hosting always; as a guest only when the host runs the car.</summary>
    public bool SessionAllows => GameState.IsAuthority || ModManager.ServerModActive(_car.Id);

    public bool Driving
    {
        get
        {
            try
            {
                var car = GameState.LocalCar;
                return car != null && _car.Catalog.IsThis(car.GetComponent<VehicleCustomizationController>());
            }
            catch (Exception ex) { DebugLog.Error("core", ex, "CarDrive.Driving"); return false; }
        }
    }

    /// <summary>Null when the button can be used, otherwise why not (shown next to it).</summary>
    public string Blocker()
    {
        if (!_car.Ready) return _car.FailedReason != null || ModManager.FailedReason(_car.Id) != null ? "Off" : "Loading";
        if (GameState.LocalPlayer == null || SpawnManager.Instance == null || GameState.LocalCar == null) return "Load into a world";
        if (!SessionAllows) return "Not on this server";
        try
        {
            var rb = GameState.LocalCar.GetComponent<Rigidbody>();
            if (rb != null && rb.velocity.magnitude > MaxSpeedToSwap) return "Stop first";
        }
        catch (Exception ex) { DebugLog.Error("core", ex, "CarDrive.Blocker"); }
        return null;
    }

    public string DriveIt()
    {
        string why = Blocker();
        if (why != null) return why + ".";
        if (Driving) return $"You are in the {_car.Def.CarName} already.";
        RememberCurrentCar();
        return Swap(_car.Def.ItemId) ?? $"Switching to the {_car.Def.CarName}…";
    }

    public string BackToMyCar()
    {
        string why = Blocker();
        if (why != null) return why + ".";
        if (!Driving) return $"You are not in the {_car.Def.CarName}.";
        uint fb = FallbackId();
        if (fb == 0) return "No other car to switch to.";
        return Swap(fb) ?? "Switching back…";
    }

    private string Swap(uint carId)
    {
        try
        {
            var sm = SpawnManager.Instance;
            var me = GameState.LocalPlayer;
            if (sm == null || me == null) return "Load into a world first.";
            // With the car's own mounted upgrades and livery, as the game's join data sends them.
            var load = CarLoadout.For(carId);
            CrashTrail.Mark(_car.Id + ": change car");
            DebugLog.Info(_car.Id, $"change car to 0x{carId:X8} (livery {load.LiveryModId}/{load.LiverySubId})");
            sm.RPC_ChangeCarAtCurrentPosition(me.Value, carId, load.Bits, load.LiveryModId, load.LiverySubId);
            return null;
        }
        catch (Exception e)
        {
            DebugLog.Error(_car.Id, e, "change car");
            return "Failed: " + e.Message;
        }
    }

    /// <summary>The car used instead of the modded car: the last stock car you drove, else the first stock car you own,
    /// else the garage base car. 0 when there is none. Never a non-stock car (core's <see cref="NonStockCars"/>: the
    /// modded cars and the cars other mods add), so a host that can't spawn it is never sent one.</summary>
    public uint FallbackId()
    {
        uint saved = _car.Save.FallbackCar;
        if (saved != 0 && !IsNonStock(saved)) return saved;
        try
        {
            var owned = InventoryUtility.GetOwnedVehicles();
            if (owned != null)
                foreach (var v in owned)
                    if (v != null && !IsNonStock(v.ItemId)) return v.ItemId;
        }
        catch (Exception ex) { DebugLog.Error("core", ex, "CarDrive.FallbackId"); }
        return _car.Catalog.Base != null ? _car.Catalog.Base.ItemId : 0;
    }

    private static bool IsNonStock(uint id) => ModdedCars.IsModdedItem(id) || NonStockCars.Contains(id);

    private void RememberCurrentCar()
    {
        try
        {
            var vcc = GameState.LocalCar?.GetComponent<VehicleCustomizationController>();
            var ci = vcc?.currentCarInfo;
            if (ci != null && !IsNonStock(ci.ItemId)) _car.Save.FallbackCar = ci.ItemId;
        }
        catch (Exception ex) { DebugLog.Error("core", ex, "CarDrive.RememberCurrentCar"); }
    }

    /// <summary>Once a second: remembers the stock car you are in as the fallback.</summary>
    public void Tick()
    {
        if (!_car.Ready || Time.unscaledTime < _nextTrack) return;
        _nextTrack = Time.unscaledTime + 1f;
        RememberCurrentCar();
    }

    /// <summary>A guest's join data: the fallback car (and its loadout) instead of this car when the session doesn't
    /// run it here.</summary>
    public void BeforeSerialize(RuntimePlayer p)
    {
        if (p == null || p.CarItemId != _car.Def.ItemId) return;
        NetworkRunner r = null;
        try { r = OverlandNetworkRunner.Instance?.NetworkRunner; } catch (Exception ex) { DebugLog.Error("core", ex, "CarDrive.BeforeSerialize"); }
        bool guest = r != null && !r.IsServer && !r.IsSinglePlayer;
        if (!guest || (_car.Ready && ModManager.ServerModActive(_car.Id))) return;
        uint fb = FallbackId();
        if (fb == 0) return;
        var load = CarLoadout.For(fb);
        p.CarItemId = fb;
        p.EnabledUpgrades = new Il2CppStructArray<ulong>(load.Words);
        p.EquippedModLiveryModId = load.LiveryModId;
        p.EquippedModLiverySubId = load.LiverySubId;
        _car.Info($"this session doesn't run the {_car.Def.CarName}: joining in your other car.");
    }
}

/// <summary>A car's mounted upgrades and equipped mod livery, read the way the game builds a player's join data
/// (RuntimePlayerInjector.SendRuntimePlayer: GetAllMountedParts, then Car.GetMountedUpgradesBitSet).</summary>
internal readonly struct CarLoadout
{
    public readonly BitSet256 Bits;
    public readonly ulong[] Words;
    public readonly long LiveryModId;
    public readonly int LiverySubId;

    private CarLoadout(BitSet256 bits, long modId, int subId)
    {
        Bits = bits;
        Words = new ulong[4];
        for (int i = 0; i < 256; i++)
            if (bits.IsSet(i)) Words[i >> 6] |= 1UL << (i & 63);
        LiveryModId = modId;
        LiverySubId = subId;
    }

    /// <summary>The loadout of car <paramref name="carId"/> (empty when it can't be read: stock parts, no livery).</summary>
    public static CarLoadout For(uint carId)
    {
        var bits = new BitSet256();
        long modId = 0;
        int subId = 0;
        try
        {
            var modded = ModdedCars.ByItem(carId);
            CarInfo ci = null;
            if (modded != null) ci = modded.Catalog.Info;
            else if (PlayerInventoryManager.ItemScriptableObjectLookup(carId, out var so)) ci = so?.TryCast<CarInfo>();
            if (ci != null)
            {
                var parts = InventoryUtility.GetAllMountedParts(ci);
                if (parts != null) bits = new Car(ci, parts).GetMountedUpgradesBitSet();
                bool modsAllowed = false;
                try { modsAllowed = OverlandNetworkRunner.Instance != null && OverlandNetworkRunner.Instance.ModsAllowed; } catch (Exception ex) { DebugLog.Error("core", ex, "CarLoadout.For"); }
                if (modsAllowed && modded == null)
                {
                    var livery = ModLiveryEquipper.GetEquipped(ci.name);
                    if (livery != null) { modId = livery.ModId; subId = livery.SubId; }
                }
            }
        }
        catch (Exception e) { DebugLog.Error("core", e, "CarLoadout.For"); Core.CorePlugin.Logger?.LogWarning("[Cars] could not read a car's upgrades: " + e.Message); }
        return new CarLoadout(bits, modId, subId);
    }
}
