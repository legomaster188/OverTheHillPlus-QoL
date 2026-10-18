using BepInEx.Configuration;

namespace OverTheHillPlus.Tag;

/// <summary>Tag & Hide and Seek's settings in BepInEx/config/oth.tag.cfg. The round settings only matter when hosting.</summary>
internal static class TagConfig
{
    public static ConfigEntry<TagMode> Mode;
    public static ConfigEntry<int> RoundMinutes, HeadStartSeconds, Seekers;
    public static ConfigEntry<float> TagBackSeconds, ContactDistance;
    public static ConfigEntry<bool> Announce, HoldVanillaSeekers, ShowScoreboard, ShowItMarker;

    public static ConfigFile File;

    public static void Bind(ConfigFile cfg)
    {
        File = cfg;
        Mode = cfg.Bind("Round", "Mode", TagMode.Tag, "Game the host starts: Tag or HideAndSeek.");
        RoundMinutes = cfg.Bind("Round", "RoundMinutes", 5, new ConfigDescription("Round length in minutes (hide and seek: after the head start).", new AcceptableValueRange<int>(1, 60)));
        HeadStartSeconds = cfg.Bind("Round", "HeadStartSeconds", 60, new ConfigDescription("Hide and seek: seconds the hiders get before the seekers may move.", new AcceptableValueRange<int>(0, 600)));
        Seekers = cfg.Bind("Round", "Seekers", 1, new ConfigDescription("Hide and seek: how many seekers start the round (picked at random).", new AcceptableValueRange<int>(1, 8)));
        TagBackSeconds = cfg.Bind("Round", "TagBackSeconds", 5f, new ConfigDescription("Tag: the newly tagged player can't tag back the one who tagged them for this long (at least 1 s, so one touch can't undo itself).", new AcceptableValueRange<float>(1f, 60f)));
        ContactDistance = cfg.Bind("Round", "ContactDistance", 3.5f, new ConfigDescription("Cars whose centres come this close (metres) count as touching, besides real collisions. 0 = real collisions only.", new AcceptableValueRange<float>(0f, 8f)));
        Announce = cfg.Bind("Round", "AnnounceToEveryone", true, "Host: announce tags, finds and results with the game's pop-up, so players without the mod can play too.");
        HoldVanillaSeekers = cfg.Bind("Round", "HoldVanillaSeekers", false, "Host: during the head start, also hold the cars of seekers who don't run the mod (they get no blackout or timer, only the pop-up).");
        ShowScoreboard = cfg.Bind("Display", "ShowScoreboard", true, "Show the round's scoreboard on screen.");
        ShowItMarker = cfg.Bind("Display", "ShowItMarker", true, "Tag: show an IT marker above the car that is it, and mark it on the map.");
    }
}
