using System.Linq;
using OverTheHillPlus.Cars;

namespace OverTheHillPlus.Nomad;

/// <summary>
/// The Nomad (mod id "nomad"): a cab-forward expedition truck for core's car framework in the class of a Unimog U1300L
/// with a flatbed, modelled at real size (no maker names, badges or lettering anywhere in the UI or the model). It
/// lands in the garage's Expedition class, with the Mammoth. Its identity values are part of the network protocol and of
/// the save sidecar (oth.nomad.&lt;profile&gt;.sidecar): they never change once released. Pure (unit-tested).
/// </summary>
public static class NomadDefinition
{
    public const string Id = "nomad";
    public const string DisplayName = "Nomad";
    /// <summary>Hand-picked above the Hilo 22's 0xB7E2A006 (0xB7E2A007 and 0xB7E2A008 are kept for the other new cars),
    /// checked against the live catalog at startup.</summary>
    public const uint ItemId = 0xB7E2A009;
    /// <summary>CarInfo.name and the livery car name ("car": "Nomad" in a skin's manifest; ASCII only).</summary>
    public const string CarName = "Nomad";
    public const string Guid = "bad3198dfd694fcd8bb3ae5488bd8395";
    /// <summary>4: the game's side steps hidden (the Nomad's own step ladders), so its garage part list changed.</summary>
    public const int Protocol = 4;

