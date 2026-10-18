using System;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;

namespace OverTheHillPlus.Radio;

// After the namespace line on purpose: the game has its own global ModManager (mod.io).
using OverTheHillPlus.Core.Manager;

/// <summary>
/// Radio: your own music in the car. Plays mp3, ogg and wav files from BepInEx/config/oth.radio (each sub-folder is a
/// station) through the game's audio, with keys for next / previous track and station and the volume, a "now
/// playing" notice, and turning itself down while the game's own music plays. Only you hear it.
/// </summary>
[BepInPlugin(Guid, Name, Version)]
[BepInDependency(Core.CorePlugin.Guid)]
public class RadioPlugin : BasePlugin
{
    public const string Guid = "com.oth.radio";
    public const string Name = "Radio";
    public const string Version = "1.0.0";

    /// <summary>Mod id: names the config file (oth.radio.cfg), the MODS page entry and the enabled switch.</summary>
    internal const string ModId = "radio";

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
            Description = "Plays your own mp3, ogg and wav files while you drive, from BepInEx/config/oth.radio (each " +
                          "sub-folder is a station). Keys for track, station and volume, a \"now playing\" notice, and " +
                          "it turns down while the game's music plays. Only you hear it.",
            Version = Version,
            DrawSimple = RadioSimple.Draw,
        });
        if (!ModManager.IsEnabled(ModId))
        {
            Logger.LogInfo($"{Name} is switched off on the MODS page: nothing of it runs this session.");
            return;
        }
        try
        {
            RadioConfig.Bind(ModManager.ConfigFor(ModId)); // BepInEx/config/oth.radio.cfg
            RadioPlayer.Init();
            Features.Registry.DiscoverAndRegister(typeof(RadioPlugin).Assembly, ModId, RadioConfig.File);
            RadioConfig.File.Save(); // writes the defaults of every entry on first start
            Harmony = new Harmony(Guid);
            PatchReport.PatchAllSafely(Harmony, typeof(RadioPlugin).Assembly, ModId);
            Logger.LogInfo($"{Name} {Version} loaded ({RadioPlayer.TrackCount} tracks in {RadioPlayer.Root}).");
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
