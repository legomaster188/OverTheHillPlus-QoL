using System;
using System.Collections.Generic;
using OverTheHillPlus.Cars;

namespace OverTheHillPlus.Jackal;

/// <summary>One link of the running gear: a moving part that turns about its pivot so its end meets a point that follows a
/// wheel (model space, Unity axes).</summary>
public sealed class JackalLink
{
    public string Part;
    public MeshVec3 Pivot, End;
    public float Length => Dist(Pivot, End);

    internal static float Dist(MeshVec3 a, MeshVec3 b)
    {
        float dx = a.X - b.X, dy = a.Y - b.Y, dz = a.Z - b.Z;
        return MathF.Sqrt(dx * dx + dy * dy + dz * dz);
    }
}

/// <summary>One side's running gear, from the model's anchors. Part and anchor names are the contract with
/// Liveries/tools/jackal/jackal_susp.py: pivot_&lt;name&gt; and end_&lt;name&gt; for the part mv_&lt;name&gt;.</summary>
public sealed class JackalSide
{
    /// <summary>"L" or "R".</summary>
    public string Side;
    public MeshVec3 FrontHub, RearHub;
    public string Knuckle;
    public JackalLink UpperArm, LowerArm, TieRod, FrontCoil, RearArm, RearCoil, HalfShaft;
}

/// <summary>
/// What the Jackal's own code needs from its model: each side's running gear and the steering wheel. <see cref="Read"/> says
/// what is missing. Pure (unit-tested against the shipped model).
/// </summary>
public sealed class JackalGeometry
{
    public const string SteerPart = "mv_steer";
    public JackalSide Left, Right;
    public MeshVec3 SteerPivot, SteerAxisEnd;

    /// <summary>Metres the rack moves at full lock, and the steering wheel's turns for a degree of road wheel.</summary>
    public const float RackTravel = 0.055f, WheelRatio = 9f;

    public static string Part(string what, string side) => "mv_" + what + "_" + side;

    /// <summary>The geometry, or null with <paramref name="problem"/> saying what the model lacks.</summary>
    public static JackalGeometry Read(MeshBlob blob, out string problem)
    {
        problem = null;
        if (blob == null) { problem = "no model"; return null; }
        var missing = new List<string>();
        MeshVec3 A(string name)
        {
            var a = blob.Anchor(name);
            if (a == null) { missing.Add(name); return default; }
            return a.Position;
        }
        JackalLink Link(string what, string side)
        {
            string part = Part(what, side);
            if (blob.Part(part) == null) missing.Add(part);
            return new JackalLink { Part = part, Pivot = A("pivot_" + what + "_" + side), End = A("end_" + what + "_" + side) };
        }
        JackalSide SideOf(string s)
        {
            string k = Part("knuckle", s);
            if (blob.Part(k) == null) missing.Add(k);
            return new JackalSide
            {
                Side = s, FrontHub = A("wheel_F" + s), RearHub = A("wheel_R" + s), Knuckle = k,
                UpperArm = Link("fup", s), LowerArm = Link("flo", s), TieRod = Link("tie", s), FrontCoil = Link("fcoil", s),
                RearArm = Link("rarm", s), RearCoil = Link("rcoil", s), HalfShaft = Link("axle", s),
            };
        }
        var g = new JackalGeometry { Left = SideOf("L"), Right = SideOf("R"), SteerPivot = A("pivot_steer"), SteerAxisEnd = A("end_steer") };
        if (blob.Part(SteerPart) == null) missing.Add(SteerPart);
        if (missing.Count > 0)
        {
            problem = "its model lacks " + string.Join(", ", missing);
            return null;
        }
        return g;
    }

    /// <summary>How much a part stretches along its own length to reach its end: 1 at rest. Never less than half or more than
    /// twice (a wheel that is thrown about by a bad frame must not make a part fly apart).</summary>
    public static float Stretch(float restLength, float length)
    {
        if (!(restLength > 1e-4f) || !float.IsFinite(length)) return 1f;
        return Math.Clamp(length / restLength, 0.5f, 2f);
    }

