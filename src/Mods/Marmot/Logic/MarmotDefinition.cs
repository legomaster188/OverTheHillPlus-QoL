using System.Linq;
using OverTheHillPlus.Cars;

namespace OverTheHillPlus.Marmot;

/// <summary>
/// The Marmot (mod id "marmot"): an early-1980s micro 4x4 hardtop for core's car framework, in the game's Trail class,
/// built to the size of the real cars of its kind (2.03 m wheelbase, 3.42 m long with the spare, 1.40 m body, 195R15
/// tyres): a boxy tub with a pressed side crease, a ribbed metal hardtop, flat-topped wings, an eleven-bar grille between
/// round headlamps, a fold-down windscreen frame, a side-hinged rear door carrying the spare, live axles on leaf springs
/// (a made-up brand: no maker names, badges or lettering anywhere in the UI or the model). Its identity values are part
/// of the network protocol and of the save sidecar (oth.marmot.&lt;profile&gt;.sidecar): they never change once released.
/// Pure (unit-tested).
/// </summary>
public static class MarmotDefinition
{
    public const string Id = "marmot";
    public const string DisplayName = "Marmot";
    /// <summary>Assigned after the Uaz slot (0xB7E2A00E), checked against the live catalog at startup.</summary>
    public const uint ItemId = 0xB7E2A00F;
    /// <summary>CarInfo.name and the livery car name ("car": "Marmot" in a skin's manifest; ASCII only).</summary>
    public const string CarName = "Marmot";
    public const string Guid = "2cfe2d24bb514aa69a87d4c74a458460";
    public const int Protocol = 1;

    /// <summary>The Polyana's pan and lantern hang from its roof rack; on the Marmot they hang on the hardtop's rear
    /// corners beside the rear door (the whip-light poles use core's standard cosm_WhipLights anchor on the rear bumper's
    /// ends).</summary>
    public static readonly CosmeticItemAnchor[] OwnItemAnchors =
    {
        new("cosm_Pan", "Visual/Rear/Pan"),
        new("cosm_Lantern", "Visual/Rear/CampingLantern"),
    };

    public static CarDefinition Create() => new()
    {
        Id = Id,
        DisplayName = DisplayName,
        Description = "A tiny early-80s 4x4 with a metal hardtop: short, narrow and light, on leaf springs with a low " +
                      "transfer gear and the spare on the back door. Slow on the road, at home on a goat track.",
        PageNote = "A 1980s micro 4x4 in the Trail class: 2 m wheelbase, 930 kg, a small four-cylinder and a five-speed with " +
                   "low range. Buy it in the garage, or try it from here. Everyone on a server needs this mod to see it.",
        ItemId = ItemId,
        CarName = CarName,
        FusionGuid = Guid,
        RegistryKey = "marmot",
        Protocol = Protocol,
        // The Polyana, the smallest stock Trail car: its 0.35 m tyres are the real 195R15s, its wheelbase (2.18 m) the
        // closest to the Marmot's 2.03 m, its 0.40 m travel suits leaf springs (Liveries/marmot/README.md).
        DefaultDonor = "Car_Polyana",
        DefaultGarageBase = "Polyana",
        AssetName = "marmot",
        HasBed = false,
        ItemAnchors = OwnItemAnchors,
        // The Marmot carries its own spare on the rear door (the Polyana's would hang over it); a 2.1 m bike rack and a
        // ladder leaning back onto the roof have no place on a 1.4 m car with a vertical rear.
        HiddenItems = new[] { "Visual/Rear/SpareWheel", "Visual/Rear/Bike", "Visual/Rear/Ladder" },
        // A real early-80s micro 4x4 (1.3 l, 64 hp): about 930 kg (x0.58 of the Polyana's 1600 kg) and 16.5 kW (x0.33,
        // 17.8 kW/t: about 0.35 of the real 50 kW/t), a 7.65 m/s limiter from a 140 km/h real top speed, a revvy small four
        // (revs x1.10), gearing so the limiter comes at 85 % of the revs on its 0.35 m tyres, springs and dampers for the
        // lighter body. Tows 1.1 t (braked). Gears and diffs: a close 5-speed (3.65 / 1.95 / 1.42 / 1.00 / 0.80 on the real car),
        // open axles, the transfer case locks front to rear.
        GearSet = "close5",
        Diffs = new byte[] { 1, 1, 3 },
        Tuning = new CarTuningProfile
        {
            // Dampers set for a body damping ratio of about 0.55 (the Polyana's own is 0.57; CarPhysicsFitTests).
            Mass = 0.58f, CentreOfMassDrop = 0.02f, Torque = 0.62f, Power = 0.33f, TopSpeed = 1.02f, RevRange = 1.10f, GearRatios = 1.12f, FinalDrive = 1.0f,
            SuspensionTravel = 1.0f, SpringRate = 0.65f, DamperRate = 0.59f, TyreGrip = 1.0f, SteeringLock = 1.05f, TowKg = 1100f,
        },
        // The period colours of these little 4x4s: red, white, sky blue, yellow, olive, silver, black, sand.
        Paints = new[]
        {
            new PaintPreset("Red", new CarRgb(176, 42, 30)),
            new PaintPreset("White", new CarRgb(232, 232, 226)),
            new PaintPreset("Sky Blue", new CarRgb(104, 146, 188)),
            new PaintPreset("Yellow", new CarRgb(222, 178, 42)),
            new PaintPreset("Olive", new CarRgb(88, 96, 62)),
            new PaintPreset("Silver", new CarRgb(170, 172, 174)),
            new PaintPreset("Black", new CarRgb(24, 24, 26)),
            new PaintPreset("Sand", new CarRgb(196, 170, 120)),
        },
        PaintCustomDefault = "#B02A1E",
        // Zone G is the steel wheels (the spare's too). The bumpers, grille, flares and mirrors stay black.
        Trims = new[]
        {
            new TrimPreset("White", new CarRgb(230, 230, 224)),
            new TrimPreset("Silver", new CarRgb(176, 178, 178)),
            new TrimPreset("Black", new CarRgb(30, 30, 33)),
            TrimPreset.Body(),
        },
        TrimCustomDefault = "#E6E6E0",
        TrimWhat = "Wheels",
        TrimHint = "The steel wheels: white (as delivered), silver, black, the body colour or your own.",
        // Stock engine sounds: the Tinny is the game's tin-box micro 4x4, then the Polyana, the Lynx and the Brick.
        EngineSounds = new[]
        {
            new EngineSound("Tinny", "Tinny (small four)"),
            new EngineSound("Polyana", "Polyana"),
            new EngineSound("Lynx", "Lynx"),
            new EngineSound("Brick", "Brick"),
        },
        DefaultEngine = 0,
        EngineHint = "Which stock car's engine your Marmot sounds like (the Tinny's small four fits it best). " +
                     "Other players with this mod hear your pick.",
        // The garage's Liveries tab, after "Paint" (ids are part of the network layout: only append).
        Liveries = new[]
        {
            new LiveryPreset("stripes", "Sunrise Stripes", "Three fading bands along the side crease over your own paint, as the dealers fitted them."),
            new LiveryPreset("ranger", "Valhallas Ranger", "The mountain park ranger's car: white with a green band and door panels."),
            new LiveryPreset("weathered", "High Pasture", "Twenty winters on a farm track: sun-faded red, a primer wing and rust in the seams."),
        },
    };
}
