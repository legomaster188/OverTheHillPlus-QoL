using BepInEx.Configuration;
using UnityEngine.InputSystem;

namespace OverTheHillPlus;

internal static class ModConfig
{
    // General
    public static ConfigEntry<Key> MenuKey;
    public static ConfigEntry<Key> HudKey;
    public static ConfigEntry<bool> ShowHud;
    public static ConfigEntry<int> FpsCap;
    public static ConfigEntry<bool> RunInBackground;
    public static ConfigEntry<int> BackgroundFpsCap;
    public static ConfigEntry<float> FovOverride;
    public static ConfigEntry<bool> SkipSplash;
    public static ConfigEntry<float> FadeSpeed;
    public static ConfigEntry<float> CompassRange;
    public static ConfigEntry<bool> AutoHeadlights;
    public static ConfigEntry<bool> HideTutorialHints;



    // Multiplayer
    public static ConfigEntry<bool> ExpandLobbySize;
    public static ConfigEntry<int> DefaultMaxPlayers;
    public static ConfigEntry<bool> LobbyGuards;
    public static ConfigEntry<int> MeasuredCapacity;
    public static ConfigEntry<bool> MainMenuButton;

    // Server browser
    public static ConfigEntry<bool> ColorRows;
    public static ConfigEntry<bool> ShowAge;
    public static ConfigEntry<bool> ThrottleSessionUpdates;
    public static ConfigEntry<float> SessionUpdateInterval;
    public static ConfigEntry<bool> ProfileServerBrowser;
    public static ConfigEntry<string> ColorPublic;
    public static ConfigEntry<string> ColorPrivate;
    public static ConfigEntry<string> ColorFull;
    public static ConfigEntry<string> ColorIncompatible;

    public static ConfigFile File;

    public static void Bind(ConfigFile cfg)
    {
        File = cfg;
        // Sliders change values every frame; save when the menu closes instead.
        cfg.SaveOnConfigSet = false;
        MenuKey = cfg.Bind("General", "MenuKey", Key.F7, "Opens/closes the mod menu.");
        FpsCap = cfg.Bind("QoL", "FpsCap", 0, "Frame-rate cap override (0 = use the game's setting). Turns VSync off when set.");
        RunInBackground = cfg.Bind("QoL", "RunInBackground", true, "Keep the game running when its window isn't focused (an alt-tabbed host otherwise freezes the session).");
        BackgroundFpsCap = cfg.Bind("QoL", "BackgroundFpsCap", 30, "Frame-rate cap while the window isn't focused (0 = no change). Saves PC power; doesn't slow the session.");
        FovOverride = cfg.Bind("QoL", "FovOverride", 0f, "Driving camera field of view (0 = use the game's setting).");
        SkipSplash = cfg.Bind("QoL", "SkipSplash", true, "Skip the logo screens at startup.");
        FadeSpeed = cfg.Bind("QoL", "FadeDurationMultiplier", 0.5f, "Screen fade length (1 = stock, 0.5 = twice as fast).");
        CompassRange = cfg.Bind("QoL", "CompassRangeMultiplier", 1f, "How far away places still show on the compass.");
        AutoHeadlights = cfg.Bind("QoL", "AutoHeadlights", false, "Headlights on at night (19:00-07:00), off in the day.");
        HideTutorialHints = cfg.Bind("QoL", "HideTutorialHints", false, "Don't show tutorial hint pop-ups.");
        HudKey = cfg.Bind("General", "HudKey", Key.F8, "Shows/hides the driving HUD.");
        ShowHud = cfg.Bind("General", "ShowHud", false, "Driving HUD: speed, RPM, gear, power, torque, slip.");



        ExpandLobbySize = cfg.Bind("Multiplayer", "ExpandLobbySize", true, "Offer 2-16 players in the Create Server screen.");
        DefaultMaxPlayers = cfg.Bind("Multiplayer", "DefaultMaxPlayers", 4, new ConfigDescription("Player limit preselected in Create Server.", new AcceptableValueRange<int>(2, 16)));
        LobbyGuards = cfg.Bind("Multiplayer", "LobbyGuards", true, "Crash guards and smart spawn points for lobbies above the stock size.");
        MeasuredCapacity = cfg.Bind("Multiplayer", "MeasuredCapacity", 0, "Player slots in the game's networked player tables, measured in-game (0 = not measured yet).");
        MainMenuButton = cfg.Bind("General", "MainMenuButton", true, "Add a PLUS button to the main menu that opens this mod's menu.");

        ColorRows = cfg.Bind("ServerBrowser", "ColorRows", true, "Color-code server rows by status.");
        ShowAge = cfg.Bind("ServerBrowser", "ShowAge", true, "Show how long ago each server was first seen.");
        ThrottleSessionUpdates = cfg.Bind("ServerBrowser", "ThrottleSessionUpdates", true, "Skip identical list updates and rebuild at most once per interval.");
        SessionUpdateInterval = cfg.Bind("ServerBrowser", "SessionUpdateInterval", 1.5f, "Minimum seconds between list rebuilds.");
        ProfileServerBrowser = cfg.Bind("ServerBrowser", "Profiling", false, "Log timing of server browser code while it is open (needs a game restart; for troubleshooting only).");
        ColorPublic = cfg.Bind("ServerBrowser", "ColorPublic", "#3FBF5F", "Public, joinable.");
        ColorPrivate = cfg.Bind("ServerBrowser", "ColorPrivate", "#E0A030", "Private / locked.");
        ColorFull = cfg.Bind("ServerBrowser", "ColorFull", "#D04545", "Full or closed.");
        ColorIncompatible = cfg.Bind("ServerBrowser", "ColorIncompatible", "#7A7A7A", "Different game version.");
        cfg.Save();
    }
}
