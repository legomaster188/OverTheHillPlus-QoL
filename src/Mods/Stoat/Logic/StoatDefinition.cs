using OverTheHillPlus.Cars;

namespace OverTheHillPlus.Stoat;

/// <summary>
/// The Stoat (mod id "stoat"): a 1960s-70s American walk-in delivery step van for core's car framework, built from scratch to the size
/// of the real ones (125 in wheelbase, 10 ft body: 3.175 m wheelbase, 5.32 m long, 2.16 m wide, 2.53 m to the roof): a tall square
/// riveted body on a light-truck chassis, a split flat windscreen, sliding cab doors, double rear doors, a one-piece hood, a jump seat
/// and a shelved cargo bay (a made-up name and maker: no badges or lettering anywhere in the UI or the model). Its identity values are
/// part of the network protocol and of the save sidecar (oth.stoat.&lt;profile&gt;.sidecar): they never change once released. Pure
/// (unit-tested).
/// </summary>
public static class StoatDefinition
{
    public const string Id = "stoat";
    public const string DisplayName = "Stoat";
    /// <summary>The next free id after the Jackal's 0xB7E2A018 (the last modded car), checked against the live catalog at startup.</summary>
    public const uint ItemId = 0xB7E2A01A;
    /// <summary>CarInfo.name and the livery car name ("car": "Stoat" in a skin's manifest; ASCII only).</summary>
    public const string CarName = "Stoat";
    public const string Guid = "637f9eea7ebd4ee092508f0b2536b081";
    public const int Protocol = 1;

    /// <summary>The tyre's radius in the model, metres: 8.75-16.5.</summary>
    public const float TyreRadius = 0.385f;

    /// <summary>The slots a step van has a place for: the nose and its bumper, the hood, the roof over the bay and the bar over the cab, the
    /// rear doors and step, the plain sides, the antenna, the winch on the bumper; the mudflaps go in by their own anchors. No side steps: it
    /// has its own step wells. No snorkel: it is cut for a raked A pillar.</summary>
    public static readonly (string Anchor, string Slot)[] SlotAnchors =
    {
        ("cosm_Front", "Visual/Front"),
        ("cosm_Rear", "Visual/Rear"),
        ("cosm_Front_Bumper", "Visual/Front_Bumper"),
        ("cosm_Rear_Bumper", "Visual/Rear_Bumper"),
        ("cosm_Hood", "Visual/Hood"),
        ("cosm_Roof", "Visual/Roof"),
        ("cosm_Roof_Bar", "Visual/Roof_Bar"),
        ("cosm_Side", "Visual/Side"),
        ("cosm_Antenna", "Visual/Antenna"),
        ("cosm_Winch", "Performance/Winch"),
    };

