using OverTheHillPlus.Cars;

namespace OverTheHillPlus.Meerkat;

/// <summary>
/// The Meerkat (mod id "meerkat"): a small two-seat roadster with pop-up headlamps, lifted and kitted out as a safari
/// rally privateer car, for core's car framework. Built at the real size of the 1989-97 roadsters of its kind (2.265 m
/// wheelbase, 3.975 m long, 1.675 m wide), 3 in of coilover lift on 205/75 R15 all-terrains, rear-wheel drive and a 1.8
/// twin-cam four (a made-up brand: no maker names, badges or lettering anywhere in the UI or the model). Its identity
/// values are part of the network protocol and of the save sidecar (oth.meerkat.&lt;profile&gt;.sidecar): they never
/// change once released. Pure (unit-tested).
/// </summary>
public static class MeerkatDefinition
{
    public const string Id = "meerkat";
    public const string DisplayName = "Meerkat";
    /// <summary>The next free id after the Grayling's 0xB7E2A016 (the last modded car), checked against the live catalog at
    /// startup.</summary>
    public const uint ItemId = 0xB7E2A017;
    /// <summary>CarInfo.name and the livery car name ("car": "Meerkat" in a skin's manifest; ASCII only).</summary>
    public const string CarName = "Meerkat";
    public const string Guid = "a68ae889725743388563daa93cdf3fb3";
    public const int Protocol = 2;

    /// <summary>The slots a small roadster has a place for: the bull bar and plates on the nose, the bumpers, the hood, the
    /// antenna, the winch in the mouth, the rear gear on the trunk lid. No roof slots (the top comes off, and the car has
    /// its own rack), no side steps or side gear (a low sill and doors), no mud flaps and no snorkel (it has its own).</summary>
    public static readonly (string Anchor, string Slot)[] SlotAnchors =
    {
        ("cosm_Front", "Visual/Front"),
        ("cosm_Rear", "Visual/Rear"),
        ("cosm_Front_Bumper", "Visual/Front_Bumper"),
        ("cosm_Rear_Bumper", "Visual/Rear_Bumper"),
        ("cosm_Hood", "Visual/Hood"),
        ("cosm_Antenna", "Visual/Antenna"),
        ("cosm_Winch", "Performance/Winch"),
    };

