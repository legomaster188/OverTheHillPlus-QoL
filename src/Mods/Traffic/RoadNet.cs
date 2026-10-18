using System;
using System.Collections.Generic;
using System.Numerics;

namespace OverTheHillPlus.Traffic;

/// <summary>
/// The roads AI Traffic drives on: polylines resampled to an even spacing, with a cursor that follows a road in
/// either direction, joins another road at its end, and looks ahead for bends. Pure (System.Numerics, no game types),
/// so the tests compile this file on its own. Distances are horizontal (x/z); heights only come along.
/// </summary>
internal sealed class RoadNet
{
    public const float Spacing = 5f;

    public sealed class Road
    {
        public readonly Vector3[] P;
        public readonly float Length;
        public readonly string Source;

        public Road(Vector3[] points, string source)
        {
            P = points;
            Source = source;
            float len = 0f;
            for (int i = 1; i < points.Length; i++) len += Flat(points[i - 1], points[i]);
            Length = len;
        }
    }

    /// <summary>A place on a road and the way the car is going along it (+1 = towards the last point).</summary>
    public struct Cursor
    {
        public int Road;
        public int Index;
        public int Dir;

        public Cursor(int road, int index, int dir)
        {
            Road = road;
            Index = index;
            Dir = dir >= 0 ? 1 : -1;
        }
    }

    public readonly List<Road> Roads = new();
    public float TotalLength { get; private set; }
    public int PointCount { get; private set; }

    public static float Flat(Vector3 a, Vector3 b)
    {
        float dx = a.X - b.X, dz = a.Z - b.Z;
        return MathF.Sqrt(dx * dx + dz * dz);
    }

    public void Clear()
    {
        Roads.Clear();
        TotalLength = 0f;
        PointCount = 0;
    }

    /// <summary>Adds a road (resampled to <see cref="Spacing"/>); shorter than <paramref name="minLength"/> is skipped.</summary>
    public bool Add(IReadOnlyList<Vector3> points, float minLength, string source)
    {
        if (points == null || points.Count < 2) return false;
        var p = Resample(points, Spacing);
        if (p.Length < 2) return false;
        var road = new Road(p, source);
        if (road.Length < minLength) return false;
        Roads.Add(road);
        TotalLength += road.Length;
        PointCount += p.Length;
        return true;
    }

    /// <summary>
    /// Even spacing along the polyline (horizontal distance), keeping the first and the last point. Points that are
    /// not finite are dropped; repeated points are skipped.
    /// </summary>
    public static Vector3[] Resample(IReadOnlyList<Vector3> pts, float spacing)
    {
        var src = new List<Vector3>(pts.Count);
        foreach (var v in pts)
        {
            if (!float.IsFinite(v.X) || !float.IsFinite(v.Y) || !float.IsFinite(v.Z)) continue;
            if (src.Count > 0 && Flat(src[src.Count - 1], v) < 0.01f) continue;
            src.Add(v);
        }
        if (src.Count < 2) return src.ToArray();
        var outp = new List<Vector3> { src[0] };
        float carry = 0f; // distance walked since the last output point
        for (int i = 1; i < src.Count; i++)
        {
            var a = src[i - 1];
            var b = src[i];
            float seg = Flat(a, b);
            float at = spacing - carry; // distance along this segment of the next output point
            while (at <= seg)
            {
                outp.Add(Vector3.Lerp(a, b, at / seg));
                at += spacing;
            }
            carry = seg - (at - spacing);
        }
        var last = src[src.Count - 1];
        if (Flat(outp[outp.Count - 1], last) > spacing * 0.25f) outp.Add(last);
        else outp[outp.Count - 1] = last;
        return outp.ToArray();
    }

    // ---------------------------------------------------------------- queries

