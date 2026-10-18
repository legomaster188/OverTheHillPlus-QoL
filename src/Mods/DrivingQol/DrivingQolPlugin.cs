using System;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;

namespace OverTheHillPlus.DrivingQol;

// After the namespace line on purpose: the game has its own global ModManager (mod.io).
using OverTheHillPlus.Core.Manager;

/// <summary>
/// Driving QoL: everyday tweaks that give no advantage. A higher frame-rate cap, a background cap and keep running when
/// alt-tabbed, the driving camera's field of view, compass range, auto headlights, skipping the logo screens, faster
/// fades, no tutorial hints, and the unstuck button.
/// </summary>
[BepInPlugin(Guid, Name, Version)]
[BepInDependency(Core.CorePlugin.Guid)]
public class DrivingQolPlugin : BasePlugin
{
    public const string Guid = "com.oth.drivingqol";
    public const string Name = "Driving QoL";
    public const string Version = "1.0.0";

    /// <summary>Mod id: names the config file (oth.drivingqol.cfg), the MODS page entry and the enabled switch.</summary>
    internal const string ModId = "drivingqol";

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
            Description = "Everyday tweaks: higher frame-rate cap, keep running when alt-tabbed, field of view, auto " +
                          "headlights, skip the logo screens, hide tutorial hints, and an unstuck button for your car.",
            Version = Version,
            DrawSimple = DrivingQolSimple.Draw,
        });
        if (!ModManager.IsEnabled(ModId))
        {
            Logger.LogInfo($"{Name} is switched off on the MODS page: nothing of it runs this session.");
            return;
        }
        try
        {
            DrivingQolConfig.Bind(ModManager.ConfigFor(ModId)); // BepInEx/config/oth.drivingqol.cfg
            DrivingQolFrame.Install();
            Features.Registry.DiscoverAndRegister(typeof(DrivingQolPlugin).Assembly, ModId, DrivingQolConfig.File);
            DrivingQolConfig.File.Save(); // writes the defaults of every entry on first start
            Harmony = new Harmony(Guid);
            PatchReport.PatchAllSafely(Harmony, typeof(DrivingQolPlugin).Assembly, ModId);
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
