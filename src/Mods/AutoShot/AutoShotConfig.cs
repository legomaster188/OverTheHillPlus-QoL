using BepInEx.Configuration;

namespace OverTheHillPlus.AutoShot;

/// <summary>Auto Screenshot's settings in BepInEx/config/oth.autoshot.cfg.</summary>
internal static class AutoShotConfig
{
    internal enum Folder
    {
        /// <summary>BepInEx/config/oth.screenshots/</summary>
        ModFolder,
        /// <summary>Pictures/Over the Hill/</summary>
        Pictures,
        /// <summary>The game's own screenshot folder.</summary>
        GameScreenshots,
    }

    public static ConfigEntry<bool> Jumps, Flips, Landmarks;
    public static ConfigEntry<float> MinAirTime, Cooldown;
    public static ConfigEntry<Folder> SaveTo;
    public static ConfigEntry<bool> ShowToast;

    public static ConfigFile File;

    public static void Bind(ConfigFile cfg)
    {
        File = cfg;
        Jumps = cfg.Bind("Triggers", "BigJumps", true, "Take a screenshot mid-air on a big jump.");
        Flips = cfg.Bind("Triggers", "Flips", true, "Take a screenshot when the car flips over in the air.");
        Landmarks = cfg.Bind("Triggers", "Landmarks", true, "Take a screenshot when you reach a place for the first time.");
        MinAirTime = cfg.Bind("Triggers", "MinAirTime", 1f, "Seconds in the air before a jump counts as big (0.5 to 5).");
        Cooldown = cfg.Bind("Triggers", "Cooldown", 20f, "Seconds at least between two screenshots (5 to 600).");
        SaveTo = cfg.Bind("Files", "Folder", Folder.ModFolder,
            "Where screenshots go: ModFolder (BepInEx/config/oth.screenshots), Pictures (Pictures/Over the Hill) or GameScreenshots (the game's own folder).");
        ShowToast = cfg.Bind("Files", "ShowToast", true, "Show a short note on screen after a screenshot is saved (never in the picture).");
    }
}
