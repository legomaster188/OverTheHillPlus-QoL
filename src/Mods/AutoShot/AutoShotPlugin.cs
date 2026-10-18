using System;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;

namespace OverTheHillPlus.AutoShot;

// After the namespace line on purpose: the game has its own global ModManager (mod.io).
using OverTheHillPlus.Core.Manager;

/// <summary>
/// Auto Screenshot: saves a screenshot (without the mods' overlays) on big jumps, flips and newly found places, with a
/// cooldown and a choice of folder. Local only.
/// </summary>
[BepInPlugin(Guid, Name, Version)]
[BepInDependency(Core.CorePlugin.Guid)]
public class AutoShotPlugin : BasePlugin
{
    public const string Guid = "com.oth.autoshot";
    public const string Name = "Auto Screenshot";
    public const string Version = "1.0.0";

    /// <summary>Mod id: names the config file (oth.autoshot.cfg), the MODS page entry and the enabled switch.</summary>
    internal const string ModId = "autoshot";

    internal static ManualLogSource Logger;

    public override void Load()
    {
        Logger = Log;
        ModManager.Register(new ModInfo
        {
            Id = ModId,
            Side = ModSide.Client,
            Name = Name,
            Description = "Saves a screenshot on big jumps, flips and newly found places, with a cooldown and a folder setting.",
            Version = Version,
            DrawSimple = AutoShotSimple.Draw,
        });
        if (!ModManager.IsEnabled(ModId))
        {
            Logger.LogInfo($"{Name} is switched off on the MODS page: nothing of it runs this session.");
            return;
        }
        try
        {
            AutoShotConfig.Bind(ModManager.ConfigFor(ModId)); // BepInEx/config/oth.autoshot.cfg
            AutoShotFrame.Install();
            Features.Registry.DiscoverAndRegister(typeof(AutoShotPlugin).Assembly, ModId, AutoShotConfig.File);
            AutoShotConfig.File.Save();
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
