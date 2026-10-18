using System;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;

namespace OverTheHillPlus.Race;

// After the namespace line on purpose: the game has its own global ModManager (mod.io).
using OverTheHillPlus.Core.Manager;

/// <summary>
/// Race Mode: the host lays out checkpoints (at its car, or from map pins), starts a countdown for everyone with the
/// mod, and times every racer through the gates in order: sprints or laps, splits against the track's best, a
/// leaderboard per track, saved tracks, and clear rules for not finishing. Solo it is a time trial. A Server mod: in a
/// session it runs only when the host runs it too.
/// </summary>
[BepInPlugin(Guid, Name, Version)]
[BepInDependency(Core.CorePlugin.Guid)]
public class RacePlugin : BasePlugin
{
    public const string Guid = "com.oth.race";
    public const string Name = "Race Mode";
    public const string Version = "1.0.0";

    /// <summary>Mod id: names the config file (oth.race.cfg), the MODS page entry and the enabled switch.</summary>
    internal const string ModId = "race";

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
            Description = "Lay out checkpoints, start a countdown for everyone, and race: sprints or laps, splits, " +
                          "a leaderboard per track and saved tracks. Solo it's a time trial.",
            Version = Version,
            DrawSimple = RaceSimple.Draw,
        });
        if (!ModManager.IsEnabled(ModId))
        {
            Logger.LogInfo($"{Name} is switched off on the MODS page: nothing of it runs this session.");
            return;
        }
        try
        {
            RaceConfig.Bind(ModManager.ConfigFor(ModId)); // BepInEx/config/oth.race.cfg
            Features.Registry.DiscoverAndRegister(typeof(RacePlugin).Assembly, ModId, RaceConfig.File);
            RaceConfig.File.Save();
            Harmony = new Harmony(Guid);
            PatchReport.PatchAllSafely(Harmony, typeof(RacePlugin).Assembly, ModId);
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
