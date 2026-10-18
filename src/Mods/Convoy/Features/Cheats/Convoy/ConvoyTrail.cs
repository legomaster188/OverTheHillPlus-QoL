using System.Collections.Generic;
using UnityEngine;

namespace OverTheHillPlus.Features.Cheats.Convoy;

/// <summary>
/// The leader's breadcrumb trail: a polyline of the ground the leader actually drove over, with the leader's speed at
/// each point. The AI cars follow this line (it is known to be drivable) and use the recorded speed as a hint of how
/// fast that stretch can be taken. Distances along the trail ("S", metres) only ever grow, so cars keep their progress
/// as an S value and nothing breaks when old points are pruned from the front.
/// </summary>
internal sealed class ConvoyTrail
{
    internal struct Point
    {
        public Vector3 Pos;
        public Vector3 Fwd;   // horizontal unit direction of travel at this point
        public float S;       // distance along the trail
        public float Speed;   // leader speed when it passed here (m/s)
    }

    private const float Step = 1.5f;          // metres between recorded points
    private const float TeleportJump = 35f;   // a jump this large between two frames is a teleport, not driving
    private const int MaxPoints = 6000;       // ~9 km of trail

    private readonly List<Point> _pts = new();
    private Vector3 _lastSeen;
    private bool _haveSeen;

    public int Count => _pts.Count;
    public Point this[int i] => _pts[i];
    public float StartS => _pts.Count > 0 ? _pts[0].S : 0f;
    public float EndS => _pts.Count > 0 ? _pts[_pts.Count - 1].S : 0f;
    public bool Usable => _pts.Count >= 2;

    /// <summary>Bumped whenever the trail is thrown away (teleport, new leader): cars must re-find it.</summary>
    public int Generation { get; private set; }

    public void Clear()
    {
        _pts.Clear();
        _haveSeen = false;
        Generation++;
    }

    /// <summary>Starts a fresh trail through the given points (oldest first), e.g. the spawn line behind the leader.</summary>
    public void Seed(IList<Vector3> points, float speed)
    {
        Clear();
        for (int i = 0; i < points.Count; i++) Append(points[i], speed, force: true);
        if (points.Count > 0)
        {
            _lastSeen = points[points.Count - 1];
            _haveSeen = true;
        }
    }

    /// <summary>
    /// Called every frame with the leader's position. Returns true when the leader jumped (teleport, respawn): the
    /// trail has been restarted from the new spot and cars have to regroup.
    /// </summary>
    public bool Record(Vector3 pos, float speed)
    {
        if (!float.IsFinite(pos.x) || !float.IsFinite(pos.y) || !float.IsFinite(pos.z)) return false;
        bool jumped = _haveSeen && (pos - _lastSeen).sqrMagnitude > TeleportJump * TeleportJump;
        _lastSeen = pos;
        _haveSeen = true;
        if (jumped)
        {
            Clear();
            Append(pos, Mathf.Abs(speed), force: true);
            _lastSeen = pos;
            _haveSeen = true;
            return true;
        }
        if (_pts.Count == 0) { Append(pos, Mathf.Abs(speed), force: true); return false; }

        var last = _pts[_pts.Count - 1];
        if (speed < -0.3f)
        {
            // Backing up: if the leader reverses over its own recent trail, cut the trail back to where it is now so
            // the cars don't drive into the dead end it backed out of.
            Rewind(pos);
            return false;
        }
        float d = Flat(pos - last.Pos).magnitude;
        if (d >= Step) Append(pos, Mathf.Abs(speed), force: false);
        return false;
    }

    private void Append(Vector3 pos, float speed, bool force)
    {
        if (_pts.Count == 0)
        {
            _pts.Add(new Point { Pos = pos, Fwd = Vector3.forward, S = 0f, Speed = speed });
            return;
        }
        var last = _pts[_pts.Count - 1];
        var delta = Flat(pos - last.Pos);
        float d = delta.magnitude;
        if (d < 0.05f) return; // same spot (force only matters for the very first point)
        var fwd = delta / d;
        if (_pts.Count == 1)
        {
            last.Fwd = fwd;
            _pts[0] = last;
        }
        // Long straight seeds are split so look-ahead and curvature sampling keep working.
        int pieces = Mathf.Max(1, Mathf.CeilToInt(d / (Step * 2f)));
        for (int k = 1; k <= pieces; k++)
        {
            float f = k / (float)pieces;
            _pts.Add(new Point
            {
                Pos = Vector3.Lerp(last.Pos, pos, f),
                Fwd = fwd,
                S = last.S + d * f,
                Speed = Mathf.Lerp(last.Speed, speed, f),
            });
        }
        if (_pts.Count > MaxPoints) _pts.RemoveRange(0, _pts.Count - MaxPoints);
    }

    private void Rewind(Vector3 pos)
    {
        if (_pts.Count < 4) return;
        float endS = EndS;
        int best = -1;
        float bestD = 2.0f;
        for (int i = _pts.Count - 2; i >= 0; i--)
        {
            if (endS - _pts[i].S > 40f) break;
            float dd = Flat(_pts[i].Pos - pos).magnitude;
            if (dd < bestD) { bestD = dd; best = i; }
        }
        if (best >= 1 && best < _pts.Count - 1) _pts.RemoveRange(best + 1, _pts.Count - best - 1);
    }

