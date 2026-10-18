using BepInEx.Configuration;

namespace OverTheHillPlus.DrivingQol;

/// <summary>
/// Driving QoL's own settings in BepInEx/config/oth.drivingqol.cfg. Same section and keys as in the old
/// OverTheHillPlus config ([QoL] ...), so the settings migration can copy them over (Server Utils' host governor reads
/// [QoL] FpsCap / BackgroundFpsCap through core by these names). The unstuck feature binds its own [Unstuck] entries on
/// <see cref="File"/> in its Register.
/// </summary>
internal static class DrivingQolConfig
{
    public static ConfigEntry<int> FpsCap;
    public static ConfigEntry<bool> RunInBackground;
    public static ConfigEntry<int> BackgroundFpsCap;
    public static ConfigEntry<float> FovOverride;
    public static ConfigEntry<bool> SkipSplash;
    public static ConfigEntry<float> FadeSpeed;
    public static ConfigEntry<float> CompassRange;
    public static ConfigEntry<bool> AutoHeadlights;
    public static ConfigEntry<bool> HideTutorialHints;

    public static ConfigFile File;

    public static void Bind(ConfigFile cfg)
    {
        File = cfg;
        FpsCap = cfg.Bind("QoL", "FpsCap", 0, "Frame-rate cap override (0 = use the game's setting). Turns VSync off when set.");
        RunInBackground = cfg.Bind("QoL", "RunInBackground", true, "Keep the game running when its window isn't focused (an alt-tabbed host otherwise freezes the session).");
        BackgroundFpsCap = cfg.Bind("QoL", "BackgroundFpsCap", 30, "Frame-rate cap while the window isn't focused (0 = no change). Saves PC power; doesn't slow the session.");
        FovOverride = cfg.Bind("QoL", "FovOverride", 0f, "Driving camera field of view (0 = use the game's setting).");
        SkipSplash = cfg.Bind("QoL", "SkipSplash", true, "Skip the logo screens at startup.");
        FadeSpeed = cfg.Bind("QoL", "FadeDurationMultiplier", 0.5f, "Screen fade length (1 = stock, 0.5 = twice as fast).");
        CompassRange = cfg.Bind("QoL", "CompassRangeMultiplier", 1f, "How far away places still show on the compass.");
        AutoHeadlights = cfg.Bind("QoL", "AutoHeadlights", false, "Headlights on at night (19:00-07:00), off in the day.");
        HideTutorialHints = cfg.Bind("QoL", "HideTutorialHints", false, "Don't show tutorial hint pop-ups.");
    }
}