    public static CarDefinition Create() => new()
    {
        Id = Id,
        DisplayName = DisplayName,
        Description = "A little two-seat roadster with pop-up headlamps, lifted 3 inches and fitted out for the rough stuff: " +
                      "all-terrain tyres, a hardtop with a roof rack and driving lamps, a skid plate, flares and mud flaps. " +
                      "Light, rear-wheel drive and quick on its feet.",
        PageNote = "A small two-seat roadster, lifted on long-travel coilovers and 205/75 all-terrains: about 1,000 kg, a 1.8 " +
                   "twin-cam, rear-wheel drive. Buy it in the garage, or try it from here. Everyone on a server needs this mod to see it.",
        ItemId = ItemId,
        CarName = CarName,
        FusionGuid = Guid,
        RegistryKey = "meerkat",
        Protocol = Protocol,
        // The Brick: the lightest stock donor (1.6 t, a short wheelbase), as for the Terrier wagon; its skeleton, garage data
        // and cosmetic slots.
        DefaultDonor = "Car_Brick",
        DefaultGarageBase = "Brick",
        AssetName = "meerkat",
        // A five-speed roadster's gearbox; open differentials (the front is never driven: the car is rear-wheel drive).
        GearSet = "close5",
        Diffs = new byte[] { 1, 1, 1 },
        // The real car's numbers on the Brick's skeleton: about 1,040 kg lifted (x0.65 of 1,600 kg), 18.7 kW (x0.375 of the
        // 50 kW cap: 18 kW/t, as strong as the strongest stock trail car, about 0.2 of the real 92 kW/t), a 170 km/h real
        // top speed on the lift and the big tyres as an 8.85 m/s limiter (x1.18), a revvy twin-cam (revs x1.25), gearing so
        // the limiter comes at 85 % of the revs on the 0.344 m tyres, long-travel coilovers (travel x1.30, soft springs,
        // dampers for a body damping ratio inside the donors'), a quick, light rack (lock x1.10), the weight a little
        // lower. Tows 500 kg.
        Tuning = new CarTuningProfile
        {
            Mass = 0.65f, CentreOfMassDrop = 0.04f, Torque = 0.72f, Power = 0.375f, TopSpeed = 1.18f, RevRange = 1.25f, GearRatios = 1.08f, FinalDrive = 1.0f,
            SuspensionTravel = 1.30f, SpringRate = 0.66f, DamperRate = 0.62f, TyreGrip = 1.0f, SteeringLock = 1.10f, TowKg = 500f,
        },
        // The hull stops at the windscreen's header and the tops reach only 3 to 5 cm over it, but the roof rack stands 20 cm
        // over the hardtop and the lamp bar 27 cm over the header: a box each, there only while it's fitted
        // (MeerkatBuild.BoxParts, in this order; MeerkatLive switches them).
        BodyBoxes = new[]
        {
            new CarBodyBox(new MeshVec3(0f, 1.389f, -0.2575f), new MeshVec3(1.05f, 0.266f, 0.455f)),
            new CarBodyBox(new MeshVec3(0f, 1.38f, -0.02f), new MeshVec3(1.08f, 0.349f, 0.15f)),
        },
        Cosmetics = CarCosmetics.Build(SlotAnchors, CarCosmetics.ItemAnchors),
        HiddenSlots = new[] { "Visual/Fender", "Performance/Snorkel", "Visual/Roof", "Visual/Roof_Bar", "Visual/Side_Step", "Visual/Side" },
        // The Brick's tall rear gear hangs off a tailgate the roadster hasn't got (it floats over the trunk lid at any
        // height; its spare wheel sinks into the lid, and the car has its own spare carrier), its coiled ropes and
        // Christmas strings are cut for a square bumper: left out.
        HiddenItems = new[]
        {
            "Visual/Rear/Bike", "Visual/Rear/CampingLantern", "Visual/Rear/Ladder", "Visual/Rear/Pan", "Visual/Rear/Guitar",
            "Visual/Rear/MountedGrill", "Visual/Rear/SpareWheel", "Visual/Rear_Bumper/Ropes_Rear",
            "Visual/Front_Bumper/Ropes_Front",
            "Visual/Front_Bumper/Christmas_Front", "Visual/Rear_Bumper/Christmas_Rear",
        },
        // The period roadster colours and a safari sand.
        Paints = new[]
        {
            new PaintPreset("Classic Red", new CarRgb(172, 22, 24)),
            new PaintPreset("Crystal White", new CarRgb(236, 236, 230)),
            new PaintPreset("Mariner Blue", new CarRgb(30, 54, 112)),
            new PaintPreset("Racing Green", new CarRgb(20, 58, 40)),
            new PaintPreset("Sunburst Yellow", new CarRgb(236, 188, 34)),
            new PaintPreset("Silver Stone", new CarRgb(176, 180, 184)),
            new PaintPreset("Brilliant Black", new CarRgb(20, 20, 22)),
            new PaintPreset("Laguna Blue", new CarRgb(44, 96, 156)),
            new PaintPreset("Safari Sand", new CarRgb(198, 178, 132)),
        },
        PaintCustomDefault = "#C6B284",
        // Zone G is the windscreen's frame and the A pillars' feet (black on the real cars).
        Trims = new[]
        {
            new TrimPreset("Black", new CarRgb(26, 26, 28)),
            TrimPreset.Body(),
            new TrimPreset("Gunmetal", new CarRgb(70, 73, 77)),
        },
        TrimCustomDefault = "#1A1A1C",
        TrimWhat = "Windscreen frame",
        TrimHint = "The windscreen's frame: black (as delivered), the body colour, gunmetal or your own.",
        // Small-engine stock sounds: the Lynx (a small 4x4 put together from other cars' parts), the Brick (the donor's small
        // four), the Ding and the Polyana. Not listened to side by side yet: the list is easy to change.
        EngineSounds = new[]
        {
            new EngineSound("Lynx", "Lynx (small four)"),
            new EngineSound("Brick", "Brick (small four)"),
            new EngineSound("DingDing", "Ding (small four)"),
            new EngineSound("Polyana", "Polyana (four)"),
        },
        DefaultEngine = 0,
        EngineHint = "Which stock car's engine your Meerkat sounds like (the Lynx's small four fits a twin-cam best). " +
                     "Other players with this mod hear your pick.",
        // The garage's Liveries tab, after "Paint" (ids are part of the network layout: only append).
        Liveries = new[]
        {
            new LiveryPreset("tassili", "Tassili", "A privateer's car from the Tassili rally: white over desert sand, a black hood panel, number 27 on the doors."),
        },
    };

    /// <summary>The career dealer's listing (Api.Career.Dealer): a base price on the career's table, sold on its money scale.</summary>
    public const long DealerPrice = 11000;

    public static OverTheHillPlus.Api.CarOffer DealerOffer() => new()
    {
        Car = CarName, Name = DisplayName, Category = "car", Price = DealerPrice, OnMoneyScale = true,
        Description = "A small lifted roadster on all-terrains with a hardtop. Light, rear-wheel drive, quick on any track.",
    };
}
