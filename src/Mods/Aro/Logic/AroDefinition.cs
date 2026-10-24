using OverTheHillPlus.Cars;

namespace OverTheHillPlus.Aro;

/// <summary>
/// The Carpați 24 (mod id "aro"): a classic 1970s Romanian five-door 4x4 hardtop for core's car framework, modelled in the
/// game's own low-poly style (no maker names, badges or lettering anywhere in the UI or the model). Its identity values
/// are part of the network protocol and of the save sidecar (oth.aro.&lt;profile&gt;.sidecar): they never change once
/// released. Pure (unit-tested).
/// </summary>
public static class AroDefinition
{
    public const string Id = "aro";
    public const string DisplayName = "Carpați 24";
    /// <summary>Hand-picked next to the Terrier's 0xB7E2A002, checked against the live catalog at startup.</summary>
    public const uint ItemId = 0xB7E2A003;
    /// <summary>CarInfo.name and the livery car name ("car": "Carpati" in a skin's manifest; ASCII only).</summary>
    public const string CarName = "Carpati";
    public const string Guid = "da14696adcd64d3c85476774196b43e3";
    public const int Protocol = 2;

    public static CarDefinition Create() => new()
    {
        Id = Id,
        DisplayName = DisplayName,
        Description = "A tough 1970s mountain 4x4 from the Carpathians: a flat-sided five-door steel box on a ladder frame, with " +
                      "part-time four-wheel drive, low gearing and a slogging four-cylinder. Slow on the road, hard to stop off it.",
        PageNote = "A classic five-door 4x4 hardtop: heavy, low-geared and tall on its springs, made for mud, rocks and " +
                   "forest tracks. Buy it in the garage, or try it from here. Everyone on a server needs this mod to see it.",
        ItemId = ItemId,
        CarName = CarName,
        FusionGuid = Guid,
        RegistryKey = "aro",
        Protocol = Protocol,
        // The Brick: the stock car of the same size (2.36 m wheelbase, 0.40 m tyres, a boxy 4x4 hardtop); its skeleton,
        // garage data and cosmetic slots (Liveries/aro/README.md).
        DefaultDonor = "Car_Brick",
        DefaultGarageBase = "Brick",
        AssetName = "aro",
        // Gears and diffs: the real car's gearbox and differentials (a 4-speed with a 2-range transfer case, part-time 4x4).
        GearSet = "truck4",
        Diffs = new byte[] { 1, 1, 3 },
        // The real 4x4's numbers on the Brick's skeleton and tyres (radius 0.40 m against 0.404 m): 1.45 t and only 24 kW
        // (an old 2.5-litre petrol four, the slowest per tonne of the modded cars bar the trucks), a 105 km/h real top speed
        // as a 6.3 m/s limiter, short gears (the limiter comes at 85 % of a low rev range); longer, slightly softer
        // suspension travel for articulation, the centre of mass a touch higher. Tows 1 t.
        Tuning = new CarTuningProfile
        {
            Mass = 0.91f, CentreOfMassDrop = -0.02f, Torque = 0.93f, Power = 0.48f, TopSpeed = 0.84f, RevRange = 0.9f, GearRatios = 1.27f, FinalDrive = 1.0f,
            SuspensionTravel = 1.15f, SpringRate = 0.9f, DamperRate = 0.9f, TyreGrip = 1.0f, SteeringLock = 0.95f, TowKg = 1000f,
        },
        // Period colours of the 1970s cars: army olive, white, beige, red, blue and the orange of the photographed car.
        Paints = new[]
        {
            new PaintPreset("Army Olive", new CarRgb(86, 94, 60)),
            new PaintPreset("White", new CarRgb(236, 236, 230)),
            new PaintPreset("Sand Beige", new CarRgb(202, 186, 142)),
            new PaintPreset("Red", new CarRgb(160, 30, 28)),
            new PaintPreset("Blue", new CarRgb(38, 70, 122)),
            new PaintPreset("Orange", new CarRgb(222, 98, 22)),
        },
        PaintCustomDefault = "#565E3C",
        // Zones G is the brightwork: bumpers, grille, lamp bezels, handles, hinges, mirror stalks.
        Trims = new[]
        {
            new TrimPreset("Chrome", new CarRgb(196, 199, 203)),
            new TrimPreset("Black", new CarRgb(26, 26, 28)),
            TrimPreset.Body(),
        },
        TrimCustomDefault = "#1A1A1C",
        TrimWhat = "Brightwork (bumpers, grille, bezels)",
        TrimHint = "The bumpers, grille, lamp bezels, handles and hinges: chrome (as delivered), black (as on the army and " +
                   "later cars), the body colour or your own.",
        // Stock engine sounds of the game's utility 4x4s and trucks, from the cars' real counterparts: the Protector (a
        // Defender-style 4x4, the closest in kind: a slogging utility four) is the default, the Brick (the donor, a
        // Land Cruiser 70-style 4x4), the Mammoth (a Unimog-style truck: the gruffest diesel) and the Ding (an old
        // FJ40-style 4x4). Not listened to side by side yet: the list is easy to change.
        EngineSounds = new[]
        {
            new EngineSound("Protector", "Protector (utility four)"),
            new EngineSound("Brick", "Brick (4x4)"),
            new EngineSound("Mammoth", "Mammoth (truck diesel)"),
            new EngineSound("DingDing", "Ding (old 4x4)"),
        },
        DefaultEngine = 0,
        EngineHint = "Which stock car's engine your Carpați sounds like (the utility 4x4s and the truck fit a slogging " +
                     "four best). Other players with this mod hear your pick.",
        // The garage's Liveries tab, after "Paint" (ids are part of the network layout: only append).
        Liveries = new[]
        {
            new LiveryPreset("white_roof", "White Roof", "The white roof the cars came with from the factory, over any body colour."),
            new LiveryPreset("weather_station", "Rock Cradle Weather Station", "The survey truck that drives the crew up to the weather station on Asgard Mountain."),
            new LiveryPreset("ex_forestry", "Old Forestry Green", "A retired forestry truck, sun faded, with the ghost of its old door lettering."),
        },
    };
}
