using BepInEx.Configuration;

namespace OverTheHillPlus.WorldTools;

/// <summary>World Tools+'s settings in BepInEx/config/oth.worldtools.cfg. They apply while you host (or play solo).</summary>
internal static class WorldToolsConfig
{
    private const string Section = "World";

    public static ConfigEntry<float> Gravity;
    public static ConfigEntry<float> TimeScale;
    public static ConfigEntry<float> DaySpeed;
    public static ConfigEntry<bool> LockWeather;
    public static ConfigEntry<int> LockedWeatherId;

    public static ConfigFile File;

    public static void Bind(ConfigFile cfg)
    {
        File = cfg;
        Gravity = cfg.Bind(Section, "Gravity", 1f, new ConfigDescription(
            "Gravity for the whole world as a multiple of normal (1 = normal, 0.165 = moon). The host simulates every car, " +
            "so everyone feels it.", new AcceptableValueRange<float>(WorldMath.MinGravity, WorldMath.MaxGravity)));
        TimeScale = cfg.Bind(Section, "TimeScale", 1f, new ConfigDescription(
            "Game speed for everyone (1 = normal, 0.5 = slow motion), through the game's own networked time scale.",
            new AcceptableValueRange<float>(WorldMath.MinTimeScale, WorldMath.MaxTimeScale)));
        DaySpeed = cfg.Bind(Section, "DaySpeed", 1f, new ConfigDescription(
            "How fast the clock runs (1 = normal, 2 = days half as long, 0.5 = twice as long). Everyone's clock follows the host.",
            new AcceptableValueRange<float>(WorldMath.MinDaySpeed, WorldMath.MaxDaySpeed)));
        LockWeather = cfg.Bind(Section, "LockWeather", false, "Keep the weather on LockedWeatherId (put back when the game changes it).");
        LockedWeatherId = cfg.Bind(Section, "LockedWeatherId", -1, "The weather profile id to keep (the game's own ids; -1 = none).");
    }
}
