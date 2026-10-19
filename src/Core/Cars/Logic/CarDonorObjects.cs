using System;
using System.Collections.Generic;

namespace OverTheHillPlus.Cars;

/// <summary>What a donor object follows on the model.</summary>
public enum DonorFollow
{
    /// <summary>Moves to one of <see cref="DonorObjectRule.Anchors"/> (the one on the donor object's side when there are
    /// two); switched off when the model has none of them.</summary>
    Anchor,
    /// <summary>Moves with wheel <see cref="DonorObjectRule.Wheel"/> (FL FR RL RR) in track and wheelbase, and with the
    /// ground in height (a smaller tyre puts the ground higher on the body).</summary>
    Wheel,
    /// <summary>Moves to <see cref="DonorObjectRule.Anchors"/> when the model has one, else with the hood slot, else with
    /// the front axle.</summary>
    Hood,
    /// <summary>Switched off.</summary>
    Off,
}

/// <summary>One donor object (a node under the car root, by path) and what it follows.</summary>
public sealed class DonorObjectRule
{
    public string Path;
    public DonorFollow Follow;
    public string[] Anchors = Array.Empty<string>();
    public int Wheel;

    public DonorObjectRule(string path, DonorFollow follow, params string[] anchors)
    {
        Path = path;
        Follow = follow;
        Anchors = anchors ?? Array.Empty<string>();
    }

    public static DonorObjectRule AtWheel(string path, int wheel) => new(path, DonorFollow.Wheel) { Wheel = wheel };
}

/// <summary>
/// The donor's runtime objects that are not part of its body mesh or its cosmetic slots: lens flares, exhaust smoke,
/// engine damage smoke, the wheels' dust and their sound. The lights the framework always moved (head, position, brake
/// and reverse lights and the bounce light) are not in here. Without this a modded car glowed where the donor's A-pillar
/// spotlights were (the Blast Wagon's and the Mammoth's), smoked from the donor's tail pipe and threw dust where the
/// donor's wheels were. A car's definition can override any rule (<see cref="CarDefinition.DonorObjects"/>): an anchor
/// name moves it there, an empty one switches it off. Every light or lens flare outside the cosmetic slots that no rule
/// names is switched off. Pure (unit-tested).
/// </summary>
public static class CarDonorObjects
{
    /// <summary>Optional model anchors (docs/car-pipeline.md, "Donor objects"): the lens flares of pillar spotlights, and
    /// where engine damage smokes from.</summary>
    public const string SpotlightL = "spotlight_L", SpotlightR = "spotlight_R", EngineSmoke = "engine_smoke";

    public static readonly DonorObjectRule[] Defaults =
    {
        new("Lights/LeftSpotlightFlare", DonorFollow.Anchor, SpotlightL),
        new("Lights/RightSpotlightFlare", DonorFollow.Anchor, SpotlightR),
        new("Effects/VehicleParticles/ExhaustParticlesManager", DonorFollow.Anchor, "exhaust_L", "exhaust_R"),
        new("Effects/EngineDamageParticles", DonorFollow.Hood, EngineSmoke),
        DonorObjectRule.AtWheel("Effects/TerrainParticles/FLWheel", 0),
        DonorObjectRule.AtWheel("Effects/TerrainParticles/FRWheel", 1),
        DonorObjectRule.AtWheel("Effects/TerrainParticles/RLWheel", 2),
        DonorObjectRule.AtWheel("Effects/TerrainParticles/RRWheel", 3),
        DonorObjectRule.AtWheel("Audio/FLWheel", 0),
        DonorObjectRule.AtWheel("Audio/FRWheel", 1),
        DonorObjectRule.AtWheel("Audio/RLWheel", 2),
        DonorObjectRule.AtWheel("Audio/RRWheel", 3),
        DonorObjectRule.AtWheel("Audio/Suspension", 3),
        DonorObjectRule.AtWheel("Audio/Impact", 3),
    };

