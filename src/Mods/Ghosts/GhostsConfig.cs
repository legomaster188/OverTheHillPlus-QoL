using BepInEx.Configuration;
using UnityEngine.InputSystem;

namespace OverTheHillPlus.Ghosts;

/// <summary>Ghost Replays' settings in BepInEx/config/oth.ghosts.cfg (the ghosts themselves are in BepInEx/config/oth.ghosts/).</summary>
internal static class GhostsConfig
{
    public static ConfigEntry<float> PlaybackSpeed;
    public static ConfigEntry<float> Opacity;
    public static ConfigEntry<bool> Loop;
    public static ConfigEntry<bool> RaceBestGhost;
    public static ConfigEntry<bool> RecordRaces;
    public static ConfigEntry<int> MaxMinutes;
    public static ConfigEntry<Key> RecordKey;

    public static ConfigFile File;

    public static void Bind(ConfigFile cfg)
    {
        File = cfg;
        PlaybackSpeed = cfg.Bind("Ghosts", "PlaybackSpeed", 1f, "Playback speed of a ghost you play from the list (races always play at 1x).");
        Opacity = cfg.Bind("Ghosts", "Opacity", 0.35f, "How solid the ghost car looks (0.1 = faint, 0.8 = nearly solid).");
        Loop = cfg.Bind("Ghosts", "Loop", false, "Start a ghost from the list again when it reaches its end.");
        RaceBestGhost = cfg.Bind("Ghosts", "RaceBestGhost", true, "On a Race Mode track, your best ghost on that track starts with the race.");
        RecordRaces = cfg.Bind("Ghosts", "RecordRaces", true, "Record every Race Mode race you drive; finished races are saved with their time.");
        MaxMinutes = cfg.Bind("Ghosts", "MaxMinutes", 30, new ConfigDescription("A recording stops and saves itself after this many minutes.",
            new AcceptableValueRange<int>(1, 120)));
        RecordKey = cfg.Bind("Ghosts", "RecordKey", Key.None, "Starts / stops a recording (None = only from the mod page).");
    }
}
