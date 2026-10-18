using System;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using OverTheHillPlus.Diagnostics;
using OverTheHillPlus.Features.Cheats.SimpleView;

namespace OverTheHillPlus.CheatsPlus;

// After the namespace line on purpose: the game has its own global ModManager (mod.io).
using OverTheHillPlus.Core.Manager;

/// <summary>
/// Cheats+ (private): driving assists, car cheats (no damage, flip, fly, nitro, super jump), car switching and spawning,
/// pebbles and items, presets, the game's own developer commands and console, engine and chassis tuning, ESP and fun
/// physics. Also the troubleshooting tools: it switches core's <see cref="DevDetails"/> on and carries the server
/// browser profiler.
/// </summary>
[BepInPlugin(Guid, Name, Version)]
[BepInDependency(Core.CorePlugin.Guid)]
public class CheatsPlugin : BasePlugin
{
    public const string Guid = "com.oth.cheats";
    public const string Name = "Cheats+";
    public const string Version = "1.0.0";

    /// <summary>Mod id: names the config file (oth.cheats.cfg), the MODS page entry and the enabled switch.</summary>
    internal const string ModId = "cheats";

    internal static ManualLogSource Logger;
    internal static Harmony Harmony;

    public override void Load()
    {
        Logger = Log;
        // Listed on the MODS page even when switched off, so it can be switched back on.
        ModManager.Register(new ModInfo
        {
            Id = ModId,
            Side = ModSide.Client,
            Name = Name,
            Description = "Driving assists, no damage, fly, nitro and super jump, car switching, pebbles and items, presets, " +
                          "the game's developer commands and console, tuning, ESP and fun physics. Solo or host for most of it.",
            Version = Version,
            Private = false,
            DrawSimple = SimpleCheatPages.Draw,
        });
        if (!ModManager.IsEnabled(ModId))
        {
            Logger.LogInfo($"{Name} is switched off on the MODS page: nothing of it runs this session.");
            return;
        }
        try
        {
            CheatsConfig.Bind(ModManager.ConfigFor(ModId)); // BepInEx/config/oth.cheats.cfg
            AdvantageFlag.HoldAchievements = () => CheatsConfig.BlockAchievements.Value;
            DevDetails.On = true;
            CheatsFrame.Install();
            CheatsPage.RegisterSections();
            Features.Registry.DiscoverAndRegister(typeof(CheatsPlugin).Assembly, ModId, CheatsConfig.File);
            CheatsConfig.File.Save(); // writes the defaults of every entry on first start
            Harmony = new Harmony(Guid);
            PatchReport.PatchAllSafely(Harmony, typeof(CheatsPlugin).Assembly, ModId);
            if (CheatsConfig.ProfileServerBrowser.Value) BrowserProfiler.Install(Harmony);
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
