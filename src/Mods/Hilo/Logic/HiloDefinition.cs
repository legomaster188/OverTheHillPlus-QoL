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
    public const int Protocol = 3;

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
        // Gears and diffs: the real car's gearbox and differentials (a 5-speed pickup, part-time 4WD, no lockers).
        GearSet = "close5",
        Diffs = new byte[] { 1, 1, 3 },
        // An open pickup bed, like the Burro's: its cargo bed and the 13 bed cosmetics stay, and the Bed slot goes to
        // cosm_Bed (the standard table has no bed slot).
        HasBed = true,
        Cosmetics = CarCosmetics.Build(CarCosmetics.SlotAnchors.Append(("cosm_Bed", "Visual/Bed")), CarCosmetics.ItemAnchors),
        // The real light pickup's numbers on the Burro's skeleton: 1.45 t (the Burro is 2.8 t) and 28 kW (a 2.4-litre petrol
        // four, about the Burro's power per tonne), a 145 km/h real top speed as a 7.8 m/s limiter (the Burro's is 6),
        // gears taller so the limiter comes at 85 % of the revs; softer springs for the half-weight body, longer travel for
        // rough ground. Tows 1.5 t. Not driven side by side yet: the numbers are easy to change.
        Tuning = new CarTuningProfile
        {
            // Dampers set for a body damping ratio of about 0.65, the Workhorse donors' own (CarPhysicsFitTests): the
            // donor's dampers cut with the springs left this light car over-damped, stiff over every bump.
            Mass = 0.52f, CentreOfMassDrop = 0.0f, Torque = 0.51f, Power = 0.47f, TopSpeed = 1.3f, RevRange = 1.0f, GearRatios = 0.69f, FinalDrive = 1.0f,
            SuspensionTravel = 1.1f, SpringRate = 0.85f, DamperRate = 0.67f, TyreGrip = 1.0f, SteeringLock = 1.0f, TowKg = 1500f,
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
        // The garage's Liveries tab, after "Paint" (ids are part of the network layout: only append).
        Liveries = new[]
        {
            new LiveryPreset("tape_stripe", "Sunset Tape Stripe", "The three colour tape stripe of the late 80s, laid over your own paint."),
            new LiveryPreset("sawmill", "Glasir Wood Sawmill", "The sawmill's yard truck, in the mill's green with its name on the doors."),
            new LiveryPreset("odd_door", "Odd Door", "A sun faded red farm truck with a cream door from the scrapyard."),
        },
    };
}