    public static CarDefinition Create() => new()
    {
        Id = Id,
        DisplayName = DisplayName,
        Description = "A walk-in delivery van from the sixties: a tall square body, sliding cab doors, shelves in the back and a jump seat " +
                      "for the helper. Slow, heavy and leans like a wardrobe, but the mail gets there.",
        PageNote = "A 1960s walk-in delivery step van: 2.75 t, a big old six, three on the tree, rear-wheel drive, soft leaf springs and a very " +
                   "tall body. Buy it in the garage, or try it from here. Everyone on a server needs this mod to see it.",
        ItemId = ItemId,
        CarName = CarName,
        FusionGuid = Guid,
        RegistryKey = "stoat",
        Protocol = Protocol,
        // The Boar: the stock car nearest in weight (2.8 t) and class (Workhorse), and a closed wagon with a roof slot, not a pickup with a
        // bed. The wheel colliders take the model's own 3.175 m wheelbase, track and 0.385 m tyres. Rear-wheel drive is set on the template
        // (StoatRig).
        DefaultDonor = "Car_Boar",
        DefaultGarageBase = "Boar",
        AssetName = "stoat",
        // Three on the tree; open differentials (the front is never driven: the car is rear-wheel drive).
        GearSet = "vintage3",
        Diffs = new byte[] { 1, 1, 1 },
        // A six in a step van on the Boar's skeleton: 2,750 kg with fuel (x0.982 of 2800 kg), 40 kW (x0.67 of the 60 kW cap: 14.6 kW/t, 0.35
        // of the real 42 kW/t of a 155 hp six), a 96 km/h real top speed (owners speak of 55 to 60 mph) as a 5.93 m/s limiter (x0.988), a
        // low-revving six (revs x0.87), gearing so the limiter comes at 85 % of the revs on 0.385 m tyres (ratios x0.83), plenty of low
        // torque (x0.85). Soft leaf springs: 1.1 Hz at a corner, 21 cm of sag, so the travel goes up by the extra sag (x1.094 of the Boar's
        // 0.45 m) to keep the model's ride height, dampers for a body damping ratio of 0.40, weak on purpose: it wallows. The weight 20 cm
        // over the Boar's: a tall body on a narrow track, so it leans hard and will tip if pushed. Steering as the Boar's 28 degrees: a
        // 13.5 m circle, what the real ones turn in. Tows 1,500 kg.
        Tuning = new CarTuningProfile
        {
            Mass = 0.982f, CentreOfMassDrop = -0.20f, Torque = 0.85f, Power = 0.67f, TopSpeed = 0.988f, RevRange = 0.87f, GearRatios = 0.83f, FinalDrive = 1.0f,
            SuspensionTravel = 1.094f, SpringRate = 0.78f, DamperRate = 0.54f, TyreGrip = 1.0f, SteeringLock = 1.0f, TowKg = 1500f,
        },
        Cosmetics = CarCosmetics.Build(SlotAnchors, CarCosmetics.ItemAnchors),
        ItemAnchors = new[]
        {
            // the plush toys on the bumper (the Boar's sit on its grille guard), the horns on the hood's edge, the wing at the roof's rear edge, the
            // long tree load forward along the roof
            new CosmeticItemAnchor("cosm_i_PlushBear", "Visual/Front/PlushBear"), new CosmeticItemAnchor("cosm_i_PlushChipmunk", "Visual/Front/PlushChipmunk"),
            new CosmeticItemAnchor("cosm_i_PlushPuppy", "Visual/Front/PlushPuppy"), new CosmeticItemAnchor("cosm_i_PlushStarPuppy", "Visual/Front/PlushStarPuppy"),
            new CosmeticItemAnchor("cosm_i_BullHorns", "Visual/Hood/BullHorns"), new CosmeticItemAnchor("cosm_i_RearWing", "Visual/Roof/RearWing"), new CosmeticItemAnchor("cosm_i_RoofChristmas", "Visual/Roof/Christmas"),
        },
        // Left out on purpose. Slots: side steps (it has its own step wells), snorkel (it is cut to run up a raked A pillar: on this upright corner it
        // hangs across the quarter window; the model keeps cosm_Snorkel for the engine-flood probes, at the intake under the engine cover).
        HiddenSlots = new[] { "Visual/Side_Step", "Performance/Snorkel" },
        // Items, by what they would do on this body. The hood's spare would stand in front of the wipers on so short a nose, and the flat vents
        // are half in its 26 degree slope. The bush cables run from a long nose to the windscreen's top: here they would lie on the glass. The
        // under-bumper bash bar and skid plates are inside the bumper. The rear gear is cut for the Boar's raked tail: on these upright doors the
        // spare, grill, guitar, ladder and bike touch at one point and stand off or go through; the pan and lantern hang from a rack this tail
        // hasn't got. The rear garland is cut for the Boar's bumper and stands 20 cm off these doors.
        HiddenItems = new[]
        {
            "Visual/Hood/Sparewheel", "Visual/Hood/Vents", "Visual/Hood/VentsOval", "Visual/Hood/Bushcables",
            "Visual/Front_Bumper/BashBar_02", "Visual/Front_Bumper/SkidPlate_01", "Visual/Front_Bumper/SkidPlate_02",
            "Visual/Rear/SpareWheel", "Visual/Rear/MountedGrill", "Visual/Rear/Guitar", "Visual/Rear/Ladder", "Visual/Rear/Bike",
            "Visual/Rear/Pan", "Visual/Rear/CampingLantern", "Visual/Rear_Bumper/Christmas_Rear",
        },
        // The tow bar's ball past the step bumper's edge, where a coupling can reach it (Unity axes: right, up, forward).
        ItemOffsets = { ["Visual/Rear_Bumper/TowBar"] = new MeshVec3(0f, 0f, -0.08f) },
        // The colours these vans came in: fleet red, parcel brown, dairy cream, postal blue, a pale green, school-bus yellow, plain white.
        Paints = new[]
        {
            new PaintPreset("Fleet Red", new CarRgb(146, 40, 32)),
            new PaintPreset("Parcel Brown", new CarRgb(94, 64, 42)),
            new PaintPreset("Dairy Cream", new CarRgb(228, 218, 190)),
            new PaintPreset("Postal Blue", new CarRgb(44, 76, 122)),
            new PaintPreset("Bakery Green", new CarRgb(116, 158, 128)),
            new PaintPreset("Bus Yellow", new CarRgb(224, 168, 34)),
            new PaintPreset("Fleet White", new CarRgb(234, 234, 228)),
            new PaintPreset("Graphite", new CarRgb(66, 70, 74)),
        },
        PaintCustomDefault = "#922820",
        // Zone G is the roof cap, the bumpers, the headlamp surrounds and the wheels.
        Trims = new[]
        {
            new TrimPreset("Ivory", new CarRgb(230, 226, 212)),
            TrimPreset.Body(),
            new TrimPreset("Silver", new CarRgb(176, 180, 184)),
            new TrimPreset("Black", new CarRgb(30, 31, 33)),
        },
        TrimCustomDefault = "#E6E2D4",
        TrimWhat = "Roof, bumpers and wheels",
        TrimHint = "The roof cap, the bumpers, the headlamp surrounds and the wheels: ivory (as delivered), the body colour, silver, black or your own.",
        // Old sixes and work trucks. Not listened to side by side yet: the list is easy to change.
        EngineSounds = new[]
        {
            new EngineSound("Timberbox", "Timberbox (1940s six)"),
            new EngineSound("Burro", "Burro (pickup)"),
            new EngineSound("Boar", "Boar"),
            new EngineSound("RustOx", "Rust Ox"),
        },
        DefaultEngine = 0,
        EngineHint = "Which stock car's engine your Stoat sounds like (the Timberbox's old six fits it best). Other players with this mod hear your pick.",
        // The garage's Liveries tab, after "Paint" (ids are part of the network layout: only append).
        Liveries = new[]
        {
            new LiveryPreset("rural_mail", "Valhallas Rural Mail", "The mail van that goes round the Valhallas: white with an airmail band, RURAL MAIL down the side, route 3 on the door."),
            new LiveryPreset("rust_ox", "Rust Ox Biscuits", "The biscuit van: cream over oxide red with a gold line, the name in big script and the slogan, baked slow, delivered slower."),
            new LiveryPreset("hollowmere", "Hollowmere Supply", "The van the Hollowmere shops send out: deep green, an ivory roof, gold-leaf lettering for parts, groceries and fuel cans."),
        },
    };

    /// <summary>The career dealer's listing (Api.Career.Dealer): a base price on the career's table, sold on its money scale.</summary>
    public const long DealerPrice = 7500;

    public static OverTheHillPlus.Api.CarOffer DealerOffer() => new()
    {
        Car = CarName, Name = DisplayName, Category = "truck", Price = DealerPrice, OnMoneyScale = true,
        Description = "A walk-in delivery van with shelves in the back. Slow and tippy, and made for the mail and shop runs.",
    };
}
