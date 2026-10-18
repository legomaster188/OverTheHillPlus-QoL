using System.Linq;
using OverTheHillPlus.Cars;

namespace OverTheHillPlus.Hilo;

/// <summary>
/// The Hilo 89 (mod id "hilo"): a late-1980s compact 4x4 pickup for core's car framework, built from a detailed model
/// (no maker names, badges or lettering anywhere in the UI or the model). Its identity values are part of
/// the network protocol and of the save sidecar (oth.hilo.&lt;profile&gt;.sidecar): they never change once released.
/// Pure (unit-tested).
/// </summary>
public static class HiloDefinition
{
    public const string Id = "hilo";
    public const string DisplayName = "Hilo 89";
    /// <summary>Hand-picked next to the Carpati's 0xB7E2A003, checked against the live catalog at startup.</summary>
    public const uint ItemId = 0xB7E2A004;
    /// <summary>CarInfo.name and the livery car name ("car": "Hilo" in a skin's manifest; ASCII only).</summary>
    public const string CarName = "Hilo";
    public const string Guid = "a1f149567da64dd7a9820d455a6fd8a7";
    public const int Protocol = 1;

    public static CarDefinition Create() => new()
    {
        Id = Id,
        DisplayName = DisplayName,
        Description = "A simple late-80s four-wheel-drive pickup: a regular cab, a long open bed with wheel arches, a solid axle at " +
                      "each end on knobbly tyres and steel wheels. Light, plain and hard to break.",
        PageNote = "A late-1980s compact 4x4 pickup: lighter and a little slower than the big trucks, sturdy, and happy on " +
                   "rough tracks. Buy it in the garage, or try it from here. Everyone on a server needs this mod to see it.",
        ItemId = ItemId,
        CarName = CarName,
        FusionGuid = Guid,
        RegistryKey = "hilo",
        Protocol = Protocol,
        // The Burro, the stock pickup (2.94 m wheelbase against the Hilo's 2.80 m, 5.0 m long against 4.70 m, a regular
        // cab and an open bed): its skeleton, garage data (vehicle class, upgrades) and cosmetic slots (Liveries/hilo/README.md).
        DefaultDonor = "Car_Burro",
        DefaultGarageBase = "Burro",
        AssetName = "hilo",
        // An open pickup bed, like the Burro's: its cargo bed and the 13 bed cosmetics stay, and the Bed slot goes to
        // cosm_Bed (the standard table has no bed slot).
        HasBed = true,
        Cosmetics = CarCosmetics.Build(CarCosmetics.SlotAnchors.Append(("cosm_Bed", "Visual/Bed")), CarCosmetics.ItemAnchors),
        // A light compact pickup on the Burro's skeleton: a fifth less weight and a little less torque with the Burro's
        // gearing kept, so it pulls about as hard for its weight and tops out about as fast; longer, softer suspension
        // travel for rough ground, normal grip and lock. Not driven side by side yet: the numbers are easy to change.
        Tuning = new CarTuningProfile
        {
            Mass = 0.8f, CentreOfMassDrop = 0.0f, Torque = 0.82f, RevRange = 1.0f, GearRatios = 1.0f, FinalDrive = 1.0f,
            SuspensionTravel = 1.1f, SpringRate = 0.9f, DamperRate = 0.9f, TyreGrip = 1.0f, SteeringLock = 1.0f,
        },
        // Period colours of the late-80s pickups: cream, faded red, sky blue, tan, white and a brown.
        Paints = new[]
        {
            new PaintPreset("Cream", new CarRgb(226, 214, 176)),
            new PaintPreset("Faded Red", new CarRgb(150, 58, 48)),
            new PaintPreset("Sky Blue", new CarRgb(118, 160, 196)),
            new PaintPreset("Tan", new CarRgb(184, 156, 108)),
            new PaintPreset("White", new CarRgb(236, 236, 230)),
            new PaintPreset("Brown", new CarRgb(104, 70, 48)),
        },
        PaintCustomDefault = "#E2D6B0",
        // Zones G is the trim: the front and rear bumpers and the mirror housings.
        Trims = new[]
        {
            new TrimPreset("Black", new CarRgb(26, 26, 28)),
            new TrimPreset("Chrome", new CarRgb(196, 199, 203)),
            new TrimPreset("Brown", new CarRgb(86, 58, 40)),
            TrimPreset.Body(),
        },
        TrimCustomDefault = "#1A1A1C",
        TrimWhat = "Bumpers and mirrors",
        TrimHint = "The front bumper, the rear bar and the mirror housings: black (as delivered), chrome, a brown two-tone, " +
                   "the body colour or your own.",
        // Stock engine sounds of the game's utility 4x4s: the Burro (the donor, a pickup), the Brick (a small 4x4), the
        // Protector and the old Ding. Not listened to side by side yet: the list is easy to change.
        EngineSounds = new[]
        {
            new EngineSound("Burro", "Burro (pickup)"),
            new EngineSound("Brick", "Brick (4x4)"),
            new EngineSound("Protector", "Protector (utility four)"),
            new EngineSound("DingDing", "Ding (old 4x4)"),
        },
        DefaultEngine = 0,
        EngineHint = "Which stock car's engine your Hilo sounds like (the utility 4x4s and the pickup fit a plain four " +
                     "best). Other players with this mod hear your pick.",
    };
}