    /// <summary>The road point nearest to <paramref name="pos"/> (brute force: call it rarely).</summary>
    public bool Nearest(Vector3 pos, out Cursor at, out float dist)
    {
        at = default;
        dist = float.MaxValue;
        for (int r = 0; r < Roads.Count; r++)
        {
            var p = Roads[r].P;
            for (int i = 0; i < p.Length; i++)
            {
                float d = Flat(p[i], pos);
                if (d < dist)
                {
                    dist = d;
                    at = new Cursor(r, i, 1);
                }
            }
        }
        return dist < float.MaxValue;
    }

    public bool Valid(Cursor c) => c.Road >= 0 && c.Road < Roads.Count && c.Index >= 0 && c.Index < Roads[c.Road].P.Length;

    public Vector3 Point(Cursor c) => Roads[c.Road].P[Clamp(c.Index, Roads[c.Road].P.Length)];

    /// <summary>The road's heading at the cursor, in its direction of travel (horizontal, unit length).</summary>
    public Vector3 Heading(Cursor c)
    {
        var p = Roads[c.Road].P;
        int i = Clamp(c.Index, p.Length);
        int j = Clamp(i + c.Dir, p.Length);
        if (j == i) j = Clamp(i - c.Dir, p.Length);
        var d = j == i ? Vector3.UnitZ : (c.Dir > 0 == j > i ? p[j] - p[i] : p[i] - p[j]);
        d.Y = 0f;
        float len = d.Length();
        return len > 1e-4f ? d / len : Vector3.UnitZ;
    }

    /// <summary>Points left until the end of the road in the direction of travel.</summary>
    public int PointsLeft(Cursor c) => c.Dir > 0 ? Roads[c.Road].P.Length - 1 - c.Index : c.Index;

    /// <summary>The cursor <paramref name="metres"/> further along (stops at the road's end).</summary>
    public Cursor Ahead(Cursor c, float metres)
    {
        int steps = (int)MathF.Round(Math.Max(0f, metres) / Spacing);
        int n = Roads[c.Road].P.Length;
        return new Cursor(c.Road, Clamp(c.Index + c.Dir * steps, n), c.Dir);
    }

    /// <summary>
    /// Moves the cursor past the points the car at <paramref name="pos"/> has reached (or that are behind it). Returns
    /// false once the road has ended in the direction of travel.
    /// </summary>
    public bool Advance(ref Cursor c, Vector3 pos, float reach = 4f)
    {
        var p = Roads[c.Road].P;
        int guard = 0;
        while (guard++ < 64)
        {
            int next = c.Index + c.Dir;
            if (next < 0 || next >= p.Length) return false;
            float here = Flat(p[c.Index], pos);
            float there = Flat(p[next], pos);
            if (here <= reach || there < here) c.Index = next;
            else break;
        }
        return PointsLeft(c) > 0;
    }

    /// <summary>
    /// At (or near) a road's end: another road with a point within <paramref name="radius"/> of the end, leading
    /// away from it. Ends of other roads are preferred; a point in the middle of a road (a junction) goes either way.
    /// Never the same road back the way it came.
    /// </summary>
    public bool TryJoin(Cursor c, float radius, Random rng, out Cursor next)
    {
        next = c;
        var end = Roads[c.Road].P[c.Dir > 0 ? Roads[c.Road].P.Length - 1 : 0];
        var options = new List<Cursor>();
        var ends = new List<Cursor>();
        for (int r = 0; r < Roads.Count; r++)
        {
            if (r == c.Road) continue;
            var p = Roads[r].P;
            int best = -1;
            float bestD = radius;
            for (int i = 0; i < p.Length; i++)
            {
                float d = Flat(p[i], end);
                if (d <= bestD) { bestD = d; best = i; }
            }
            if (best < 0) continue;
            bool first = best <= 1, last = best >= p.Length - 2;
            if (first) ends.Add(new Cursor(r, best, 1));
            else if (last) ends.Add(new Cursor(r, best, -1));
            else
            {
                options.Add(new Cursor(r, best, 1));
                options.Add(new Cursor(r, best, -1));
            }
        }
        var pick = ends.Count > 0 ? ends : options;
        if (pick.Count == 0) return false;
        next = pick[rng.Next(pick.Count)];
        return true;
    }

