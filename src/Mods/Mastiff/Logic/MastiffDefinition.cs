using OverTheHillPlus.Cars;

namespace OverTheHillPlus.Mastiff;

/// <summary>
/// The Mastiff (mod id "mastiff"): a medium-duty 4x4 wrecker for core's car framework, in the style of the American trucks of the
/// 1970s and 80s, built from scratch to their size (3.86 m wheelbase, 96 in bumper to back of cab, 2.44 m wide over the rear duals):
/// a square conventional cab on a heavy riveted chassis, a steel tow body with side lockers, a twin-boom crane with cable, sheaves
/// and hook blocks, screw outriggers, a big push bumper, an amber beacon bar (a made-up name and maker: no badges or lettering
/// anywhere in the UI or the model). It lands in the garage's Expedition class, with the Mammoth. Its identity values are part of
/// the network protocol and of the save sidecar (oth.mastiff.&lt;profile&gt;.sidecar): they never change once released. Pure
/// (unit-tested).
/// </summary>
public static class MastiffDefinition
{
    public const string Id = "mastiff";
    public const string DisplayName = "Mastiff";
    /// <summary>The next free id after the Jackal's 0xB7E2A018, checked against the live catalog at startup.</summary>
    public const uint ItemId = 0xB7E2A019;
    /// <summary>CarInfo.name and the livery car name ("car": "Mastiff" in a skin's manifest; ASCII only).</summary>
    public const string CarName = "Mastiff";
    public const string Guid = "2180f2c3ad6a4c168718368f99d0172a";
    public const int Protocol = 1;

    /// <summary>The model's tyre radius, metres: 10.00-20 all round, duals behind.</summary>
    public const float TyreRadius = 0.525f;

    /// <summary>The slots a wrecker has a place for: the push bumper (plates, shackles, the winch), the hood, the cab roof ahead of
    /// the beacon bar, the antenna, the tail (whip poles on the lockers, rear gear on the sill) and the frame end (tow bar, plate).
    /// It has its own steps, lockers and mud flaps, so no side steps, side gear or fender slot.</summary>
    public static readonly (string Anchor, string Slot)[] SlotAnchors =
    {
        ("cosm_Front", "Visual/Front"),
        ("cosm_Rear", "Visual/Rear"),
        ("cosm_Front_Bumper", "Visual/Front_Bumper"),
        ("cosm_Rear_Bumper", "Visual/Rear_Bumper"),
        ("cosm_Hood", "Visual/Hood"),
        ("cosm_Roof", "Visual/Roof"),
        ("cosm_Roof_Bar", "Visual/Roof_Bar"),
        ("cosm_Antenna", "Visual/Antenna"),
        ("cosm_Winch", "Performance/Winch"),
    };

