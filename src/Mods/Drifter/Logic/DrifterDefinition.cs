using OverTheHillPlus.Cars;

namespace OverTheHillPlus.Drifter;

/// <summary>
/// The Drifter (mod id "drifter"): a 1980s style 4x4 cab-over camper van for core's car framework, in the class of a T3
/// Syncro with a pop-top roof, modelled at real size (no maker names, badges or lettering anywhere in the UI or the model). It is an Expedition
/// class car: it takes the Marshal's garage data, so the garage lists it with the Marshal and the Mammoth. Its identity
/// values are part of the network protocol and of the save sidecar (oth.drifter.&lt;profile&gt;.sidecar): they never
/// change once released. Pure (unit-tested).
/// </summary>
public static class DrifterDefinition
{
    public const string Id = "drifter";
    public const string DisplayName = "Drifter";
    /// <summary>Hand-picked next to the Hilo 22's 0xB7E2A006 (0xB7E2A007 is kept for another new car), checked against the
    /// live catalog at startup.</summary>
    public const uint ItemId = 0xB7E2A008;
    /// <summary>CarInfo.name and the livery car name ("car": "Drifter" in a skin's manifest; ASCII only).</summary>
    public const string CarName = "Drifter";
    public const string Guid = "c80a5695d4994a84afa3eb6ea26393e5";
    /// <summary>3: the real-size rebuild (2.46 m wheelbase, 1.58 m track, 0.40 m tyres and the gearing for them).</summary>
    public const int Protocol = 4;

