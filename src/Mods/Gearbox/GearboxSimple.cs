using OverTheHillPlus.UI;

namespace OverTheHillPlus.GearboxPlus;

/// <summary>Gearbox+'s everyday settings, first on its MODS page (the keys are in its Advanced fold).</summary>
internal static class GearboxSimple
{
    private static readonly int[] GearSteps = { 2, 3, 4, 5 };
    private static readonly string[] OffOn = { "Off", "On" };
    private static readonly string[] Pads = { "None", "D-pad up", "D-pad down", "D-pad left", "D-pad right", "Left stick press", "Right stick press", "Y / triangle" };
    private static readonly string[] Modes = { "Extend the game's step", "The real car's gearbox" };
    private static readonly string[] Sources = { DiffMath.SourceName(DiffSource.Stock), DiffMath.SourceName(DiffSource.RealCar), DiffMath.SourceName(DiffSource.Custom) };
    private static readonly string[] DiffModes =
        { DiffMath.Name(DiffMode.Stock), DiffMath.Name(DiffMode.Open), DiffMath.Name(DiffMode.LimitedSlip), DiffMath.Name(DiffMode.Locked) };
    private static readonly float[] Scales = { 0.6f, 0.75f, 0.9f, 1f, 1.15f, 1.3f, 1.5f, 1.75f, 2f };

