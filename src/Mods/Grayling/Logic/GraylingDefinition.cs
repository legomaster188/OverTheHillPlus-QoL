using OverTheHillPlus.Cars;

namespace OverTheHillPlus.Grayling;

/// <summary>
/// The Grayling (mod id "grayling"): a small aluminium fishing boat with a tiller outboard, for core's car framework, built
/// from scratch to the size of the real 14 ft riveted semi-V lake boats (4.30 m long, 1.72 m beam, three bench seats, a
/// 15 hp short-shaft outboard on a 15 in transom; a made-up brand: no maker names, badges or lettering anywhere in the UI
/// or the model). The game has no buoyancy: the boat's own mod floats it (<see cref="BoatHull"/>) where the game
/// simulates it, and its four small wheels are hidden, there only so it can sit on the shore or roll up a ramp. Its
/// identity values are part of the network protocol and of the save sidecar (oth.grayling.&lt;profile&gt;.sidecar): they
/// never change once released. Pure (unit-tested).
/// </summary>
public static class GraylingDefinition
{
    public const string Id = "grayling";
    public const string DisplayName = "Grayling";
    /// <summary>The third free id after the Gemse's 0xB7E2A013 (the Marten has 0xB7E2A014, the Ermine 0xB7E2A015),
    /// checked against the live catalog at startup.</summary>
    public const uint ItemId = 0xB7E2A016;
    /// <summary>CarInfo.name and the livery car name ("car": "Grayling" in a skin's manifest; ASCII only).</summary>
    public const string CarName = "Grayling";
    public const string Guid = "6b065996f6434a85a1420c932acb1a61";
    /// <summary>2: the hull's collider reaches the keel, the tiller is its own moving part.</summary>
    public const int Protocol = 2;

    /// <summary>The hidden wheels' radius (the framework's lower limit is 0.2 m): their bottoms are the plane the boat stands
    /// on when it is out of the water, 2 cm under the keel.</summary>
    public const float WheelRadius = 0.22f;
    /// <summary>Hidden wheel places inside the hull (Unity axes): narrow at the front, where the bottom is narrow, and aft
    /// of the middle, so the centre of mass (placed between the wheels like the donor's) lands near the hull's centre of
    /// buoyancy: the boat floats about level with the motor and the helmsman at the stern.</summary>
    public const float FrontHalf = 0.30f, RearHalf = 0.45f, FrontZ = 0.55f, RearZ = -1.35f;

    /// <summary>The slots a small boat has room for: a whip light pole or a lantern on the transom, and the antenna at its
    /// corner.</summary>
    public static readonly (string Anchor, string Slot)[] SlotAnchors =
    {
        ("cosm_Rear", "Visual/Rear"),
        ("cosm_Antenna", "Visual/Antenna"),
    };

    public static readonly CosmeticItemAnchor[] OwnItemAnchors =
    {
        new("cosm_WhipLights", "Visual/Rear/WhipLights_01", optional: true),
        new("cosm_WhipLights", "Visual/Rear/WhipLights_02", optional: true),
        new("cosm_WhipLights", "Visual/Rear/WhipLights_03", optional: true),
        new("cosm_WhipLights", "Visual/Rear/WhipLights_04", optional: true),
        new("cosm_WhipLights", "Visual/Rear/WhipLights_05", optional: true),
        new("cosm_WhipLights", "Visual/Rear/WhipLights_06", optional: true),
        new("cosm_WhipLights", "Visual/Rear/WhipLights_07", optional: true),
        new("cosm_Lantern", "Visual/Rear/CampingLantern"),
    };

