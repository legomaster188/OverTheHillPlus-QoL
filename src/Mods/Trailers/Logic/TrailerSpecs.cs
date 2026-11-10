using System;
using System.Collections.Generic;

namespace OverTheHillPlus.Trailers;

/// <summary>The trailers. The numbers are part of the network protocol (messages carry them): never renumber, only add.</summary>
public enum TrailerKind : byte
{
    Utility = 0,
    Camper = 1,
    Hauler = 2,
    Boat = 3,
    /// <summary>The 40 ft flatbed semi-trailer: on a semi tractor's fifth wheel (a modded car with a "fifth_wheel" anchor).</summary>
    Semi = 4,
}

/// <summary>One trailer's fixed identity and tuning. Pure data (unit-tested).</summary>
public sealed class TrailerSpec
{
    public TrailerKind Kind;
    /// <summary>Key in core's network prefab order (Core/Shared/NetPrefabPlan.Order).</summary>
    public string RegistryKey;
    /// <summary>NetworkObjectGuid of the prefab entry, 32 hex digits, fixed forever.</summary>
    public string Guid;
    /// <summary>Fusion's path for the entry (never a real asset).</summary>
    public string AssetPath;
    /// <summary>Embedded model file (src/Mods/Trailers/Assets).</summary>
    public string MeshFile;
    public string Name;
    public string Description;

    /// <summary>Empty mass, kg (light next to a 1.5-2.5 t car keeps the hitch stable in resimulations).</summary>
    public float Mass;
    /// <summary>Centre of mass height above the axle line's ground point, m (low: hard to tip).</summary>
    public float ComHeight;
    /// <summary>Centre of mass along the trailer, m forward of the axle (a little ahead: tongue weight).</summary>
    public float ComForward;
    /// <summary>Suspension travel above the rest position, m.</summary>
    public float Travel = 0.14f;
    /// <summary>Static sag at rest, m (sets the spring rate from the load per wheel).</summary>
    public float Sag = 0.09f;
    /// <summary>Damping ratio of the suspension.</summary>
    public float Damping = 0.6f;
    /// <summary>Tyre grip coefficient (lateral and braking).</summary>
    public float Grip = 1.0f;
    /// <summary>Rolling resistance coefficient.</summary>
    public float Rolling = 0.015f;
    /// <summary>Mass the car hauler gains with a car on its deck, kg (0: no cargo).</summary>
    public float CargoMass;
    /// <summary>Boat trailer: the share of <see cref="Mass"/> that is its boat (gone while the boat is off), kg.</summary>
    public float LoadMass;

    /// <summary>Hitch: jackknife stop (yaw either way), pitch either way, roll either way, degrees.</summary>
    public float YawLimit = 80f, PitchLimit = 60f, RollLimit = 35f;

    /// <summary>A semi-trailer: its kingpin goes on a tractor's fifth wheel (the car model's "fifth_wheel" anchor), not on
    /// a tow ball; the model's hitch anchor is "kingpin".</summary>
    public bool FifthWheel;
    /// <summary>A shared texture of its own (embedded file; null: the trailers' shared one) and its baked occlusion.</summary>
    public string Texture, Occlusion;
    /// <summary>Landing gear: how far its feet come down when it stands unhitched, m (0: none). The model's "gear" part
    /// is drawn that much lower, and its feet ("gear_L" / "gear_R" anchors, retracted) hold the nose up.</summary>
    public float GearDrop;
}

/// <summary>The trailer table and the model check. Pure code (unit-tested).</summary>
public static class TrailerSpecs
{
    /// <summary>Bumped when a template's network layout or the trailers' state changes: part of each entry's layout in core's
    /// join hash. 2: the boat trailer's boat comes off (its own part and collider, the boat's place in the state).</summary>
    public const int Protocol = 2;

    /// <summary>The stock prefab the templates are copied from (Fusion table name).</summary>
    public const string Donor = "WinchablePlank";

