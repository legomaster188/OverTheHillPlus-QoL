using OverTheHillPlus.Cars;

namespace OverTheHillPlus.Longhorn;

/// <summary>
/// The Longhorn 389 (mod id "longhorn"): an American long-nose conventional tractor unit (a day cab, no trailer) for
/// core's car framework, built from scratch (no source model, a made-up brand: no maker names, badges or lettering
/// anywhere in the UI or the model). Its identity values are part of the network protocol and of the save sidecar
/// (oth.longhorn.&lt;profile&gt;.sidecar): they never change once released. Pure (unit-tested).
/// </summary>
public static class LonghornDefinition
{
    public const string Id = "longhorn";
    public const string DisplayName = "Longhorn 389";
    /// <summary>The third free id after the Hilo 22's 0xB7E2A006 and the ones reserved up to 0xB7E2A00B; checked against the live catalog at startup.</summary>
    public const uint ItemId = 0xB7E2A00C;
    /// <summary>CarInfo.name and the livery car name ("car": "Longhorn" in a skin's manifest; ASCII only).</summary>
    public const string CarName = "Longhorn";
    public const string Guid = "ba0d4ba8025842499b88ab37d9a794f3";
    public const int Protocol = 4;

    /// <summary>The slots that make sense on a tractor: plates and shackles on the bumper, hood ornaments, lights on the
    /// cab roof, the antenna, and the rear slot (whip-light poles in brackets on the cab's back, the rest of its gear on
    /// the deck plate or hung on the back wall). Bull bars, the bed, the rear bumper, side steps, the snorkel, the winch
    /// and the mudflaps are not offered (the truck has its own steps, tanks, mudflaps and fifth wheel).</summary>
    public static readonly (string Anchor, string Slot)[] SlotAnchors =
    {
        ("cosm_Front_Bumper", "Visual/Front_Bumper"),
        ("cosm_Hood", "Visual/Hood"),
        ("cosm_Roof_Bar", "Visual/Roof_Bar"),
        ("cosm_Antenna", "Visual/Antenna"),
        ("cosm_Rear", "Visual/Rear"),
    };

    /// <summary>Items placed one by one (the model's cosm_i_&lt;item&gt; anchors, Liveries/tools/semi/build_semi.py
    /// COSM_ITEMS): a donor slot made for a short pickup can't hold them all where they belong on a 1.9 m hood and a cab
    /// back, so the hood ornaments spread along the hood and each rear item gets its own spot.</summary>
    public static readonly string[] PlacedItems =
    {
        "Visual/Hood/CattleSkull", "Visual/Hood/RamSkull", "Visual/Hood/BullHorns", "Visual/Hood/Wreath_Front",
        "Visual/Hood/Sparewheel", "Visual/Hood/Bushcables",
        "Visual/Rear/Bike", "Visual/Rear/MountedGrill", "Visual/Rear/Ladder", "Visual/Rear/Guitar", "Visual/Rear/CampingLantern",
        "Visual/Rear/Pan", "Visual/Rear/WhipLights_01", "Visual/Rear/WhipLights_02", "Visual/Rear/WhipLights_03",
        "Visual/Rear/WhipLights_04", "Visual/Rear/WhipLights_05", "Visual/Rear/WhipLights_06", "Visual/Rear/WhipLights_07",
    };

    /// <summary>The model's anchor for a placed item: "cosm_i_" + the item's last path part.</summary>
    public static string ItemAnchorName(string item) => "cosm_i_" + item.Substring(item.LastIndexOf('/') + 1);

    public static CosmeticItemAnchor[] ItemAnchors() =>
        System.Array.ConvertAll(PlacedItems, i => new CosmeticItemAnchor(ItemAnchorName(i), i));

