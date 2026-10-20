using OverTheHillPlus.Cars;

namespace OverTheHillPlus.Gemse;

/// <summary>
/// The Gemse 6x6 (mod id "gemse"): a 1970s Alpine air-cooled high-mobility six-wheeler with a hardtop body for core's
/// car framework, built from scratch at real size (a made-up maker: no maker names, badges or lettering anywhere in the
/// UI or the model). It is an Expedition class car on the Marshal. The two rear axles are one physical axle at the
/// tandem's centre drawn twice (core's CarTandem, like the Longhorn). Its identity values are part of the network
/// protocol and of the save sidecar (oth.gemse.&lt;profile&gt;.sidecar): they never change once released. Pure (unit-tested).
/// </summary>
public static class GemseDefinition
{
    public const string Id = "gemse";
    public const string DisplayName = "Gemse 6x6";
    /// <summary>Assigned for this car (0xB7E2A00E to 0xB7E2A012 went to the other round-6 cars), checked against the live
    /// catalog at startup.</summary>
    public const uint ItemId = 0xB7E2A013;
    /// <summary>CarInfo.name and the livery car name ("car": "Gemse" in a skin's manifest; ASCII only).</summary>
    public const string CarName = "Gemse";
    public const string Guid = "bd987891ab964d219c2209f8aa17e39c";
    public const int Protocol = 1;

    /// <summary>Single items that can't sit right on this body: the camping lantern and the pan hang from a roof rack's
    /// overhang the hardtop doesn't have.</summary>
    public static readonly string[] Hidden =
    {
        "Visual/Rear/CampingLantern", "Visual/Rear/Pan",
    };