    /// <summary>The lights CarBody always moved itself (MoveLights): never switched off by the sweep.</summary>
    public static readonly string[] MovedLights =
    {
        "Lights/LeftHeadlight", "Lights/RightHeadlight", "Lights/LeftPositionBrakelight", "Lights/RightPositionBrakelight",
        "Lights/LeftBrakingBrakelight", "Lights/RightBrakingBrakelight", "Lights/LeftReverselight", "Lights/RightReverselight",
        "Lights/FakeBounceLight",
    };

    /// <summary>The rules for a car: the defaults, each replaced by the car's override for the same path (an anchor name:
    /// follow it, off when the model lacks it; empty: off), plus the car's overrides for other paths.</summary>
    public static List<DonorObjectRule> For(IReadOnlyList<DonorObjectOverride> overrides)
    {
        var list = new List<DonorObjectRule>();
        var by = new Dictionary<string, DonorObjectOverride>(StringComparer.Ordinal);
        if (overrides != null)
            foreach (var o in overrides)
                if (o != null && !string.IsNullOrEmpty(o.Path)) by[o.Path] = o;
        foreach (var d in Defaults)
        {
            if (by.TryGetValue(d.Path, out var o)) { list.Add(FromOverride(o)); by.Remove(d.Path); }
            else list.Add(d);
        }
        foreach (var o in by.Values) list.Add(FromOverride(o));
        return list;
    }

    private static DonorObjectRule FromOverride(DonorObjectOverride o) =>
        string.IsNullOrEmpty(o.Anchor) ? new DonorObjectRule(o.Path, DonorFollow.Off) : new DonorObjectRule(o.Path, DonorFollow.Anchor, o.Anchor);

    /// <summary>The anchor a rule moves its object to: of <see cref="DonorObjectRule.Anchors"/> the models has, the one
    /// on the donor object's side (x sign) when it has both sides' (names ending _L / _R), else the first it has; null
    /// when it has none (or the rule doesn't follow an anchor).</summary>
    public static string PickAnchor(DonorObjectRule rule, Func<string, bool> hasAnchor, float donorX)
    {
        if (rule == null || hasAnchor == null || rule.Anchors.Length == 0) return null;
        if (rule.Follow is not (DonorFollow.Anchor or DonorFollow.Hood)) return null;
        string first = null;
        foreach (var a in rule.Anchors)
        {
            if (!hasAnchor(a)) continue;
            first ??= a;
            bool left = a.EndsWith("_L", StringComparison.Ordinal), right = a.EndsWith("_R", StringComparison.Ordinal);
            if ((left && donorX < 0f) || (right && donorX > 0f)) return a;
        }
        return first;
    }

    /// <summary>True when <paramref name="path"/> (below the car root) is a light or flare the framework places: one of
    /// <see cref="MovedLights"/> or a rule's path. Anything under Parts/ (the cosmetic slots, which move with their slot)
    /// counts as placed too.</summary>
    public static bool Placed(string path, IReadOnlyList<DonorObjectRule> rules)
    {
        if (string.IsNullOrEmpty(path)) return false;
        if (path.StartsWith("Parts/", StringComparison.Ordinal)) return true;
        foreach (var m in MovedLights)
            if (Under(path, m)) return true;
        if (rules != null)
            foreach (var r in rules)
                if (r.Follow != DonorFollow.Off && Under(path, r.Path)) return true;
        return false;
    }

    private static bool Under(string path, string node) =>
        path == node || (path.Length > node.Length && path.StartsWith(node, StringComparison.Ordinal) && path[node.Length] == '/');
}

/// <summary>A car's own rule for a donor object (<see cref="CarDefinition.DonorObjects"/>): <see cref="Anchor"/> names the
/// model anchor it moves to; empty switches it off.</summary>
public sealed class DonorObjectOverride
{
    public string Path, Anchor;
    public DonorObjectOverride(string path, string anchor) { Path = path; Anchor = anchor; }
}

public sealed class CarBodyBox
{
    public MeshVec3 Center, Size;
    public CarBodyBox(MeshVec3 center, MeshVec3 size) { Center = center; Size = size; }
    public float MinY => Center.Y - Size.Y * 0.5f;
    public float MinZ => Center.Z - Size.Z * 0.5f;
    public float MaxZ => Center.Z + Size.Z * 0.5f;
}
