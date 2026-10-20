using BepInEx.Configuration;
using UnityEngine.InputSystem;

namespace OverTheHillPlus.GearboxPlus;

/// <summary>Gamepad buttons Gearbox+ can listen to (the game's own gear buttons keep working as they are).</summary>
internal enum PadButton
{
    None,
    DpadUp,
    DpadDown,
    DpadLeft,
    DpadRight,
    LeftStickPress,
    RightStickPress,
    North,
}

/// <summary>Gearbox+'s settings in BepInEx/config/oth.gearbox.cfg.</summary>
internal static class GearboxConfig
{
    public static ConfigEntry<int> Gears;
    public static ConfigEntry<string> StockCars;
    public static ConfigEntry<GearMode> Mode;

    public static ConfigEntry<DiffSource> DiffFrom;
    public static ConfigEntry<DiffMode> DiffFront, DiffRear, DiffCentre;
    public static ConfigEntry<Key> DiffLockKey;
    public static ConfigEntry<PadButton> DiffLockPad;
    public static ConfigEntry<bool> ShowDiffs;

    public static ConfigEntry<bool> Neutral;
    public static ConfigEntry<Key> NeutralKey, ShiftUpKey, ShiftDownKey;
    public static ConfigEntry<PadButton> NeutralPad, ShiftUpPad, ShiftDownPad;

    public static ConfigEntry<bool> ShowIndicator;
    public static ConfigEntry<HudCorner> Corner;
    public static ConfigEntry<float> Scale, Opacity;

    public static ConfigFile File;

    public static void Bind(ConfigFile cfg)
    {
        File = cfg;
        Mode = cfg.Bind("Gears", "Mode", GearMode.Extend, "Extend: more gears that continue each car's own step (ForwardGears below). Realistic: the gearbox of the real car (a close 5-speed for the cars, 4 or 5 speeds for the trucks, 3 for the vintage trucks, a low first gear for the kei trucks), laid on the car's own gearing; ForwardGears is not used. Everyone in a session should use the same mode.");
        Gears = cfg.Bind("Gears", "ForwardGears", 4, "Forward gears for every car in Extend mode: 2 (the game's own), 3, 4 or 5. The extra gears continue each car's own step from 1st to 2nd. Everyone in a session should use the same number.");
        StockCars = cfg.Bind("Gears", "KeepStockCars", "", "Cars that keep the game's two gears, by car name, separated by | (the MODS page sets it for the car you drive).");

        Neutral = cfg.Bind("Neutral", "RealNeutral", true, "A real neutral between reverse and 1st on the manual gearbox: stepping from 1st down or from reverse up stops at N.");
        NeutralKey = cfg.Bind("Neutral", "NeutralKey", Key.N, "Key that shifts the manual gearbox to neutral (None = no key).");
        NeutralPad = cfg.Bind("Neutral", "NeutralPad", PadButton.None, "Gamepad button that shifts the manual gearbox to neutral.");

        ShiftUpKey = cfg.Bind("Shifting", "ShiftUpKey", Key.None, "Extra key for shifting up on the manual gearbox (the game's own gear buttons keep working).");
        ShiftDownKey = cfg.Bind("Shifting", "ShiftDownKey", Key.None, "Extra key for shifting down on the manual gearbox.");
        ShiftUpPad = cfg.Bind("Shifting", "ShiftUpPad", PadButton.None, "Extra gamepad button for shifting up on the manual gearbox.");
        ShiftDownPad = cfg.Bind("Shifting", "ShiftDownPad", PadButton.None, "Extra gamepad button for shifting down on the manual gearbox.");

        DiffFrom = cfg.Bind("Differentials", "Source", DiffSource.Stock, "Where the differentials come from: Stock (the game's own: locked), RealCar (each car's real differentials: open axles, a locker on the lock key) or Custom (the three settings below). Your own car only; the host simulates it the same way.");
        DiffFront = cfg.Bind("Differentials", "Front", DiffMode.Stock, "Custom: the front axle's differential: Stock (locked), Open, LimitedSlip or Locked.");
        DiffRear = cfg.Bind("Differentials", "Rear", DiffMode.Stock, "Custom: the rear axle's differential.");
        DiffCentre = cfg.Bind("Differentials", "Centre", DiffMode.Stock, "Custom: the differential between the axles. The game's own 4x4 drivetrain joins the axles directly, so this only works on a car that has a centre differential.");
        DiffLockKey = cfg.Bind("Differentials", "LockKey", Key.K, "Key that locks every differential while it is on (and puts your setting back when pressed again). Does nothing while the source is Stock: the game's differentials are locked already.");
        DiffLockPad = cfg.Bind("Differentials", "LockPad", PadButton.None, "Gamepad button for the same.");
        ShowDiffs = cfg.Bind("Indicator", "ShowDifferentials", true, "A second line on the gear indicator with the differentials (F front, R rear, C centre: OPEN, LSD or LOCK) while a differential setting other than Stock is in use.");

        ShowIndicator = cfg.Bind("Indicator", "Show", true, "Show the gear indicator (R N 1 2 3 4 and 4H / 4L) while driving.");
        Corner = cfg.Bind("Indicator", "Corner", HudCorner.TopRight, "Screen corner of the gear indicator (below the driving HUD in the top right).");
        Scale = cfg.Bind("Indicator", "Scale", 1f, "Size of the gear indicator (0.6 to 2).");
        Opacity = cfg.Bind("Indicator", "Opacity", 0.82f, "Background opacity of the gear indicator (0.2 to 1).");
    }
}
