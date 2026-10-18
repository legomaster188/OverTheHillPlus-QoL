using BepInEx.Configuration;
using UnityEngine.InputSystem;

namespace OverTheHillPlus.HudPlus;

/// <summary>HUD+'s settings in BepInEx/config/oth.hudplus.cfg.</summary>
internal static class HudPlusConfig
{
    public static ConfigEntry<bool> ShowSpeedo;
    public static ConfigEntry<SpeedoSkin> Skin;
    public static ConfigEntry<HudCorner> SpeedoCorner;
    public static ConfigEntry<bool> Mph;
    public static ConfigEntry<float> SpeedoScale;
    public static ConfigEntry<int> DialMax;
    public static ConfigEntry<bool> ShowRpm;

    public static ConfigEntry<bool> ShowStatus;
    public static ConfigEntry<HudCorner> StatusCorner;
    public static ConfigEntry<bool> ShowDamage, ShowTyres, ShowWinch, ShowOdometer;

    public static ConfigEntry<float> Opacity;
    public static ConfigEntry<Key> ToggleKey;

    public static ConfigFile File;

    public static void Bind(ConfigFile cfg)
    {
        File = cfg;
        ShowSpeedo = cfg.Bind("Speedometer", "Show", true, "Show the speedometer.");
        Skin = cfg.Bind("Speedometer", "Skin", SpeedoSkin.Analog, "Look of the speedometer: Analog (dial), Digital (big numbers and an RPM bar) or Minimal (just the speed).");
        SpeedoCorner = cfg.Bind("Speedometer", "Corner", HudCorner.BottomRight, "Screen corner of the speedometer.");
        Mph = cfg.Bind("Speedometer", "Mph", false, "Miles per hour (and miles on the odometer) instead of km/h.");
        SpeedoScale = cfg.Bind("Speedometer", "Scale", 1f, "Size of the speedometer (0.6 to 2).");
        DialMax = cfg.Bind("Speedometer", "DialMax", 0, "Top of the analog dial (0 = picked from the car's speed, e.g. 160).");
        ShowRpm = cfg.Bind("Speedometer", "ShowRpm", true, "Show the RPM (and gear) on the analog and digital skins.");

        ShowStatus = cfg.Bind("Status", "Show", true, "Show the car status card.");
        StatusCorner = cfg.Bind("Status", "Corner", HudCorner.BottomRight, "Screen corner of the status card (above the speedometer when they share one).");
        ShowDamage = cfg.Bind("Status", "Damage", true, "Car health on the status card.");
        ShowTyres = cfg.Bind("Status", "Tyres", true, "Tyre grip (and wetness) on the status card.");
        ShowWinch = cfg.Bind("Status", "Winch", true, "Winch state on the status card.");
        ShowOdometer = cfg.Bind("Status", "Odometer", true, "The car's odometer on the status card.");

        Opacity = cfg.Bind("General", "Opacity", 0.82f, "Background opacity of the HUD+ panels (0.2 to 1).");
        ToggleKey = cfg.Bind("General", "ToggleKey", Key.None, "Shows / hides all of HUD+ (None = no hotkey).");
    }
}