    public static CarDefinition Create() => new()
    {
        Id = Id,
        DisplayName = DisplayName,
        Description = "A 1970s Alpine six-wheeler with a hardtop: a short sloped nose, flat panels, a flat one-piece windscreen and " +
                      "round lamps, an air-cooled petrol four between the front seats, swing axles with portal hubs and six " +
                      "7.50R16 tyres. Slow on the road and not strong, but it climbs, wades and crawls almost anywhere.",
        PageNote = "An Expedition-class 6x6: light for its size, low-geared and slow, with all six wheels driven and long " +
                   "suspension travel. Buy it in the garage, or try it from here. Everyone on a server needs this mod to see it.",
        ItemId = ItemId,
        CarName = CarName,
        FusionGuid = Guid,
        RegistryKey = "gemse",
        Protocol = Protocol,
        // The Marshal, the Expedition class van: its 2.66 m wheelbase and 1.59 m track are the closest stock match to the
        // model's 2.49 m physics wheelbase (front axle to the tandem's centre) and real 1.44 m track; the Mammoth has a
        // 2.53 m wheelbase on a 1.80 m track. Its skeleton, garage data (the Expedition class, upgrades) and cosmetic
        // slots. The framework moves the wheel colliders to the model's anchors and gives them its 0.4025 m tyre radius
        // (7.50R16), so the car sits about 12 cm lower than the Marshal on its 0.53 m tyres.
        DefaultDonor = "Car_Marshal",
        DefaultGarageBase = "Marshal",
        AssetName = "gemse",
        // the built-in steps under the doors replace the game's side steps (and the side Christmas lights in that slot)
        HiddenSlots = new[] { "Visual/Side_Step" },
        HiddenItems = Hidden,
        // Gears and diffs: a 5-speed with a low range, open axle differentials (the real ones lock hydraulically: the gear table's diff
        // lock), no centre differential (locked).
        GearSet = "truck5",
        Diffs = new byte[] { 1, 1, 3 },
        // The real 6x6's numbers on the Marshal's skeleton (SPECS method, RealisticTuningTests): 2.4 t (Mass 0.75 of 3200
        // kg); 65 kW real is 27 kW/t, a third of that is under the 12 kW/t floor, so 31 kW (Power 0.55 of the Marshal's
        // 57 kW cap: 13 kW/t, the weakest Expedition car per tonne but far lighter); 186 Nm (Torque 0.93 of 200);
        // 100 km/h real top speed as a 6.1 m/s limiter (TopSpeed 1.11 of 5.5); a 4000 rpm petrol (RevRange 0.9);
        // GearRatios for the 0.4025 m tyres: 0.85 x 4050 rpm x 0.4025 m x 0.10472 / (6.1 m/s x 3.5 x 8) = 0.85, so the
        // limiter comes at 85 % of the redline in top gear. Long travel on swing axles (x1.15), springs and dampers
        // scaled to the weight (damping ratio about 0.64, the Marshal's 0.62), a lower centre of mass than the tall van
        // it replaces. Tows 5 t on the road (1.8 t off it).
        Tuning = new CarTuningProfile
        {
            Mass = 0.75f, CentreOfMassDrop = 0.06f, Torque = 0.93f, Power = 0.55f, TopSpeed = 1.11f, RevRange = 0.9f, GearRatios = 0.85f, FinalDrive = 1.0f,
            SuspensionTravel = 1.15f, SpringRate = 0.80f, DamperRate = 0.80f, TyreGrip = 1.0f, SteeringLock = 1.0f, TowKg = 5000f,
        },
        // Army olive, field green, sand, fire-brigade red, white, slate blue, ochre and black.
        Paints = new[]
        {
            new PaintPreset("Olive", new CarRgb(80, 82, 58)),
            new PaintPreset("Field Green", new CarRgb(62, 74, 52)),
            new PaintPreset("Sand", new CarRgb(196, 178, 136)),
            new PaintPreset("Fire Red", new CarRgb(160, 26, 24)),
            new PaintPreset("White", new CarRgb(232, 232, 226)),
            new PaintPreset("Slate Blue", new CarRgb(70, 88, 110)),
            new PaintPreset("Ochre", new CarRgb(184, 140, 52)),
            new PaintPreset("Black", new CarRgb(26, 26, 28)),
        },
        PaintCustomDefault = "#50523A",
        // Zone G is the roof: a two-tone hardtop (white over red for a fire or rescue truck), or the body colour.
        Trims = new[]
        {
            TrimPreset.Body(),
            new TrimPreset("White", new CarRgb(232, 232, 226)),
            new TrimPreset("Sand", new CarRgb(196, 178, 136)),
            new TrimPreset("Black", new CarRgb(26, 26, 28)),
            new TrimPreset("Grey", new CarRgb(132, 134, 130)),
        },
        TrimCustomDefault = "#E8E8E2",
        TrimWhat = "Roof",
        TrimHint = "The hardtop's roof above the drip rails: the body colour (as delivered), white for a rescue or fire " +
                   "truck, sand, black, grey or your own.",
        // Stock engine sounds: the Protector's utility four is the nearest to the air-cooled petrol four (the default), then
        // the DingDing's old four, the Marshal's van, the Mammoth's truck diesel and the Brick. Chosen by type, not
        // listened to side by side: the list is easy to change.
        EngineSounds = new[]
        {
            new EngineSound("Protector", "Protector (utility four)"),
            new EngineSound("DingDing", "DingDing (old four)"),
            new EngineSound("Marshal", "Marshal (van)"),
            new EngineSound("Mammoth", "Mammoth (truck diesel)"),
            new EngineSound("Brick", "Brick (4x4)"),
        },
        DefaultEngine = 0,
        EngineHint = "Which stock car's engine your Gemse sounds like (the Protector's four fits it best). " +
                     "Other players with this mod hear your pick.",
        // The garage's Liveries tab, after "Paint" (ids are part of the network layout: only append).
        Liveries = new[]
        {
            new LiveryPreset("surplus_stencils", "Surplus Stencils", "Ex-army stencils over your own paint: the vehicle number, tyre pressures, the load and a bridge disc."),
            new LiveryPreset("tassili_expedition", "Tassili Expedition", "A desert outfitter's truck that runs the Tassili n'Ajjer camps: warm sand, a white roof, brown lines."),
            new LiveryPreset("watchtower_fire", "Watchtower Fire Crew", "The wildfire crew truck from Harrison Watchtower above Emerald Lake: fire red under a white roof."),
        },
    };
}