    /// <summary>A wheel's steering angle (degrees, + to the right as Unity turns about +y) from its axle's direction in the car's
    /// frame: spin about the axle doesn't change it, and either wheel's axle may point left or right.</summary>
    public static float SteerFromAxle(float axleX, float axleZ)
    {
        if (!float.IsFinite(axleX) || !float.IsFinite(axleZ)) return 0f;
        float a = MathF.Atan2(-axleZ, axleX) * (180f / MathF.PI);
        if (a > 90f) a -= 180f;
        else if (a < -90f) a += 180f;
        return a;
    }
}

/// <summary>The ground under a Jackal's tyre, as the game's material labels sort it.</summary>
public enum JackalGround : byte { Hard = 0, Trail = 1, Mud = 2, Snow = 3, Sand = 4, Rock = 5, Ice = 6 }

/// <summary>
/// How a sand rail's tyres work: light, on a ribbed front that cuts a line and paddles that scoop. The grip of each axle on each
/// ground on top of the game's tyre, what the rail's low weight on wide tyres keeps it from sinking (Terrain+'s flotation), and
/// what the paddles give back of the game's soft-ground drag. Pure (unit-tested).
/// </summary>
public static class JackalTyres
{
    /// <summary>Share of soft-ground sinking and drag it floats over: 640 kg on 28 cm paddles is a fifth of a loaded 4x4's
    /// ground pressure, far from a snowmobile's.</summary>
    public const float Flotation = 0.45f;

    /// <summary>The grip factor of one wheel (paddles behind, ribs in front). The paddles dig into anything loose and skate on
    /// anything hard; the ribs steer on sand and snow but are nothing special.</summary>
    public static float Grip(bool rear, JackalGround g) => rear
        ? g switch
        {
            JackalGround.Hard => 0.84f, JackalGround.Trail => 1.00f, JackalGround.Mud => 1.12f, JackalGround.Snow => 1.25f,
            JackalGround.Sand => 1.35f, JackalGround.Rock => 0.82f, JackalGround.Ice => 0.78f, _ => 1f,
        }
        : g switch
        {
            JackalGround.Hard => 0.97f, JackalGround.Trail => 1.00f, JackalGround.Mud => 0.92f, JackalGround.Snow => 1.04f,
            JackalGround.Sand => 1.08f, JackalGround.Rock => 0.95f, JackalGround.Ice => 0.86f, _ => 1f,
        };

    /// <summary>The rear radius that goes with this front radius.</summary>
    public static float RearFor(float front) => front * (JackalDefinition.RearTyre / JackalDefinition.FrontTyre);

    /// <summary>The rear wheel has lost most of its extra size against the front (something gave all four wheels one radius).</summary>
    public static bool RearLost(float front, float rear) =>
        front > 0f && rear > 0f && rear - front < 0.5f * (RearFor(front) - front);

    /// <summary>The push (m/s²) the paddles give back on soft ground: half the game's drag (its material's drag fraction of g),
    /// with the throttle open and the car moving, never over 1.2 m/s².</summary>
    public static float SoftPush(float gameDragG, float throttle, float speed)
    {
        if (!(gameDragG > 0f) || !(throttle > 0.05f) || !(speed >= 0.3f)) return 0f;
        return Math.Min(1.2f, 9.81f * gameDragG * 0.5f * Math.Min(1f, throttle));
    }
}

/// <summary>Where the Jackal's weight sits, and the springs that carry it (a rear-engine rail is 42/58 front to rear; springs
/// follow the load so both axles sag the same and the car stands level).</summary>
public static class JackalBalance
{
    /// <summary>Share of the weight on the rear axle.</summary>
    public const float RearShare = 0.58f;

    /// <summary>The share of the wheelbase ahead of the rear axle where the centre of mass goes.</summary>
    public static float ComFromRear => 1f - RearShare;

    public static float SpringFactor(bool rear) => (rear ? RearShare : 1f - RearShare) / 0.5f;
}
