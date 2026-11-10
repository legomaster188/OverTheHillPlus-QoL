using System;
using System.Collections.Generic;
using OverTheHillPlus.Cars;

namespace OverTheHillPlus.Ermine;

/// <summary>One side's front suspension, from the model's anchors (model space, Unity axes).</summary>
public sealed class SkiSide
{
    /// <summary>"L" or "R".</summary>
    public string Side;
    public MeshVec3 SkiPivot, SpindlePivot, Tip, Tail;
    public MeshVec3 UpperPivot, UpperBall, LowerPivot, LowerBall;
    public MeshVec3 ShockTop, ShockBottom;

    public float UpperLength => Dist(UpperPivot, UpperBall);
    public float LowerLength => Dist(LowerPivot, LowerBall);
    /// <summary>The ski's probe span (tip to tail along the ski), metres.</summary>
    public float Span => Dist(Tip, Tail);

    internal static float Dist(MeshVec3 a, MeshVec3 b)
    {
        float dx = a.X - b.X, dy = a.Y - b.Y, dz = a.Z - b.Z;
        return MathF.Sqrt(dx * dx + dy * dy + dz * dz);
    }
}

/// <summary>
/// What the Ermine's own code needs from its model (Liveries/tools/ermine/build_ermine.py writes it): the front
/// suspension of each side, the handlebars' steering post, the track's loop and lug, and the track's wheels. The anchor
/// and part names are the contract between the builder and this mod; <see cref="Read"/> says what is missing. Pure
/// (unit-tested against the shipped model).
/// </summary>
public sealed class ErmineGeometry
{
    public const string LugPart = "mv_lug", BarsPart = "mv_bars", IdlerPrefix = "mv_idler_";
    public const string TrackPrefix = "trk_";
    /// <summary>The lugs' spacing round the loop, metres (a real 15 in track has its lugs every 2.86 in).</summary>
    public const float LugPitch = 0.0726f;

    public SkiSide Left, Right;
    public MeshVec3 BarsPivot, PostBase;
    public TrackLoop Loop;
    public int Lugs;
    /// <summary>The track's wheels (part name, axle, radius).</summary>
    public readonly List<(string Part, MeshVec3 Axle, float Radius)> Idlers = new();

    public static string Part(string what, string side) => "mv_" + what + "_" + side;

    /// <summary>The geometry, or null with <paramref name="problem"/> saying what the model lacks.</summary>
    public static ErmineGeometry Read(MeshBlob blob, out string problem)
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
        SkiSide SideOf(string s) => new()
        {
            Side = s,
            SkiPivot = A("pivot_ski_" + s), SpindlePivot = A("pivot_spindle_" + s), Tip = A("skitip_" + s), Tail = A("skitail_" + s),
            UpperPivot = A("pivot_arm_up_" + s), UpperBall = A("ball_arm_up_" + s),
            LowerPivot = A("pivot_arm_lo_" + s), LowerBall = A("ball_arm_lo_" + s),
            ShockTop = A("pivot_shock_" + s), ShockBottom = A("end_shock_" + s),
        };
        var g = new ErmineGeometry { Left = SideOf("L"), Right = SideOf("R"), BarsPivot = A("pivot_bars"), PostBase = A("post_base") };
        foreach (var part in new[] { "ski", "spindle", "arm_up", "arm_lo", "shock" })
            foreach (var s in new[] { "L", "R" })
                if (blob.Part(Part(part, s)) == null) missing.Add(Part(part, s));
        if (blob.Part(LugPart) == null) missing.Add(LugPart);
        if (blob.Part(BarsPart) == null) missing.Add(BarsPart);
        var pts = new List<LoopPoint>();
        for (int i = 0; i < 1000; i++)
        {
            var a = blob.Anchor(TrackPrefix + i.ToString("000", System.Globalization.CultureInfo.InvariantCulture));
            if (a == null) break;
            pts.Add(new LoopPoint(a.Position.Z, a.Position.Y));
        }
        if (pts.Count < 8) missing.Add($"the track loop ({TrackPrefix}000...: {pts.Count} points)");
        foreach (var p in blob.MovingParts())
        {
            if (!p.Name.StartsWith(IdlerPrefix, StringComparison.Ordinal)) continue;
            var axle = blob.Anchor(MeshBlob.PivotOf(p.Name));
            if (axle == null) { missing.Add(MeshBlob.PivotOf(p.Name)); continue; }
            p.Bounds(out var lo, out var hi);
            float r = Math.Max(hi.Y - lo.Y, hi.Z - lo.Z) * 0.5f;
            g.Idlers.Add((p.Name, axle.Position, Math.Max(0.03f, r)));
        }
        if (missing.Count > 0)
        {
            problem = "its model lacks " + string.Join(", ", missing);
            return null;
        }
        g.Loop = new TrackLoop(pts);
        g.Lugs = g.Loop.LugCount(LugPitch);
        return g;
    }

    /// <summary>The distance round the loop of the anchor closest to the lug part's own place (its pivot): where the
    /// modelled lug sits at rest, so every copy is turned relative to it.</summary>
    public float LugRestS(MeshVec3 lugPivot)
    {
        float best = float.MaxValue, bestS = 0f;
        int n = 400;
        for (int k = 0; k < n; k++)
        {
            float s = Loop.Length * k / n;
            Loop.At(s, out var p, out _);
            float d = (p.Z - lugPivot.Z) * (p.Z - lugPivot.Z) + (p.Y - lugPivot.Y) * (p.Y - lugPivot.Y);
            if (d < best) { best = d; bestS = s; }
        }
        return bestS;
    }
}
