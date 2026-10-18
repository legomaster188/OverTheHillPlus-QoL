using System;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;

namespace OverTheHillPlus.MapHud;

// After the namespace line on purpose: the game has its own global ModManager (mod.io).
using OverTheHillPlus.Core.Manager;

/// <summary>
/// Map &amp; HUD: everyone on screen. Players beyond the game's four slots on the map, the compass and the repair
/// health bars, in the pause menu's player list and as name tags above their cars, plus the driving HUD (speed, RPM,
/// gear).
/// </summary>
[BepInPlugin(Guid, Name, Version)]
[BepInDependency(Core.CorePlugin.Guid)]
public class MapHudPlugin : BasePlugin
{
    public const string Guid = "com.oth.maphud";
    public const string Name = "Map & HUD";
    public const string Version = "1.0.0";

    /// <summary>Mod id: names the config file (oth.maphud.cfg), the MODS page entry and the enabled switch.</summary>
    internal const string ModId = "maphud";

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
            Description = "Every player on the map, the compass and the pause menu's player list (also past the game's " +
                          "four slots), name tags above their cars, and a driving HUD with speed, RPM and gear.",
            Version = Version,
            DrawSimple = MapHudSimple.Draw,
        });
        if (!ModManager.IsEnabled(ModId))
        {
            Logger.LogInfo($"{Name} is switched off on the MODS page: nothing of it runs this session.");
            return;
        }
        try
        {
            MapHudConfig.Bind(ModManager.ConfigFor(ModId)); // BepInEx/config/oth.maphud.cfg
            MapHudFrame.Install();
            Features.Registry.DiscoverAndRegister(typeof(MapHudPlugin).Assembly, ModId, MapHudConfig.File);
            MapHudConfig.File.Save(); // writes the defaults of every entry on first start
            Harmony = new Harmony(Guid);
            PatchReport.PatchAllSafely(Harmony, typeof(MapHudPlugin).Assembly, ModId);
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
