using BepInEx.Configuration;
using UnityEngine.InputSystem;

namespace OverTheHillPlus.Radio;

/// <summary>When the radio may play.</summary>
internal enum RadioPlayWhen { InCar, Always }

/// <summary>Radio's settings in BepInEx/config/oth.radio.cfg (the music itself goes in the oth.radio folder).</summary>
internal static class RadioConfig
{
    public static ConfigEntry<bool> Power;
    public static ConfigEntry<float> Volume;
    public static ConfigEntry<RadioPlayWhen> PlayWhen;
    public static ConfigEntry<bool> Shuffle;
    public static ConfigEntry<string> Station;
    public static ConfigEntry<bool> DuckForGameMusic;
    public static ConfigEntry<float> DuckLevel;
    public static ConfigEntry<bool> FollowMasterVolume;
    public static ConfigEntry<bool> ShowNowPlaying;

    public static ConfigEntry<Key> PowerKey, NextKey, PrevKey, NextStationKey, PrevStationKey, VolumeUpKey, VolumeDownKey;

    public static ConfigFile File;

    public static void Bind(ConfigFile cfg)
    {
        File = cfg;
        Power = cfg.Bind("Radio", "On", true, "Radio switched on (the power key flips it).");
        Volume = cfg.Bind("Radio", "Volume", 0.6f, new ConfigDescription("Radio volume.", new AcceptableValueRange<float>(0f, 1f)));
        PlayWhen = cfg.Bind("Radio", "PlayWhen", RadioPlayWhen.InCar, "InCar: only while you are in your car in a world. Always: in the menus too.");
        Shuffle = cfg.Bind("Radio", "Shuffle", false, "Play each station in a random order.");
        Station = cfg.Bind("Radio", "Station", "", "The station (sub-folder) last listened to.");
        DuckForGameMusic = cfg.Bind("Radio", "DuckForGameMusic", true, "Turn the radio down while the game's own music plays.");
        DuckLevel = cfg.Bind("Radio", "DuckLevel", 0.25f, new ConfigDescription("Radio volume while the game's music plays (share of the normal volume).", new AcceptableValueRange<float>(0f, 1f)));
        FollowMasterVolume = cfg.Bind("Radio", "FollowMasterVolume", true, "Scale the radio by the game's master volume.");
        ShowNowPlaying = cfg.Bind("Radio", "ShowNowPlaying", true, "Show the station and track at the top of the screen when a track starts.");

        PowerKey = cfg.Bind("Radio keys", "PowerKey", Key.Slash, "Switch the radio on / off.");
        NextKey = cfg.Bind("Radio keys", "NextTrackKey", Key.Period, "Next track.");
        PrevKey = cfg.Bind("Radio keys", "PreviousTrackKey", Key.Comma, "Previous track.");
        NextStationKey = cfg.Bind("Radio keys", "NextStationKey", Key.Quote, "Next station.");
        PrevStationKey = cfg.Bind("Radio keys", "PreviousStationKey", Key.Semicolon, "Previous station.");
        VolumeUpKey = cfg.Bind("Radio keys", "VolumeUpKey", Key.Equals, "Radio louder.");
        VolumeDownKey = cfg.Bind("Radio keys", "VolumeDownKey", Key.Minus, "Radio quieter.");
    }
}
