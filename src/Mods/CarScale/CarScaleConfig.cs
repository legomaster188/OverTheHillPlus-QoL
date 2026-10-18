using BepInEx.Configuration;

namespace OverTheHillPlus.CarScale;

/// <summary>Car Scale's settings in BepInEx/config/oth.carscale.cfg.</summary>
internal static class CarScaleConfig
{
    private const string Section = "Car Scale";

    public static ConfigEntry<ScaleMath.Mode> Mode;
    public static ConfigEntry<float> Size;
    public static ConfigEntry<bool> AllowRequests;
    public static ConfigEntry<bool> ScaleSuspension;
    public static ConfigEntry<float> MySize;

    public static ConfigFile File;

    public static void Bind(ConfigFile cfg)
    {
        File = cfg;
        Mode = cfg.Bind(Section, "Mode", ScaleMath.Mode.Off,
            "Host / solo: Off, JustMe (your car), Everyone (every player's car) or PerPlayer (sizes picked on the MODS page).");
        Size = cfg.Bind(Section, "Size", 2f, new ConfigDescription(
            "Host / solo: the size for JustMe and Everyone (1 = normal).", new AcceptableValueRange<float>(ScaleMath.Min, ScaleMath.Max)));
        AllowRequests = cfg.Bind(Section, "LetPlayersChoose", true,
            "Host: players with this mod may pick their own car's size (Everyone and your per-player picks still win).");
        ScaleSuspension = cfg.Bind(Section, "ScaleSuspension", true,
            "Host / solo: scale the suspension travel and damping with the size (the tyre radius, weight and brakes always follow).");
        MySize = cfg.Bind(Section, "MySize", 1f, new ConfigDescription(
            "As a guest: the size you ask the host for (when the host lets players choose).", new AcceptableValueRange<float>(ScaleMath.Min, ScaleMath.Max)));
    }
}
