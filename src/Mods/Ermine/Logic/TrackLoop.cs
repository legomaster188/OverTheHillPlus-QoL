using System;
using System.Collections.Generic;

namespace OverTheHillPlus.Ermine;

/// <summary>A point in the track's side plane: <see cref="Z"/> along the sled (forward), <see cref="Y"/> up.</summary>
public readonly struct LoopPoint
{
    public readonly float Z, Y;
    public LoopPoint(float z, float y) { Z = z; Y = y; }
    public override string ToString() => $"({Z:0.###}, {Y:0.###})";
}

/// <summary>
/// The rubber track's loop as a closed polyline in the sled's side plane (the model's <c>trk_NNN</c> anchors, in order
/// round the loop: forward along the bottom run, round the front, back along the top and round the rear idler), and the
/// lugs spaced evenly round it. The lugs are moved round the loop by the track's travel, so the track turns with the
/// sled's speed (and spins with the wheel on a standing start). Pure (unit-tested).
/// </summary>
public sealed class TrackLoop
{
    private readonly LoopPoint[] _p;
    private readonly float[] _s;    // the distance round the loop at each point (the last entry: the whole length)

    public TrackLoop(IReadOnlyList<LoopPoint> points)
    {
        if (points == null || points.Count < 3) throw new ArgumentException("a track loop needs at least three points");
        _p = new LoopPoint[points.Count];
        for (int i = 0; i < points.Count; i++) _p[i] = points[i];
        _s = new float[_p.Length + 1];
        for (int i = 0; i < _p.Length; i++)
        {
            var a = _p[i];
            var b = _p[(i + 1) % _p.Length];
            _s[i + 1] = _s[i] + MathF.Sqrt((b.Z - a.Z) * (b.Z - a.Z) + (b.Y - a.Y) * (b.Y - a.Y));
        }
        if (!(Length > 0.1f)) throw new ArgumentException("the track loop has no length");
    }

    /// <summary>The loop's length, metres.</summary>
    public float Length => _s[_p.Length];

    public int Count => _p.Length;

    /// <summary>Position and unit tangent (the direction the belt runs) at distance <paramref name="s"/> round the loop
    /// (any value: it wraps).</summary>
    public void At(float s, out LoopPoint pos, out LoopPoint tangent)
    {
        float L = Length;
        s %= L;
        if (s < 0f) s += L;
        int lo = 0, hi = _p.Length - 1;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) / 2;
            if (_s[mid] <= s) lo = mid;
            else hi = mid - 1;
        }
        var a = _p[lo];
        var b = _p[(lo + 1) % _p.Length];
        float seg = _s[lo + 1] - _s[lo];
        float t = seg > 1e-6f ? (s - _s[lo]) / seg : 0f;
        pos = new LoopPoint(a.Z + (b.Z - a.Z) * t, a.Y + (b.Y - a.Y) * t);
        float dz = b.Z - a.Z, dy = b.Y - a.Y, n = MathF.Sqrt(dz * dz + dy * dy);
        tangent = n > 1e-6f ? new LoopPoint(dz / n, dy / n) : new LoopPoint(1f, 0f);
    }

    /// <summary>The tangent's angle in the side plane, radians (0 = running forward, π/2 = running up).</summary>
    public static float Angle(LoopPoint tangent) => MathF.Atan2(tangent.Y, tangent.Z);

    /// <summary>
    /// How many lugs fit round the loop at about <paramref name="pitch"/> metres apart (a whole number, so the first and
    /// the last lug are as far apart as any two), at least 3.
    /// </summary>
    public int LugCount(float pitch) => pitch > 0.01f ? Math.Max(3, (int)MathF.Round(Length / pitch)) : 3;

    /// <summary>The track's travel after <paramref name="dt"/> seconds at <paramref name="speed"/> m/s (the bottom run
    /// moves backwards relative to the sled when it drives forward, so the loop's distance grows the other way), wrapped
    /// into one lug spacing so it never grows without bound.</summary>
    public static float Advance(float travel, float speed, float dt, float spacing)
    {
        if (!float.IsFinite(speed) || !float.IsFinite(dt) || !(spacing > 0f)) return travel;
        float t = travel - speed * dt;
        t %= spacing;
        if (t < 0f) t += spacing;
        return t;
    }
}