    internal static void Draw()
    {
        // Not bound when the mod failed to start: nothing to show then.
        if (GearboxConfig.Gears == null) return;
        SimpleMenu.Heading("Gears");
        SimpleMenu.Choice("Gearbox", "Extend gives every car more gears by continuing its own step. The real car's gearbox gives each car the box of its real counterpart: " +
                                     "a close 5-speed for the cars, 4 or 5 speeds for the trucks, 3 for the vintage trucks, a low first gear for the kei trucks. Everyone in a session should use the same.",
            (int)GearboxConfig.Mode.Value, Modes, i => GearboxConfig.Mode.Value = (GearMode)i, wrap: true);
        bool extend = GearboxConfig.Mode.Value == GearMode.Extend;
        SimpleMenu.IntChoice("Forward gears", "2 is the game's own. More continue each car's own step from 1st to 2nd; 4H and 4L work as before. Everyone in a session should use the same number. Used by the Extend gearbox only.",
            GearboxConfig.Gears.Value, GearSteps, v => v == 2 ? "2 (stock)" : v.ToString(), v => GearboxConfig.Gears.Value = v, extend);

        var input = Shifter.LocalInput;
        string car = input != null ? CarGears.NameOf(input) : null;
        bool keep = car != null && GearMath.IsKeptStock(GearboxConfig.StockCars.Value, car);
        SimpleMenu.Choice("Keep this car stock", car == null ? "Get in a car first: the setting is saved by car name." : "Gives " + car + " the game's two gears whatever the number above says.",
            keep ? 1 : 0, OffOn, i => GearboxConfig.StockCars.Value = GearMath.SetKeptStock(GearboxConfig.StockCars.Value, car, i == 1), car != null);
        if (!CarGears.Active) SimpleMenu.Note("Not running in this session: the extra gears need a solo game, your own server, or a server that lists Gearbox+.");
        else if (car != null)
        {
            var set = !extend && !keep ? GearSets.For(car, ModdedLookup.GearSetOf) : null;
            string ratios = CarGears.TableText(input);
            SimpleMenu.Note(car + " has " + (CarGears.GearCount(input) > 0 ? CarGears.GearCount(input).ToString() : "?") + " forward gears now" +
                            (set != null ? " (" + set.Name + ")" : "") + (ratios != null ? ": " + ratios : "") + ".");
        }

        SimpleMenu.Heading("Manual gearbox");
        SimpleMenu.Toggle(GearboxConfig.Neutral, "Real neutral", "Stepping from 1st down or from reverse up stops at N, where the engine revs freely. Needs the game's manual gearbox (Controls).");
        bool neutral = GearboxConfig.Neutral.Value;
        SimpleMenu.Choice("Neutral: gamepad button", "A key for it is in the Advanced fold (default N).", (int)GearboxConfig.NeutralPad.Value, Pads,
            i => GearboxConfig.NeutralPad.Value = (PadButton)i, neutral);
        SimpleMenu.Choice("Shift up: gamepad button", "On top of the game's own gear buttons.", (int)GearboxConfig.ShiftUpPad.Value, Pads,
            i => GearboxConfig.ShiftUpPad.Value = (PadButton)i);
        SimpleMenu.Choice("Shift down: gamepad button", null, (int)GearboxConfig.ShiftDownPad.Value, Pads,
            i => GearboxConfig.ShiftDownPad.Value = (PadButton)i);
        SimpleMenu.Note("The automatic mode of the game uses the new gears by itself: it shifts through all of them.");

        SimpleMenu.Heading("Differentials");
        SimpleMenu.Choice("Differentials", "Stock is the game's: every differential locked. The real car's gives the modded cars their real open differentials, with the lock key as the driver's locker. " +
                                           "My choice uses the three settings below. Your own car only: the host simulates it the same way.",
            (int)GearboxConfig.DiffFrom.Value, Sources, i => GearboxConfig.DiffFrom.Value = (DiffSource)i, wrap: true);
        bool custom = GearboxConfig.DiffFrom.Value == DiffSource.Custom;
        SimpleMenu.Choice("Front axle", "Open lets the wheels turn at different speeds in a corner (and spin one wheel on a slope); locked drives both wheels the same.", (int)GearboxConfig.DiffFront.Value, DiffModes,
            i => GearboxConfig.DiffFront.Value = (DiffMode)i, custom, wrap: true);
        SimpleMenu.Choice("Rear axle", null, (int)GearboxConfig.DiffRear.Value, DiffModes, i => GearboxConfig.DiffRear.Value = (DiffMode)i, custom, wrap: true);
        SimpleMenu.Choice("Centre", "Between the axles. The game's 4x4s join the axles directly (no centre differential), so this only does something on a car that has one.", (int)GearboxConfig.DiffCentre.Value, DiffModes,
            i => GearboxConfig.DiffCentre.Value = (DiffMode)i, custom, wrap: true);
        SimpleMenu.Choice("Lock key: gamepad button", "The lock key (default K, in the Advanced fold) locks every differential while it is on; press it again to go back to your setting.",
            (int)GearboxConfig.DiffLockPad.Value, Pads, i => GearboxConfig.DiffLockPad.Value = (PadButton)i, GearboxConfig.DiffFrom.Value != DiffSource.Stock);
        SimpleMenu.Toggle(GearboxConfig.ShowDiffs, "Show them on the indicator", "A second line: F front, R rear, C centre as OPEN, LSD or LOCK, while a setting other than Stock is in use.");
        if (CarDiffs.HasLocal)
        {
            var d = CarDiffs.Local;
            SimpleMenu.Note("Your car now: " + DiffMath.Line(d, CarDiffs.LocalHasCentre) + (d.LockAll ? " (lock key on)" : "") +
                            (CarDiffs.LocalBroken ? ". The game's differentials stay: this car could not be changed." : "") +
                            (!CarDiffs.LocalHasCentre && d.Centre != DiffMode.Stock ? ". No centre differential on this car." : ""));
        }
        else if (GearboxConfig.DiffFrom.Value != DiffSource.Stock)
            SimpleMenu.Note("Get in a car to see its differentials. Limited slip is the unproven part: open and locked are forced with the physics' own overrides.");
        else SimpleMenu.Note("Pick The real car's or My choice to change them: the game's own differentials are locked.");

        SimpleMenu.Heading("Gear indicator");
        SimpleMenu.Toggle(GearboxConfig.ShowIndicator, "Show the indicator", "R N 1 2 3 4 with your gear lit, and 4H / 4L.");
        bool on = GearboxConfig.ShowIndicator.Value;
        SimpleMenu.Choice("Corner", null, (int)GearboxConfig.Corner.Value, HudLayout.CornerNames,
            i => GearboxConfig.Corner.Value = HudLayout.Corner(i), on);
        SimpleMenu.FloatChoice("Size", null, GearboxConfig.Scale.Value, Scales, v => v.ToString("0.##") + "×",
            v => GearboxConfig.Scale.Value = v, on);
    }
}
