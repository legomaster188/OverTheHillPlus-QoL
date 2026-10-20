using System;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;

namespace OverTheHillPlus.Cockpit;

// After the namespace line on purpose: the game has its own global ModManager (mod.io).
using OverTheHillPlus.Core.Manager;

/// <summary>
/// Cockpit: a first-person view from the driver's seat, with a dashboard, wheel, gauges and pillars built at runtime to
/// fit each car, free look and head sway. Your view only; nothing is sent to anyone.
/// </summary>
[BepInPlugin(Guid, Name, Version)]
[BepInDependency(Core.CorePlugin.Guid)]
public class CockpitPlugin : BasePlugin
{
    public const string Guid = "com.oth.cockpit";
    public const string Name = "Cockpit+";
    public const string Version = "1.2.1";

    /// <summary>Mod id: names the config file (oth.cockpit.cfg), the MODS page entry and the enabled switch.</summary>
    internal const string ModId = "cockpit";

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
            Description = "Sit in the driver's seat: a dashboard, steering wheel, speedometer, rev counter and gear lamps " +
                          "built to fit each car, free look, head sway on bumps, and a key or the mouse wheel to get in.",
            Version = Version,
            DrawSimple = CockpitSimple.Draw,
        });
        if (!ModManager.IsEnabled(ModId))
        {
            Logger.LogInfo($"{Name} is switched off on the MODS page: nothing of it runs this session.");
            return;
        }
        try
        {
            CockpitConfig.Bind(ModManager.ConfigFor(ModId)); // BepInEx/config/oth.cockpit.cfg
            CockpitRig.Install();
            Features.Registry.DiscoverAndRegister(typeof(CockpitPlugin).Assembly, ModId, CockpitConfig.File);
            CockpitConfig.File.Save();
            Harmony = new Harmony(Guid);
            PatchReport.PatchAllSafely(Harmony, typeof(CockpitPlugin).Assembly, ModId);
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
