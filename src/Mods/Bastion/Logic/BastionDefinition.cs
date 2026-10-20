using OverTheHillPlus.Cars;

namespace OverTheHillPlus.Bastion;

/// <summary>
/// The Bastion 200 (mod id "bastion"): a 2000s full-size 4x4 SUV for core's car framework, built from a detailed model (no
/// maker names, badges or lettering anywhere in the UI or the model). Its identity values are part of the network protocol
/// and of the save sidecar (oth.bastion.&lt;profile&gt;.sidecar): they never change once released. Pure (unit-tested).
/// </summary>
public static class BastionDefinition
{
    public const string Id = "bastion";
    public const string DisplayName = "Bastion 200";
    /// <summary>Hand-picked next to the Hilo's 0xB7E2A004, checked against the live catalog at startup.</summary>
    public const uint ItemId = 0xB7E2A005;
    /// <summary>CarInfo.name and the livery car name ("car": "Bastion" in a skin's manifest; ASCII only).</summary>
    public const string CarName = "Bastion";
    public const string Guid = "1fec9eb7d3364486b9f27959287ad72a";
    public const int Protocol = 3;

    public static CarDefinition Create() => new()
    {
        Id = Id,
        DisplayName = DisplayName,
        Description = "A full-size 4x4 wagon from the late 2000s: a tall five-door body on a ladder frame with a big V8, " +
                      "roof rails, a sunroof and 20 inch wheels. Heavy and strong, as happy on a mountain track as on the highway.",
        PageNote = "A full-size 2000s 4x4 SUV: heavier and stronger than the Vera, with long suspension travel for " +
                   "rough ground. Buy it in the garage, or try it from here. Everyone on a server needs this mod to see it.",
        ItemId = ItemId,
        CarName = CarName,
        FusionGuid = Guid,
        RegistryKey = "bastion",
        Protocol = Protocol,
        // The TrailBurner: the released stock 4x4 SUV closest in size (1.98 m wide like the Bastion, 4.83 m long against
        // 4.97 m, a 2.68 m wheelbase against 2.85 m, 0.44 m tyres against 0.41 m) and, unlike the Cumbre (a pickup with a
        // bed) or the Roamer (0.26 m longer in the wheelbase and 0.14 m narrower), with the same roof slots
        // (Liveries/bastion/README.md). The tyre difference settles the car about 3 cm lower than the TrailBurner.
        DefaultDonor = "Car_TrailBurner",
        DefaultGarageBase = "TrailBurner",
        AssetName = "bastion",
        // Gears and diffs: the real car's gearbox and differentials (a 6-speed full-time 4WD with a Torsen centre; the lock key is its rear locker).
        GearSet = "close5",
        Diffs = new byte[] { 1, 1, 2 },
        // The real big 4x4's numbers on the TrailBurner's skeleton: 2.65 t (a third more than the TrailBurner) and 51 kW
        // (about 19 kW/t, no more than the strongest stock wagon; the V8's torque stays), a governed 180 km/h real top speed as a 9.4 m/s limiter,
        // gearing so the limiter comes at 85 % of the revs, a slightly lower centre of mass, longer travel on firmer
        // springs for rough ground; a touch less steering lock for the longer wheelbase. Tows 3.5 t.
        Tuning = new CarTuningProfile
        {
            Mass = 1.32f, CentreOfMassDrop = 0.03f, Torque = 1.91f, Power = 0.83f, TopSpeed = 1.175f, RevRange = 0.96f, GearRatios = 0.89f, FinalDrive = 1.0f,
            SuspensionTravel = 1.12f, SpringRate = 1.15f, DamperRate = 1.15f, TyreGrip = 1.0f, SteeringLock = 0.95f, TowKg = 3500f,
        },
        // Colours of the 2000s SUVs: white pearl, silver, black, dark grey, dark blue, champagne and dark green.
        Paints = new[]
        {
            new PaintPreset("White Pearl", new CarRgb(238, 238, 232)),
            new PaintPreset("Silver", new CarRgb(186, 190, 194)),
            new PaintPreset("Black", new CarRgb(22, 22, 24)),
            new PaintPreset("Dark Grey", new CarRgb(74, 78, 82)),
            new PaintPreset("Dark Blue", new CarRgb(26, 40, 74)),
            new PaintPreset("Champagne", new CarRgb(200, 184, 150)),
            new PaintPreset("Dark Green", new CarRgb(36, 60, 46)),
        },
        PaintCustomDefault = "#EEEEE8",
        // Zones G is the brightwork: the grille frame and bars, the door strips and the lower trim lines.
        Trims = new[]
        {
            new TrimPreset("Chrome", new CarRgb(196, 199, 203)),
            new TrimPreset("Black", new CarRgb(26, 26, 28)),
            TrimPreset.Body(),
        },
        TrimCustomDefault = "#1A1A1C",
        TrimWhat = "Brightwork (grille, door strips)",
        TrimHint = "The grille frame and bars, the door and sill strips and the lamp trim: chrome (as delivered), black (a " +
                   "blacked-out look), the body colour or your own.",
        // Stock engine sounds of the game's big wagons and 4x4s: the Grand Woodman's big V8 (the closest to a 5.7 litre
        // V8) is the default, the Cumbre and the Roamer (V6s) and the TrailBurner (the donor). Not listened to side by
        // side yet: the list is easy to change.
        EngineSounds = new[]
        {
            new EngineSound("GrandWoodman", "Grand Woodman (big V8)"),
            new EngineSound("Cumbre", "Cumbre (V6)"),
            new EngineSound("Roamer", "Roamer (smooth V6)"),
            new EngineSound("TrailBurner", "TrailBurner"),
        },
        DefaultEngine = 0,
        EngineHint = "Which stock car's engine your Bastion sounds like (the Grand Woodman's big V8 fits it best). " +
                     "Other players with this mod hear your pick.",
        // The garage's Liveries tab, after "Paint" (ids are part of the network layout: only append).
        Liveries = new[]
        {
            new LiveryPreset("two_tone", "Champagne Two-Tone", "Champagne over graphite, split at the door strips like the old two-tone option."),
            new LiveryPreset("fraser_research", "Fraser Research Boardwalk", "The field wagon of the research boardwalk up on Gimli Ridge."),
            new LiveryPreset("raid_assist", "Shaka Bivouac Assistance", "A sun faded support wagon from a desert raid through the Tassili n'Ajjer."),
        },
    };
}
