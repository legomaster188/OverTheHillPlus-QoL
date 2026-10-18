using BepInEx.Configuration;
using UnityEngine.InputSystem;

namespace OverTheHillPlus.Stats;

/// <summary>Trip Stats' settings in BepInEx/config/oth.stats.cfg. The numbers themselves are in oth.stats.json.</summary>
internal static class StatsConfig
{
    internal enum CardShows
    {
        Session,
        AllTime,
        Both,
    }

    public static ConfigEntry<bool> ShowCard;
    public static ConfigEntry<HudCorner> Corner;
    public static ConfigEntry<CardShows> Shows;
    public static ConfigEntry<bool> Imperial;
    public static ConfigEntry<float> CardOpacity;
    public static ConfigEntry<Key> CardKey;
    public static ConfigEntry<bool> NewSessionResets;

    public static ConfigFile File;

    public static void Bind(ConfigFile cfg)
    {
        File = cfg;
        ShowCard = cfg.Bind("Card", "Show", true, "Show the trip card on screen while driving.");
        Corner = cfg.Bind("Card", "Corner", HudCorner.TopLeft, "Screen corner of the trip card.");
        Shows = cfg.Bind("Card", "Shows", CardShows.Session, "What the card shows: this session, all time, or both.");
        Imperial = cfg.Bind("Card", "Imperial", false, "Miles, mph and feet instead of kilometres, km/h and metres.");
        CardOpacity = cfg.Bind("Card", "Opacity", 0.82f, "Background opacity of the card (0.2 to 1).");
        CardKey = cfg.Bind("Card", "ToggleKey", Key.None, "Shows / hides the trip card (None = no hotkey).");
        NewSessionResets = cfg.Bind("Session", "ResetOnNewSession", true,
            "Start the session numbers again when you join or start another session (all-time numbers always add up).");
    }
}