    public static CarDefinition Create() => new()
    {
        Id = Id,
        DisplayName = DisplayName,
        Description = "A 14 ft aluminium fishing boat with a tiller outboard: three bench seats, oarlocks and a dry bow. Slow and " +
                      "steady, made for a calm lake and a long day out.",
        PageNote = "A small aluminium lake boat with a 15 hp outboard. Take it out at a shore: drive it from here at the water's " +
                   "edge, or back its trailer down a ramp. Everyone on a server needs this mod to see it.",
        ItemId = ItemId,
        CarName = CarName,
        FusionGuid = Guid,
        RegistryKey = "grayling",
        Protocol = Protocol,
        Kind = VehicleKind.Boat,
        // The Polyana, the lightest small stock car: its skeleton and garage data. Its wheels become four hidden 0.22 m
        // ones inside the hull.
        DefaultDonor = "Car_Polyana",
        DefaultGarageBase = "Polyana",
        AssetName = "grayling",
        HasBed = false,
        Cosmetics = CarCosmetics.Build(SlotAnchors, OwnItemAnchors),
        HiddenItems = new[] { "Visual/Rear/SpareWheel", "Visual/Rear/Bike", "Visual/Rear/Ladder", "Visual/Rear/MountedGrill", "Visual/Rear/Guitar", "Visual/Rear/Pan" },
        // Afloat with its rider: 280 kg (x0.175 of the Polyana's 1600). The water does the rest (BoatHull: buoyancy, drag,
        // the outboard's thrust and steering); the engine's numbers only set its sound and the wheels' manners on land:
        // a small engine, soft springs (about 1.4 Hz) and a damping ratio of 0.45, the weight low in the hull.
        // an outboard has forward, neutral and reverse: Gearbox+'s fewest gears
        GearSet = "vintage3",
        Diffs = new byte[] { 1, 1, 3 },
        Tuning = new CarTuningProfile
        {
            Mass = 0.175f, CentreOfMassDrop = 0.18f, Torque = 0.30f, Power = 0.30f, TopSpeed = 0.80f, RevRange = 1.20f, GearRatios = 1.0f, FinalDrive = 1.0f,
            SuspensionTravel = 0.8f, SpringRate = 0.15f, DamperRate = 0.13f, TyreGrip = 1.0f, SteeringLock = 0.8f, TowKg = 150f,
        },
        // The colours aluminium boats are painted in: a lake green, a deep blue, tan, a dark red, white, black, a grey and
        // the bare metal.
        Paints = new[]
        {
            new PaintPreset("Lake Green", new CarRgb(70, 104, 76)),
            new PaintPreset("Deep Blue", new CarRgb(28, 54, 96)),
            new PaintPreset("Tan", new CarRgb(176, 156, 118)),
            new PaintPreset("Dark Red", new CarRgb(122, 26, 26)),
            new PaintPreset("White", new CarRgb(226, 227, 224)),
            new PaintPreset("Black", new CarRgb(28, 29, 31)),
            new PaintPreset("Grey", new CarRgb(118, 122, 124)),
            new PaintPreset("Bare Aluminium", new CarRgb(184, 188, 190)),
        },
        PaintCustomDefault = "#46684C",
        // Zone G: the inside of the hull (the ribs, the floor and the seat ends).
        Trims = new[]
        {
            new TrimPreset("Grey", new CarRgb(132, 136, 136)),
            TrimPreset.Body(),
            new TrimPreset("Tan", new CarRgb(170, 152, 116)),
            new TrimPreset("Bare Aluminium", new CarRgb(184, 188, 190)),
        },
        TrimCustomDefault = "#848888",
        TrimWhat = "Inside of the hull",
        TrimHint = "The inside of the hull, the ribs and the floor: grey (as delivered), the body colour, tan, bare aluminium or your own.",
        // Stock engine sounds of the smallest engines (an outboard's little twin is closest to a small four). Not listened
        // to side by side yet: the list is easy to change.
        EngineSounds = new[]
        {
            new EngineSound("Tinny", "Tinny (small four)"),
            new EngineSound("DingDing", "Ding (small four)"),
            new EngineSound("Trailrat", "Trailrat (buggy)"),
            new EngineSound("Polyana", "Polyana"),
        },
        DefaultEngine = 0,
        EngineHint = "Which stock car's engine your Grayling's outboard sounds like. Other players with this mod hear your pick.",
        Liveries = new[]
        {
            new LiveryPreset("fishermans_cove", "Fisherman's Cove", "A rental boat from Fisherman's Cove: faded green over a dark red bottom, a white stripe and a big painted number."),
        },
    };

    /// <summary>The career dealer's listing (Api.Career.Dealer): a base price on the career's table, sold on its money scale.</summary>
    public const long DealerPrice = 6000;

    public static OverTheHillPlus.Api.CarOffer DealerOffer() => new()
    {
        Car = CarName, Name = DisplayName, Category = "boat", Price = DealerPrice, OnMoneyScale = true,
        Description = "A 14 ft aluminium fishing boat with a tiller outboard. Launch it from a lake shore.",
    };
}
