using System;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;

namespace OverTheHillPlus.UnlockAll;

// After the namespace line on purpose: the game has its own global ModManager (mod.io).
using OverTheHillPlus.Core.Manager;

/// <summary>
/// Unlock All (private): unlocks cars, upgrades, areas and more on your profile, in bulk through the game's own
/// developer unlock commands, or car by car and map by map. Host or solo only. Paid DLC is never unlocked.
///
/// Findings (Cpp2IL of the updated game, interop checked with the member dumper):
/// - TestAreaUnlocker.WaitUntilFullyInitialized (the game's own test unlocker) calls, with an empty CommandArg[]:
///   CheatsController.UnlockAllCars, UnlockAllCosmetics, then MapManager.Instance.FogOfWarManager.ClearFullFogOfWar +
///   OverlandSaver.SaveGame, then UnlockAllCabins + SaveGame. CheatsController.UnlockAllAll runs UnlockAllAreas,
///   UnlockAllCabins, UnlockAllCars, UnlockAllCosmetics, UnlockAllLiveries, UnlockAllToolSlots, UnlockAllUpgrades.
/// - The add paths underneath: UnlockAllCars -> PlayerInventory.AddVehicleItem(id, 0, 1, false, false) for each
///   InventoryUtility.GetAllVehicles() not owned yet; UnlockAllUpgrades / UnlockAllCosmetics -> AddItem(partId,
///   VehicleState{VehicleItemId}, false) per vehicle; UnlockAllLiveries -> AddItem(id, count, inUse, isNew) over the whole
///   catalog; UnlockAllAreas -> AddVirtualItem(new AreaItem(id, unlocked)) for Data/Game/Regions/RegionsList (it does
///   not save: we call OverlandSaver.SaveGame after); UnlockAllCabins discovers the scene's cabins and saves.
/// - DLC: GetAllVehicles filters with DlcManager.IsAllowed (no DLC, or DLC owned and switched on), but the liveries,
///   cosmetics and upgrades loops do not look at DLC at all. So <see cref="DlcGuard"/> sits on the four
///   PlayerInventory adders while one of our unlocks runs and drops anything whose catalog entry needs a DLC
///   (InventoryItemScriptableObject.RequiredDlc or DlcManager.GetRequiredDlc, also for the vehicle of a vehicle part),
///   owned or not. DlcManager itself is only read, never changed.
/// - Not every command reads the console: UnlockAllAreas, UnlockAllCabins and AddItem (so UnlockAllToolSlots), like
///   SetOdometer, RevealFogOfWar and RevealUnknownPoiIcons, start with "if (Terminal.IssuedError) return" (reading
///   Terminal.Shell, which throws when the console was never opened) and log to Terminal.Buffer. UnlockAllCars,
///   UnlockAllCosmetics, UnlockAllLiveries and UnlockAllUpgrades don't touch it. Before each command
///   <see cref="Unlocker"/> gives the console a shell and log when it has none (Terminal.Start replaces both when the
///   real console starts) and clears the last error.
/// - Unlock All+ adds per-car, per-map and item sections that call the PlayerInventory adders, the cabins and the fog
///   directly (no console): see <see cref="CarUnlocks"/>, <see cref="MapUnlocks"/>, <see cref="ItemUnlocks"/> and
///   the pure <see cref="UnlockPlan"/>. They run through <see cref="Unlocker.Guarded"/>: host or solo, DLC guard on,
///   AdvantageFlag marked, saved with OverlandSaver.SaveGame.
/// </summary>
[BepInPlugin(Guid, Name, Version)]
[BepInDependency(Core.CorePlugin.Guid)]
public class UnlockAllPlugin : BasePlugin
{
    public const string Guid = "com.oth.unlockall";
    public const string Name = "Unlock All";
    public const string Version = "1.1.0";

    /// <summary>Mod id: names the config file (oth.unlockall.cfg), the MODS page entry and the enabled switch.</summary>
    internal const string ModId = "unlockall";

    internal static ManualLogSource Logger;
    internal static Harmony Harmony;

    public override void Load()
    {
        Logger = Log;
        ModManager.Register(new ModInfo
        {
            Id = ModId,
            Side = ModSide.Client,
            Name = Name,
            Description = "Unlocks cars, upgrades, cosmetics, liveries, areas, cabins, map fog, tool slots and money on your " +
                          "profile, all at once or car by car and map by map. Host or solo only. Paid DLC is never unlocked.",
            Version = Version,
            Private = false,
            DrawSimple = UnlockAllSimple.Draw,
        });
        if (!ModManager.IsEnabled(ModId))
        {
            Logger.LogInfo($"{Name} is switched off on the MODS page: nothing of it runs this session.");
            return;
        }
        try
        {
            UnlockAllConfig.Bind(ModManager.ConfigFor(ModId)); // BepInEx/config/oth.unlockall.cfg
            UnlockAllConfig.File.Save(); // writes the defaults on first start
            Harmony = new Harmony(Guid);
            if (!DlcGuard.Install(Harmony))
                Logger.LogWarning($"{Name}: the DLC guard could not be installed, so the unlocks are off this session.");
            CoreBehaviour.AfterFeatureTicks += DlcGuard.StartupCheckTick;
            Logger.LogInfo($"{Name} {Version} loaded.");
            DebugLog.Info(ModId, $"loaded {Version}");
        }
        catch (Exception e)
        {
            // The page says "Failed to start"; core and the other mods keep running.
            ModManager.MarkFailed(ModId, e.Message);
            Logger.LogError($"{Name} failed to start: {e}");
        }
    }
}
