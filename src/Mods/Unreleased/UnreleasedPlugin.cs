using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using OverTheHillPlus.Features;

namespace OverTheHillPlus.Unreleased;

// After the namespace line on purpose: the game has its own global ModManager (mod.io).
using OverTheHillPlus.Core.Manager;

/// <summary>
/// Unreleased Cars (private): the game's four complete but unreleased cars (Grizzly, Baguette, Brigand,
/// Cordillera) in the garage, with the developer's own data where the game has it. Host side: the host spawns every
/// car, so guests (even without mods) see them, and a guest drives one only with a host that runs this mod.
/// The parts: <see cref="UnreleasedCars"/> (catalog, addresses, names), <see cref="SaveGuard"/> (the save file
/// never holds them), <see cref="GuestGate"/> (guests), <see cref="UnreleasedPatches"/> (hooks).
/// </summary>
[BepInPlugin(Guid, Name, Version)]
[BepInDependency(Core.CorePlugin.Guid)]
public class UnreleasedPlugin : BasePlugin
{
    public const string Guid = "com.oth.unreleased";
    public const string Name = "Unreleased Cars";
    public const string Version = "1.0.0";

    /// <summary>Mod id: names the config file (oth.unreleased.cfg), the MODS page entry and the enabled switch.</summary>
    internal const string ModId = "unreleased";

    internal static ManualLogSource Logger;
    /// <summary>Why the mod turned itself off this session (shown on its page), or null.</summary>
    internal static string FailedReason { get; private set; }
    internal static Harmony Harmony;
    private static readonly HashSet<string> Logged = new();
    private static float _nextAdvantageCheck;

    public override void Load()
    {
        Logger = Log;
        ModManager.Register(new ModInfo
        {
            Id = ModId,
            Side = ModSide.Host,
            Name = Name,
            Description = "The game's four unreleased cars (Grizzly, Baguette, Brigand, Cordillera) in your garage. " +
                          "Guests see them; to drive one, a guest needs a host with this mod. Saves stay loadable without it.",
            Version = Version,
            DrawSimple = UnreleasedSimple.Draw,
        });
        if (!ModManager.IsEnabled(ModId))
        {
            Logger.LogInfo($"{Name} is switched off on the MODS page: nothing of it runs this session.");
            return;
        }
        try
        {
            UnreleasedConfig.Bind(ModManager.ConfigFor(ModId)); // BepInEx/config/oth.unreleased.cfg
            UnreleasedConfig.File.Save();
            Harmony = new Harmony(Guid);
            PatchReport.PatchAllSafely(Harmony, typeof(UnreleasedPlugin).Assembly, ModId);
            // Without the save guard our ids would reach the save file, and without the garage filter the game would
            // sell or hand out the cars: then the cars are never set up at all.
            string missing = MissingRequiredPatches();
            if (missing != null) { Fail("hooks missing: " + missing); return; }
            if (!GuestGate.Register()) { Fail("its ModNet channel (114) is taken"); return; }
            Registry.OnTick(Tick);
            Registry.OnGui(Notice.Draw);
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

    private void Fail(string why)
    {
        FailedReason = why;
        try { Harmony.UnpatchSelf(); } catch (Exception ex) { DebugLog.Error("unreleased", ex, "UnreleasedPlugin.Fail"); }
        ModManager.MarkFailed(ModId, why);
        Logger.LogError($"{Name} is off this session: {why}. The cars are not set up, so nothing of them reaches a save.");
    }

    /// <summary>
    /// The hooks the cars can't do without, or null when all are in (checked by owner, not by attempt): the save
    /// guard, the garage filter, and the three places a car is spawned or sent (solo-only swaps, address cache).
    /// </summary>
    private static string MissingRequiredPatches()
    {
        var missing = new List<string>();
        if (!Has(AccessTools.Method(typeof(OverlandSaver), nameof(OverlandSaver.SaveGame)), true, true)) missing.Add("OverlandSaver.SaveGame");
        if (!Has(AccessTools.Method(typeof(InventoryUtility), nameof(InventoryUtility.GetAllVehicles)), false, true)) missing.Add("InventoryUtility.GetAllVehicles");
        // The solo-only swaps and the Brigand's / Cordillera's address cache.
        if (!Has(AccessTools.Method(typeof(OverlandNetworkRunner), nameof(OverlandNetworkRunner.SetupCar)), true, false)) missing.Add("OverlandNetworkRunner.SetupCar");
        if (!Has(AccessTools.Method(typeof(OverlandNetworkRunner), nameof(OverlandNetworkRunner.SpawnPlayer)), true, false)) missing.Add("OverlandNetworkRunner.SpawnPlayer");
        if (!Has(AccessTools.Method(typeof(RuntimePlayer), nameof(RuntimePlayer.Serialize)), true, false)) missing.Add("RuntimePlayer.Serialize");
        return missing.Count == 0 ? null : string.Join(", ", missing);
    }

    private static bool Has(MethodBase method, bool prefix, bool postfix)
    {
        try
        {
            var info = method == null ? null : Harmony.GetPatchInfo(method);
            if (info == null) return false;
            return (!prefix || info.Prefixes.Any(p => p.owner == Guid)) && (!postfix || info.Postfixes.Any(p => p.owner == Guid));
        }
        catch (Exception ex) { DebugLog.Error("unreleased", ex, "UnreleasedPlugin.Has"); return false; }
    }

    private static void Tick()
    {
        UnreleasedCars.Tick();
        SaveGuard.Tick();
        UnreleasedSimple.AutoAddTick();
        // Driving one counts as using the mod's powers (achievements pause), like adding one does.
        if (AdvantageFlag.UsedThisSession || GameState.Runner == null || UnityEngine.Time.unscaledTime < _nextAdvantageCheck) return;
        _nextAdvantageCheck = UnityEngine.Time.unscaledTime + 1f;
        if (UnreleasedCars.IsOurCar(SaveGuard.CurrentVehicleId())) AdvantageFlag.MarkUsed();
    }

    internal static void LogOnce(string key, string message)
    {
        if (Logged.Add(key)) Logger?.LogWarning("[Unreleased] " + message);
    }

    internal static void InfoOnce(string key, string message)
    {
        if (Logged.Add(key)) Logger?.LogInfo("[Unreleased] " + message);
    }
}
