using BepInEx.Configuration;

namespace OverTheHillPlus.RealNights;

/// <summary>Nights+'s settings in BepInEx/config/oth.realnights.cfg.</summary>
internal static class RealNightsConfig
{
    public static ConfigEntry<bool> DarkNights;
    public static ConfigEntry<float> Darkness, StormDarkness, TwilightKeep;
    public static ConfigEntry<bool> Stars;
    public static ConfigEntry<float> StarBrightness, MilkyWay;
    public static ConfigEntry<bool> Lamps, LampShadows;
    public static ConfigEntry<float> LampIntensity, LampRange, HeadlightBoost;
    public static ConfigEntry<int> MaxLights, LightsPerCar;
    public static ConfigEntry<float> CullDistance;

    public static ConfigFile File;

    public static void Bind(ConfigFile cfg)
    {
        File = cfg;
        DarkNights = cfg.Bind("Night", "DarkNights", true, "Darker nights: the ambient light and the moon fall, so only moonlight and your lights are left.");
        Darkness = cfg.Bind("Night", "Darkness", 0.75f, "How dark the night gets: 0 = the game's night, 1 = almost black.");
        StormDarkness = cfg.Bind("Night", "StormDarkness", 0.85f, "How much storms darken the sky: at night they go pitch black, by day they only dim it. 0 = the game's storms.");
        TwilightKeep = cfg.Bind("Night", "TwilightKeep", 0.5f, "How long the game's own dusk and dawn colours last before the dark arrives (0 = darkens evenly, 1 = late).");

        Stars = cfg.Bind("Sky", "Stars", true, "A starry sky on clear nights, with the Milky Way. Off: the game's own stars.");
        StarBrightness = cfg.Bind("Sky", "StarBrightness", 1.5f, "Star brightness on clear nights (Graphics+'s star setting is replaced by this at night).");
        MilkyWay = cfg.Bind("Sky", "MilkyWay", 1.2f, "Milky Way brightness on clear nights (0 = none).");

        Lamps = cfg.Bind("Lamps", "Lamps", true, "Roof light bars, bull-bar lamps and the custom light bars light up the ground at night when your headlights are on.");
        LampIntensity = cfg.Bind("Lamps", "Intensity", 1f, "How bright the lamps shine, compared with the car's own headlights.");
        LampRange = cfg.Bind("Lamps", "Range", 1f, "How far the lamps shine, compared with the car's own headlights.");
        LampShadows = cfg.Bind("Lamps", "Shadows", true, "Your own car's front lamp casts shadows (a little slower). Other cars' lamps never do.");
        MaxLights = cfg.Bind("Lamps", "MaxLights", 8, "The most lamp lights in the world at once; the nearest cars get them first.");
        LightsPerCar = cfg.Bind("Lamps", "LightsPerCar", 3, "The most lamp lights on one car.");
        CullDistance = cfg.Bind("Lamps", "CullDistance", 100f, "Cars farther away than this (metres) get no lamp lights.");
        HeadlightBoost = cfg.Bind("Lamps", "HeadlightBoost", 1.25f, "Headlights shine this much brighter and farther at night (1 = the game's).");
    }
}
