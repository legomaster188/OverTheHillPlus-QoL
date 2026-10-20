using System;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;

namespace OverTheHillPlus.CameraPlus;

// After the namespace line on purpose: the game has its own global ModManager (mod.io).
using OverTheHillPlus.Core.Manager;

/// <summary>
/// Camera+: hood, bumper, chase-far and drone cameras, a field of view per camera, and a key to cycle them. Your view
/// only; nothing is sent to anyone.
/// </summary>
[BepInPlugin(Guid, Name, Version)]
[BepInDependency(Core.CorePlugin.Guid)]
public class CameraPlugin : BasePlugin
{
    public const string Guid = "com.oth.camera";
    public const string Name = "Camera+";
    public const string Version = "1.2.1";

    /// <summary>Mod id: names the config file (oth.camera.cfg), the MODS page entry and the enabled switch.</summary>
    internal const string ModId = "camera";

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
            Description = "Hood, bumper, chase-far and drone cameras (a free orbit that follows your car), a field of " +
                          "view for each, and a key to cycle them.",
            Version = Version,
            DrawSimple = CameraSimple.Draw,
        });
        if (!ModManager.IsEnabled(ModId))
        {
            Logger.LogInfo($"{Name} is switched off on the MODS page: nothing of it runs this session.");
            return;
        }
        try
        {
            CameraConfig.Bind(ModManager.ConfigFor(ModId)); // BepInEx/config/oth.camera.cfg
            CameraFrame.Install();
            Features.Registry.DiscoverAndRegister(typeof(CameraPlugin).Assembly, ModId, CameraConfig.File);
            CameraConfig.File.Save();
            Harmony = new Harmony(Guid);
            PatchReport.PatchAllSafely(Harmony, typeof(CameraPlugin).Assembly, ModId);
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
