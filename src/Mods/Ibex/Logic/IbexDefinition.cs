using System.Linq;
using OverTheHillPlus.Cars;

namespace OverTheHillPlus.Ibex;

/// <summary>
/// The Ibex (mod id "ibex"): a 1970s-80s Soviet-style 4x4 utility jeep with a canvas top for core's car framework, in
/// the game's Trail class, built to the real car's size: a flat-sided steel tub with four doors on exposed hinges, door
/// frames with sliding glass, a flat windscreen, a canvas roof and rear curtains, a flat front panel with two mesh
/// grilles and round headlamps in pods, the spare on the rear door (a made-up brand, "Sever": no maker names, badges or
/// lettering anywhere in the UI or the model). Its identity values are part of the network protocol and of the save
/// sidecar (oth.ibex.&lt;profile&gt;.sidecar): they never change once released. Pure (unit-tested).
/// </summary>
public static class IbexDefinition
{
    public const string Id = "ibex";
    public const string DisplayName = "Ibex";
    /// <summary>Hand-picked after the Mule's 0xB7E2A00D, checked against the live catalog at startup.</summary>
    public const uint ItemId = 0xB7E2A00E;
    /// <summary>CarInfo.name and the livery car name ("car": "Ibex" in a skin's manifest; ASCII only).</summary>
    public const string CarName = "Ibex";
    public const string Guid = "c5cab20d0d4b4ad6aa8a8b96aa18b531";
    /// <summary>2: the body box over the spare on the rear door.</summary>
    public const int Protocol = 2;

    /// <summary>Items that mount somewhere else on this body than their slot puts them: the shackles on the faces of
    /// the split rear bumper, the pan hung on the canvas back beside its window, the tow bar bolted to the rear crossmember
    /// (the slot itself sits on the bumperettes, 10 cm further forward).</summary>
    public static readonly CosmeticItemAnchor[] OwnItemAnchors =
    {
        new("cosm_i_Shackles_Rear", "Visual/Rear_Bumper/Shackles_Rear"),
        new("cosm_Pan", "Visual/Rear/Pan"),
        new("cosm_i_TowBar", "Visual/Rear_Bumper/TowBar"),
    };

    /// <summary>The Brick's rear-door items have no room on this one: it carries its own spare there (a second spare,
    /// the bike rack, the grill, the guitar and the ladder would sit in it), and the lantern's bracket would cut
    /// through the canvas.</summary>
    public static readonly string[] OwnHiddenItems =
    {
        "Visual/Rear/SpareWheel", "Visual/Rear/Bike", "Visual/Rear/MountedGrill", "Visual/Rear/Guitar", "Visual/Rear/Ladder",
        "Visual/Rear/CampingLantern",
    };

