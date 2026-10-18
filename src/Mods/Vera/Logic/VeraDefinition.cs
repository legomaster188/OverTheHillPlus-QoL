using OverTheHillPlus.Cars;

namespace OverTheHillPlus.Vera;

/// <summary>
/// The Vera Crossover for core's car framework. The identity values are part of the mod's network protocol (every peer
/// must agree) and of the save sidecar (oth.vera.&lt;profile&gt;.sidecar), so they never change: they are the values the
/// Vera had before the framework (item 0xB7E2A001, Car_Vera, the same guid, registry key "vera", config file
/// oth.vera.cfg with its [Vera] and [Look] keys, look channel 15 in the old message layout). Pure (unit-tested).
/// </summary>
public static class VeraDefinition
{
    public const string Id = "vera";
    public const string DisplayName = "Vera Crossover";
    /// <summary>Hand-picked, checked against the live catalog at startup.</summary>
    public const uint ItemId = 0xB7E2A001;
    public const string CarName = "Vera";
    public const string Guid = "3c59974a52bd4d809080c396c298f873";

    /// <summary>2: TrailBurner donor and garage base, core's network prefab registry. 3: cosmetics and water resistance
    /// kept in the upgrade lists (the mounted-upgrade bits index those lists, so every peer needs the same ones). Core's
    /// car framework builds the very same template and layout, so it stays 3: a Vera-only game still plays with builds
    /// from before the framework.</summary>
    public const int Protocol = 3;

    public static CarDefinition Create() => new()
    {
        Id = Id,
        DisplayName = DisplayName,
        Description = "A mid-size seven-seat crossover from the late 2000s: heavier and lower than the trucks, made for long " +
                      "paved roads. Chrome grille bar, swept headlights, roof rails and twin exhausts.",
        PageNote = "A mid-size seven-seat crossover: heavier and lower than the trucks, made for paved roads. " +
                   "Buy it in the garage, or try it from here. Everyone on a server needs this mod to see it.",
        ItemId = ItemId,
        CarName = CarName,
        FusionGuid = Guid,
        RegistryKey = "vera",
        Protocol = Protocol,
        // The TrailBurner: the released stock car (no DLC) closest to the Vera's size (2.68 m wheelbase, 1.64 m track,
        // 4.8 m body). Car_Cordillera (unreleased, a long truck) still works as a donor.
        DefaultDonor = "Car_TrailBurner",
        DefaultGarageBase = "Car_TrailBurner",
        AssetName = "vera",
        // A heavier, road-biased crossover: more weight, a lower centre of mass, firmer and shorter suspension, more road
        // grip, a bit more torque, taller gears, less steering lock.
        Tuning = new CarTuningProfile
        {
            Mass = 1.18f, CentreOfMassDrop = 0.06f, Torque = 1.12f, RevRange = 0.94f, GearRatios = 0.9f, FinalDrive = 0.95f,
            SuspensionTravel = 0.82f, SpringRate = 1.2f, DamperRate = 1.15f, TyreGrip = 1.08f, SteeringLock = 0.88f,
        },
        Paints = new[]
        {
            new PaintPreset("White", new CarRgb(236, 236, 232)),
            new PaintPreset("Silver", new CarRgb(188, 192, 196)),
            new PaintPreset("Grey", new CarRgb(108, 112, 116)),
            new PaintPreset("Black", new CarRgb(22, 22, 24)),
            new PaintPreset("Dark Blue", new CarRgb(28, 44, 78)),
            new PaintPreset("Red", new CarRgb(138, 18, 22)),
            new PaintPreset("Champagne", new CarRgb(196, 178, 140)),
            new PaintPreset("Green", new CarRgb(46, 70, 52)),
        },
        PaintCustomDefault = "#2E4F7A",
        Trims = new[]
        {
            new TrimPreset("Grey", new CarRgb(88, 88, 90)),
            new TrimPreset("Black", new CarRgb(24, 24, 26)),
            TrimPreset.Body(),
        },
        TrimCustomDefault = "#3A3A3C",
        TrimWhat = "Lower trim (cladding)",
        TrimHint = "The lower trim (cladding): grey, black, the body colour or your own.",
        // Picked from the game's car descriptions: the Roamer (a boxy 90s adventure SUV) is the closest to a smooth V6.
        EngineSounds = new[]
        {
            new EngineSound("Roamer", "Roamer (smooth V6)"),
            new EngineSound("Cumbre", "Cumbre (V6)"),
            new EngineSound("GrandWoodman", "Grand Woodman (big V8)"),
            new EngineSound("TrailBurner", "TrailBurner"),
        },
        DefaultEngine = 0,
        EngineHint = "Which stock car's engine your Vera sounds like (the Roamer's smooth V6 fits it best). " +
                     "Other players with this mod hear your pick.",
    };
}
