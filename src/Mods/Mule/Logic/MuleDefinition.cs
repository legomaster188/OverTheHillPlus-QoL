using System.Linq;
using OverTheHillPlus.Cars;

namespace OverTheHillPlus.Mule;

/// <summary>
/// The Mule (mod id "mule"): a late-1940s / early-50s American half-ton stepside pickup for core's car framework, in the
/// game's Workhorse class, built to the size of a 1948 Ford F-1 / 1951 Chevrolet 3100: separate round front fenders with the
/// headlamps in them, a five-bar grille, a split windscreen, running boards and a 6.5 ft stepside box with a wooden floor
/// (a made-up brand: no maker names, badges or lettering anywhere in the UI or the model). Its identity values are part of
/// the network protocol and of the save sidecar (oth.mule.&lt;profile&gt;.sidecar): they never change once released.
/// Pure (unit-tested).
/// </summary>
public static class MuleDefinition
{
    public const string Id = "mule";
    public const string DisplayName = "Mule";
    /// <summary>Hand-picked next to the Hilo 22's 0xB7E2A006, checked against the live catalog at startup.</summary>
    public const uint ItemId = 0xB7E2A00D;
    /// <summary>CarInfo.name and the livery car name ("car": "Mule" in a skin's manifest; ASCII only).</summary>
    public const string CarName = "Mule";
    public const string Guid = "f808f27c5f334dd1a61ef4a2fba8e1a7";
    public const int Protocol = 4;

    /// <summary>The whip-light poles of the Rear slot stand on the ends of the Mule's rear bumper, and the pan and the
    /// lantern hang on its own tailgate corner: the Burro's bed is wider, so where the slot alone puts them they float
    /// beside the Mule's narrower box.</summary>
    public static readonly CosmeticItemAnchor[] OwnItemAnchors =
        Enumerable.Range(1, 7).Select(i => new CosmeticItemAnchor("cosm_Whip", "Visual/Rear/WhipLights_0" + i))
            .Append(new CosmeticItemAnchor("cosm_Pan", "Visual/Rear/Pan"))
            .Append(new CosmeticItemAnchor("cosm_Lantern", "Visual/Rear/CampingLantern"))
            .ToArray();

