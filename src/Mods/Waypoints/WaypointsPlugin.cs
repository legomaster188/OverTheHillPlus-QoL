using System;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;

namespace OverTheHillPlus.Waypoints;

// After the namespace line on purpose: the game has its own global ModManager (mod.io).
using OverTheHillPlus.Core.Manager;

/// <summary>
/// Waypoints+: named map pins saved per map, shared with the session (the host keeps the shared list), a dotted route
/// to the pin you pick (in the world and on the map), and the host can push a pin to everyone. A Server mod: the
/// shared part runs only in sessions whose host runs it too; saved pins and the route work anywhere.
/// </summary>
[BepInPlugin(Guid, Name, Version)]
[BepInDependency(Core.CorePlugin.Guid)]
public class WaypointsPlugin : BasePlugin
{
    public const string Guid = "com.oth.waypoints";
    public const string Name = "Waypoints+";
    public const string Version = "1.0.0";

    /// <summary>Mod id: names the config file (oth.waypoints.cfg), the MODS page entry and the enabled switch.</summary>
    internal const string ModId = "waypoints";

    internal static ManualLogSource Logger;
    internal static Harmony Harmony;

    public override void Load()
    {
        Logger = Log;
        ModManager.Register(new ModInfo
        {
            Id = ModId,
            Side = ModSide.Server,
            Name = Name,
            Description = "Save named map pins, share them with the session, follow a dotted route to the pin you pick, " +
                          "and (as host) push a pin to everyone.",
            Version = Version,
            DrawSimple = WaypointsSimple.Draw,
        });
        if (!ModManager.IsEnabled(ModId))
        {
            Logger.LogInfo($"{Name} is switched off on the MODS page: nothing of it runs this session.");
            return;
        }
        try
        {
            WaypointsConfig.Bind(ModManager.ConfigFor(ModId)); // BepInEx/config/oth.waypoints.cfg
            Features.Registry.DiscoverAndRegister(typeof(WaypointsPlugin).Assembly, ModId, WaypointsConfig.File);
            WaypointsConfig.File.Save();
            Harmony = new Harmony(Guid);
            PatchReport.PatchAllSafely(Harmony, typeof(WaypointsPlugin).Assembly, ModId);
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