    public static CarDefinition Create() => new()
    {
        Id = Id,
        DisplayName = DisplayName,
        Description = "An American long-nose tractor unit: a long squared hood with a tall chrome grille, a day cab, tall dual " +
                      "stacks, steps and fuel tanks under the doors, a fifth wheel and a tandem drive axle on dual wheels. Very " +
                      "heavy, huge torque, a low top speed but steady.",
        PageNote = "A day-cab tractor (no trailer): by far the heaviest car, with huge torque and long gearing, so it pulls " +
                   "like nothing else but tops out low. Buy it in the garage, or try it from here. Everyone on a server needs " +
                   "this mod to see it.",
        ItemId = ItemId,
        CarName = CarName,
        FusionGuid = Guid,
        RegistryKey = "longhorn",
        Protocol = Protocol,
        // The Blast Wagon, the stock work truck with the longest wheelbase (3.31 m against the Longhorn's real 4.70 m between
        // its steer axle and the middle of the tandem) and 0.48 m tyres (the Longhorn's 11R22.5 are 0.525 m): its skeleton,
        // garage data (vehicle class, upgrades) and cosmetic slots (Liveries/longhorn/README.md). The Mammoth has a 2.53 m
        // wheelbase.
        DefaultDonor = "Car_BlastWagon",
        DefaultGarageBase = "BlastWagon",
        AssetName = "longhorn",
        Cosmetics = CarCosmetics.Build(SlotAnchors, ItemAnchors()),
        // The model's collider part covers the hood and cab only (z 0 to 3.41): the frame rails, deck plate and fifth
        // wheel behind the cab get a box (Liveries/tools/semi/semi_const.py: rails 0.80 to 1.07 m high either side of
        // x 0.43, the fifth wheel's top plate at 1.22 m, the frame's end 0.8 m behind the rear axle at z -3.01).
        BodyBoxes = new[] { new CarBodyBox(new MeshVec3(0f, 1.01f, -1.90f), new MeshVec3(1.10f, 0.42f, 3.82f)) },
        // The real tractor's numbers on the Blast Wagon's skeleton: 8.6 t (mass x3.07 of 2800 kg) and 144 kW (Power 2.4 on the
        // donor's 60 kW cap: about 17 kW/t), a governed 105 km/h real top speed as a 6.3 m/s limiter (TopSpeed 1.05), a
        // slow-revving diesel (revs x0.75), gearing so the limiter comes at 85 % of the revs on the 0.525 m tyres, springs and
        // dampers scaled to the weight (x3.0 keeps the Blast Wagon's static sag), a small steering lock (x0.9: the real 4.70 m
        // wheelbase keeps about the old turning circle). Unrated for towing: a semi tows anything. Gears and diffs: five of the
        // Eaton's gears, open axles and an inter-axle differential (limited slip).
        GearSet = "heavy5",
        Diffs = new byte[] { 1, 1, 2 },
        Tuning = new CarTuningProfile
        {
            Mass = 3.07f, CentreOfMassDrop = 0.10f, Torque = 3.5f, Power = 2.4f, TopSpeed = 1.05f, RevRange = 0.75f, GearRatios = 0.91f, FinalDrive = 1.0f,
            SuspensionTravel = 0.9f, SpringRate = 3.0f, DamperRate = 3.0f, TyreGrip = 1.0f, SteeringLock = 0.9f,
        },
        // Classic truck colours: black, white, candy red, royal blue, forest green, school-bus yellow, burnt orange, silver.
        Paints = new[]
        {
            new PaintPreset("Gloss Black", new CarRgb(20, 20, 22)),
            new PaintPreset("Arctic White", new CarRgb(236, 236, 232)),
            new PaintPreset("Candy Red", new CarRgb(140, 14, 24)),
            new PaintPreset("Royal Blue", new CarRgb(26, 50, 124)),
            new PaintPreset("Forest Green", new CarRgb(24, 70, 44)),
            new PaintPreset("School Bus Yellow", new CarRgb(240, 176, 0)),
            new PaintPreset("Burnt Orange", new CarRgb(184, 78, 20)),
            new PaintPreset("Silver", new CarRgb(166, 170, 176)),
        },
        PaintCustomDefault = "#8C0E18",
        // Zone G is the two-tone: the fenders and the cab's lower panels.
        Trims = new[]
        {
            TrimPreset.Body(),
            new TrimPreset("Black", new CarRgb(24, 24, 27)),
            new TrimPreset("Chrome", new CarRgb(200, 203, 208)),
            new TrimPreset("Cream", new CarRgb(228, 218, 186)),
            new TrimPreset("Charcoal", new CarRgb(66, 68, 72)),
            new TrimPreset("Gold", new CarRgb(178, 138, 38)),
        },
        TrimCustomDefault = "#18181B",
        TrimWhat = "Two-tone (fenders and lower cab)",
        TrimHint = "The second colour on the fenders and the cab's lower panels: the body colour (one colour all over, as " +
                   "delivered), black or chrome, a classic two-tone or your own.",
        // Stock engine sounds of the game's heaviest cars, heaviest first: the Mammoth (the Expedition-class truck diesel,
        // the default, set through CarLooks.ApplyEngine), the Marshal (the other Expedition car), the Blast Wagon and the
        // Timberbox (Workhorse). Not listened to side by side yet: the list is easy to change.
        EngineSounds = new[]
        {
            new EngineSound("Mammoth", "Mammoth (truck diesel)"),
            new EngineSound("Marshal", "Marshal"),
            new EngineSound("BlastWagon", "Blast Wagon (work truck)"),
            new EngineSound("Timberbox", "Timberbox"),
        },
        DefaultEngine = 0,
        EngineHint = "Which stock car's engine your Longhorn sounds like (the Mammoth's truck diesel fits it best). " +
                     "Other players with this mod hear your pick.",
        // The garage's Liveries tab, after "Paint" (ids are part of the network layout: only append).
        Liveries = new[]
        {
            new LiveryPreset("cream_spear", "Cream Spear", "An owner-operator's cream spear with a gold pinstripe, laid over your own paint."),
            new LiveryPreset("grand_erg", "Grand Erg Heavy Haul", "A desert heavy-haul fleet's tractor, fleet white over red with the name on the hood."),
            new LiveryPreset("hamra_quarry", "Hamra Quarry", "A quarry truck from the Hamra Plateau: sun-faded red, with a white hood off another truck."),
        },
    };
}
