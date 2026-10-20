using System.Linq;
using OverTheHillPlus.Cars;

namespace OverTheHillPlus.Hilo22;

/// <summary>
/// The Hilo 22 (mod id "hilo22"): a modern (2022) double-cab 4x4 pickup for core's car framework, built from a detailed
/// model (no maker names, badges or lettering anywhere in the UI or the model). Its identity values are part of the
/// network protocol and of the save sidecar (oth.hilo22.&lt;profile&gt;.sidecar): they never change once released.
/// Pure (unit-tested).
/// </summary>
public static class Hilo22Definition
{
    public const string Id = "hilo22";
    public const string DisplayName = "Hilo 22";
    /// <summary>Hand-picked next to the Bastion's 0xB7E2A005, checked against the live catalog at startup.</summary>
    public const uint ItemId = 0xB7E2A006;
    /// <summary>CarInfo.name and the livery car name ("car": "Hilo22" in a skin's manifest; ASCII only).</summary>
    public const string CarName = "Hilo22";
    public const string Guid = "7406e4e8c15a42ad93263ad63a41bf4b";
    public const int Protocol = 4;

    public static CarDefinition Create() => new()
    {
        Id = Id,
        DisplayName = DisplayName,
        Description = "A modern four-wheel-drive double-cab pickup: a turbo-diesel with plenty of torque, a four-door cab, " +
                      "wide black arch flares, side steps and a long open bed. Heavier and stronger than the old pickups, " +
                      "still lighter than the big SUVs.",
        PageNote = "A 2022 double-cab 4x4 pickup: torquier and heavier than the Hilo 89, lighter than the Bastion, with long " +
                   "travel for rough ground. Buy it in the garage, or try it from here. Everyone on a server needs this mod to see it.",
        ItemId = ItemId,
        CarName = CarName,
        FusionGuid = Guid,
        RegistryKey = "hilo22",
        Protocol = Protocol,
        // The Burro, the stock pickup (2.94 m wheelbase against the Hilo 22's 3.08 m, 5.01 m long against 5.33 m, a cab and
        // an open bed): its skeleton, garage data (vehicle class, upgrades) and cosmetic slots (Liveries/hilo22/README.md).
        // The Cumbre (2.96 m, a flatbed truck) has no bed slot and a cab-forward body.
        DefaultDonor = "Car_Burro",
        DefaultGarageBase = "Burro",
        AssetName = "hilo22",
        // Gears and diffs: the real car's gearbox and differentials (a 6-speed double cab, part-time 4WD: the rear locker is the lock key).
        GearSet = "close5",
        Diffs = new byte[] { 1, 1, 3 },
        // An open pickup bed, like the Burro's: its cargo bed and the 13 bed cosmetics stay, and the Bed slot goes to
        // cosm_Bed (the standard table has no bed slot).
        HasBed = true,
        Cosmetics = CarCosmetics.Build(CarCosmetics.SlotAnchors.Append(("cosm_Bed", "Visual/Bed")), CarCosmetics.ItemAnchors),
        // The real double cab's numbers on the Burro's skeleton: 2.15 t (lighter than the Bastion, which it was wrongly
        // heavier than) and 42 kW (about 19.5 kW/t, no more than the stock workhorses; the turbo-diesel's torque stays), a 175 km/h real top
        // speed as a 9 m/s limiter (the Burro's is 6), tall gearing so the limiter comes at 85 % of the revs, long travel on
        // a little firmer springs for rough ground. Tows 3.5 t. Not driven side by side
        // yet: the numbers are easy to change.
        Tuning = new CarTuningProfile
        {
            // Dampers set for a body damping ratio of about 0.65, the Workhorse donors' own (CarPhysicsFitTests): the
            // donor's dampers cut with the springs left this light car over-damped, stiff over every bump.
            Mass = 0.77f, CentreOfMassDrop = 0.02f, Torque = 1.05f, Power = 0.70f, TopSpeed = 1.5f, RevRange = 1.0f, GearRatios = 0.62f, FinalDrive = 1.0f,
            SuspensionTravel = 1.1f, SpringRate = 0.92f, DamperRate = 0.85f, TyreGrip = 1.0f, SteeringLock = 0.97f, TowKg = 3500f,
        },
        // The colours of the 2022 range: super white, silver, attitude black, grey metallic, emotional red, nebula blue,
        // oxide bronze, and a sand.
        Paints = new[]
        {
            new PaintPreset("Super White", new CarRgb(240, 240, 237)),
            new PaintPreset("Silver", new CarRgb(190, 194, 198)),
            new PaintPreset("Attitude Black", new CarRgb(24, 24, 26)),
            new PaintPreset("Grey Metallic", new CarRgb(96, 100, 106)),
            new PaintPreset("Emotional Red", new CarRgb(150, 24, 30)),
            new PaintPreset("Nebula Blue", new CarRgb(38, 62, 108)),
            new PaintPreset("Oxide Bronze", new CarRgb(112, 82, 62)),
            new PaintPreset("Sand", new CarRgb(200, 184, 150)),
        },
        PaintCustomDefault = "#F0F0ED",
        // Zones G is the trim: the grille surround, the door handles, the mirror caps, the side steps and the bumper corners.
        Trims = new[]
        {
            new TrimPreset("Black", new CarRgb(30, 30, 33)),
            new TrimPreset("Chrome", new CarRgb(196, 199, 203)),
            TrimPreset.Body(),
        },
        TrimCustomDefault = "#1E1E21",
        TrimWhat = "Trim (grille surround, handles, mirrors, steps)",
        TrimHint = "The grille surround, the door handles, the mirror caps, the side steps and the bumper corners: black (as " +
                   "delivered), chrome, the body colour or your own.",
        // Stock engine sounds of the game's pickups and utility 4x4s: the Burro (the donor, a pickup), the Cumbre, the
        // Protector and the TrailBurner. Not listened to side by side yet: the list is easy to change.
        EngineSounds = new[]
        {
            new EngineSound("Burro", "Burro (pickup)"),
            new EngineSound("Cumbre", "Cumbre (V6)"),
            new EngineSound("Protector", "Protector (utility four)"),
            new EngineSound("TrailBurner", "TrailBurner"),
        },
        DefaultEngine = 0,
        EngineHint = "Which stock car's engine your Hilo 22 sounds like (the Burro's pickup engine fits it best). " +
                     "Other players with this mod hear your pick.",
        // The garage's Liveries tab, after "Paint" (ids are part of the network layout: only append).
        Liveries = new[]
        {
            new LiveryPreset("sport_pack", "Satin Sport Pack", "Satin black roof, hood and lower sides with thin orange lines, over your own paint."),
            new LiveryPreset("whitecap", "Whitecap Alpine Lodge", "The lodge's mountain truck from Gimli Ridge, white with a slate blue ridge line."),
            new LiveryPreset("aures_rally", "Privateer 214", "A privateer's truck after a week in the Aurès Mountains, number 214."),
        },
    };
}
