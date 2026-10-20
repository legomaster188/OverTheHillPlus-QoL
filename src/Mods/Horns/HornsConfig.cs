using System;
using BepInEx.Configuration;

namespace OverTheHillPlus.Horns;

/// <summary>Custom Horns' settings in BepInEx/config/oth.horns.cfg (your own wav files go in the oth.horns folder).</summary>
internal static class HornsConfig
{
    public static ConfigEntry<string> Horn;
    public static ConfigEntry<HornMode> CustomMode;
    public static ConfigEntry<float> Volume;
    public static ConfigEntry<bool> HearOthers;
    public static ConfigEntry<bool> OthersCustomFiles;
    public static ConfigEntry<bool> FollowGameVolume;
    public static ConfigEntry<float> Range;

    public static ConfigFile File;

    public static void Bind(ConfigFile cfg)
    {
        File = cfg;
        Horn = cfg.Bind("Horns", "Horn", "stock",
            "Your horn: stock (the game's), builtin:<id> (one of this mod's), or custom:<file.wav> (a file in BepInEx/config/oth.horns).");
        CustomMode = cfg.Bind("Horns", "CustomMode", HornMode.WholeClip, "Your own wav file: Hold = loops while you hold the horn, WholeClip = plays once per press.");
        Volume = cfg.Bind("Horns", "Volume", 1f, new ConfigDescription("Volume of the horns this mod plays.", new AcceptableValueRange<float>(0f, 1f)));
        HearOthers = cfg.Bind("Horns", "HearOthers", true, "Hear the horns other players with this mod picked (off: everyone else sounds like the game's horn).");
        OthersCustomFiles = cfg.Bind("Horns", "OthersCustomFiles", true, "Download and play other players' own wav horns (off: only the built-in ones).");
        FollowGameVolume = cfg.Bind("Horns", "FollowGameVolume", true, "Scale horns by the game's master and sound-effects volume.");
        Range = cfg.Bind("Horns", "Range", 150f, new ConfigDescription("How far away (m) you still hear a horn.", new AcceptableValueRange<float>(30f, 400f)));
    }

    // ---------------------------------------------------------------- the Horn value

    public const string StockValue = "stock";

    public static string BuiltInValue(byte id) => "builtin:" + id;

    public static string CustomValue(string file) => "custom:" + file;

    /// <summary>Reads a Horn value; anything it doesn't understand is the stock horn.</summary>
    public static HornKind Parse(string value, out byte builtIn, out string file)
    {
        builtIn = 0;
        file = null;
        value = (value ?? "").Trim();
        if (value.StartsWith("builtin:", StringComparison.OrdinalIgnoreCase)
            && byte.TryParse(value.Substring(8), out builtIn) && HornSynth.Find(builtIn) != null)
            return HornKind.BuiltIn;
        if (value.StartsWith("custom:", StringComparison.OrdinalIgnoreCase) && value.Length > 7)
        {
            file = value.Substring(7);
            return HornKind.Custom;
        }
        builtIn = 0;
        return HornKind.Stock;
    }
}
