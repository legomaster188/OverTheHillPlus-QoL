using System;
using System.Collections.Generic;
using Fusion;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using OverTheHillPlus.Features.Public.ModNet;

namespace OverTheHillPlus.Unreleased;

/// <summary>
/// Sessions and the unreleased cars. The host spawns every player's car and looks its id up in its own catalog
/// (OverlandNetworkRunner.SetupCar / SpawnPlayer), so a guest may only drive one of ours with a host that runs
/// this mod.
/// - The host advertises a ModNet channel while the cars are set up (no messages: <see cref="ModNet.HostHas"/> is the
///   whole protocol). A guest whose host doesn't advertise it gets the cars hidden in the garage, with a notice.
/// - Joining: RuntimePlayerInjector.SendRuntimePlayer sends the car in use to the host as soon as the scene is
///   loaded, before ModNet has talked to the host. A guest about to send the Grizzly or Baguette switches to a stock
///   car first (it can pick ours again in the garage once the host turns out to run the mod), so it never arrives
///   without a car.
/// - Brigand and Cordillera (CarInfo only with this mod; a vanilla game throws in
///   VehicleCustomizationController.InitVehicleUpgrades when one spawns, and a vanilla player can join while one is
///   out) are solo only (ruling P15): hidden in the garage outside solo, and anywhere a car is spawned or sent in a
///   session (SetupCar, SpawnPlayer's RuntimePlayer, RuntimePlayer.Serialize) they become their donor car. Only what
///   is spawned or sent changes: the inventory and the save keep the car the player picked.
///   OverlandNetworkRunner.IsSinglePlayer reads the runner's start mode ([this+64].mode == 1), fixed for the session:
///   a solo session never turns into a multiplayer one (going online starts a new runner, which spawns again).
/// </summary>
internal static class GuestGate
{
    /// <summary>ModNet channel id (core's table, ModNet.Channels.Reserved114: never reuse or renumber).</summary>
    internal const ushort Channel = ModNet.Channels.Reserved114;

    /// <summary>Registers the channel. False when it is taken.</summary>
    internal static bool Register() =>
        ModNet.Register(Channel, new ModNet.ChannelOptions
        {
            Name = "unreleased cars",
            Version = 1,
            MaxPayload = 0,
            RatePerSecond = 0.1f,
            Burst = 1,
            HostOnlySender = true,
            Active = () => UnreleasedCars.AnyRegistered,
        }, _ => { });

    /// <summary>In someone else's session (not solo, not hosting).</summary>
    internal static bool IsGuest
    {
        get
        {
            var r = GameState.Runner;
            if (r == null) return false;
            try { return r.IsRunning && !r.IsServer && !r.IsSinglePlayer; } catch (Exception ex) { DebugLog.Error("unreleased", ex, "GuestGate.IsGuest"); return false; }
        }
    }

    /// <summary>
    /// No session (menus) or a single-player one: nobody else can see a car. The game's own runner decides first
    /// (core's cached runner refreshes once a second and can still be empty right after hosting or joining); with no
    /// readable runner it is solo only when no Fusion runner is live at all (<see cref="UnreleasedRules.DecideSolo"/>).
    /// </summary>
    internal static bool IsSolo
    {
        get
        {
            try { return UnreleasedRules.DecideSolo(OverlandSingle(), CachedSingle(), AnyLiveRunner()); }
            catch (Exception ex) { DebugLog.Error("unreleased", ex, "GuestGate.IsSolo"); return false; } // unknown: treat as a session
        }
    }

    private static bool? OverlandSingle()
    {
        try
        {
            var onr = OverlandNetworkRunner.Instance;
            var nr = onr?.NetworkRunner;
            if (nr == null || nr.IsShutdown) return null;
            return onr.IsSinglePlayer; // the runner's start mode, set when the session starts
        }
        catch (Exception ex) { DebugLog.Error("unreleased", ex, "GuestGate.OverlandSingle"); return null; }
    }

    private static bool? CachedSingle()
    {
        try
        {
            var r = GameState.Runner;
            if (r == null || r.IsShutdown) return null;
            return r.IsSinglePlayer;
        }
        catch (Exception ex) { DebugLog.Error("unreleased", ex, "GuestGate.CachedSingle"); return null; }
    }

    private static bool AnyLiveRunner()
    {
        try
        {
            // Fusion's IReadOnlyList is backed by its List<NetworkRunner> _instances.
            var all = NetworkRunner.Instances?.TryCast<Il2CppSystem.Collections.Generic.List<NetworkRunner>>();
            if (all == null) return NetworkRunner.Instances != null; // there, but unreadable: assume a live one
            for (int i = 0; i < all.Count; i++)
            {
                var r = all[i];
                if (r != null && !r.IsShutdown) return true;
            }
            return false;
        }
        catch (Exception ex) { DebugLog.Error("unreleased", ex, "GuestGate.AnyLiveRunner"); return true; } // can't tell: assume one is
    }

