using BepInEx.Configuration;

namespace OverTheHillPlus.ServerBrowser;

/// <summary>
/// Server Browser+'s own settings in BepInEx/config/oth.serverbrowser.cfg. Same sections and keys as in the old
/// OverTheHillPlus config ([ServerBrowser] ...), so the settings migration can copy them over. The features of this
/// mod (Browser+, friends, refresh, All regions, Rejoin) bind their own entries on <see cref="File"/> in their Register.
/// </summary>
internal static class ServerBrowserConfig
{
    public static ConfigEntry<bool> ColorRows;
    public static ConfigEntry<bool> ShowAge;
    public static ConfigEntry<bool> RestoreNameColours;
    public static ConfigEntry<bool> ThrottleSessionUpdates;
    public static ConfigEntry<float> SessionUpdateInterval;
    public static ConfigEntry<string> ColorPublic;
    public static ConfigEntry<string> ColorPrivate;
    public static ConfigEntry<string> ColorFull;
    public static ConfigEntry<string> ColorIncompatible;

    public static ConfigFile File;

    public static void Bind(ConfigFile cfg)
    {
        File = cfg;
        ColorRows = cfg.Bind("ServerBrowser", "ColorRows", true, "Color-code server rows by status.");
        ShowAge = cfg.Bind("ServerBrowser", "ShowAge", true, "Show how long ago each server was first seen.");
        RestoreNameColours = cfg.Bind("ServerBrowser", "RestoreNameColours", true,
            "Show server names with their colours again (the game strips them since its 2026-10-16 update). Only harmless tags are kept.");
        ThrottleSessionUpdates = cfg.Bind("ServerBrowser", "ThrottleSessionUpdates", true, "Skip identical list updates and rebuild at most once per interval.");
        SessionUpdateInterval = cfg.Bind("ServerBrowser", "SessionUpdateInterval", 1.5f, "Minimum seconds between list rebuilds.");
        ColorPublic = cfg.Bind("ServerBrowser", "ColorPublic", "#3FBF5F", "Public, joinable.");
        ColorPrivate = cfg.Bind("ServerBrowser", "ColorPrivate", "#E0A030", "Private / locked.");
        ColorFull = cfg.Bind("ServerBrowser", "ColorFull", "#D04545", "Full or closed.");
        ColorIncompatible = cfg.Bind("ServerBrowser", "ColorIncompatible", "#7A7A7A", "Different game version.");
    }
}