    /// <summary>How much the road turns over the next <paramref name="metres"/> (sum of heading changes, degrees).</summary>
    public float TurnAngle(Cursor c, float metres)
    {
        var p = Roads[c.Road].P;
        int steps = Math.Max(2, (int)(metres / Spacing));
        float total = 0f;
        Vector3? prevDir = null;
        int i = c.Index;
        for (int k = 0; k < steps; k++)
        {
            int j = i + c.Dir;
            if (j < 0 || j >= p.Length) break;
            var d = p[j] - p[i];
            d.Y = 0f;
            float len = d.Length();
            if (len > 1e-4f)
            {
                d /= len;
                if (prevDir.HasValue)
                {
                    float dot = Math.Clamp(Vector3.Dot(prevDir.Value, d), -1f, 1f);
                    total += MathF.Acos(dot) * 180f / MathF.PI;
                }
                prevDir = d;
            }
            i = j;
        }
        return total;
    }

    /// <summary><paramref name="count"/> random places on the roads (weighted by road length), each with a random direction.</summary>
    public List<Cursor> RandomPlaces(Random rng, int count)
    {
        var list = new List<Cursor>(count);
        if (Roads.Count == 0 || PointCount == 0) return list;
        for (int k = 0; k < count; k++)
        {
            int pick = rng.Next(PointCount);
            for (int r = 0; r < Roads.Count; r++)
            {
                int n = Roads[r].P.Length;
                if (pick < n)
                {
                    list.Add(new Cursor(r, pick, rng.Next(2) == 0 ? 1 : -1));
                    break;
                }
                pick -= n;
            }
        }
        return list;
    }

    private static int Clamp(int i, int n) => i < 0 ? 0 : i >= n ? n - 1 : i;
}

/// <summary>
/// Roads learned from where players drive: each player's path as a polyline (a point every few metres while moving),
/// cut where the player jumped (respawn, teleport). Driven paths are known to be drivable, so AI Traffic uses them
/// when the map has no road splines. Pure.
/// </summary>
internal sealed class LearnedTrails
{
    public float MinStep = 6f, MaxJump = 30f, MinSpeed = 3f;
    public int MaxPoints = 20000;

    private readonly Dictionary<int, List<Vector3>> _open = new();
    private readonly List<Vector3[]> _done = new();

    /// <summary>Changes whenever a trail grows, so the road network knows when to rebuild.</summary>
    public int Version { get; private set; }
    public int Points { get; private set; }

    public void Sample(int who, Vector3 pos, float speed)
    {
        if (!float.IsFinite(pos.X) || !float.IsFinite(pos.Y) || !float.IsFinite(pos.Z)) return;
        _open.TryGetValue(who, out var trail);
        if (trail != null && trail.Count > 0)
        {
            float d = RoadNet.Flat(trail[trail.Count - 1], pos);
            if (d > MaxJump) { Close(who); trail = null; }
            else if (d < MinStep || Math.Abs(speed) < MinSpeed) return;
        }
        else if (Math.Abs(speed) < MinSpeed) return;
        if (Points >= MaxPoints) return;
        if (trail == null) _open[who] = trail = new List<Vector3>();
        trail.Add(pos);
        Points++;
        Version++;
    }

    /// <summary>Ends a player's current trail (they left, or jumped).</summary>
    public void Close(int who)
    {
        if (!_open.TryGetValue(who, out var trail)) return;
        _open.Remove(who);
        if (trail.Count >= 2) _done.Add(trail.ToArray());
        else Points -= trail.Count;
        Version++;
    }

    public void Clear()
    {
        _open.Clear();
        _done.Clear();
        Points = 0;
        Version++;
    }

    /// <summary>Every trail so far (finished ones and the ones still being driven).</summary>
    public List<IReadOnlyList<Vector3>> All()
    {
        var all = new List<IReadOnlyList<Vector3>>(_done.Count + _open.Count);
        foreach (var t in _done) all.Add(t);
        foreach (var t in _open.Values) if (t.Count >= 2) all.Add(t.ToArray());
        return all;
    }
}
