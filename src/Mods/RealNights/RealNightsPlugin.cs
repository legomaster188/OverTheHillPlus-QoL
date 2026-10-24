using System;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;

namespace OverTheHillPlus.RealNights;

// After the namespace line on purpose: the game has its own global ModManager (mod.io).
using OverTheHillPlus.Core.Manager;

/// <summary>
/// Nights+: nights that are actually dark (moonlight only, adjustable), a starry sky with the Milky Way on clear
/// nights, storms that go pitch black, and the lamps on your car (roof light bars, bull-bar lamps, the custom light
/// bars) and the headlights lighting the ground. Only changes what you see; works with Graphics+ (which keeps the day).
/// </summary>
[BepInPlugin(Guid, Name, Version)]
[BepInDependency(Core.CorePlugin.Guid)]
public class RealNightsPlugin : BasePlugin
{
    public const string Guid = "com.oth.realnights";
    public const string Name = "Nights+";
    public const string Version = "1.2.1";

    /// <summary>Mod id: names the config file (oth.realnights.cfg), the MODS page entry and the enabled switch.</summary>
    internal const string ModId = "realnights";

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
            Description = "Dark nights with only moonlight, a starry sky with the Milky Way, storms that go pitch black, " +
                          "and the lamps on your car lighting the ground. Only changes what you see.",
            Version = Version,
            DrawSimple = RealNightsSimple.Draw,
        });
        if (!ModManager.IsEnabled(ModId))
        {
            Logger.LogInfo($"{Name} is switched off on the MODS page: nothing of it runs this session.");
            return;
        }
        try
        {
            RealNightsConfig.Bind(ModManager.ConfigFor(ModId)); // BepInEx/config/oth.realnights.cfg
            Features.Registry.DiscoverAndRegister(typeof(RealNightsPlugin).Assembly, ModId, RealNightsConfig.File);
            RealNightsConfig.File.Save(); // writes the defaults of every entry on first start
            Harmony = new Harmony(Guid);
            PatchReport.PatchAllSafely(Harmony, typeof(RealNightsPlugin).Assembly, ModId);
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
