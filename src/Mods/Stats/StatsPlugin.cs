using System;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;

namespace OverTheHillPlus.Stats;

// After the namespace line on purpose: the game has its own global ModManager (mod.io).
using OverTheHillPlus.Core.Manager;

/// <summary>
/// Trip Stats: distance, top speed, air time, longest jump, flips and time driven, for this session and all time, with
/// a small HUD card. Only reads your own car; nothing is sent to anyone.
/// </summary>
[BepInPlugin(Guid, Name, Version)]
[BepInDependency(Core.CorePlugin.Guid)]
public class StatsPlugin : BasePlugin
{
    public const string Guid = "com.oth.stats";
    public const string Name = "Trip Stats";
    public const string Version = "1.0.0";

    /// <summary>Mod id: names the config file (oth.stats.cfg), the saved numbers (oth.stats.json) and the MODS page entry.</summary>
    internal const string ModId = "stats";

    internal static ManualLogSource Logger;

    public override void Load()
    {
        Logger = Log;
        ModManager.Register(new ModInfo
        {
            Id = ModId,
            Side = ModSide.Client,
            Name = Name,
            Description = "Distance, top speed, air time, longest jump, flips and time driven, for this session and all " +
                          "time, with a small card on screen.",
            Version = Version,
            DrawSimple = StatsSimple.Draw,
        });
        if (!ModManager.IsEnabled(ModId))
        {
            Logger.LogInfo($"{Name} is switched off on the MODS page: nothing of it runs this session.");
            return;
        }
        try
        {
            StatsConfig.Bind(ModManager.ConfigFor(ModId)); // BepInEx/config/oth.stats.cfg
            StatsTracker.Load();
            StatsFrame.Install();
            Features.Registry.DiscoverAndRegister(typeof(StatsPlugin).Assembly, ModId, StatsConfig.File);
            StatsConfig.File.Save();
            Logger.LogInfo($"{Name} {Version} loaded.");
            DebugLog.Info(ModId, $"loaded {Version}");
        }
        catch (Exception e)
        {
            ModManager.MarkFailed(ModId, e.Message);
            Logger.LogError($"{Name} failed to start: {e}");
        }
    }
}