    public static CarDefinition Create() => new()
    {
        Id = Id,
        DisplayName = DisplayName,
        Description = "A medium-duty 4x4 wrecker: a square cab on a heavy riveted frame, a steel tow body with side lockers, a twin-boom " +
                      "crane on its own winches, a big push bumper and duals behind. Very heavy and geared low: it won't win a race, " +
                      "but it gets to the ditch and it gets you out.",
        PageNote = "A 4x4 wrecker of about 8.4 t: a 6.6 litre petrol V8, five gears, low gearing, tows 12 t. Good for the career's recovery " +
                   "and help jobs. Buy it in the garage, or try it from here. Everyone on a server needs this mod to see it.",
        ItemId = ItemId,
        CarName = CarName,
        FusionGuid = Guid,
        RegistryKey = "mastiff",
        Protocol = Protocol,
        // The Mammoth, the stock Expedition truck: its 0.52 m tyres are the 10.00-20's size, so the ride height stays, and every stock
        // truck is a locked 4x4. Its skeleton, garage data (the Expedition vehicle class, upgrades) and cosmetic slots
        // (Liveries/mastiff/README.md). The wheelbase (3.86 m) and the tracks (1.96 and 1.84 m) come from the model.
        DefaultDonor = "Car_Mammoth",
        DefaultGarageBase = "Mammoth",
        Wrecker = true,
        AssetName = "mastiff",
        // A 4x4 wrecker on the Mammoth's skeleton: 8.4 t (x2.625 of 3200 kg), the 404 cu in V8's real 366 lb ft (496 N m: torque x2.48 of
        // 200), 91 kW (x1.6 of the 57 kW cap: 10.8 kW/t, a little over half the real 18.7, the Expedition class's scale), a 95 km/h
        // real top speed as a 5.89 m/s limiter (x1.07), gearing so the limiter comes at 85 % of the revs on the 0.525 m tyres (x1.275:
        // low, as it should be), leaf springs at 1.2 Hz with dampers for a body damping ratio of 0.55, the travel trimmed for the
        // stiffer springs' smaller sag, so the model stands in the world as drawn. Tows 12 t. Gears and diffs: the five-speed, open
        // axles and a locked centre (the front axle is switched in; the real truck has no centre differential).
        GearSet = "truck5",
        Diffs = new byte[] { 1, 1, 3 },
        Tuning = new CarTuningProfile
        {
            Mass = 2.625f, CentreOfMassDrop = 0.05f, Torque = 2.48f, Power = 1.6f, TopSpeed = 1.07f, RevRange = 1.0f, GearRatios = 1.275f, FinalDrive = 1.0f,
            SuspensionTravel = 0.975f, SpringRate = 2.9f, DamperRate = 2.45f, TyreGrip = 1.0f, SteeringLock = 1.0f, TowKg = 12000f,
        },
        Cosmetics = CarCosmetics.Build(SlotAnchors, CarCosmetics.ItemAnchors),
        // The tow bar on the drawbar plate under the frame end, so the ball comes to 0.64 m where the trailers' couplers reach (the frame
        // ends at 0.84 m); the seat on the pintle hook; the pan and the lantern off the tow sling's top bar.
        ItemAnchors = new[]
        {
            new CosmeticItemAnchor("cosm_i_TowBar", "Visual/Rear_Bumper/TowBar"),
            new CosmeticItemAnchor("cosm_i_ToiletSeat", "Visual/Rear_Bumper/ToiletSeat"),
            new CosmeticItemAnchor("cosm_i_Pan", "Visual/Rear/Pan"),
            new CosmeticItemAnchor("cosm_i_CampingLantern", "Visual/Rear/CampingLantern"),
        },
        // The chassis behind the cab, the tow body and the booms' heads: the model's collider part covers the bumper, nose and cab only
        // (Liveries/tools/mastiff/mastiff_const.py). Unity axes: right, up, forward.
        BodyBoxes = new[]
        {
            new CarBodyBox(new MeshVec3(0f, 0.92f, -0.14f), new MeshVec3(1.60f, 0.74f, 1.14f)),
            new CarBodyBox(new MeshVec3(0f, 1.40f, -2.10f), new MeshVec3(2.44f, 0.95f, 2.80f)),
            new CarBodyBox(new MeshVec3(0f, 2.65f, -3.20f), new MeshVec3(1.25f, 1.30f, 0.90f)),
        },
        // Fender: it has its own mud flaps behind the duals and no room for the game's on the fenders' lips. Side: cut for a closed body
        // side, and the lockers are in the way. Side_Step: its own two-tread steps.
        HiddenSlots = new[] { "Visual/Fender", "Visual/Side", "Visual/Side_Step" },
        // Items, by what they would do here. The bull bars, bash bars and skid plates: it has its own push bumper and posts in the same
        // place, and a skid plate cut for the Mammoth's nose would hang in front of the axle. The tail's spare, bike, ladder, grill and
        // guitar hang where the tow sling is. The rear bash bar would go through the outriggers. The roof loads are racks and boats a
        // metre or two longer than this cab's roof, which has the beacon bar on it, and the rocket stands through that bar: only the
        // crown has room.
        HiddenItems = new[]
        {
            "Visual/Front/Bullbar_00", "Visual/Front/Bullbar_00_LightsRound", "Visual/Front/Bullbar_00_LightsSquare",
            "Visual/Front/Bullbar_01", "Visual/Front/Bullbar_01_LightsRound", "Visual/Front/Bullbar_01_LightsSquare",
            "Visual/Front/Bullbar_02", "Visual/Front/Bullbar_02_LightsRound", "Visual/Front/Bullbar_02_LightsSquare",
            "Visual/Front_Bumper/BashBar_01", "Visual/Front_Bumper/BashBar_02", "Visual/Front_Bumper/SkidPlate_01", "Visual/Front_Bumper/SkidPlate_02",
            "Visual/Rear/SpareWheel", "Visual/Rear/Bike", "Visual/Rear/Ladder", "Visual/Rear/MountedGrill", "Visual/Rear/Guitar",
            "Visual/Rear_Bumper/BashBar_01_Rear",
            "Visual/Roof/RoofSpare", "Visual/Roof/Camper", "Visual/Roof/Tent", "Visual/Roof/Christmas", "Visual/Roof/Overlander", "Visual/Roof/Rescue",
            "Visual/Roof/Party", "Visual/Roof/Refreshments", "Visual/Roof/Kayak", "Visual/Roof/Canoe", "Visual/Roof/Surfboard", "Visual/Roof/Wintersports",
            "Visual/Roof/Fisher", "Visual/Roof/Boat", "Visual/Roof/Hotdog", "Visual/Roof/IceCream", "Visual/Roof/RearWing", "Visual/Roof/Rocket",
        },
        // The colours trucks like this were sprayed in: fleet yellow, fire red, a highway orange, a forest green, a county blue, white,
        // black and a dull cream.
        Paints = new[]
        {
            new PaintPreset("Fleet Yellow", new CarRgb(217, 162, 27)),
            new PaintPreset("Fire Red", new CarRgb(158, 28, 24)),
            new PaintPreset("Highway Orange", new CarRgb(216, 104, 24)),
            new PaintPreset("Forest Green", new CarRgb(30, 74, 50)),
            new PaintPreset("County Blue", new CarRgb(34, 66, 120)),
            new PaintPreset("White", new CarRgb(232, 232, 226)),
            new PaintPreset("Black", new CarRgb(24, 24, 26)),
            new PaintPreset("Cream", new CarRgb(222, 208, 170)),
        },
        PaintCustomDefault = "#D9A21B",
        // Zone G is the wrecker: the tow body and its lockers, the crane, the outriggers and the push bumper.
        Trims = new[]
        {
            new TrimPreset("Graphite", new CarRgb(58, 63, 68)),
            TrimPreset.Body(),
            new TrimPreset("Black", new CarRgb(26, 27, 29)),
            new TrimPreset("White", new CarRgb(232, 232, 226)),
            new TrimPreset("Fire Red", new CarRgb(158, 28, 24)),
            new TrimPreset("Galvanised", new CarRgb(150, 154, 156)),
        },
        TrimCustomDefault = "#3A3F44",
        TrimWhat = "Tow body and crane",
        TrimHint = "The tow body, the lockers, the crane and the push bumper: graphite (as delivered), the cab's colour, black, white, " +
                   "red, galvanised or your own.",
        // Stock engine sounds: the Grand Woodman's big V8 is the closest the game has to a petrol truck V8; then the Mammoth's truck
        // diesel, the Blast Wagon and the Timberbox. Chosen by engine type, not listened to side by side.
        EngineSounds = new[]
        {
            new EngineSound("GrandWoodman", "Grand Woodman (big V8)"),
            new EngineSound("Mammoth", "Mammoth (truck diesel)"),
            new EngineSound("BlastWagon", "Blast Wagon (work truck)"),
            new EngineSound("Timberbox", "Timberbox (1940s six)"),
        },
        DefaultEngine = 0,
        EngineHint = "Which stock car's engine your Mastiff sounds like (the Grand Woodman's V8 fits it best). Other players with this mod hear your pick.",
        // The garage's Liveries tab, after "Paint" (ids are part of the network layout: only append).
        Liveries = new[]
        {
            new LiveryPreset("gimli", "Gimli Ridge Recovery", "The ridge's own wrecker: forest green over cream, a gold line along the cab and the lockers, hand-lettered doors."),
            new LiveryPreset("asgard", "Asgard Mountain Rescue", "White with red chevrons on the tail and bands down the booms, the kind of truck you are glad to see."),
            new LiveryPreset("elkalaa", "El Kalaa Outpost", "The outpost's recovery truck in the Tassili: sand paint over rust, faded by the sun, a stencilled number."),
        },
    };

    /// <summary>The career dealer's listing (Api.Career.Dealer): a base price on the career's table, sold on its money scale.</summary>
    public const long DealerPrice = 48000;

    public static OverTheHillPlus.Api.CarOffer DealerOffer() => new()
    {
        Car = CarName, Name = DisplayName, Category = "truck", Price = DealerPrice, OnMoneyScale = true,
        Description = "A 4x4 wrecker with a twin-boom crane. Tows 12 t.",
    };
}
