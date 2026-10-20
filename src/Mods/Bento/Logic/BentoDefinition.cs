using System.Linq;
using OverTheHillPlus.Cars;

namespace OverTheHillPlus.Bento;

/// <summary>
/// The Bento (mod id "bento"): a Japanese kei flatbed mini truck for core's car framework, built from a detailed model
/// (no maker names, badges or lettering anywhere in the UI or the model). Its identity values are part of the network
/// protocol and of the save sidecar (oth.bento.&lt;profile&gt;.sidecar): they never change once released.
/// Pure (unit-tested).
/// </summary>
public static class BentoDefinition
{
    public const string Id = "bento";
    public const string DisplayName = "Bento";
    /// <summary>Hand-picked after the Pixie's 0xB7E2A00A (0xB7E2A009 is the last id taken before them), checked against
    /// the live catalog at startup.</summary>
    public const uint ItemId = 0xB7E2A00B;
    /// <summary>CarInfo.name and the livery car name ("car": "Bento" in a skin's manifest; ASCII only).</summary>
    public const string CarName = "Bento";
    public const string Guid = "960a2f65ca4a47e4a247a6d8ebd83d3c";
    public const int Protocol = 3;

    public static CarDefinition Create() => new()
    {
        Id = Id,
        DisplayName = DisplayName,
        Description = "A tiny two-seat flatbed truck from the narrow back roads: a small engine under the seats, a short " +
                      "wheelbase, a flat bed behind the cab and hardly any weight to throw around. Light, nimble and " +
                      "happy anywhere a big pickup won't fit.",
        PageNote = "A kei-class flatbed mini truck: tiny, light and nimble, with its small engine under the seat. Buy it in " +
                   "the garage, or try it from here. Everyone on a server needs this mod to see it.",
        ItemId = ItemId,
        CarName = CarName,
        FusionGuid = Guid,
        RegistryKey = "bento",
        Protocol = Protocol,
        // The Burro: the smallest stock truck with a cargo bed (the stock cars are all bigger: 2.94 m wheelbase and
        // 5.01 m against the Bento's 2.0 m and 3.4 m; the smaller Workhorse, the Boar, is a wagon with no bed slot). Its
        // skeleton, garage data (Workhorse class, upgrades) and cosmetic slots (Liveries/bento/README.md).
        DefaultDonor = "Car_Burro",
        DefaultGarageBase = "Burro",
        AssetName = "bento",
        // Gears and diffs: the real car's gearbox and differentials (a kei 5-speed with a low first gear, part-time 4WD).
        GearSet = "kei5",
        Diffs = new byte[] { 1, 1, 3 },
        // An open flatbed, like the Burro's: its cargo bed and the 13 bed cosmetics stay, and the Bed slot goes to
        // cosm_Bed (the standard table has no bed slot).
        HasBed = true,
        Cosmetics = CarCosmetics.Build(CarCosmetics.SlotAnchors.Append(("cosm_Bed", "Visual/Bed")), CarCosmetics.ItemAnchors),
        // A 660 cc kei truck on the Burro's skeleton: 0.87 t (under a third of the Burro's 2.8 t) and 14 kW (a small
        // high-revving engine on 12" wheels, so the rev range is raised), a 105 km/h real top speed as a 6.3 m/s limiter,
        // gearing so the limiter comes at 85 % of the revs, soft short-travel springs scaled to the weight (the Burro's are
        // for a 2.8 t car) and a tight steering lock. The weight sits low, under the seats. Tows 350 kg. Not driven side by
        // side yet: the numbers are easy to change.
        Tuning = new CarTuningProfile
        {
            // Dampers set for a body damping ratio of about 0.65, the Workhorse donors' own (CarPhysicsFitTests): the
            // donor's dampers cut with the springs left this light car over-damped, stiff over every bump.
            Mass = 0.31f, CentreOfMassDrop = 0.05f, Torque = 0.19f, Power = 0.23f, TopSpeed = 1.05f, RevRange = 1.35f, GearRatios = 0.83f, FinalDrive = 1.0f,
            SuspensionTravel = 0.9f, SpringRate = 0.55f, DamperRate = 0.42f, TyreGrip = 0.95f, SteeringLock = 1.12f, TowKg = 350f,
        },
        // Kei truck colours: a white first, silver, red, a dark blue, a sage green and a beige.
        Paints = new[]
        {
            new PaintPreset("Taffeta White", new CarRgb(238, 238, 234)),
            new PaintPreset("Silver", new CarRgb(186, 190, 193)),
            new PaintPreset("Red", new CarRgb(168, 28, 34)),
            new PaintPreset("Dark Blue", new CarRgb(30, 48, 90)),
            new PaintPreset("Sage Green", new CarRgb(140, 160, 134)),
            new PaintPreset("Beige", new CarRgb(206, 192, 158)),
        },
        PaintCustomDefault = "#EEEEEA",
        // Zones G is the trim: the mirror housings, the door handles and the front bumper's lower valance.
        Trims = new[]
        {
            new TrimPreset("Black", new CarRgb(30, 30, 33)),
            TrimPreset.Body(),
            new TrimPreset("Chrome", new CarRgb(196, 199, 203)),
        },
        TrimCustomDefault = "#1E1E21",
        TrimWhat = "Trim (mirrors, door handles, front valance)",
        TrimHint = "The mirror housings, the door handles and the front bumper's lower valance: black (as delivered), " +
                   "the body colour, chrome or your own.",
        // Stock engine sounds of the small cars: the Brick, the Ding, the Tinny and the Lynx. Not listened to side by
        // side yet: the list is easy to change.
        EngineSounds = new[]
        {
            new EngineSound("Brick", "Brick (small four)"),
            new EngineSound("DingDing", "Ding (small four)"),
            new EngineSound("Tinny", "Tinny (small four)"),
            new EngineSound("Lynx", "Lynx (four)"),
        },
        DefaultEngine = 2, // index in EngineSounds (kept so saved choices still mean the same sound)
        EngineHint = "Which stock car's engine your Bento sounds like (the Tinny's small four fits a tiny truck best). " +
                     "Other players with this mod hear your pick.",
        // The garage's Liveries tab, after "Paint" (ids are part of the network layout: only append).
        Liveries = new[]
        {
            new LiveryPreset("triple_stripe", "Triple Stripe", "The dealer's three-line tape stripe and 4WD of the early 90s, over your own paint."),
            new LiveryPreset("beatrice_ridge_orchard", "Beatrice Ridge Orchard", "The orchard's truck, white with its name sign-written on the doors and the tailgate."),
            new LiveryPreset("spare_tailgate", "Spare Tailgate", "An old green work truck, sun faded, with a primer tailgate from the scrapyard."),
        },
    };
}
