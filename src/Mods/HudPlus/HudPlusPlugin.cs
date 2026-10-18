using System;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;

namespace OverTheHillPlus.HudPlus;

// After the namespace line on purpose: the game has its own global ModManager (mod.io).
using OverTheHillPlus.Core.Manager;

/// <summary>
/// HUD+: a speedometer in three skins (analog, digital, minimal) and a car status card (health, tyres, winch,
/// odometer). Only reads your own car; nothing is sent to anyone.
/// </summary>
[BepInPlugin(Guid, Name, Version)]
[BepInDependency(Core.CorePlugin.Guid)]
public class HudPlusPlugin : BasePlugin
{
    public const string Guid = "com.oth.hudplus";
    public const string Name = "HUD+";
    public const string Version = "1.0.0";

    /// <summary>Mod id: names the config file (oth.hudplus.cfg), the MODS page entry and the enabled switch.</summary>
    internal const string ModId = "hudplus";

    internal static ManualLogSource Logger;

    public override void Load()
    {
        Logger = Log;
        ModManager.Register(new ModInfo
        {
            Id = ModId,
            Side = ModSide.Client,
            Name = Name,
            Description = "Speedometer skins (analog, digital, minimal) and a car status card: health, tyres, winch and odometer.",
            Version = Version,
            DrawSimple = HudPlusSimple.Draw,
        });
        if (!ModManager.IsEnabled(ModId))
        {
            Logger.LogInfo($"{Name} is switched off on the MODS page: nothing of it runs this session.");
            return;
        }
        try
        {
            HudPlusConfig.Bind(ModManager.ConfigFor(ModId)); // BepInEx/config/oth.hudplus.cfg
            HudPlusFrame.Install();
            Features.Registry.DiscoverAndRegister(typeof(HudPlusPlugin).Assembly, ModId, HudPlusConfig.File);
            HudPlusConfig.File.Save();
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