    public static readonly TrailerSpec[] All =
    {
        new()
        {
            Kind = TrailerKind.Utility, RegistryKey = "trailer.utility", Guid = "ce3a28988b2847e890885ecd6c9b69fe",
            AssetPath = "Assets/Mods/OTHP/Trailer_Utility.prefab", MeshFile = "utility.mesh",
            Name = "Utility trailer", Description = "An open steel tub on one axle: light and easy to tow.",
            Mass = 320f, ComHeight = 0.48f, ComForward = 0.10f,
        },
        new()
        {
            Kind = TrailerKind.Camper, RegistryKey = "trailer.camper", Guid = "1b5ec0ff48da4289a6cce0b762361b7c",
            AssetPath = "Assets/Mods/OTHP/Trailer_Camper.prefab", MeshFile = "camper.mesh",
            Name = "Teardrop camper", Description = "A small camper on one axle: tall, so mind the side slopes.",
            Mass = 560f, ComHeight = 0.70f, ComForward = 0.12f, RollLimit = 30f,
        },
        new()
        {
            Kind = TrailerKind.Hauler, RegistryKey = "trailer.hauler", Guid = "5b15188839c140319960bba102dd1200",
            AssetPath = "Assets/Mods/OTHP/Trailer_Hauler.prefab", MeshFile = "hauler.mesh",
            Name = "Car hauler", Description = "A long flat deck on two axles. Can carry one of your own cars as a load.",
            Mass = 720f, ComHeight = 0.62f, ComForward = 0.20f, CargoMass = 800f, YawLimit = 75f,
        },
        new()
        {
            Kind = TrailerKind.Boat, RegistryKey = "trailer.boat", Guid = "be95e00dfc3141f2ae91b78619df4dc6",
            AssetPath = "Assets/Mods/OTHP/Trailer_Boat.prefab", MeshFile = "boat.mesh",
            Name = "Boat trailer", Description = "A galvanised trailer with a small runabout strapped on.",
            Mass = 640f, ComHeight = 0.78f, ComForward = 0.15f, LoadMass = 200f,
        },
        new()
        {
            Kind = TrailerKind.Semi, RegistryKey = "trailer.semi", Guid = "7d2f4c81a9e54b36b0c5e8a13f6d92b4",
            AssetPath = "Assets/Mods/OTHP/Trailer_Semi.prefab", MeshFile = "flatbed.mesh",
            Name = "Flatbed semi-trailer", Description = "A 40 ft flatbed on a tandem. Needs a semi truck's fifth wheel. Built for long paved roads.",
            // 4.1 t empty (40 ft steel combo flatbed), the centre of mass a little ahead of mid-deck and about frame height;
            // air ride. A fifth wheel lets the trailer pitch (room for the game's crests) and hardly roll.
            Mass = 4100f, ComHeight = 1.05f, ComForward = 4.2f, Travel = 0.12f, Sag = 0.08f, Damping = 0.7f, Rolling = 0.008f,
            YawLimit = 88f, PitchLimit = 32f, RollLimit = 8f,
            FifthWheel = true, Texture = "flatbed_albedo.png", Occlusion = "flatbed_ao.png", GearDrop = 0.44f,
        },
    };

    public static TrailerSpec Of(TrailerKind kind)
    {
        foreach (var s in All)
            if (s.Kind == kind) return s;
        return null;
    }

    public static bool IsKind(byte b) => b <= (byte)TrailerKind.Semi;

    /// <summary>The model anchor the trailer hitches by: "kingpin" for a semi-trailer, "hitch" for the others.</summary>
    public static string HitchAnchor(TrailerSpec s) => s != null && s.FifthWheel ? "kingpin" : "hitch";

    /// <summary>The car model anchor a semi-trailer's kingpin locks into.</summary>
    public const string FifthWheelAnchor = "fifth_wheel";

    /// <summary>What every peer's template must agree on beyond the guid (core's join hash).</summary>
    public static string Layout(TrailerSpec s) => $"v{Protocol}|{Donor}|{s.MeshFile}";

    /// <summary>Wheel anchors of a model: wheel_L1, wheel_R1, then wheel_L2, wheel_R2 when it has a second axle.</summary>
    public static List<string> WheelAnchors(Func<string, bool> has)
    {
        var l = new List<string>();
        for (int axle = 1; axle <= 4; axle++)
        {
            string left = "wheel_L" + axle, right = "wheel_R" + axle;
            if (!has(left) || !has(right)) break;
            l.Add(left);
            l.Add(right);
        }
        return l;
    }

    /// <summary>Null when the model has what a trailer needs (a body, a wheel, a tyre, a hitch, a wheel pair and
    /// convex colliders within Unity's 255-triangle limit); otherwise what is wrong.</summary>
    public static string CheckModel(OverTheHillPlus.Cars.MeshBlob blob)
    {
        if (blob == null) return "no model";
        var missing = new List<string>();
        if (blob.First(OverTheHillPlus.Cars.MeshPartKind.Body) == null) missing.Add("body");
        if (blob.First(OverTheHillPlus.Cars.MeshPartKind.Wheel) == null) missing.Add("wheel");
        if (blob.First(OverTheHillPlus.Cars.MeshPartKind.Tyre) == null) missing.Add("tyre");
        if (blob.Anchor("hitch") == null && blob.Anchor("kingpin") == null) missing.Add("hitch");
        if (WheelAnchors(n => blob.Anchor(n) != null).Count == 0) missing.Add("wheel_L1/wheel_R1");
        var colliders = blob.PartsOf(OverTheHillPlus.Cars.MeshPartKind.Collider);
        if (colliders.Count == 0) missing.Add("collider");
        if (missing.Count > 0) return "missing " + string.Join(", ", missing);
        foreach (var c in colliders)
        {
            int tris = c.Indices.Length / 3;
            if (tris > 255) return $"collider {c.Name} has {tris} triangles (convex colliders take at most 255)";
            if (c.VertexCount < 4) return $"collider {c.Name} is not a solid";
        }
        return null;
    }
}
