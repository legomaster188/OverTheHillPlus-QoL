using System.Linq;
using OverTheHillPlus.Cars;

namespace OverTheHillPlus.Drover;

/// <summary>
/// The Drover (mod id "drover"): a mid-1970s American full-size 4x4 pickup, regular cab with an 8 ft styleside box, for
/// core's car framework, in the game's Workhorse class, built to the real trucks' size (133 in wheelbase, 5.42 m long,
/// 33 in tyres): the body-side dent, drip rails, vent wings, round lamps in square bezels in an egg-crate grille, chrome
/// bumpers (a made-up brand: no maker names, badges or lettering anywhere in the UI or the model). Its identity values
/// are part of the network protocol and of the save sidecar (oth.drover.&lt;profile&gt;.sidecar): they never change once
/// released. Pure (unit-tested).
/// </summary>
public static class DroverDefinition
{
    public const string Id = "drover";
    public const string DisplayName = "Drover";
    /// <summary>Assigned for this car (round 6: 0xB7E2A010), checked against the live catalog at startup.</summary>
    public const uint ItemId = 0xB7E2A010;
    /// <summary>CarInfo.name and the livery car name ("car": "Drover" in a skin's manifest; ASCII only).</summary>
    public const string CarName = "Drover";
    public const string Guid = "73b362c3afff4d21a1bcf4a493fe43a6";
    public const int Protocol = 1;

    public static CarDefinition Create() => new()
    {
        Id = Id,
        DisplayName = DisplayName,
        Description = "A mid-1970s full-size 4x4 pickup with the long 8 ft box: the dent down its sides, round lamps in a " +
                      "chrome egg-crate grille, a bench seat and a big V8. Heavy, tall and strong, built for work and mud.",
        PageNote = "A 1970s full-size 4x4 pickup in the Workhorse class, built to the real trucks' size: a regular cab and an " +
                   "8 ft box. Buy it in the garage, or try it from here. Everyone on a server needs this mod to see it.",
        ItemId = ItemId,
        CarName = CarName,
        FusionGuid = Guid,
        RegistryKey = "drover",
        Protocol = Protocol,
        // The Blast Wagon, the stock work truck (3.31 m wheelbase against the Drover's 3.38 m, 1.70 m track, 0.48 m tyres
        // against 0.415 m, a cab and an open bed): its skeleton, garage data (vehicle class, upgrades) and cosmetic slots
        // (Liveries/drover/README.md). Its A-pillar spotlight flares stay off (the Drover has no pillar lamps).
        DefaultDonor = "Car_BlastWagon",
        DefaultGarageBase = "BlastWagon",
        AssetName = "drover",
        // An open bed: the bed cosmetics stay, and the Bed slot goes to cosm_Bed.
        HasBed = true,
        Cosmetics = CarCosmetics.Build(CarCosmetics.SlotAnchors.Append(("cosm_Bed", "Visual/Bed")), CarCosmetics.ItemAnchors),
        // A real 1973-79 F-series-type 4x4 long bed: about 2000 kg (x0.71 of the Blast Wagon's 2800 kg) and 42 kW (a 400 V8's
        // 126 kW net, about 21 kW/t after the game's 0.35), an 8.0 m/s limiter from a 150 km/h real top speed, a slower-revving
        // V8 (revs x0.9) and gearing so the limiter comes at 85 % of the revs on the 0.415 m tyres; springs and dampers for
        // the lighter body at the donor's damping ratio (about 0.64). Tows 3.2 t. Gears and diffs: a 4-speed truck box, open axles
        // (the limited-slip rear was an option), no centre diff (part-time 4x4: locked in 4WD).
        GearSet = "truck4",
        Diffs = new byte[] { 1, 1, 3 },
        Tuning = new CarTuningProfile
        {
            Mass = 0.71f, CentreOfMassDrop = 0.0f, Torque = 0.78f, Power = 0.70f, TopSpeed = 1.34f, RevRange = 0.9f, GearRatios = 0.75f, FinalDrive = 0.905f,
            SuspensionTravel = 1.1f, SpringRate = 0.80f, DamperRate = 0.75f, TyreGrip = 1.0f, SteeringLock = 0.95f, TowKg = 3200f,
        },
        // Period truck colours (made-up names): copper brown, white, a deep red, harvest gold, avocado, sky blue, black.
        Paints = new[]
        {
            new PaintPreset("Copper Brown", new CarRgb(122, 74, 34)),
            new PaintPreset("Bright White", new CarRgb(232, 232, 226)),
            new PaintPreset("Barn Red", new CarRgb(132, 30, 26)),
            new PaintPreset("Harvest Gold", new CarRgb(206, 160, 62)),
            new PaintPreset("Avocado", new CarRgb(96, 104, 52)),
            new PaintPreset("Sky Blue", new CarRgb(110, 146, 180)),
            new PaintPreset("Black", new CarRgb(24, 24, 26)),
        },
        PaintCustomDefault = "#7A4A22",
        // Zone G: the steel wheels and the grille's egg-crate insert. The bumpers, the grille surround, the lamp bezels,
        // the hubcaps and the window trim stay chrome.
        Trims = new[]
        {
            new TrimPreset("White", new CarRgb(228, 226, 220)),
            new TrimPreset("Silver", new CarRgb(176, 178, 178)),
            new TrimPreset("Black", new CarRgb(30, 30, 33)),
            TrimPreset.Body(),
        },
        TrimCustomDefault = "#E4E2DC",
        TrimWhat = "Trim (wheels and grille insert)",
        TrimHint = "The steel wheels and the grille's egg-crate insert: white (as delivered), silver, black, the body colour or " +
                   "your own. The bumpers, the grille surround and the hubcaps stay chrome.",
        // Stock engine sounds: the Blast Wagon, the game's old work truck (its donor too), then the Burro pickup, the Boar
        // and the Rust Ox's old petrol engine.
        EngineSounds = new[]
        {
            new EngineSound("BlastWagon", "Blast Wagon (work truck)"),
            new EngineSound("Burro", "Burro (pickup)"),
            new EngineSound("Boar", "Boar"),
            new EngineSound("RustOx", "Rust Ox"),
        },
        DefaultEngine = 0,
        EngineHint = "Which stock car's engine your Drover sounds like (the Blast Wagon's old work-truck engine fits it best). " +
                     "Other players with this mod hear your pick.",
        // The garage's Liveries tab, after "Paint" (ids are part of the network layout: only append).
        Liveries = new[]
        {
            new LiveryPreset("white_band", "White Band Two-Tone", "A white roof and the side dent filled in white over your own paint, the factory two-tone of the time."),
            new LiveryPreset("headless_valley", "Headless Valley Ranch", "The ranch's hay truck from Asgard Mountain: avocado and white, its brand on the doors and tailgate."),
            new LiveryPreset("fire_patrol", "Harrison Watchtower Fire Patrol", "The Emerald Lake fire patrol truck: red with a white roof and stripe, unit 7."),
        },
    };
}
