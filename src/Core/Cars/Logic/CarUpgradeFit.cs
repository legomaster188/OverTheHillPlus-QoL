using System;
using System.Collections.Generic;

namespace OverTheHillPlus.Cars;

/// <summary>
/// The mechanical upgrades that depend on where things are on the car, made to work on a modded body the way they do
/// on its donor. Pure code (unit-tested); CarBody and CarCatalog apply it.
/// <list type="bullet">
/// <item>Snorkel (water resistance): the game floods the engine when the live intake probe is under water. The probes
///   are children of the donor prefab's <c>WaterDetector</c> node, one per tier: <c>EngineAirIntakeWaterDetector_1</c>
///   at the hood (stock), <c>_2</c> just over snorkel level 1's top, <c>_3</c> just over level 2's top
///   (VehicleWaterDetector.SetEngineIntakeIndex picks one). On a modded car the snorkel and hood slots move to the
///   model's anchors, so each probe moves by the same amount as what it sits on. <c>FrontCarSubmerged</c> /
///   <c>BackCarSubmerged</c> sit at the donor's roof: they follow the body's top.</item>
/// <item>Suspension lift: SuspensionLiftUpgradeEffect sets every wheel's travel to the CarInfo's
///   <c>suspensionSetup.suspensionTravel</c> plus the stage's modifier. The CarInfo is a copy of the base car's, so
///   without a fix the lift (and taking it off) threw away the car's own travel (<see cref="CarTuningProfile.SuspensionTravel"/>).
///   The car's CarInfo gets the base travel times that multiplier, so each stage adds its lift to the car's own travel.</item>
/// </list>
/// </summary>
public static class CarUpgradeFit
{
    public const string WaterDetectorNode = "WaterDetector";
    public const string Hood = "Visual/Hood";
    public const string SnorkelLevel1 = "Performance/Snorkel/SnorkelLevel01";
    public const string SnorkelLevel2 = "Performance/Snorkel/SnorkelLevel02";
    /// <summary>Not a cosmetic target: the probe follows the top of the body.</summary>
    public const string BodyTop = "<body top>";

    /// <summary>What a probe under the donor's WaterDetector node follows, best first (the next one is used when the
    /// first did not move, e.g. a hidden snorkel slot); empty: it stays (the tyre and touching-water probes, which are
    /// measured from the ground the wheels stand on).</summary>
    public static IReadOnlyList<string> Follows(string probe)
    {
        if (string.IsNullOrEmpty(probe)) return Array.Empty<string>();
        if (probe.StartsWith("EngineAirIntakeWaterDetector", StringComparison.Ordinal))
        {
            if (probe.EndsWith("_2", StringComparison.Ordinal)) return new[] { SnorkelLevel1, Hood };
            if (probe.EndsWith("_3", StringComparison.Ordinal)) return new[] { SnorkelLevel2, SnorkelLevel1, Hood };
            return new[] { Hood };
        }
        if (probe == "FrontCarSubmerged" || probe == "BackCarSubmerged") return new[] { BodyTop };
        return Array.Empty<string>();
    }

    /// <summary>
    /// Where a probe goes (car-root space, Unity axes). <paramref name="moves"/>: how far each cosmetic target moved
    /// when it was placed on the model (target path to offset); the first of <see cref="Follows"/> that moved gives the
    /// offset (its height and its place along the car; the probes stay on the donor's centre line). A
    /// <see cref="BodyTop"/> probe keeps its height under the roof (body top) and its fraction along the body.
    /// Null when the probe stays where it is.
    /// </summary>
    public static MeshVec3? Place(string probe, MeshVec3 donor, IReadOnlyDictionary<string, MeshVec3> moves,
        BodySpan donorBody, BodySpan body)
    {
        foreach (var f in Follows(probe))
        {
            if (f == BodyTop)
            {
                if (!donorBody.Valid || !body.Valid) return null;
                float t = (donor.Z - donorBody.MinZ) / (donorBody.MaxZ - donorBody.MinZ);
                return new MeshVec3(donor.X, donor.Y + (body.Top - donorBody.Top), body.MinZ + t * (body.MaxZ - body.MinZ));
            }
            if (moves != null && moves.TryGetValue(f, out var m) && (Math.Abs(m.Y) > 1e-4f || Math.Abs(m.Z) > 1e-4f))
                return new MeshVec3(donor.X, donor.Y + m.Y, donor.Z + m.Z);
        }
        return null;
    }

    /// <summary>The travel the car's CarInfo should hold for the suspension lift: the base car's times the car's own
    /// travel multiplier (clamped like the profile's); the base itself when it isn't a positive number.</summary>
    public static float LiftBaseTravel(float baseTravel, float travelMultiplier)
    {
        if (float.IsNaN(baseTravel) || float.IsInfinity(baseTravel) || baseTravel <= 0f) return baseTravel;
        if (float.IsNaN(travelMultiplier) || float.IsInfinity(travelMultiplier)) return baseTravel;
        return baseTravel * Math.Clamp(travelMultiplier, CarTuningProfile.MinMultiplier, CarTuningProfile.MaxMultiplier);
    }
}

/// <summary>A body's extent along the car (z) and its top (y), car-root space.</summary>
public readonly struct BodySpan
{
    public readonly float MinZ, MaxZ, Top;
    public BodySpan(float minZ, float maxZ, float top) { MinZ = minZ; MaxZ = maxZ; Top = top; }
    public bool Valid => MaxZ - MinZ > 0.1f && !float.IsNaN(Top) && !float.IsInfinity(Top);
}
