using System;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;

namespace OverTheHillPlus.LiveryPacks;

// After the namespace line on purpose: the game has its own global ModManager (mod.io).
using OverTheHillPlus.Core.Manager;

/// <summary>
/// Livery Packs: local livery packs from BepInEx/config/oth.liveries/, in the same format as the game's livery mods,
/// added to the garage's own livery list without mod.io. Only reads inside that folder (see LiveryPackScan).
/// </summary>
[BepInPlugin(Guid, Name, Version)]
[BepInDependency(Core.CorePlugin.Guid)]
public class LiveryPacksPlugin : BasePlugin
{
    public const string Guid = "com.oth.liverypacks";
    public const string Name = "Livery Packs";
    public const string Version = "1.0.0";

    /// <summary>Mod id: names the config file (oth.liverypacks.cfg), the MODS page entry and the enabled switch.</summary>
    internal const string ModId = "liverypacks";

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
            Description = "Loads livery packs from a folder (the same format as the game's livery mods) into the " +
                          "garage's livery list, without mod.io. Others see them only if they have the same pack.",
            Version = Version,
            DrawSimple = LiveryPacksSimple.Draw,
        });
        if (!ModManager.IsEnabled(ModId))
        {
            Logger.LogInfo($"{Name} is switched off on the MODS page: nothing of it runs this session.");
            return;
        }
        try
        {
            LiveryPacksConfig.Bind(ModManager.ConfigFor(ModId)); // BepInEx/config/oth.liverypacks.cfg
            LiveryPacksLoader.EnsureFolder();
            Features.Registry.DiscoverAndRegister(typeof(LiveryPacksPlugin).Assembly, ModId, LiveryPacksConfig.File);
            LiveryPacksConfig.File.Save(); // writes the defaults of every entry on first start
            Harmony = new Harmony(Guid);
            PatchReport.PatchAllSafely(Harmony, typeof(LiveryPacksPlugin).Assembly, ModId);
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