    public static CarDefinition Create() => new()
    {
        Id = Id,
        DisplayName = DisplayName,
        Description = "A 1970s utility 4x4 with a canvas top: a flat steel body with four doors, a mesh grille, " +
                      "round headlamps in pods and the spare on the back door. Tall, narrow, slow and hard to stop off the road.",
        PageNote = "A canvas-topped four-door 4x4 in the Trail class: high clearance on portal axles, a 4-speed with low " +
                   "range, an old petrol four. Buy it in the garage, or try it from here. Everyone on a server needs this mod to see it.",
        ItemId = ItemId,
        CarName = CarName,
        FusionGuid = Guid,
        RegistryKey = "ibex",
        Protocol = Protocol,
        // The Brick: a Trail-class 4x4 of the same size (2.36 m wheelbase against the Ibex's 2.38 m, 3.94 m long against
        // 4.03 m, 0.40 m tyres against 0.384 m): its skeleton, garage data (vehicle class, upgrades) and cosmetic slots.
        DefaultDonor = "Car_Brick",
        DefaultGarageBase = "Brick",
        AssetName = "ibex",
        // The Brick's slot table with the Ibex's own item anchors; the rear-door items that can't fit are hidden.
        ItemAnchors = OwnItemAnchors,
        HiddenItems = OwnHiddenItems,
        // The collider part (the tub's hull) stops at the rear bumper: the spare on the rear door (a 0.77 m wheel, 0.22 m
        // deep, centred 0.20 m left at 1.03 m high, Liveries/tools/ibex/ibex_const.py SPARE) gets a box of its own.
        BodyBoxes = new[] { new CarBodyBox(new MeshVec3(-0.20f, 1.03f, -2.00f), new MeshVec3(0.77f, 0.77f, 0.22f)) },
        // Gears and diffs: the real car's 4-speed with a 2-range transfer case, open axles, part-time 4x4 (locked centre).
        GearSet = "truck4",
        Diffs = new byte[] { 1, 1, 3 },
        // The real car on the Brick's skeleton: 1.65 t with fuel (x1.03 of 1.6 t) and 21.5 kW (a 55 kW 2.45-litre petrol
        // four: 13 kW/t, under the Carpati as the real cars are), a 6.7 m/s limiter from a 115 km/h real top speed, short
        // gears so the limiter comes at 85 % of a low rev range on the real 0.384 m 215/90-15 tyres; long soft leaf springs
        // (body damping ratio about 0.52), a high centre of mass, a wide turning circle. Tows 850 kg.
        Tuning = new CarTuningProfile
        {
            Mass = 1.03f, CentreOfMassDrop = -0.03f, Torque = 0.95f, Power = 0.43f, TopSpeed = 0.89f, RevRange = 0.88f, GearRatios = 1.13f, FinalDrive = 1.0f,
            SuspensionTravel = 1.12f, SpringRate = 0.92f, DamperRate = 0.88f, TyreGrip = 1.0f, SteeringLock = 0.92f, TowKg = 850f,
        },
        // The colours these cars wore: army olive, white, sand, light grey, sky blue, signal red, yellow.
        Paints = new[]
        {
            new PaintPreset("Army Olive", new CarRgb(91, 100, 64)),
            new PaintPreset("White", new CarRgb(234, 234, 228)),
            new PaintPreset("Sand Beige", new CarRgb(198, 182, 140)),
            new PaintPreset("Light Grey", new CarRgb(156, 160, 156)),
            new PaintPreset("Sky Blue", new CarRgb(104, 146, 176)),
            new PaintPreset("Signal Red", new CarRgb(168, 30, 26)),
            new PaintPreset("Yellow", new CarRgb(222, 178, 48)),
        },
        PaintCustomDefault = "#5B6440",
        // Zone G is the canvas top (roof and rear curtains). The wheels and hinges are body colour, the bumpers black.
        Trims = new[]
        {
            new TrimPreset("Khaki Canvas", new CarRgb(143, 138, 104)),
            new TrimPreset("Olive Canvas", new CarRgb(88, 92, 66)),
            new TrimPreset("Black Canvas", new CarRgb(36, 36, 34)),
            new TrimPreset("Grey Canvas", new CarRgb(120, 122, 118)),
            new TrimPreset("Sand Canvas", new CarRgb(190, 176, 140)),
            TrimPreset.Body(),
        },
        TrimCustomDefault = "#8F8A68",
        TrimWhat = "Canvas top",
        TrimHint = "The canvas roof and rear curtains: khaki (as delivered), olive, black, grey, sand, the body colour or " +
                   "your own. The wheels are body colour, the bumpers black.",
        // Stock engine sounds of the game's old utility 4x4s: the Protector (a Defender-style four, the closest in kind),
        // the Ding (an old FJ40-style 4x4), the Brick (the donor) and the Trailrat. Not listened to side by side yet.
        EngineSounds = new[]
        {
            new EngineSound("Protector", "Protector (utility four)"),
            new EngineSound("DingDing", "Ding (old 4x4)"),
            new EngineSound("Brick", "Brick (4x4)"),
            new EngineSound("Trailrat", "Trailrat"),
        },
        DefaultEngine = 0,
        EngineHint = "Which stock car's engine your Ibex sounds like (an old utility four fits it best). " +
                     "Other players with this mod hear your pick.",
        // The garage's Liveries tab, after "Paint" (ids are part of the network layout: only append).
        Liveries = new[]
        {
            new LiveryPreset("field_numbers", "Field Numbers", "White stencilled numbers on the doors and the hood, over your own paint."),
            new LiveryPreset("ranger_patrol", "Emerald Lake Ranger Patrol", "The lake rangers' patrol car: forest green, a black top and stencilled doors."),
            new LiveryPreset("gimli_post", "Gimli Ridge Post", "The mail runabout of Gimli Ridge: pale blue with a white band and lettered doors."),
        },
    };
}
