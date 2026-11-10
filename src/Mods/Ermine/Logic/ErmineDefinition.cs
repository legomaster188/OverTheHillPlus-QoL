using OverTheHillPlus.Cars;

namespace OverTheHillPlus.Ermine;

/// <summary>
/// The Ermine (mod id "ermine"): a crossover snowmobile for core's car framework, built from scratch to the size of the real
/// 600-class crossover sleds (3.05 m long, 1.24 m wide, 1.24 m to the screen, skis 1.08 m apart, a 15 x 146 in track):
/// two skis on A-arms at the front, a rubber track under a tunnel at the back, handlebars, a seat for one rider (a made-up
/// brand: no maker names, badges or lettering anywhere in the UI or the model). The game's physics drives wheels, so
/// it drives on four hidden ones laid out under the skis and the track, and its own mod moves the skis, the A-arms, the
/// handlebars and the track's lugs and wheels, and gives it a sled's grip (lots on snow and ice, very little on dirt, rock
/// and tarmac). Its identity values are part of the network protocol and of the save sidecar
/// (oth.ermine.&lt;profile&gt;.sidecar): they never change once released. Pure (unit-tested).
/// </summary>
public static class ErmineDefinition
{
    public const string Id = "ermine";
    public const string DisplayName = "Ermine";
    /// <summary>The second free id after the Gemse's 0xB7E2A013 (the Marten has 0xB7E2A014), checked against the live
    /// catalog at startup.</summary>
    public const uint ItemId = 0xB7E2A015;
    /// <summary>CarInfo.name and the livery car name ("car": "Ermine" in a skin's manifest; ASCII only).</summary>
    public const string CarName = "Ermine";
    public const string Guid = "d85efe69d5d84e42a2e327bf9075469d";
    public const int Protocol = 2;

    // ---------------------------------------------------------------- the hidden wheels (model metres, Unity axes)

    /// <summary>The hidden wheels' radius: the smallest the framework takes (0.2 m) plus a margin. Their centres stand this
    /// high over the snow: under the skis' bolts at the front and under the middle of the track's run at the back.</summary>
    public const float WheelRadius = 0.22f;
    /// <summary>Half the ski stance (1.08 m centre to centre) and half the width the rear wheels stand apart inside the
    /// 0.38 m track.</summary>
    public const float SkiHalf = 0.54f, TrackHalf = 0.17f;
    /// <summary>Front hidden wheels (under the ski bolts) and rear ones (the middle of the track's ground run), along the sled.</summary>
    public const float FrontZ = 0.72f, RearZ = -0.72f;

    /// <summary>The slots a sled has room for: the lantern and the pan hung on its rear bumper, and the antenna (a flag
    /// whip at the rack's corner). Everything else (bull bars, bumpers' gear, roof racks, steps, mudflaps, snorkels,
    /// winches, the whip lights' two poles at a car's width) has no place on it.</summary>
    public static readonly (string Anchor, string Slot)[] SlotAnchors =
    {
        ("cosm_Rear", "Visual/Rear"),
        ("cosm_Antenna", "Visual/Antenna"),
    };

    public static readonly CosmeticItemAnchor[] OwnItemAnchors =
    {
        new("cosm_Pan", "Visual/Rear/Pan"),
        new("cosm_Lantern", "Visual/Rear/CampingLantern"),
    };

