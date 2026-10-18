using BepInEx.Configuration;

namespace OverTheHillPlus.FullMap;

/// <summary>Explore Full Map's settings in BepInEx/config/oth.fullmap.cfg.</summary>
internal static class FullMapConfig
{
    public static ConfigEntry<bool> KeepRevealed;

    public static ConfigFile File;

    public static void Bind(ConfigFile cfg)
    {
        File = cfg;
        KeepRevealed = cfg.Bind("Map", "KeepRevealed", false,
            "Reveal the whole map again whenever a world (or a new map) loads. Local only: works as a guest too.");
    }
}
