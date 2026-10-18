using BepInEx.Configuration;
using UnityEngine.InputSystem;

namespace OverTheHillPlus.CheatsPlus;

/// <summary>
/// Cheats+'s own settings in BepInEx/config/oth.cheats.cfg. Same sections and keys as in the old OverTheHillPlus
/// config ([General] FlipKey / BlockAchievementsAfterCheating, [Movement], [Vehicles], [Assists], [Cheats], and
/// [ServerBrowser] Profiling), so the settings migration can copy them over. The features (tuning, chassis, ESP,
/// fun physics) bind their own sections on <see cref="File"/> in their Register.
/// </summary>
internal static class CheatsConfig
{
    public static ConfigEntry<Key> FlipKey;
    public static ConfigEntry<Key> FlyKey;
    public static ConfigEntry<Key> NitroKey;
    public static ConfigEntry<Key> JumpKey;
    public static ConfigEntry<float> FlySpeed;
    public static ConfigEntry<float> NitroAcceleration;
    public static ConfigEntry<float> JumpSpeed;
    public static ConfigEntry<bool> LongWinch;
    public static ConfigEntry<bool> BlockAchievements;

    // Driving assists
    public static ConfigEntry<bool> AssistsEnabled;
    public static ConfigEntry<float> GripMultiplier;
    public static ConfigEntry<bool> SlipperyBoost;
    public static ConfigEntry<float> SlipperyMultiplier;
    public static ConfigEntry<bool> TractionControl;
    public static ConfigEntry<float> TractionTargetSlip;
    public static ConfigEntry<float> TractionStrength;
    public static ConfigEntry<bool> AntiRoll;
    public static ConfigEntry<float> AntiRollStrength;
    public static ConfigEntry<float> Downforce;

    // Cheats
    public static ConfigEntry<bool> NoDamage;
    public static ConfigEntry<float> ExtraAcceleration;
    public static ConfigEntry<bool> ForceDevConsole;

    // Troubleshooting
    public static ConfigEntry<bool> ProfileServerBrowser;

    public static ConfigFile File;

    public static void Bind(ConfigFile cfg)
    {
        File = cfg;
        FlipKey = cfg.Bind("General", "FlipKey", Key.F6, "Puts your car back on its wheels (solo/host only).");
        FlyKey = cfg.Bind("Movement", "FlyKey", Key.F9, "Toggle fly mode (WASD, Space up, Ctrl down, Shift fast).");
        NitroKey = cfg.Bind("Movement", "NitroKey", Key.B, "Hold for nitro.");
        JumpKey = cfg.Bind("Movement", "JumpKey", Key.J, "Super jump.");
        FlySpeed = cfg.Bind("Movement", "FlySpeed", 25f, "Fly speed in m/s (Shift = 3x).");
        NitroAcceleration = cfg.Bind("Movement", "NitroAcceleration", 18f, "Nitro push in m/s^2.");
        JumpSpeed = cfg.Bind("Movement", "JumpSpeed", 9f, "Super jump upward speed in m/s.");
        LongWinch = cfg.Bind("Vehicles", "LongWinch", false, "Winch reaches much farther (longer rope when hosting).");
        BlockAchievements = cfg.Bind("General", "BlockAchievementsAfterCheating", true, "Don't unlock Steam achievements in a game session where cheats were used.");

        AssistsEnabled = cfg.Bind("Assists", "Enabled", true, "Master switch for all driving assists.");
        GripMultiplier = cfg.Bind("Assists", "GripMultiplier", 1.25f, "Tire grip on every surface (1 = stock).");
        SlipperyBoost = cfg.Bind("Assists", "SlipperyBoost", true, "Extra grip on mud, snow, ice, sand, water and loose rock.");
        SlipperyMultiplier = cfg.Bind("Assists", "SlipperyMultiplier", 1.5f, "Grip multiplier applied on slippery surfaces (stacks with GripMultiplier).");
        TractionControl = cfg.Bind("Assists", "TractionControl", true, "Cuts throttle when wheels spin past the tire's grip peak.");
        TractionTargetSlip = cfg.Bind("Assists", "TractionTargetSlip", 0.9f, "Normalized slip where traction control starts acting (1 = tire peak).");
        TractionStrength = cfg.Bind("Assists", "TractionStrength", 0.8f, "How hard traction control cuts throttle (0-1).");
        AntiRoll = cfg.Bind("Assists", "AntiRoll", true, "Pushes the car back upright when it leans hard.");
        AntiRollStrength = cfg.Bind("Assists", "AntiRollStrength", 1f, "Anti-rollover strength (0-3).");
        Downforce = cfg.Bind("Assists", "Downforce", 0.2f, "Extra downward force in g while on the ground (0-1).");

        NoDamage = cfg.Bind("Cheats", "NoDamage", false, "Your car takes no damage (solo/host only: the host simulates cars).");
        ExtraAcceleration = cfg.Bind("Cheats", "ExtraAcceleration", 0f, "Extra forward push in m/s^2 while on throttle (0 = off).");
        ForceDevConsole = cfg.Bind("Cheats", "ForceDevConsole", true, "Treat you as a dev-team member so the built-in console can open (solo/host only).");

        ProfileServerBrowser = cfg.Bind("ServerBrowser", "Profiling", false, "Log timing of server browser code while it is open (needs a game restart; for troubleshooting only).");
    }
}
