using System.Linq;
using OverTheHillPlus.Cars;

namespace OverTheHillPlus.Limber;

/// <summary>
/// The Limber (mod id "limber"): a 1970s British military one-tonne cab-forward 4x4 gun tractor for core's car framework,
/// built at the real size of a Land Rover 101 Forward Control type: a flat-sided aluminium cab over the front wheels with a
/// split windscreen, round headlamps low in the front panel, a drop-side body under a canvas tilt and 9.00x16 tyres (a
/// made-up name: no maker names, badges or lettering anywhere in the UI or the model). It lands in the garage's Expedition
/// class with the Marshal. Its identity values are part of the network protocol and of the save sidecar
/// (oth.limber.&lt;profile&gt;.sidecar): they never change once released. Pure (unit-tested).
/// </summary>
public static class LimberDefinition
{
    public const string Id = "limber";
    public const string DisplayName = "Limber";
    /// <summary>Assigned for this car (round 6), checked against the live catalog at startup.</summary>
    public const uint ItemId = 0xB7E2A011;
    /// <summary>CarInfo.name and the livery car name ("car": "Limber" in a skin's manifest; ASCII only).</summary>
    public const string CarName = "Limber";
    public const string Guid = "c93b6c73e25c45b8ab05d7612a185a6f";
    public const int Protocol = 1;

    /// <summary>The Marshal hangs its pan and its camping lantern off its roof rack; on the Limber that would be the canvas
    /// tilt, so they hang from the top of the tailgate instead (cosm_Pan, cosm_Lantern in the model).</summary>
    public static readonly CosmeticItemAnchor[] OwnItemAnchors =
    {
        new("cosm_Pan", "Visual/Rear/Pan"),
        new("cosm_Lantern", "Visual/Rear/CampingLantern"),
    };

    public static CarDefinition Create() => new()
    {
        Id = Id,
        DisplayName = DisplayName,
        Description = "A 1970s army one-tonner: the cab sits over the front wheels, with a split windscreen, round lamps low in a " +
                      "flat front panel, a drop-side body under a canvas tilt and tall cross-country tyres. Light for its size, " +
                      "with a V8 and full-time four-wheel drive.",
        PageNote = "A military forward-control 4x4 in the Expedition class: short, tall and nimble for a truck, with a canvas-covered " +
                   "load body and a pintle hook. Buy it in the garage, or try it from here. Everyone on a server needs this mod to see it.",
        ItemId = ItemId,
        CarName = CarName,
        FusionGuid = Guid,
        RegistryKey = "limber",
        Protocol = Protocol,
        // The Marshal, the stock Expedition van (2.66 m wheelbase and 1.59 m track against the Limber's real 2.565 m and
        // 1.52 / 1.55 m; the Mammoth is 2.19 m wide over a 1.80 m track): its skeleton, garage data (the Expedition vehicle
        // class, upgrades) and cosmetic slots (Liveries/limber/README.md). An enclosed load body like the Marshal's: no bed.
        DefaultDonor = "Car_Marshal",
        DefaultGarageBase = "Marshal",
        AssetName = "limber",
        ItemAnchors = OwnItemAnchors,
        // It has its own two-tread steps behind the front wheels: the game's side steps would cut through them.
        HiddenSlots = new[] { "Visual/Side_Step" },
        // The real truck's numbers on the Marshal's skeleton: 1950 kg (x0.61 of the Marshal's 3200 kg), 31 kW (a military
        // 3.5 litre V8 of about 90 kW: 46 kW/t, a third of it in the game's scale), a 6.5 m/s (23 km/h) limiter from a
        // real top speed of about 110 km/h, gearing so the limiter comes at 85 % of the revs on the real 0.44 m 9.00x16
        // tyres, softer springs and dampers for the lighter body (a damping ratio of 0.6, the donor's own), the steering
        // lock of its 7.2 m turning circle. Tows 1.9 t (the light gun it was built for). Gears and diffs: a 4-speed with a
        // 2-range transfer box, open axles and a limited-slip centre (the real one is an open centre diff with a lock).
        GearSet = "truck4",
        Diffs = new byte[] { 1, 1, 2 },
        Tuning = new CarTuningProfile
        {
            Mass = 0.61f, CentreOfMassDrop = 0.02f, Torque = 0.70f, Power = 0.55f, TopSpeed = 1.18f, RevRange = 1.0f, GearRatios = 0.97f, FinalDrive = 1.0f,
            SuspensionTravel = 0.9f, SpringRate = 0.68f, DamperRate = 0.62f, TyreGrip = 1.0f, SteeringLock = 0.82f, TowKg = 1900f,
        },
        // Service colours of the period: deep bronze green, the later NATO green, light stone for the desert, RAF blue-grey,
        // matt black and white.
        Paints = new[]
        {
            new PaintPreset("Deep Bronze Green", new CarRgb(49, 58, 40)),
            new PaintPreset("NATO Green", new CarRgb(78, 84, 56)),
            new PaintPreset("Light Stone", new CarRgb(196, 176, 132)),
            new PaintPreset("RAF Blue-Grey", new CarRgb(88, 100, 112)),
            new PaintPreset("Matt Black", new CarRgb(32, 32, 31)),
            new PaintPreset("White", new CarRgb(232, 232, 226)),
        },
        PaintCustomDefault = "#313A28",
        // Zone G is the canvas: the tilt over the load body.
        Trims = new[]
        {
            new TrimPreset("Olive Canvas", new CarRgb(98, 96, 70)),
            new TrimPreset("Sand Canvas", new CarRgb(178, 160, 120)),
            new TrimPreset("Black Canvas", new CarRgb(40, 40, 38)),
            TrimPreset.Body(),
        },
        TrimCustomDefault = "#626046",
        TrimWhat = "Canvas (the tilt)",
        TrimHint = "The canvas tilt over the load body: olive (as issued), sand, black, the body colour or your own. The bumpers, " +
                   "frame and lamp bodies stay black.",
        // Stock engine sounds: the Grand Woodman's big V8 is the closest to the Limber's light-alloy V8, then the Protector
        // (a utility 4x4), the Marshal (the donor) and the Mammoth's truck diesel. Chosen by type, not listened to side by side.
        EngineSounds = new[]
        {
            new EngineSound("GrandWoodman", "Grand Woodman (big V8)"),
            new EngineSound("Protector", "Protector (utility 4x4)"),
            new EngineSound("Marshal", "Marshal (van diesel)"),
            new EngineSound("Mammoth", "Mammoth (truck diesel)"),
        },
        DefaultEngine = 0,
        EngineHint = "Which stock car's engine your Limber sounds like (the Grand Woodman's V8 fits it best). " +
                     "Other players with this mod hear your pick.",
        // The garage's Liveries tab, after "Paint" (ids are part of the network layout: only append).
        Liveries = new[]
        {
            new LiveryPreset("disruptive", "Disruptive", "Black disruptive camouflage over your own paint, the way the army brushed it on."),
            new LiveryPreset("el_kalaa", "El Kalaa Outpost", "The supply truck of El Kalaa Outpost in the Tassili n'Ajjer: light stone, sun-bleached canvas, stencilled doors."),
            new LiveryPreset("harrison_fire", "Harrison Watchtower", "Sold off and sent up to Harrison Watchtower as the fire crew's truck: red, with a white roof and the crew's number."),
        },
    };
}
