using System.Linq;
using OverTheHillPlus.Cars;

namespace OverTheHillPlus.Badger;

/// <summary>
/// The Badger (mod id "badger"): a 2-tonne cab-over 4x4 army truck from the made-up Ostrog works, in the class of a
/// GAZ-66, for core's car framework, modelled at real size (no maker names, badges or lettering anywhere in the UI or
/// the model). It lands in the garage's Expedition class, with the Mammoth. Its identity values are part of the network
/// protocol and of the save sidecar (oth.badger.&lt;profile&gt;.sidecar): they never change once released. Pure
/// (unit-tested).
/// </summary>
public static class BadgerDefinition
{
    public const string Id = "badger";
    public const string DisplayName = "Badger";
    /// <summary>Assigned for round 6 (0xB7E2A00E to 0xB7E2A013 went to the six new cars), checked against the live
    /// catalog at startup.</summary>
    public const uint ItemId = 0xB7E2A012;
    /// <summary>CarInfo.name and the livery car name ("car": "Badger" in a skin's manifest; ASCII only).</summary>
    public const string CarName = "Badger";
    public const string Guid = "60e64ac4953e4da3bf4230ad704c72ff";
    public const int Protocol = 1;

    public static CarDefinition Create() => new()
    {
        Id = Id,
        DisplayName = DisplayName,
        Description = "A 2-tonne cab-over army truck: a rounded cab sitting on the V8, a slotted grille and round lamps low in its front, tall single tyres " +
                      "you can let down from the cab and a canvas tilt over a drop-side body. Light for a truck and hard to stop " +
                      "off road, but it leans in the corners.",
        PageNote = "An Expedition-class 4x4 army truck with a canvas-covered body: lighter and quicker than the Mammoth, with " +
                   "low gears and a front axle you switch in. Buy it in the garage, or try it from here. Everyone on a server needs this " +
                   "mod to see it.",
        ItemId = ItemId,
        CarName = CarName,
        FusionGuid = Guid,
        RegistryKey = "badger",
        Protocol = Protocol,
        // The Mammoth, the stock Expedition truck (2.53 m wheelbase, 1.80 m track, a cab-forward flatbed, 0.52 m tyres):
        // its skeleton, garage data (the Expedition vehicle class, upgrades) and cosmetic slots (Liveries/badger/README.md).
        // The wheelbase (3.30 m) and the tracks (1.80 / 1.75 m) come from the model.
        DefaultDonor = "Car_Mammoth",
        DefaultGarageBase = "Mammoth",
        AssetName = "badger",
        // A covered body, like the Mammoth's open bed underneath: its cargo bed stays (loads ride under the canvas). The bed
        // cosmetics (camper, tent and party conversions) can't sit under the tilt, and the Mammoth's side steps would cut
        // through the Badger's own steps, so both slots stay hidden. The Mammoth has no Hood items: the model's cosm_Hood
        // anchor (top of the grille) is for the engine intake probe and the engine smoke.
        HasBed = true,
        Cosmetics = CarCosmetics.Build(CarCosmetics.SlotAnchors.Append(("cosm_Bed", "Visual/Bed")), CarCosmetics.ItemAnchors),
        HiddenSlots = new[] { "Visual/Bed", "Visual/Side_Step" },
        // It carries its own spare between the cab and the body; the Rear slot's spare would hang on the canvas.
        HiddenItems = new[] { "Visual/Rear/SpareWheel" },
        // The tow bar hangs from the slot's spot on the Mammoth's high rear bumper; here it bolts under the frame end
        // at the pintle hook's height, on its own anchor (the rest of the Rear_Bumper slot sits on the crossmember).
        ItemAnchors = new[] { new CosmeticItemAnchor("cosm_i_TowBar", "Visual/Rear_Bumper/TowBar") },
        // The real truck's numbers on the Mammoth's skeleton: 3.47 t kerb (x1.085), 43 kW (12.5 kW/t: about half the real
        // 85 kW V8's 24.5 kW/t, the Expedition class's scale), torque a little over the Mammoth's per tonne, a 90 km/h real
        // top speed as a 5.7 m/s limiter, gearing so the limiter comes at 85 % of the revs on the 12.00-18 tyres (radius
        // 0.535 m), springs and dampers scaled with the mass (body damping ratio 0.62), steering for its 9.5 m turning
        // circle. Tows 2 t. Gears and diffs: four gears in two ranges, open axles and a locked centre (no centre diff: the front axle
        // is switched in). The real truck has cam-type limited-slip axles; the gear table keeps real setups on open axles until its
        // limited-slip mode is proven in the game.
        GearSet = "truck4",
        Diffs = new byte[] { 1, 1, 3 },
        Tuning = new CarTuningProfile
        {
            Mass = 1.085f, CentreOfMassDrop = 0.0f, Torque = 1.15f, Power = 0.76f, TopSpeed = 1.035f, RevRange = 1.0f, GearRatios = 1.34f, FinalDrive = 1.0f,
            SuspensionTravel = 1.0f, SpringRate = 1.10f, DamperRate = 1.10f, TyreGrip = 1.0f, SteeringLock = 0.85f, TowKg = 2000f,
        },
        // Army and service colours: protective green, a winter white, sand, oilfield orange, fire red and a grey blue.
        Paints = new[]
        {
            new PaintPreset("Army Green", new CarRgb(78, 86, 52)),
            new PaintPreset("Winter White", new CarRgb(226, 226, 218)),
            new PaintPreset("Sand", new CarRgb(184, 166, 124)),
            new PaintPreset("Oilfield Orange", new CarRgb(212, 96, 30)),
            new PaintPreset("Fire Red", new CarRgb(166, 30, 26)),
            new PaintPreset("Grey Blue", new CarRgb(84, 98, 112)),
        },
        PaintCustomDefault = "#4E5634",
        // Zones G is the canvas tilt (the body colour covers the cab, the body and the wheels).
        Trims = new[]
        {
            new TrimPreset("Army Canvas", new CarRgb(104, 102, 72)),
            new TrimPreset("Sand Canvas", new CarRgb(176, 160, 120)),
            new TrimPreset("Grey Canvas", new CarRgb(112, 114, 110)),
            new TrimPreset("Black Canvas", new CarRgb(36, 36, 36)),
            TrimPreset.Body(),
        },
        TrimCustomDefault = "#686648",
        TrimWhat = "Canvas tilt",
        TrimHint = "The canvas over the body: army green canvas (as delivered), sand, grey, black, the body colour or your own.",
        // Stock engine sounds (CarLooks.ApplyEngine sets CarAudioManager.vehicleType): the Grand Woodman's big V8 is the default,
        // the closest the game has to a petrol V8 truck; then the Mammoth's truck diesel, the Blast Wagon, the Timberbox and
        // the Burro. Chosen by engine type, not listened to side by side.
        EngineSounds = new[]
        {
            new EngineSound("GrandWoodman", "Grand Woodman (big V8)"),
            new EngineSound("Mammoth", "Mammoth (truck diesel)"),
            new EngineSound("BlastWagon", "Blast Wagon (work truck)"),
            new EngineSound("Timberbox", "Timberbox (1940s six)"),
            new EngineSound("Burro", "Burro (pickup)"),
        },
        DefaultEngine = 0,
        EngineHint = "Which stock car's engine your Badger sounds like (the Grand Woodman's V8 fits it best). " +
                     "Other players with this mod hear your pick.",
        // The garage's Liveries tab, after "Paint" (ids are part of the network layout: only append).
        Liveries = new[]
        {
            new LiveryPreset("tactical_numbers", "Tactical Numbers", "White unit numbers on the doors and the tailgate, brushed on over your own paint."),
            new LiveryPreset("whitewash", "Whitewash", "Army green under a winter whitewash brushed on by hand, worn through on the edges."),
            new LiveryPreset("el_madraj", "El Madraj Base Camp", "The supply truck of El Madraj Base Camp in the Aurès Mountains: sand paint, a red band and the camp's name."),
        },
    };
}
