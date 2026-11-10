using OverTheHillPlus.Cars;

namespace OverTheHillPlus.Marten;

/// <summary>
/// The Marten (mod id "marten"): a two-seat utility side-by-side for core's car framework, built from scratch to the size
/// of the real two-seat sport-utility side-by-sides (2.08 m wheelbase, 3.05 m long, 1.60 m wide, 1.93 m to the roof,
/// 28 in knobby tyres): a roll cage with a hard roof, a short tipping cargo bed, long-travel double wishbones with
/// coil-overs, a sharp hood with twin lamps, black plastic flares and doors (a made-up brand: no maker names, badges or
/// lettering anywhere in the UI or the model). Its identity values are part of the network protocol and of the save
/// sidecar (oth.marten.&lt;profile&gt;.sidecar): they never change once released. Pure (unit-tested).
/// </summary>
public static class MartenDefinition
{
    public const string Id = "marten";
    public const string DisplayName = "Marten";
    /// <summary>The next free id after the Gemse's 0xB7E2A013 (the last modded car), checked against the live catalog at
    /// startup. The Ermine and the Grayling take 0xB7E2A015 and 0xB7E2A016.</summary>
    public const uint ItemId = 0xB7E2A014;
    /// <summary>CarInfo.name and the livery car name ("car": "Marten" in a skin's manifest; ASCII only).</summary>
    public const string CarName = "Marten";
    public const string Guid = "4b8f2e71c3d94a6e9f05a1d7c28e6b93";
    public const int Protocol = 1;

    /// <summary>The slots a side-by-side has a place for: the front (bull bar on the nose), the rear (gear on the bed's
    /// headache rack), both bumpers, the hood, the roof bar (lights on the cage roof), the sides, the antenna, the winch
    /// behind the front bumper and the bed. No side steps (it has none: you step over the sill) and
    /// no roof slot (the cage roof carries the roof bar).</summary>
    public static readonly (string Anchor, string Slot)[] SlotAnchors =
    {
        ("cosm_Front", "Visual/Front"),
        ("cosm_Rear", "Visual/Rear"),
        ("cosm_Front_Bumper", "Visual/Front_Bumper"),
        ("cosm_Rear_Bumper", "Visual/Rear_Bumper"),
        ("cosm_Hood", "Visual/Hood"),
        ("cosm_Roof_Bar", "Visual/Roof_Bar"),
        ("cosm_Side", "Visual/Side"),
        ("cosm_Antenna", "Visual/Antenna"),
        ("cosm_Winch", "Performance/Winch"),
        ("cosm_Bed", "Visual/Bed"),
    };

