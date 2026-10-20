using BepInEx.Configuration;
using UnityEngine.InputSystem;

namespace OverTheHillPlus.Flares;

/// <summary>Flares+'s settings in BepInEx/config/oth.flares.cfg.</summary>
internal static class FlaresConfig
{
    public static ConfigEntry<Key> FlareKey;
    public static ConfigEntry<bool> ShowFlares, OnMap, HostRelay, AutoFlare, TagsFar, TagMetres, TagOcclusion;
    public static ConfigEntry<float> Cooldown, Duration, Apex, PingSeconds, AutoAfter, AutoRepeat;
    public static ConfigEntry<float> TagDistance, TagFadeStart, TagFullSize, TagMinSize;

    public static ConfigFile File;

    public static void Bind(ConfigFile cfg)
    {
        File = cfg;
        const string flare = "Flare";
        FlareKey = cfg.Bind(flare, "Key", Key.F4, "Fires a flare from your car (None = no key).");
        Cooldown = cfg.Bind(flare, "Cooldown", 10f, new ConfigDescription(
            "Seconds between your flares (a host never allows a player more than one every 3 s and six a minute).", new AcceptableValueRange<float>(5f, 60f)));
        ShowFlares = cfg.Bind(flare, "ShowOthers", true, "Show the flares other players fire (off: you still fire yours for them).");
        Duration = cfg.Bind(flare, "Duration", 25f, new ConfigDescription("Seconds a flare stays up.", new AcceptableValueRange<float>(8f, 60f)));
        Apex = cfg.Bind(flare, "Height", 160f, new ConfigDescription("Metres a flare climbs.", new AcceptableValueRange<float>(60f, 400f)));
        OnMap = cfg.Bind(flare, "ShowOnMap", true, "A flare also shows as a ping on the map, the compass and the minimap (if installed) for a while.");
        PingSeconds = cfg.Bind(flare, "PingSeconds", 20f, new ConfigDescription("Seconds the ping stays on the maps.", new AcceptableValueRange<float>(5f, 60f)));
        HostRelay = cfg.Bind("Host", "RelayFlares", true, "Host: pass the flares of players with this mod on to everyone (the host checks each one).");

        AutoFlare = cfg.Bind("Auto flare", "Enabled", false, "Fire a flare by itself when your car lies on its side or roof and does not move.");
        AutoAfter = cfg.Bind("Auto flare", "After", 15f, new ConfigDescription("Seconds stuck before the flare goes up.", new AcceptableValueRange<float>(5f, 120f)));
        AutoRepeat = cfg.Bind("Auto flare", "RepeatEvery", 60f, new ConfigDescription("Seconds until the next one while you are still stuck.", new AcceptableValueRange<float>(20f, 300f)));

        const string tags = "Name tags";
        TagsFar = cfg.Bind(tags, "SeeFarther", true, "Keep the name tags above other players' cars up much farther away (the tags are Map & HUD's / the game's own).");
        TagDistance = cfg.Bind(tags, "Distance", 600f, new ConfigDescription("Metres at which a name tag is gone.", new AcceptableValueRange<float>(100f, 3000f)));
        TagFadeStart = cfg.Bind(tags, "FadeStart", 0.6f, new ConfigDescription("Where the fade begins, as a fraction of Distance.", new AcceptableValueRange<float>(0.1f, 0.95f)));
        TagFullSize = cfg.Bind(tags, "FullSizeUntil", 60f, new ConfigDescription("Tags are full size up to this many metres, then shrink.", new AcceptableValueRange<float>(10f, 500f)));
        TagMinSize = cfg.Bind(tags, "MinSize", 0.55f, new ConfigDescription("The smallest a tag gets (1 = never shrinks).", new AcceptableValueRange<float>(0.3f, 1f)));
        TagMetres = cfg.Bind(tags, "ShowDistance", true, "Show the distance in metres under the name.");
        TagOcclusion = cfg.Bind(tags, "HideBehindObstacles", false, "Hide a tag while terrain or buildings are between you and the car.");
    }
}
