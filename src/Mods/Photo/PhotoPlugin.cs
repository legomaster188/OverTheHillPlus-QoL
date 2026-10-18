using System;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;

namespace OverTheHillPlus.Photo;

// After the namespace line on purpose: the game has its own global ModManager (mod.io).
using OverTheHillPlus.Core.Manager;

/// <summary>
/// Photo &amp; Spectate: high-resolution screenshots, clean view (HUD hidden), cinematic chase-camera presets, wider
/// photo mode limits, and watching another player's car with your own camera. Only changes your own view and files.
/// </summary>
[BepInPlugin(Guid, Name, Version)]
[BepInDependency(Core.CorePlugin.Guid)]
public class PhotoPlugin : BasePlugin
{
    public const string Guid = "com.oth.photo";
    public const string Name = "Photo & Spectate";
    public const string Version = "1.0.0";

    /// <summary>Mod id: names the config file (oth.photo.cfg), the MODS page entry and the enabled switch.</summary>
    internal const string ModId = "photo";

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
            Description = "High-resolution screenshots, a clean view without the HUD, cinematic camera presets, a wider " +
                          "photo mode, and watching other players' cars with your own camera.",
            Version = Version,
            DrawSimple = PhotoSimple.Draw,
        });
        if (!ModManager.IsEnabled(ModId))
        {
            Logger.LogInfo($"{Name} is switched off on the MODS page: nothing of it runs this session.");
            return;
        }
        try
        {
            PhotoModConfig.Bind(ModManager.ConfigFor(ModId)); // BepInEx/config/oth.photo.cfg
            PhotoFrame.Install();
            Features.Registry.DiscoverAndRegister(typeof(PhotoPlugin).Assembly, ModId, PhotoModConfig.File);
            PhotoModConfig.File.Save(); // writes the defaults of every entry on first start
            Harmony = new Harmony(Guid);
            PatchReport.PatchAllSafely(Harmony, typeof(PhotoPlugin).Assembly, ModId);
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
