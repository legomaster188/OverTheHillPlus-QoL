using System;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;

namespace OverTheHillPlus.Cargo;

// After the namespace line on purpose: the game has its own global ModManager (mod.io).
using OverTheHillPlus.Core.Manager;

/// <summary>
/// Cargo Jobs: pick up and deliver jobs between the map's points of interest, for pebbles. The host keeps one job
/// board for the session (every modded player sees it on the MODS page and takes jobs from it) and judges pick-ups and
/// deliveries from the car positions it simulates itself. Pay lands in each player's own save: the host pays itself
/// directly, and a guest's game adds the pebbles only when the host confirms that guest's delivery, at most what the
/// job pays, once. Pay per hour is capped (a setting) so a session can't farm pebbles.
/// </summary>
[BepInPlugin(Guid, Name, Version)]
[BepInDependency(Core.CorePlugin.Guid)]
public class CargoPlugin : BasePlugin
{
    public const string Guid = "com.oth.cargo";
    public const string Name = "Cargo Jobs";
    public const string Version = "1.0.0";

    /// <summary>Mod id: names the config file (oth.cargo.cfg), the MODS page entry and the enabled switch.</summary>
    internal const string ModId = "cargo";

    internal static ManualLogSource Logger;
    internal static Harmony Harmony;

    public override void Load()
    {
        Logger = Log;
        // Listed on the MODS page even when switched off, so it can be switched back on.
        ModManager.Register(new ModInfo
        {
            Id = ModId,
            Side = ModSide.Server,
            Name = Name,
            Description = "Pick up and deliver jobs between points of interest for pebbles, from one job board the " +
                          "whole session shares. The host checks every pick-up and delivery, with an on-time bonus " +
                          "and a cap on pay per hour.",
            Version = Version,
            DrawSimple = CargoSimple.Draw,
            DrawAdvanced = CargoSimple.DrawAdvanced,
        });
        if (!ModManager.IsEnabled(ModId))
        {
            Logger.LogInfo($"{Name} is switched off on the MODS page: nothing of it runs this session.");
            return;
        }
        try
        {
            CargoConfig.Bind(ModManager.ConfigFor(ModId)); // BepInEx/config/oth.cargo.cfg
            Features.Registry.DiscoverAndRegister(typeof(CargoPlugin).Assembly, ModId, CargoConfig.File);
            CargoConfig.File.Save(); // writes the defaults of every entry on first start
            Harmony = new Harmony(Guid);
            PatchReport.PatchAllSafely(Harmony, typeof(CargoPlugin).Assembly, ModId);
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
