using OverTheHillPlus.Cars;

namespace OverTheHillPlus.Jackal;

/// <summary>
/// The Jackal (mod id "jackal"): a two-seat rear-engine tube-frame sand rail for core's car framework, in the style of the
/// air-cooled off-road buggies of the 1970s and 80s, built from scratch to their size (2.44 m wheelbase, 3.88 m long, 1.74 m
/// wide over the rear tyres, 1.51 m to the cage): a bent tube frame with a full cage, a flat four out in the open behind the
/// axle, 5.60-15 ribs in front and 30 in paddles behind (a made-up name and maker: no badges or lettering anywhere in the UI
/// or the model). Its identity values are part of the network protocol and of the save sidecar
/// (oth.jackal.&lt;profile&gt;.sidecar): they never change once released. Pure (unit-tested).
/// </summary>
public static class JackalDefinition
{
    public const string Id = "jackal";
    public const string DisplayName = "Jackal";
    /// <summary>The next free id after the Meerkat's 0xB7E2A017 (the last modded car), checked against the live catalog at
    /// startup.</summary>
    public const uint ItemId = 0xB7E2A018;
    /// <summary>CarInfo.name and the livery car name ("car": "Jackal" in a skin's manifest; ASCII only).</summary>
    public const string CarName = "Jackal";
    public const string Guid = "044e234e06be4fd59859efe41ef9a479";
    public const int Protocol = 1;

    /// <summary>The tyre radii of the model, metres: 5.60-15 ribs in front, 30 x 11-15 paddles behind.</summary>
    public const float FrontTyre = 0.333f, RearTyre = 0.381f;