    /// <summary>Drops points far behind every car that still needs them (keeps at least a short tail).</summary>
    public void PruneBefore(float s)
    {
        if (_pts.Count < 400) return;
        int i = IndexAtOrBefore(s);
        if (i > 50) _pts.RemoveRange(0, i - 10);
    }

    /// <summary>Index of the last point with S &lt;= s (0 if s is before the start).</summary>
    public int IndexAtOrBefore(float s)
    {
        int lo = 0, hi = _pts.Count - 1;
        if (hi < 0) return 0;
        if (s <= _pts[0].S) return 0;
        if (s >= _pts[hi].S) return hi;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) >> 1;
            if (_pts[mid].S <= s) lo = mid; else hi = mid - 1;
        }
        return lo;
    }

    /// <summary>Position, direction and recorded speed at distance s (clamped to the trail).</summary>
    public Vector3 PointAt(float s, out Vector3 fwd, out float speed)
    {
        fwd = Vector3.forward;
        speed = 0f;
        if (_pts.Count == 0) return Vector3.zero;
        if (_pts.Count == 1) { fwd = _pts[0].Fwd; speed = _pts[0].Speed; return _pts[0].Pos; }
        int i = IndexAtOrBefore(s);
        if (i >= _pts.Count - 1)
        {
            var e = _pts[_pts.Count - 1];
            fwd = e.Fwd;
            speed = e.Speed;
            return e.Pos;
        }
        var a = _pts[i];
        var b = _pts[i + 1];
        float span = Mathf.Max(0.001f, b.S - a.S);
        float t = Mathf.Clamp01((s - a.S) / span);
        fwd = b.Fwd;
        speed = Mathf.Lerp(a.Speed, b.Speed, t);
        return Vector3.Lerp(a.Pos, b.Pos, t);
    }

    public Vector3 PointAt(float s) => PointAt(s, out _, out _);

    /// <summary>
    /// Closest point of the trail to p, searched in a window around the previous progress (so a trail that crosses
    /// itself doesn't make a car jump ahead). Returns false when the trail is empty.
    /// </summary>
    public bool Project(Vector3 p, float hintS, float back, float ahead, out float s, out float dist)
    {
        s = hintS;
        dist = float.MaxValue;
        if (_pts.Count < 2) return false;
        int i0 = IndexAtOrBefore(hintS - back);
        int i1 = IndexAtOrBefore(hintS + ahead) + 1;
        if (i1 > _pts.Count - 1) i1 = _pts.Count - 1;
        var q = Flat(p);
        for (int i = i0; i < i1; i++)
        {
            var a = Flat(_pts[i].Pos);
            var b = Flat(_pts[i + 1].Pos);
            var ab = b - a;
            float len2 = ab.sqrMagnitude;
            float t = len2 < 1e-6f ? 0f : Mathf.Clamp01(Vector3.Dot(q - a, ab) / len2);
            var c = a + ab * t;
            float d = (q - c).magnitude;
            // Vertical distance counts a bit too (bridges over the trail, switchbacks stacked on a slope).
            float dy = Mathf.Abs(p.y - Mathf.Lerp(_pts[i].Pos.y, _pts[i + 1].Pos.y, t));
            d += Mathf.Max(0f, dy - 2f);
            if (d < dist)
            {
                dist = d;
                s = Mathf.Lerp(_pts[i].S, _pts[i + 1].S, t);
            }
        }
        return true;
    }

    /// <summary>Closest point anywhere on the trail (coarse full scan; call rarely).</summary>
    public bool ProjectGlobal(Vector3 p, out float s, out float dist)
    {
        s = 0f;
        dist = float.MaxValue;
        if (_pts.Count < 2) return false;
        int stride = _pts.Count > 800 ? 3 : 1;
        int bestI = 0;
        for (int i = 0; i < _pts.Count; i += stride)
        {
            float d = Flat(_pts[i].Pos - p).magnitude + Mathf.Max(0f, Mathf.Abs(_pts[i].Pos.y - p.y) - 2f);
            if (d < dist) { dist = d; bestI = i; }
        }
        float hint = _pts[bestI].S;
        return Project(p, hint, 6f, 6f, out s, out dist);
    }

    /// <summary>Largest change of direction (degrees) between s0 and s1, sampled every 2 m.</summary>
    public float TurnAngle(float s0, float s1)
    {
        if (_pts.Count < 3) return 0f;
        PointAt(s0, out var f0, out _);
        float max = 0f;
        for (float s = s0 + 2f; s <= s1; s += 2f)
        {
            PointAt(s, out var f, out _);
            float a = Vector3.Angle(f0, f);
            if (a > max) max = a;
        }
        return max;
    }

    /// <summary>Lowest leader speed recorded between s0 and s1.</summary>
    public float MinSpeed(float s0, float s1)
    {
        if (_pts.Count == 0) return 0f;
        int i0 = IndexAtOrBefore(s0), i1 = IndexAtOrBefore(s1);
        float min = float.MaxValue;
        for (int i = i0; i <= i1 && i < _pts.Count; i++) if (_pts[i].Speed < min) min = _pts[i].Speed;
        return min == float.MaxValue ? 0f : min;
    }

    internal static Vector3 Flat(Vector3 v) => new(v.x, 0f, v.z);
}