    /// <summary>Guest: the host runs this mod (with its cars set up).</summary>
    internal static bool HostRunsMod => ModNet.HostHas(Channel);

    /// <summary>What blocks this game from using the cars right now, or null.</summary>
    internal static string Blocker => IsGuest && !HostRunsMod
        ? "This host doesn't run Unreleased Cars: your unreleased cars are hidden in its garage."
        : null;

    /// <summary>Why the Brigand and Cordillera are hidden right now, or null.</summary>
    internal static string CopiesBlocker => Blocker == null && !IsSolo
        ? "Brigand and Cordillera are solo only: in a session they're hidden and drive as their donor car."
        : null;

    /// <summary>The donor id a solo-only car is spawned or sent as in this session, or 0 to keep <paramref name="carId"/>.</summary>
    internal static uint SessionSwap(uint carId)
    {
        if (!UnreleasedCars.IsOurCar(carId)) return 0;
        var car = UnreleasedCars.Find(carId);
        if (car == null) return 0;
        return UnreleasedRules.SessionCarSwap(carId, IsSolo, car.SoloOnly, car.DonorId);
    }

    /// <summary>Swaps a RuntimePlayer's car to its donor when it is a solo-only car in a session (upgrades cleared:
    /// the copied list leaves out the donor's liveries, so the bits don't match the donor's). True when swapped.</summary>
    internal static bool SwapRuntimePlayer(RuntimePlayer rp, string where)
    {
        if (rp == null) return false;
        uint car = rp.CarItemId;
        uint donor = SessionSwap(car);
        if (donor == 0) return false;
        rp.CarItemId = donor;
        rp.EnabledUpgrades = new Il2CppStructArray<ulong>(4);
        DebugLog.Info(UnreleasedPlugin.ModId, $"{where}: the {UnreleasedCars.Find(car)?.Name} is solo only, sent as car {donor} in this session");
        UnreleasedPlugin.Logger.LogInfo($"[Unreleased] {where}: the {UnreleasedCars.Find(car)?.Name} is solo only, sent as car {donor} in this session");
        return true;
    }

    /// <summary>Prefix of RuntimePlayerInjector.SendRuntimePlayer.</summary>
    internal static void BeforeSendRuntimePlayer(NetworkRunner runner)
    {
        try
        {
            if (runner == null || runner.IsServer || runner.IsSinglePlayer || !runner.IsClient) return;
            uint current = SaveGuard.CurrentVehicleId();
            if (!UnreleasedCars.IsOurCar(current) || HostRunsMod) return;
            var car = UnreleasedCars.Find(current);
            if (car is { SoloOnly: true, DonorId: not 0 }) return; // sent as its donor (RuntimePlayer.Serialize): any host has that
            uint stock = SwitchToStockCar();
            string name = car?.Name ?? "unreleased car";
            UnreleasedPlugin.Logger.LogInfo($"[Unreleased] joining: the host can't spawn the {name} until it shows it runs this mod, " +
                                            (stock != 0 ? $"so this game joins with car {stock}" : "and no stock car is owned"));
            Notice.Show("Joined with a stock car", $"Pick the {name} in the garage if the host runs Unreleased Cars.");
        }
        catch (Exception e) { DebugLog.Error("unreleased", e, "GuestGate.BeforeSendRuntimePlayer"); UnreleasedPlugin.LogOnce("join", "join check failed: " + e.Message); }
    }

    /// <summary>Marks the stand-in stock car in use instead of ours (like picking it in the garage). Returns its id, 0 = none owned.</summary>
    private static uint SwitchToStockCar()
    {
        var items = SaveGuard.Inventory()?.Items;
        if (items == null) return 0;
        var owned = new List<uint>();
        for (int i = 0; i < items.Count; i++)
        {
            var vi = items[i]?.TryCast<VehicleItem>();
            if (vi != null) owned.Add(vi.ItemId);
        }
        uint stock = UnreleasedRules.ChooseFallback(owned, UnreleasedCars.CarIds, SaveGuard.LastStockInUse);
        if (stock == 0) return 0;
        SaveGuard.SetOnlyInUse(items, stock);
        return stock;
    }

    /// <summary>Postfix of the garage's vehicle list: says why owned unreleased cars aren't there.</summary>
    internal static void AfterGarageBuilt()
    {
        bool hostLacks = Blocker != null, copiesHidden = CopiesBlocker != null;
        if (!hostLacks && !copiesHidden) return;
        uint inUse = SaveGuard.CurrentVehicleId();
        foreach (var car in UnreleasedCars.All)
        {
            if (!car.Registered || !car.IsOn || !UnreleasedPatches.Owns(car.Id)) continue;
            if (hostLacks) { Notice.Show("Unreleased cars hidden", "This host doesn't run Unreleased Cars."); return; }
            // The copy in use stays listed (it drives as its donor here): only a really hidden one gets the notice.
            if (car.SoloOnly && car.Id != inUse) { Notice.Show("Brigand and Cordillera hidden", "They're solo only for now."); return; }
        }
    }
}
