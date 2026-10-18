using System;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;

namespace OverTheHillPlus.HostPowers;

// After the namespace line on purpose: the game has its own global ModManager (mod.io).
using OverTheHillPlus.Core.Manager;

/// <summary>
/// Host Powers (private): what your game can do to the session it hosts, for every player (with or without mods):
/// repair, wash, refill, bring, flip, launch and kick; weather, time and camping; lobby god mode; events and lobby car
/// rules; moving players and teleport requests; teleports and waypoints; the track builder; and the Create Server
/// rows for start weather, time and tools.
/// </summary>
[BepInPlugin(Guid, Name, Version)]
[BepInDependency(Core.CorePlugin.Guid)]
public class HostPowersPlugin : BasePlugin
{
    public const string Guid = "com.oth.hostpowers";
    public const string Name = "Host Powers";
    public const string Version = "1.0.0";

    /// <summary>Mod id: names the config file (oth.hostpowers.cfg), the MODS page entry and the enabled switch.</summary>
    internal const string ModId = "hostpowers";

    internal static ManualLogSource Logger;
    internal static Harmony Harmony;

    public override void Load()
    {
        Logger = Log;
        // Listed on the MODS page even when switched off, so it can be switched back on.
        ModManager.Register(new ModInfo
        {
            Id = ModId,
            Side = ModSide.Host,
            Name = Name,
            Description = "When you host: repair, wash, bring, flip or kick players, weather and time, events, moving " +
                          "everyone to you or a pin, teleports, the track builder and start settings for your servers.",
            Version = Version,
            Private = false,
            DrawSimple = HostPowersSimple.Draw,
        });
        if (!ModManager.IsEnabled(ModId))
        {
            Logger.LogInfo($"{Name} is switched off on the MODS page: nothing of it runs this session.");
            return;
        }
        try
        {
            HostPowersConfig.Bind(ModManager.ConfigFor(ModId)); // BepInEx/config/oth.hostpowers.cfg
            HostPowersFrame.Install();
            HostPowersPage.RegisterSections();
            Features.Registry.DiscoverAndRegister(typeof(HostPowersPlugin).Assembly, ModId, HostPowersConfig.File);
            HostPowersConfig.File.Save(); // writes the defaults of every entry on first start
            Harmony = new Harmony(Guid);
            PatchReport.PatchAllSafely(Harmony, typeof(HostPowersPlugin).Assembly, ModId);
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
