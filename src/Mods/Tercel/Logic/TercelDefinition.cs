using OverTheHillPlus.Cars;

namespace OverTheHillPlus.Tercel;

/// <summary>
/// The Terrier 4WD Wagon (mod id "tercel"): a small early-80s four-wheel-drive wagon for core's car framework, modelled
/// in the game's own low-poly style on the 1982-88 Tercel 4WD wagon (no maker names or badges anywhere in the UI or
/// the model). Its identity values are part of the network protocol and of the save sidecar
/// (oth.tercel.&lt;profile&gt;.sidecar): they never change once released. Pure (unit-tested).
/// </summary>
public static class TercelDefinition
{
    public const string Id = "tercel";
    public const string DisplayName = "Terrier 4WD Wagon";
    /// <summary>Hand-picked next to the Vera's 0xB7E2A001, checked against the live catalog at startup.</summary>
    public const uint ItemId = 0xB7E2A002;
    /// <summary>CarInfo.name and the livery car name ("car": "Terrier" in a skin's manifest).</summary>
    public const string CarName = "Terrier";
    public const string Guid = "f18e62f924614c6a87327e9160d6f710";
    public const int Protocol = 1;

    public static CarDefinition Create() => new()
    {
        Id = Id,
        DisplayName = DisplayName,
        Description = "A small early-80s wagon with part-time four-wheel drive and a 1.5-litre four: a tall, glassy cabin, " +
                      "a raised roof over the load bay, thick black bumpers and plain steel wheels. Light, narrow and happiest " +
                      "on gravel.",
        PageNote = "A small early-80s 4WD wagon: light, narrow and modest, made for gravel and forest roads. " +
                   "Buy it in the garage, or try it from here. Everyone on a server needs this mod to see it.",
        ItemId = ItemId,
        CarName = CarName,
        FusionGuid = Guid,
        RegistryKey = "tercel",
        Protocol = Protocol,
        // The Brick: the closest small stock car (2.36 m wheelbase, 1.36 m track); its skeleton, garage data and
        // cosmetic slots (Liveries/tercel/README.md).
        DefaultDonor = "Car_Brick",
        DefaultGarageBase = "Brick",
        AssetName = "tercel",
        // Lighter, softer and more modest than the Brick, on narrow 13" tyres (radius 0.31 m against the Brick's
        // 0.40 m: CarWheelFit fits the wheel colliders to it). Smaller wheels alone make the gearing about 23 % shorter,
        // so the gears are made taller to keep a sensible top speed (about 7 % under the Brick's); torque at the wheels
        // ends up about 15 % under the Brick's, on 15 % less weight.
        Tuning = new CarTuningProfile
        {
            Mass = 0.85f, CentreOfMassDrop = 0.03f, Torque = 0.78f, RevRange = 1.06f, GearRatios = 0.9f, FinalDrive = 0.92f,
            SuspensionTravel = 0.92f, SpringRate = 0.85f, DamperRate = 0.85f, TyreGrip = 0.92f, SteeringLock = 1.04f,
        },
        // Period colours of the early-80s wagons.
        Paints = new[]
        {
            new PaintPreset("Light Green Metallic", new CarRgb(140, 168, 138)),
            new PaintPreset("Light Blue Metallic", new CarRgb(132, 160, 190)),
            new PaintPreset("Beige", new CarRgb(206, 190, 152)),
            new PaintPreset("White", new CarRgb(236, 236, 230)),
            new PaintPreset("Red", new CarRgb(158, 28, 30)),
            new PaintPreset("Silver", new CarRgb(184, 188, 190)),
            new PaintPreset("Dark Brown", new CarRgb(82, 56, 40)),
        },
        PaintCustomDefault = "#8CA88A",
        // Zones G is the lower tailgate panel (darker on the real cars).
        Trims = new[]
        {
            new TrimPreset("Black", new CarRgb(26, 26, 28)),
            TrimPreset.Body(),
            new TrimPreset("Grey", new CarRgb(100, 100, 104)),
        },
        TrimCustomDefault = "#3A3A3C",
        TrimWhat = "Lower tailgate panel",
        TrimHint = "The lower tailgate panel: black (as on most of the real wagons), the body colour, grey or your own.",
        // The small-engine stock sounds, from the game's car descriptions: the Brick (the donor: the updated Ding, a small
        // boxy car from the same family of small Japanese-style vans and wagons), the Ding (the small original it grew
        // from), the Polyana (a small 4x4 with a 1.6-litre-class four) and the Lynx (a small 4x4 put together from
        // other cars' parts). Not listened to side by side yet: the list is easy to change.
        EngineSounds = new[]
        {
            new EngineSound("Brick", "Brick (small four)"),
            new EngineSound("DingDing", "Ding (small four)"),
            new EngineSound("Polyana", "Polyana (four)"),
            new EngineSound("Lynx", "Lynx (four)"),
        },
        DefaultEngine = 0,
        EngineHint = "Which stock car's engine your Terrier sounds like (the small cars' fours fit a 1.5-litre wagon). " +
                     "Other players with this mod hear your pick.",
    };
}