    /// <summary>The slots a sand rail has a place for: the nose (plush toys, hood items), the nose hoop (bumper items, winch),
    /// the rear bumper, the cage's roof rails and main hoop top (roof loads and bar items), the rear hoop (whip poles, antenna, a
    /// pan and a lantern). No fenders for mudflaps, no steps, no closed side for the side gear, no pillar for a snorkel.</summary>
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
        Description = "A two-seat sand rail: a bent tube frame, a flat four out in the open behind the axle, thin ribbed tyres in " +
                      "front and paddles behind. Light, rear-wheel drive and very happy on sand and snow.",
        PageNote = "A two-seat sand rail: about 640 kg, a 1.8 litre air-cooled flat four, rear-wheel drive, long soft suspension, " +
                   "ribbed fronts and paddle rears. Buy it in the garage, or try it from here. Everyone on a server needs this mod to see it.",
        ItemId = ItemId,
        CarName = CarName,
        FusionGuid = Guid,
        RegistryKey = "jackal",
        Protocol = Protocol,
        // The Brick: the lightest stock donor (1.6 t, a short wheelbase, the Trail class's quick steering), as for the Meerkat;
        // its skeleton, garage data and cosmetic slots. Rear-wheel drive and the rear weight are set on the template (JackalRig).
        DefaultDonor = "Car_Brick",
        DefaultGarageBase = "Brick",
        AssetName = "jackal",
        // The air-cooled four-speed; open differentials (the front is never driven: the car is rear-wheel drive).
        GearSet = "truck4",
        Diffs = new byte[] { 1, 1, 1 },
        // A 1.8 litre twin-carb flat four rail on the Brick's skeleton: about 640 kg with fuel (x0.40 of 1600 kg), 24.5 kW
        // (x0.49 of the 50 kW cap: 38 kW/t, 0.35 of the real 109 kW/t), a 130 km/h real top speed as a 7.26 m/s limiter
        // (x0.968), a revvy air-cooled four (revs x1.20), gearing so the limiter comes at 85 % of the revs on the 0.381 m
        // paddles (ratios x1.40), soft long-travel springs (1.25 Hz at a corner, 16 cm of sag, so the travel goes to 0.60 m to
        // keep the model's ride height), dampers for a body damping ratio of 0.45, the weight low, a short quick steering box.
        // Tows 300 kg.
        Tuning = new CarTuningProfile
        {
            Mass = 0.40f, CentreOfMassDrop = 0.05f, Torque = 0.70f, Power = 0.49f, TopSpeed = 0.968f, RevRange = 1.20f, GearRatios = 1.40f, FinalDrive = 1.0f,
            SuspensionTravel = 1.095f, SpringRate = 0.267f, DamperRate = 0.26f, TyreGrip = 1.0f, SteeringLock = 0.90f, TowKg = 300f,
        },
        Cosmetics = CarCosmetics.Build(SlotAnchors, CarCosmetics.ItemAnchors),
        ItemAnchors = new[]
        {
            new CosmeticItemAnchor("cosm_i_Pan", "Visual/Rear/Pan"),
            new CosmeticItemAnchor("cosm_i_CampingLantern", "Visual/Rear/CampingLantern"),
        },
        // Left out on purpose. Slots: mudflaps (no fenders to hang them on), side gear (cut for a closed side, the cage has
        // none), side steps (you step over the sill), snorkel (it runs up a car's A pillar; the air cleaners are at the back).
        // The model keeps cosm_Snorkel for the engine-flood probes.
        HiddenSlots = new[] { "Visual/Fender", "Visual/Side", "Visual/Side_Step", "Performance/Snorkel" },
        // Items, by what they would do on a tube frame. The bull bars are as wide as a car (the rail's nose is 0.42 m across and
        // has its own hoop). The spare, bike, ladder, grill and guitar hang on a tailgate this car hasn't got, over its engine.
        // The front string lights, bash bar and ropes are cut for a car's bumper, the skid plates would go through the nose
        // tray. The roof's Christmas load and boat are longer than the whole car, the rear wing floats behind the cage, and the roof
        // spare's wheels would come out oval on a rack this short.
        HiddenItems = new[]
        {
            "Visual/Front/bullbar_Brick", "Visual/Front/Bullbar_00", "Visual/Front/Bullbar_00_LightsRound", "Visual/Front/Bullbar_00_LightsSquare",
            "Visual/Front/Bullbar_01", "Visual/Front/Bullbar_01_LightsRound", "Visual/Front/Bullbar_01_LightsSquare",
            "Visual/Front/Bullbar_02", "Visual/Front/Bullbar_02_LightsRound", "Visual/Front/Bullbar_02_LightsSquare",
            "Visual/Rear/SpareWheel", "Visual/Rear/Bike", "Visual/Rear/Ladder", "Visual/Rear/MountedGrill", "Visual/Rear/Guitar",
            "Visual/Front_Bumper/Christmas_Front", "Visual/Front_Bumper/BashBar_01", "Visual/Front_Bumper/Ropes_Front",
            "Visual/Front_Bumper/SkidPlate_01", "Visual/Front_Bumper/SkidPlate_02",
            "Visual/Roof/Christmas", "Visual/Roof/Boat", "Visual/Roof/RearWing", "Visual/Roof/RoofSpare",
        },
        // The tow bar's bracket on the bumper's plate, not at the slot's height (Unity axes: right, up, forward).
        ItemOffsets = { ["Visual/Rear_Bumper/TowBar"] = new MeshVec3(0f, 0.04f, -0.10f) },
        // The colours these frames were sprayed in: safety orange, sand yellow, candy red, metal-flake blue, lime, black,
        // white and a dull olive.
        Paints = new[]
        {
            new PaintPreset("Safety Orange", new CarRgb(222, 92, 20)),
            new PaintPreset("Dune Yellow", new CarRgb(232, 182, 40)),
            new PaintPreset("Candy Red", new CarRgb(176, 26, 30)),
            new PaintPreset("Flake Blue", new CarRgb(34, 84, 160)),
            new PaintPreset("Lime", new CarRgb(142, 186, 44)),
            new PaintPreset("Black", new CarRgb(26, 27, 29)),
            new PaintPreset("White", new CarRgb(232, 232, 228)),
            new PaintPreset("Olive", new CarRgb(92, 98, 62)),
        },
        PaintCustomDefault = "#DE5C14",
        // Zone G is the sheet metal: the nose, the sill skins, the floor pan and the firewall.
        Trims = new[]
        {
            new TrimPreset("Bare Aluminium", new CarRgb(186, 190, 194)),
            TrimPreset.Body(),
            new TrimPreset("Black", new CarRgb(28, 29, 31)),
            new TrimPreset("White", new CarRgb(232, 232, 228)),
        },
        TrimCustomDefault = "#BABEC2",
        TrimWhat = "Sheet metal",
        TrimHint = "The nose, the sill skins and the floor: bare aluminium (as delivered), the frame's colour, black, white or your own.",
        // Small stock engines: the Trailrat's buggy sound fits an air-cooled four best. Not listened to side by side yet: the
        // list is easy to change.
        EngineSounds = new[]
        {
            new EngineSound("Trailrat", "Trailrat (buggy)"),
            new EngineSound("Lynx", "Lynx (small four)"),
            new EngineSound("Tinny", "Tinny (small four)"),
            new EngineSound("DingDing", "Ding (small four)"),
        },
        DefaultEngine = 0,
        EngineHint = "Which stock car's engine your Jackal sounds like (the Trailrat's buggy engine fits it best). Other players with this mod hear your pick.",
        // The garage's Liveries tab, after "Paint" (ids are part of the network layout: only append).
        Liveries = new[]
        {
            new LiveryPreset("gunther", "Crazy Günther", "The mechanic's own rail: mustard frame, cream panels, wavy stripes, a daisy on the nose and his saying down the sides."),
            new LiveryPreset("shabah", "Shabah el Raml", "The Tassili camp's runabout: a sand frame, satin black panels with a sand block on the sills and nose, a camp number."),
            new LiveryPreset("porte", "Porte du Désert", "A privateer's desert racer: white with a red cage top, red and blue stripes, race number 16 and the garage's sponsors."),
        },
    };

    /// <summary>The career dealer's listing (Api.Career.Dealer): a base price on the career's table, sold on its money scale.</summary>
    public const long DealerPrice = 8500;

    public static OverTheHillPlus.Api.CarOffer DealerOffer() => new()
    {
        Car = CarName, Name = DisplayName, Category = "car", Price = DealerPrice, OnMoneyScale = true,
        Description = "A two-seat sand rail with a flat four behind the axle. Light, rear-wheel drive and at home on sand and snow.",
    };
}
