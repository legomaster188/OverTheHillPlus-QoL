using BepInEx.Configuration;

namespace OverTheHillPlus.Stacking;

/// <summary>Cosmetic Stacking's settings in BepInEx/config/oth.stacking.cfg.</summary>
internal static class StackingConfig
{
    /// <summary>A gamepad button to hold instead of Shift (the keyboard's Shift always works).</summary>
    internal enum PadHold
    {
        None,
        LeftTrigger,
        LeftStickPress,
        RightStickPress,
    }

    public static ConfigEntry<bool> ShiftStacks, ReplaceWholeStack, ShowHint;
    public static ConfigEntry<PadHold> Gamepad;

    public static ConfigFile File;

    public static void Bind(ConfigFile cfg)
    {
        File = cfg;
        ShiftStacks = cfg.Bind("Stacking", "ShiftStacks", true,
            "Shift + click (or Shift + Enter) on a cosmetic in the garage puts it on without taking off the one of its type already on.");
        ReplaceWholeStack = cfg.Bind("Stacking", "ReplaceWholeStack", true,
            "A plain click on a cosmetic replaces every cosmetic of its type that is on (the game alone takes off only the first of a stack).");
        Gamepad = cfg.Bind("Stacking", "GamepadHold", PadHold.None,
            "A gamepad button to hold while pressing the submit button to stack: None, LeftTrigger, LeftStickPress or RightStickPress.");
        ShowHint = cfg.Bind("Hint", "ShowHint", true, "Show the stacking hint at the bottom of the garage's cosmetics screen.");
    }

    /// <summary>The held button's name for the hint, or null for none.</summary>
    public static string PadName(PadHold p) => p switch
    {
        PadHold.LeftTrigger => "LT",
        PadHold.LeftStickPress => "L3",
        PadHold.RightStickPress => "R3",
        _ => null,
    };
}
