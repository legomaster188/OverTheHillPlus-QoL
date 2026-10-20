using OverTheHillPlus.Cars;

namespace OverTheHillPlus.Pixie;

/// <summary>
/// The Pixie (mod id "pixie"): a kei-class mini van for core's car framework, built from a detailed model (no maker names,
/// badges or lettering anywhere in the UI or the model). Its identity values are part of the network protocol and of the
/// save sidecar (oth.pixie.&lt;profile&gt;.sidecar): they never change once released. Pure (unit-tested).
/// </summary>
public static class PixieDefinition
{
    public const string Id = "pixie";
    public const string DisplayName = "Pixie";
    /// <summary>Hand-picked after the Hilo 22's 0xB7E2A006 (0xB7E2A007 to 0xB7E2A009 are taken by other cars), checked
    /// against the live catalog at startup.</summary>
    public const uint ItemId = 0xB7E2A00A;
    /// <summary>CarInfo.name and the livery car name ("car": "Pixie" in a skin's manifest; ASCII only).</summary>
    public const string CarName = "Pixie";
    public const string Guid = "be58f3d38d584cf9961ae1b4d76949c4";
    public const int Protocol = 3;

    public static CarDefinition Create() => new()
    {
        Id = Id,
        DisplayName = DisplayName,
        Description = "A tiny work van, 3.4 metres long and 1.47 wide: a little three-cylinder, sliding side doors, a hatch " +
                      "and just enough load space. Light, slow and nimble, it squeezes through where nothing else fits.",
        PageNote = "A kei-class mini van: about half the weight of the pickups, a small engine, narrow 12 inch tyres and a " +
                   "very tight turning circle. Buy it in the garage, or try it from here. Everyone on a server needs this mod to see it.",
        ItemId = ItemId,
        CarName = CarName,
        FusionGuid = Guid,
        RegistryKey = "pixie",
        Protocol = Protocol,
        // The Boar, the smallest of the Workhorse cars (4.55 m, four wheels, 3.12 m wheelbase against the Pixie's 2.42 m):
        // its skeleton, garage data (vehicle class, upgrades) and cosmetic slots (Liveries/pixie/README.md). The wheel
        // colliders take the model's own wheelbase, track and (much smaller) tyre radius.
        DefaultDonor = "Car_Boar",
        DefaultGarageBase = "Boar",
        AssetName = "pixie",
        // Gears and diffs: the real car's gearbox and differentials (a kei 5-speed, part-time 4WD).
        GearSet = "kei5",
        Diffs = new byte[] { 1, 1, 3 },
        // A kei work van's numbers on the Boar's skeleton: 0.95 t (a third of the Boar's 2.8 t) and 14 kW (a 660 cc
        // free-revving three-cylinder: the weakest car in the game, but light), a 105 km/h real top speed as a 6.3 m/s
        // limiter, gearing so the limiter comes at 85 % of the revs, soft short springs scaled to the weight (the Boar's are
        // for a 2.8 t car) on small tyres, a very tight steering lock. Part-time 4WD feel: the Boar's drive is kept. Tows
        // 350 kg (a kei's rating: only the light utility trailer). Not driven side by side yet: the numbers are easy to change.
        Tuning = new CarTuningProfile
        {
            // Dampers set for a body damping ratio of about 0.65, the Workhorse donors' own (CarPhysicsFitTests): the
            // donor's dampers cut with the springs left this light car over-damped, stiff over every bump.
            Mass = 0.34f, CentreOfMassDrop = 0.04f, Torque = 0.22f, Power = 0.23f, TopSpeed = 1.05f, RevRange = 1.18f, GearRatios = 0.73f, FinalDrive = 1.0f,
            SuspensionTravel = 0.85f, SpringRate = 0.55f, DamperRate = 0.44f, TyreGrip = 0.88f, SteeringLock = 1.2f, TowKg = 350f,
        },
        // The kei vans' usual colours: white above all, silver, light blue, red, dark green and cream.
        Paints = new[]
        {
            new PaintPreset("Kei White", new CarRgb(244, 244, 241)),
            new PaintPreset("Silver", new CarRgb(188, 192, 197)),
            new PaintPreset("Light Blue", new CarRgb(148, 184, 214)),
            new PaintPreset("Red", new CarRgb(168, 28, 34)),
            new PaintPreset("Dark Green", new CarRgb(36, 70, 52)),
            new PaintPreset("Cream", new CarRgb(232, 220, 182)),
        },
        PaintCustomDefault = "#F4F4F1",
        // Zone G is the trim: the door handles and the mirror housings.
        Trims = new[]
        {
            new TrimPreset("Black", new CarRgb(30, 30, 33)),
            TrimPreset.Body(),
            new TrimPreset("Chrome", new CarRgb(196, 199, 203)),
        },
        TrimCustomDefault = "#1E1E21",
        TrimWhat = "Trim (door handles, mirrors)",
        TrimHint = "The door handles and the mirror housings: black (as delivered), the body colour, chrome or your own.",
        // Stock engines of the small cars: the Polyana (a compact 4x4 four), the Lynx, the Tinny and the Boar (the donor).
        // Not listened to side by side yet: the list is easy to change.
        EngineSounds = new[]
        {
            new EngineSound("Polyana", "Polyana (small four)"),
            new EngineSound("Lynx", "Lynx"),
            new EngineSound("Tinny", "Tinny"),
            new EngineSound("Boar", "Boar (donor)"),
        },
        DefaultEngine = 2, // index in EngineSounds (kept so saved choices still mean the same sound)
        EngineHint = "Which stock car's engine your Pixie sounds like (the Tinny's small four fits a 660 cc van best). " +
                     "Other players with this mod hear your pick.",
        // The garage's Liveries tab, after "Paint" (ids are part of the network layout: only append).
        Liveries = new[]
        {
            new LiveryPreset("slate_two_tone", "Slate Two-Tone", "The factory two-tone: a dark slate lower body under the side crease, over your own paint."),
            new LiveryPreset("birch_grove_bakery", "Birch Grove Bakery", "The bakery's delivery van, white with the shop's name on the sliding doors."),
            new LiveryPreset("bridal_veil_41", "Bridal Veil 41", "A privateer's hill-climb van, red with the white number plates of the Bridal Veil Falls Climb."),
        },
    };
}