    public static CarDefinition Create() => new()
    {
        Id = Id,
        DisplayName = DisplayName,
        Description = "A two-seat side-by-side: a roll cage with a roof, a short tipping bed, long-travel suspension and big " +
                      "knobby tyres. Light, quick and happy on any track a truck won't fit.",
        PageNote = "A two-seat utility side-by-side: about 800 kg, a big twin, long-travel arms and 28 inch knobbies. Buy it in " +
                   "the garage, or try it from here. Everyone on a server needs this mod to see it.",
        ItemId = ItemId,
        CarName = CarName,
        FusionGuid = Guid,
        RegistryKey = "marten",
        Protocol = Protocol,
        // The Burro, like the Bento: a stock truck with a cargo bed (its bed slot and the 13 bed cosmetics stay), the
        // Workhorse class, 0.41 m tyres (the Marten's 28 in knobbies are 0.356 m).
        DefaultDonor = "Car_Burro",
        DefaultGarageBase = "Burro",
        AssetName = "marten",
        HasBed = true,
        Cosmetics = CarCosmetics.Build(SlotAnchors, CarCosmetics.ItemAnchors),
        // The game's bed loads are cut for the Burro's long bed: on this 0.92 m bed only the surfboard, the cooler and the
        // rolled tent sit right; the rest hang over the cab or off the tailgate at any scale.
        // No mudflaps (the flares sit right over the tyres, the game's flaps hang off them) and no snorkel (it is cut to
        // run up a car's A pillar): both slots stay hidden. The model keeps cosm_Snorkel and cosm_Hood for the engine-flood
        // probes (CarUpgradeFit).
        HiddenSlots = new[] { "Visual/Fender", "Performance/Snorkel" },
        HiddenItems = new[]
        {
            "Visual/Bed/Boat Bed", "Visual/Bed/Camper_Bed", "Visual/Bed/Canoe_Bed", "Visual/Bed/Christmas_Bed",
            "Visual/Bed/Fisher_Bed", "Visual/Bed/Kayak_Bed", "Visual/Bed/Overlander_Bed", "Visual/Bed/Party_Bed",
            "Visual/Bed/Rescue_Bed", "Visual/Bed/Wintersports_Bed",
        },
        // A 1000 cc twin side-by-side on the Burro's skeleton: about 800 kg with fuel (x0.286 of 2800 kg) and 26 kW (x0.435
        // of the 60 kW cap: about 0.35 of the real 93 kW/t), a 105 km/h real top speed as a 6.27 m/s limiter (x1.045), a
        // revvy twin behind a belt drive (revs x1.30), gearing so the limiter comes at 85 % of the revs on 0.356 m tyres,
        // soft long-travel springs (about 1.3 Hz at a corner, travel x1.35) with dampers for a body damping ratio of 0.45,
        // a tight turning circle (lock x1.15), the weight a little lower than the donor's. Tows 680 kg. Diffs: open axles and the 4x4
        // engaged (the real cars' turf mode); the belt drive has no gears, so Gearbox+ steps it like a kei truck's five.
        GearSet = "kei5",
        Diffs = new byte[] { 1, 1, 3 },
        Tuning = new CarTuningProfile
        {
            Mass = 0.286f, CentreOfMassDrop = 0.03f, Torque = 0.42f, Power = 0.435f, TopSpeed = 1.045f, RevRange = 1.30f, GearRatios = 1.08f, FinalDrive = 1.0f,
            SuspensionTravel = 1.35f, SpringRate = 0.32f, DamperRate = 0.21f, TyreGrip = 1.0f, SteeringLock = 1.15f, TowKg = 680f,
        },
        // The side-by-side colours: a sage green, black, red, sand, slate blue, white, orange and graphite.
        Paints = new[]
        {
            new PaintPreset("Sage", new CarRgb(106, 118, 88)),
            new PaintPreset("Black", new CarRgb(26, 27, 29)),
            new PaintPreset("Red", new CarRgb(170, 28, 30)),
            new PaintPreset("Sand", new CarRgb(184, 160, 116)),
            new PaintPreset("Slate Blue", new CarRgb(58, 84, 112)),
            new PaintPreset("White", new CarRgb(230, 231, 228)),
            new PaintPreset("Orange", new CarRgb(214, 96, 22)),
            new PaintPreset("Graphite", new CarRgb(74, 77, 80)),
        },
        PaintCustomDefault = "#6A7658",
        // Zone G is the roll cage and the bumpers' tubes.
        Trims = new[]
        {
            new TrimPreset("Black", new CarRgb(28, 29, 31)),
            TrimPreset.Body(),
            new TrimPreset("Silver", new CarRgb(168, 171, 174)),
            new TrimPreset("Gunmetal", new CarRgb(70, 73, 77)),
        },
        TrimCustomDefault = "#1C1D1F",
        TrimWhat = "Roll cage and bumpers",
        TrimHint = "The roll cage and the bumpers' tubes: black (as delivered), the body colour, silver, gunmetal or your own.",
        // Stock engine sounds of the small light cars: the Trailrat, the Tinny, the Ding and the Lynx. Not listened to side
        // by side yet: the list is easy to change.
        EngineSounds = new[]
        {
            new EngineSound("Trailrat", "Trailrat (buggy)"),
            new EngineSound("Tinny", "Tinny (small four)"),
            new EngineSound("DingDing", "Ding (small four)"),
            new EngineSound("Lynx", "Lynx"),
        },
        DefaultEngine = 0,
        EngineHint = "Which stock car's engine your Marten sounds like (the Trailrat's buggy engine fits it best). " +
                     "Other players with this mod hear your pick.",
        // The garage's Liveries tab, after "Paint" (ids are part of the network layout: only append).
        Liveries = new[]
        {
            new LiveryPreset("windy_joe", "Windy Joe Trail Crew", "The trail crew's side-by-side from the Windy Joe Trail: white with a green band, door lettering and a unit number."),
        },
    };

    /// <summary>The career dealer's listing (Api.Career.Dealer): a base price on the career's table, sold on its money scale.</summary>
    public const long DealerPrice = 9000;

    public static OverTheHillPlus.Api.CarOffer DealerOffer() => new()
    {
        Car = CarName, Name = DisplayName, Category = "utv", Price = DealerPrice, OnMoneyScale = true,
        Description = "A two-seat side-by-side with a tipping bed. Light, quick and fits any trail.",
    };
}