    public static CarDefinition Create() => new()
    {
        Id = Id,
        DisplayName = DisplayName,
        Description = "A late-1940s half-ton pickup: round front fenders with the headlamps in them, a five-bar grille, " +
                      "running boards and a stepside box with a wooden floor. Light, slow and simple, with a narrow-tyred " +
                      "old truck's feel.",
        PageNote = "A 1940s-style half-ton stepside in the Workhorse class: light, slow and torquey for its size, with a 6.5 ft " +
                   "wooden-floored box. Buy it in the garage, or try it from here. Everyone on a server needs this mod to see it.",
        ItemId = ItemId,
        CarName = CarName,
        FusionGuid = Guid,
        RegistryKey = "mule",
        Protocol = Protocol,
        // The Burro, the stock pickup and a Workhorse (2.94 m wheelbase against the Mule's 2.92 m, a cab and an open bed):
        // its skeleton, garage data (vehicle class, upgrades) and cosmetic slots (Liveries/mule/README.md).
        DefaultDonor = "Car_Burro",
        DefaultGarageBase = "Burro",
        AssetName = "mule",
        // An open bed, like the Burro's: its cargo bed and the bed cosmetics stay, and the Bed slot goes to cosm_Bed.
        HasBed = true,
        // the standard whip-light anchor gives way to the Mule's own (cosm_Whip, ItemAnchors below): one anchor per target
        Cosmetics = CarCosmetics.Build(CarCosmetics.SlotAnchors.Append(("cosm_Bed", "Visual/Bed")),
            CarCosmetics.ItemAnchors.Where(a => !a.Item.StartsWith("Visual/Rear/WhipLights_", System.StringComparison.Ordinal))),
        ItemAnchors = OwnItemAnchors,
        // The Burro's side steps would cut through the running boards, and its side light string is 30 cm wider than them.
        HiddenSlots = new[] { "Visual/Side_Step" },
        // A real 1948-52 half-ton: about 1500 kg (x0.54 of the Burro's 2800 kg) and 27 kW (a 95 hp six: about 18 kW/t), a 6.3 m/s
        // (23 km/h) limiter from a 105 km/h real top speed, a slow-revving engine (revs x0.88), gearing so the limiter comes at
        // 85 % of the revs on the real 0.36 m 6.00-16 tyres, softer springs for the half-weight body. Tows 1 t. Gears and diffs: a
        // 3-speed with open diffs.
        GearSet = "vintage3",
        Diffs = new byte[] { 1, 1, 3 },
        Tuning = new CarTuningProfile
        {
            // Dampers set for a body damping ratio of about 0.65, the Workhorse donors' own (CarPhysicsFitTests): the
            // donor's dampers cut with the springs left this light car over-damped, stiff over every bump.
            Mass = 0.54f, CentreOfMassDrop = 0.03f, Torque = 0.56f, Power = 0.45f, TopSpeed = 1.05f, RevRange = 0.88f, GearRatios = 0.74f, FinalDrive = 1.0f,
            SuspensionTravel = 1.1f, SpringRate = 0.62f, DamperRate = 0.58f, TyreGrip = 1.0f, SteeringLock = 1.0f, TowKg = 1000f,
        },
        // Period work colours: forest green, barn red, cream, black, faded blue, primer grey, mustard.
        Paints = new[]
        {
            new PaintPreset("Forest Green", new CarRgb(44, 82, 58)),
            new PaintPreset("Barn Red", new CarRgb(128, 36, 30)),
            new PaintPreset("Cream", new CarRgb(224, 214, 182)),
            new PaintPreset("Black", new CarRgb(26, 26, 28)),
            new PaintPreset("Faded Blue", new CarRgb(92, 118, 138)),
            new PaintPreset("Primer Grey", new CarRgb(122, 124, 122)),
            new PaintPreset("Mustard", new CarRgb(202, 158, 52)),
        },
        PaintCustomDefault = "#2C523A",
        // Zone G is the painted trim of these trucks: the grille bars and surround and the steel wheels. The bumpers, hubcaps,
        // lamp rims and the windscreen surround stay chrome.
        Trims = new[]
        {
            new TrimPreset("Cream", new CarRgb(226, 220, 200)),
            new TrimPreset("Silver", new CarRgb(176, 178, 178)),
            new TrimPreset("Red", new CarRgb(150, 32, 26)),
            new TrimPreset("Black", new CarRgb(30, 30, 33)),
            TrimPreset.Body(),
        },
        TrimCustomDefault = "#E2DCC8",
        TrimWhat = "Trim (grille and wheels)",
        TrimHint = "The grille bars and the steel wheels: cream (as delivered), silver, red, black, the body colour or your own. " +
                   "The bumpers and hubcaps stay chrome.",
        // Stock engine sounds: the Timberbox and the Blast Wagon are the game's 1940s trucks (a big petrol six), then the
        // Boar and the Burro.
        EngineSounds = new[]
        {
            new EngineSound("Timberbox", "Timberbox (1940s six)"),
            new EngineSound("BlastWagon", "Blast Wagon"),
            new EngineSound("Boar", "Boar"),
            new EngineSound("Burro", "Burro (pickup)"),
        },
        DefaultEngine = 0,
        EngineHint = "Which stock car's engine your Mule sounds like (the Timberbox's old six fits it best). " +
                     "Other players with this mod hear your pick.",
        // The garage's Liveries tab, after "Paint" (ids are part of the network layout: only append).
        Liveries = new[]
        {
            new LiveryPreset("two_tone", "Sunday Best", "An ivory roof and hood spears over your own paint, with a hand-laid red pinstripe."),
            new LiveryPreset("feed_seed", "Bighorn Lake Feed & Seed", "The feed store's delivery truck, barn red with hand-lettered doors."),
            new LiveryPreset("barn_find", "Barn Find", "Forty years in a barn: chalky blue paint, rust through on the tops and a black fender off another truck."),
        },
    };
}
