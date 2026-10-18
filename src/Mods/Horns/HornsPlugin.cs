using System;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;

namespace OverTheHillPlus.Horns;

// After the namespace line on purpose: the game has its own global ModManager (mod.io).
using OverTheHillPlus.Core.Manager;

/// <summary>
/// Custom Horns: pick a horn (built-in ones made at runtime, or your own wav file from BepInEx/config/oth.horns), and
/// every player with this mod hears it when you honk. Synced over the mod network as a horn id; custom files go in
/// chunks with a 256 KB cap. A Server mod: its networked part runs only where the session runs it too.
/// </summary>
[BepInPlugin(Guid, Name, Version)]
[BepInDependency(Core.CorePlugin.Guid)]
public class HornsPlugin : BasePlugin
{
    public const string Guid = "com.oth.horns";
    public const string Name = "Custom Horns";
    public const string Version = "1.0.0";

    /// <summary>Mod id: names the config file (oth.horns.cfg), the MODS page entry and the enabled switch.</summary>
    internal const string ModId = "horns";

    internal static ManualLogSource Logger;
    internal static Harmony Harmony;

    public override void Load()
    {
        Logger = Log;
        // Listed on the MODS page even when switched off, so it can be switched back on.
        ModManager.Register(new ModInfo
        {
            Id = ModId,
            Side = ModSide.Server,
            Name = Name,
            Description = "Choose a horn sound: a built-in one or your own wav file. When you honk, every player " +
                          "with this mod hears your horn (others hear the game's). Synced over the mod network; " +
                          "own files are sent in chunks, up to 256 KB and 10 s.",
            Version = Version,
            DrawSimple = HornsSimple.Draw,
        });
        if (!ModManager.IsEnabled(ModId))
        {
            Logger.LogInfo($"{Name} is switched off on the MODS page: nothing of it runs this session.");
            return;
        }
        try
        {
            HornsConfig.Bind(ModManager.ConfigFor(ModId)); // BepInEx/config/oth.horns.cfg
            HornSounds.Init();
            Features.Registry.DiscoverAndRegister(typeof(HornsPlugin).Assembly, ModId, HornsConfig.File);
            HornsConfig.File.Save(); // writes the defaults of every entry on first start
            Harmony = new Harmony(Guid);
            PatchReport.PatchAllSafely(Harmony, typeof(HornsPlugin).Assembly, ModId);
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