    public static CarDefinition Create() => new()
    {
        Id = Id,
        DisplayName = DisplayName,
        Description = "A cab-forward expedition truck: a flat-sided cab, a short bonnet, flat-topped wings with round headlamps, " +
                      "portal axles on 20 inch tyres and a high drop-side flatbed. The heaviest car in the garage and slow on the road, " +
                      "it crawls over almost anything off it.",
        PageNote = "An Expedition-class 4x4 truck with portal axles and a flatbed: heavy, low-geared and slow, made for rocks, mud and " +
                   "long trails. Buy it in the garage, or try it from here. Everyone on a server needs this mod to see it.",
        ItemId = ItemId,
        CarName = CarName,
        FusionGuid = Guid,
        RegistryKey = "nomad",
        Protocol = Protocol,
        // The Mammoth, the stock Expedition truck (2.53 m wheelbase, 1.80 m track, a cab-forward flatbed): its
        // skeleton, garage data (the Expedition vehicle class, upgrades) and cosmetic slots (Liveries/nomad/README.md).
        // The Marshal, the other Expedition car, is a box van with no bed.
        DefaultDonor = "Car_Mammoth",
        DefaultGarageBase = "Mammoth",
        AssetName = "nomad",
        // An open flatbed, like the Mammoth's: its cargo bed and the bed cosmetics stay, and the Bed slot goes to cosm_Bed
        // (the standard table has no bed slot). The Mammoth has no Hood slot, so the Hood cosmetics stay hidden.
        HasBed = true,
        Cosmetics = CarCosmetics.Build(CarCosmetics.SlotAnchors.Append(("cosm_Bed", "Visual/Bed")), CarCosmetics.ItemAnchors),
        // The tow bar's plate hung 15 cm under the rear beam with the rest of the rear bumper slot: up against the beam's
        // underside, 2 cm forward so the plate sits under it, the shank under the jaw coupling (ball centre about 0.92 m up).
        // The Trailers mod hitches to the ball it finds on the bar, so trailers follow.
        ItemOffsets = { ["Visual/Rear_Bumper/TowBar"] = new MeshVec3(0f, 0.155f, 0.02f) },
        // The Nomad has its own step ladders under the doors: the game's tube steps (and the side Christmas lights, wound
        // round the same tube) hung below and through them, so the slot is hidden (the rock sliders already leave it out).
        HiddenSlots = new[] { "Visual/Side_Step" },
        // The real truck's numbers on the Mammoth's skeleton: 5.6 t (the heaviest car in the game) and 52 kW (about 9 kW/t: by far
        // the weakest per tonne, but not the 6 kW/t the pure scaling gives, which would not climb; 4L is the crawl range), an
        // 85 km/h real top speed as the Mammoth's 5.5 m/s limiter, short gears (12.5R20 tyre radius 0.54 m) so the limiter comes
        // at 85 % of the revs, springs stiffened for the weight on long portal-axle travel. Tows 7.5 t. The wheelbase (2.90 m)
        // and track come from the model. Gears and diffs: four gears in two ranges, open axles, a locked centre (part-time portal axles).
        GearSet = "truck4",
        Diffs = new byte[] { 1, 1, 3 },
        Tuning = new CarTuningProfile
        {
            Mass = 1.75f, CentreOfMassDrop = -0.08f, Torque = 1.15f, Power = 0.91f, TopSpeed = 1.0f, RevRange = 0.92f, GearRatios = 1.29f, FinalDrive = 1.0f,
            SuspensionTravel = 1.5f, SpringRate = 1.5f, DamperRate = 1.5f, TyreGrip = 1.0f, SteeringLock = 0.95f, TowKg = 7500f,
        },
        // Expedition colours: desert sand, olive drab, cream, safari orange, white and slate blue.
        Paints = new[]
        {
            new PaintPreset("Desert Sand", new CarRgb(205, 185, 145)),
            new PaintPreset("Olive Drab", new CarRgb(92, 98, 62)),
            new PaintPreset("Cream", new CarRgb(232, 222, 190)),
            new PaintPreset("Safari Orange", new CarRgb(214, 102, 36)),
            new PaintPreset("Expedition White", new CarRgb(238, 238, 232)),
            new PaintPreset("Slate Blue", new CarRgb(74, 92, 116)),
        },
        PaintCustomDefault = "#CDB991",
        // Zones G is the trim: bumpers, rear beam, grille surround, lamp bezels, handles, hinges, mirror arms, steps.
        Trims = new[]
        {
            new TrimPreset("Black", new CarRgb(26, 26, 28)),
            new TrimPreset("Chrome", new CarRgb(196, 199, 203)),
            TrimPreset.Body(),
        },
        TrimCustomDefault = "#1A1A1C",
        TrimWhat = "Trim (bumpers, grille, bezels, steps)",
        TrimHint = "The bumpers, rear beam, grille surround, lamp bezels, handles, hinges, mirror arms and steps: black (as " +
                   "delivered), chrome, the body colour or your own.",
        // Stock engine sounds (CarLooks.ApplyEngine sets CarAudioManager.vehicleType): the Mammoth's truck diesel is the
        // default, the closest the game has to the 5.7 litre six of a Unimog-class truck; then the Blast Wagon's bigger
        // diesel, the Marshal's van diesel, the Timberbox and the Burro. Chosen by class, not listened to side by side.
        EngineSounds = new[]
        {
            new EngineSound("Mammoth", "Mammoth (truck diesel)"),
            new EngineSound("BlastWagon", "Blast Wagon (big diesel)"),
            new EngineSound("Marshal", "Marshal (van diesel)"),
            new EngineSound("Timberbox", "Timberbox (work truck)"),
            new EngineSound("Burro", "Burro (pickup)"),
        },
        DefaultEngine = 0,
        EngineHint = "Which stock car's engine your Nomad sounds like (the Mammoth's truck diesel fits it best). " +
                     "Other players with this mod hear your pick.",
        // The garage's Liveries tab, after "Paint" (ids are part of the network layout: only append).
        Liveries = new[]
        {
            new LiveryPreset("ivory_top", "Ivory Top", "An ivory upper cab and roof over your own paint, with a fine coachline under it."),
            new LiveryPreset("mountain_rescue", "Crossroads Rescue", "The mountain rescue truck from Crossroads Shelter, in signal red."),
            new LiveryPreset("switchback_crew", "Switchback Crew", "A retired road crew truck from the Wakefield Switchback, sun faded, its old lettering still showing."),
        },
    };
}