    public static CarDefinition Create() => new()
    {
        Id = Id,
        DisplayName = DisplayName,
        Description = "A crossover snowmobile: skis up front, a rubber track at the back, handlebars and a seat for one. Fast " +
                      "and sure-footed on snow and ice, and hopeless on dirt and tarmac.",
        PageNote = "A 600-class crossover sled: about 345 kg with its rider, a two-stroke twin and a 15 x 146 in track. It grips " +
                   "and floats on snow and ice and scrapes to a halt on bare ground. Buy it in the garage, or try it from here. " +
                   "Everyone on a server needs this mod to see it.",
        ItemId = ItemId,
        CarName = CarName,
        FusionGuid = Guid,
        RegistryKey = "ermine",
        Protocol = Protocol,
        Kind = VehicleKind.Snowmobile,
        // The Polyana, the lightest small stock car (1.6 t, Trail class): its skeleton and garage data. Its 0.35 m wheels
        // become the hidden 0.22 m ones (the framework's lower limit is 0.2 m).
        DefaultDonor = "Car_Polyana",
        DefaultGarageBase = "Polyana",
        AssetName = "ermine",
        HasBed = false,
        Cosmetics = CarCosmetics.Build(SlotAnchors, OwnItemAnchors),
        HiddenItems = new[]
        {
            "Visual/Rear/SpareWheel", "Visual/Rear/Bike", "Visual/Rear/Ladder", "Visual/Rear/MountedGrill", "Visual/Rear/Guitar",
            "Visual/Rear/WhipLights_01", "Visual/Rear/WhipLights_02", "Visual/Rear/WhipLights_03", "Visual/Rear/WhipLights_04",
            "Visual/Rear/WhipLights_05", "Visual/Rear/WhipLights_06", "Visual/Rear/WhipLights_07",
        },
        // A 600-class crossover sled with its rider: 345 kg (x0.216 of the Polyana's 1600) and 31.5 kW (x0.63 of the 50 kW cap:
        // 0.35 of the real 261 kW/t), a 140 km/h real top speed as a 7.65 m/s limiter (x1.02), a two-stroke's revs (x1.45),
        // gearing so the limiter comes at 85 % of the revs on the 0.22 m hidden wheels, soft long-travel springs (about
        // 1.5 Hz at a corner) with dampers for a body damping ratio of 0.45, the weight low between the skis and the track
        // (12 cm under the donor's), skis that don't steer as hard as a car (lock x0.9). Grip is the sled's own
        // (SledGround). It tows a little (a sled pulls a cargo sled): 230 kg.
        // a belt drive like the side-by-side's (Gearbox+ steps it as five close gears); the four hidden wheels all drive
        GearSet = "kei5",
        Diffs = new byte[] { 1, 1, 3 },
        Tuning = new CarTuningProfile
        {
            Mass = 0.216f, CentreOfMassDrop = 0.12f, Torque = 0.55f, Power = 0.63f, TopSpeed = 1.02f, RevRange = 1.45f, GearRatios = 0.93f, FinalDrive = 1.0f,
            SuspensionTravel = 1.05f, SpringRate = 0.20f, DamperRate = 0.16f, TyreGrip = 1.0f, SteeringLock = 0.9f, TowKg = 230f,
        },
        // Sled colours: a bright race yellow, red, white, black, a lime green, a cobalt blue, orange and a gunmetal.
        Paints = new[]
        {
            new PaintPreset("Yellow", new CarRgb(236, 190, 20)),
            new PaintPreset("Red", new CarRgb(186, 24, 30)),
            new PaintPreset("White", new CarRgb(234, 235, 233)),
            new PaintPreset("Black", new CarRgb(26, 27, 29)),
            new PaintPreset("Lime", new CarRgb(132, 186, 38)),
            new PaintPreset("Cobalt", new CarRgb(28, 74, 160)),
            new PaintPreset("Orange", new CarRgb(226, 104, 20)),
            new PaintPreset("Gunmetal", new CarRgb(70, 74, 78)),
        },
        PaintCustomDefault = "#ECBE14",
        // Zone G: the tunnel (the second colour: black as delivered, or painted to match).
        Trims = new[]
        {
            new TrimPreset("Black", new CarRgb(28, 29, 31)),
            TrimPreset.Body(),
            new TrimPreset("White", new CarRgb(230, 231, 229)),
            new TrimPreset("Charcoal", new CarRgb(56, 58, 62)),
        },
        TrimCustomDefault = "#1C1D1F",
        TrimWhat = "Tunnel",
        TrimHint = "The tunnel over the track: black (as delivered), the body colour, white, charcoal or your own.",
        // Stock engine sounds of the small light cars, the buggy first. Not listened to side by side yet: the list is easy
        // to change.
        EngineSounds = new[]
        {
            new EngineSound("Trailrat", "Trailrat (buggy)"),
            new EngineSound("Tinny", "Tinny (small four)"),
            new EngineSound("DingDing", "Ding (small four)"),
            new EngineSound("Polyana", "Polyana"),
        },
        DefaultEngine = 0,
        EngineHint = "Which stock car's engine your Ermine sounds like (the Trailrat's buggy engine is the closest to a " +
                     "two-stroke twin). Other players with this mod hear your pick.",
        Liveries = new[]
        {
            new LiveryPreset("whitecap_patrol", "Whitecap Patrol", "The Whitecap Alpine Lodge's patrol sled: a red hood, white sides with the lodge's lettering and a big number."),
        },
    };

    /// <summary>The career dealer's listing (Api.Career.Dealer): a base price on the career's table, sold on its money scale.</summary>
    public const long DealerPrice = 7500;

    public static OverTheHillPlus.Api.CarOffer DealerOffer() => new()
    {
        Car = CarName, Name = DisplayName, Category = "snowmobile", Price = DealerPrice, OnMoneyScale = true,
        Description = "A two-up snowmobile. Floats over deep snow, hates bare ground.",
    };
}
