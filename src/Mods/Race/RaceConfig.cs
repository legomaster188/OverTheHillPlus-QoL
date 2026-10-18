using BepInEx.Configuration;
using UnityEngine.InputSystem;

namespace OverTheHillPlus.Race;

/// <summary>Race Mode settings in BepInEx/config/oth.race.cfg. Tracks and leaderboards are files in oth.race/.</summary>
internal static class RaceConfig
{
    public static ConfigEntry<Key> DropKey;
    public static ConfigEntry<float> GateRadius;

    public static ConfigEntry<int> Countdown;
    public static ConfigEntry<float> TimeLimitMinutes, FinishWindowSeconds, StartZone;
    public static ConfigEntry<bool> HostRaces, AnnounceResults;

    public static ConfigEntry<bool> JoinRaces;

    public static ConfigEntry<bool> ShowHud, ShowTrackIdle, ShowRoute, ShowOnMap;

    public static ConfigFile File;

    public static void Bind(ConfigFile cfg)
    {
        File = cfg;
        DropKey = cfg.Bind("Track", "DropKey", Key.None, "Hotkey (host or solo): add a checkpoint where your car is. None = no hotkey.");
        GateRadius = cfg.Bind("Track", "GateRadius", 12f, "Size (radius, metres) of new checkpoints.");

        Countdown = cfg.Bind("Race", "Countdown", 5, "Seconds from Start to GO.");
        TimeLimitMinutes = cfg.Bind("Race", "TimeLimitMinutes", 15f, "Racers still out on the track this long after GO don't finish.");
        FinishWindowSeconds = cfg.Bind("Race", "FinishWindowSeconds", 90f, "After the first racer finishes, the others have this long.");
        StartZone = cfg.Bind("Race", "StartZone", 50f, "At GO every racer must be this close to the start (metres; 0 = no check), so driving off in the countdown doesn't pay.");
        HostRaces = cfg.Bind("Race", "HostRaces", true, "When hosting: race yourself too (off: only run the race for your guests).");
        AnnounceResults = cfg.Bind("Race", "AnnounceResults", true, "When hosting: show the winner to everyone in a short notice (players without the mod too).");

        JoinRaces = cfg.Bind("Guest", "JoinRaces", true, "As a guest: take part in the host's races.");

        ShowHud = cfg.Bind("Hud", "ShowHud", true, "Countdown, timer, checkpoint, split and results on screen.");
        ShowTrackIdle = cfg.Bind("Hud", "ShowTrackIdle", true, "Show the track's checkpoints in the world when no race is on.");
        ShowRoute = cfg.Bind("Hud", "ShowRoute", true, "Dotted line toward the next checkpoint during a race.");
        ShowOnMap = cfg.Bind("Hud", "ShowOnMap", true, "Show the checkpoints (and the way to the next one) on the map screen.");
    }
}