    public static CarDefinition Create() => new()
    {
        Id = Id,
        DisplayName = DisplayName,
        Description = "A 1980s four-wheel-drive camper van built for long trips off the road: a pop-top roof, a two-tone body, " +
                      "a sliding door, a roof rack with a ladder and a swing-away spare wheel, on all-terrain tyres and a small " +
                      "lift. Softly sprung and easy to live with, and still capable on a rough track.",
        PageNote = "An Expedition-class camper van: lighter and more comfortable than the Marshal, with long soft " +
                   "suspension for rough ground. Buy it in the garage, or try it from here. Everyone on a " +
                   "server needs this mod to see it.",
        ItemId = ItemId,
        CarName = CarName,
        FusionGuid = Guid,
        RegistryKey = "drifter",
        Protocol = Protocol,
        // The Marshal: the Expedition class van, so its skeleton, garage data (the Expedition class, upgrades) and cosmetic
        // slots fit. The Mammoth (a truck with a bed) is the other Expedition car. The model is real size (Liveries/drifter/
        // README.md): the framework moves the wheel colliders to its 2.46 m wheelbase and 1.58 m track and gives them its
        // 0.40 m tyre radius (235/85R16), so the car sits 13 cm lower than the Marshal on its 0.53 m tyres. The Marshal has
        // no hood slot; the model still has a cosm_Hood anchor on the cowl for the engine intake water probe.
        DefaultDonor = "Car_Marshal",
        DefaultGarageBase = "Marshal",
        AssetName = "drifter",
        // Gears and diffs: the real car's gearbox and differentials (a 5-speed 4x4 van, open axles, a locked centre).
        GearSet = "truck5",
        Diffs = new byte[] { 1, 1, 3 },
        // A camper van's numbers on the Marshal's skeleton: 2.6 t (a fifth lighter than the Marshal, a heavy expedition
        // truck) and 42 kW (a diesel van: slow for its weight, about 16 kW/t), a 125 km/h real top speed as a 7.1 m/s limiter
        // (the Marshal's is 5.5), gearing so the limiter comes at 85 % of the revs, long soft suspension (more travel, a
        // little softer springs and dampers) for a comfortable ride and good articulation, a lower centre of mass than the
        // tall box it replaces, and a bit more steering lock for its shorter body.
        // Tows 2.5 t. GearRatios for the 0.40 m tyres: 0.85 x 4500 rpm x 0.40 m x 0.10472 / (7.1 m/s x 3.5 x 8) = 0.81,
        // so the limiter comes at 85 % of the Marshal's redline in its top gear (RealisticTuningTests checks it).
        Tuning = new CarTuningProfile
        {
            Mass = 0.81f, CentreOfMassDrop = 0.05f, Torque = 0.85f, Power = 0.74f, TopSpeed = 1.29f, RevRange = 1.0f, GearRatios = 0.81f, FinalDrive = 1.0f,
            SuspensionTravel = 1.2f, SpringRate = 0.88f, DamperRate = 0.92f, TyreGrip = 1.0f, SteeringLock = 1.05f, TowKg = 2500f,
        },
        // Two-tone camper classics: the main colour is the lower body and the front, the second colour (the trim) the cabin
        // above the waist line and the pop-top.
        Paints = new[]
        {
            new PaintPreset("Sky Blue", new CarRgb(104, 158, 190)),
            new PaintPreset("Orange", new CarRgb(214, 104, 30)),
            new PaintPreset("Sand", new CarRgb(196, 176, 134)),
            new PaintPreset("Sage Green", new CarRgb(126, 142, 112)),
            new PaintPreset("Red", new CarRgb(168, 34, 30)),
            new PaintPreset("White", new CarRgb(236, 236, 230)),
        },
        PaintCustomDefault = "#689EBE",
        Trims = new[]
        {
            new TrimPreset("Cream", new CarRgb(236, 224, 190)),
            new TrimPreset("White", new CarRgb(238, 238, 232)),
            new TrimPreset("Sand", new CarRgb(206, 188, 148)),
            new TrimPreset("Black", new CarRgb(28, 28, 30)),
            TrimPreset.Body(),
        },
        TrimCustomDefault = "#ECE0BE",
        TrimWhat = "Upper body (cabin and pop-top)",
        TrimHint = "The second colour of the two-tone paint: the cabin above the waist line and the pop-top roof. Cream (as " +
                   "delivered), white, sand, black, the body colour (one colour all over) or your own.",
        // Stock engine sounds of the game's Expedition class and utility 4x4s: the Marshal (the donor, a van), the Mammoth
        // (a truck diesel), the Protector (a utility four) and the Brick (a 4x4). Not listened to side by side yet: the list
        // is easy to change.
        EngineSounds = new[]
        {
            new EngineSound("Marshal", "Marshal (van diesel)"),
            new EngineSound("Mammoth", "Mammoth (truck diesel)"),
            new EngineSound("Protector", "Protector (utility four)"),
            new EngineSound("Brick", "Brick (4x4)"),
        },
        // Every slot is fitted (the anchors are pinned by hand in Liveries/tools/drifter/build_drifter.py). Single items that
        // can't sit right are hidden: the Drifter has its own swing-away spare wheel and ladder, so the Marshal's spare and
        // ladder would stack on them, and its bike rack, guitar and grill hang where the spare is; the lantern and the pan
        // hang from a rack overhang the Drifter's rack doesn't have; the left side pack would cover the sliding door; the
        // side Christmas lights share the step slot, which sits under the sill. The whip light poles stay (on the rack).
        HiddenItems = new[]
        {
            "Visual/Rear/SpareWheel", "Visual/Rear/Ladder", "Visual/Rear/Bike", "Visual/Rear/Guitar", "Visual/Rear/MountedGrill",
            "Visual/Rear/CampingLantern", "Visual/Rear/Pan", "Visual/Side/Side_Left", "Visual/Side_Step/Christmas_Side",
        },
        // The swing-away spare and the ladder hang 0.3 m behind the collider part (the hull stops at the rear panel): a box
        // round them so they don't pass through walls, rocks and other cars (the QA audit's physics check).
        BodyBoxes = new[] { new CarBodyBox(new MeshVec3(-0.30f, 0.93f, -2.54f), new MeshVec3(1.20f, 0.95f, 0.31f)) },
        DefaultEngine = 0,
        EngineHint = "Which stock car's engine your Drifter sounds like (the Marshal's van diesel fits it best). " +
                     "Other players with this mod hear your pick.",
    };
}
