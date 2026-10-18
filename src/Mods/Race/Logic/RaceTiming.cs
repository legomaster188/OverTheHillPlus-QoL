using System;
using System.Collections.Generic;
using System.Globalization;

namespace OverTheHillPlus.Race;

/// <summary>A position (pure; the game's Vector3 converts at the edges).</summary>
internal readonly struct RaceVec
{
    public readonly float X, Y, Z;
    public RaceVec(float x, float y, float z) { X = x; Y = y; Z = z; }

    public static float Distance(RaceVec a, RaceVec b)
    {
        float dx = a.X - b.X, dy = a.Y - b.Y, dz = a.Z - b.Z;
        return MathF.Sqrt(dx * dx + dy * dy + dz * dz);
    }
}

/// <summary>The rules a race runs by (the host's settings).</summary>
internal sealed class RaceRules
{
    /// <summary>Everyone still racing this long after GO is out (time limit).</summary>
    public double TimeLimitSeconds = 900;
    /// <summary>After the first racer finishes, the others have this long.</summary>
    public double FinishWindowSeconds = 90;
    /// <summary>A move this long between two samples, at more than <see cref="JumpSpeed"/>, is a jump (not driving).</summary>
    public float JumpMeters = 40f;
    public float JumpSpeed = 150f;
    /// <summary>At GO a racer must be this close to the start (flat metres; 0 = no check), so driving off during the
    /// countdown doesn't pay.</summary>
    public float StartZoneMeters = 50f;
}

internal static class RaceGeometry
{
    /// <summary>Gates are circles on the ground; a car this far above or below the gate's centre still counts.</summary>
    public static float HeightTolerance(RaceGate g) => Math.Max(10f, g.Radius);

    /// <summary>
    /// True when the move from <paramref name="a"/> to <paramref name="b"/> passes through the gate (flat circle test,
    /// then the height at that point). <paramref name="s"/> is how far along the move (0..1) it first entered.
    /// </summary>
    public static bool Crossing(RaceVec a, RaceVec b, RaceGate g, out float s)
    {
        s = 0f;
        float r = g.Radius;
        float ax = a.X - g.X, az = a.Z - g.Z;
        float dx = b.X - a.X, dz = b.Z - a.Z;
        float c = ax * ax + az * az - r * r;
        if (c <= 0f) return HeightOk(a, b, g, 0f);
        float qa = dx * dx + dz * dz;
        if (qa < 1e-8f) return false;
        float qb = 2f * (ax * dx + az * dz);
        float disc = qb * qb - 4f * qa * c;
        if (disc < 0f) return false;
        float t = (-qb - MathF.Sqrt(disc)) / (2f * qa);
        if (t < 0f || t > 1f) return false;
        s = t;
        return HeightOk(a, b, g, t);
    }

    private static bool HeightOk(RaceVec a, RaceVec b, RaceGate g, float t)
    {
        float y = a.Y + (b.Y - a.Y) * t;
        return Math.Abs(y - g.Y) <= HeightTolerance(g);
    }
}

internal enum RacerState : byte { Racing = 0, Finished = 1, Dnf = 2 }

internal enum DnfReason : byte { None = 0, GaveUp = 1, Left = 2, TimeLimit = 3, FinishWindow = 4, Jumped = 5, Stopped = 6, NotAtStart = 7 }

/// <summary>One gate pass.</summary>
internal readonly struct GateHit
{
    public readonly int Seq, Gate, Lap;
    public readonly long ElapsedMs;
    public readonly bool LapEnd, Finish;

    public GateHit(int seq, int gate, int lap, long elapsedMs, bool lapEnd, bool finish)
    {
        Seq = seq; Gate = gate; Lap = lap; ElapsedMs = elapsedMs; LapEnd = lapEnd; Finish = finish;
    }
}

/// <summary>
/// One racer's run (pure): fed with car positions over time, it passes the gates in order, records the split at every
/// gate (time since GO, interpolated between samples so frame rate doesn't matter), and finishes or drops out.
/// Samples before GO only remember the position (creeping over a line in the countdown counts for nothing).
/// </summary>
internal sealed class RacerRun
{
    private readonly RaceTrack _track;
    private readonly RaceRules _rules;
    private bool _hasLast, _startChecked;
    private RaceVec _last;
    private double _lastTime;

    public RacerRun(RaceTrack track, double goTime, RaceRules rules)
    {
        _track = track;
        _rules = rules ?? new RaceRules();
        GoTime = goTime;
    }

    public double GoTime { get; }
    public int NextSeq { get; private set; }
    public RacerState State { get; private set; }
    public DnfReason Reason { get; private set; }
    public long FinishMs { get; private set; }
    /// <summary>Time since GO at each gate passed (ms).</summary>
    public List<long> Splits { get; } = new();
    public long LastSplitMs => Splits.Count > 0 ? Splits[Splits.Count - 1] : 0;

    /// <summary>Feeds a position; gate passes are added to <paramref name="hits"/>.</summary>
    public void Sample(double now, RaceVec pos, List<GateHit> hits)
    {
        if (State != RacerState.Racing) return;
        if (!float.IsFinite(pos.X) || !float.IsFinite(pos.Y) || !float.IsFinite(pos.Z)) return;
        if (!_hasLast || now <= _lastTime)
        {
            _hasLast = true;
            _last = pos;
            _lastTime = now;
            return;
        }
        var from = _last;
        double fromTime = _lastTime;
        _last = pos;
        _lastTime = now;
        if (now < GoTime) return;

        if (!_startChecked)
        {
            _startChecked = true;
            // Where the car was at GO (between the last countdown sample and this one).
            var atGo = from;
            if (fromTime < GoTime && now > fromTime)
            {
                float k = (float)((GoTime - fromTime) / (now - fromTime));
                atGo = new RaceVec(from.X + (pos.X - from.X) * k, from.Y + (pos.Y - from.Y) * k, from.Z + (pos.Z - from.Z) * k);
            }
            var start = _track.Gates[0];
            float dx = atGo.X - start.X, dz = atGo.Z - start.Z;
            float zone = Math.Max(_rules.StartZoneMeters, start.Radius);
            if (_rules.StartZoneMeters > 0f && dx * dx + dz * dz > zone * zone)
            {
                Dnf(DnfReason.NotAtStart);
                return;
            }
        }

        float moved = RaceVec.Distance(from, pos);
        double dt = now - fromTime;
        if (moved > _rules.JumpMeters && moved / Math.Max(dt, 1e-3) > _rules.JumpSpeed)
        {
            Dnf(DnfReason.Jumped);
            return;
        }

        // Several gates can sit within one move (close gates, a long frame): check them in order.
        var a = from;
        double aTime = Math.Max(fromTime, GoTime);
        for (int guard = 0; guard < 4 && State == RacerState.Racing; guard++)
        {
            var gate = _track.Gates[_track.GateAt(NextSeq)];
            if (!RaceGeometry.Crossing(a, pos, gate, out float s)) break;
            double at = aTime + (now - aTime) * s;
            long ms = Math.Max(0L, (long)Math.Round((at - GoTime) * 1000.0));
            int seq = NextSeq;
            bool finish = seq + 1 >= _track.SequenceLength;
            Splits.Add(ms);
            hits?.Add(new GateHit(seq, _track.GateAt(seq), _track.LapOf(seq), ms, _track.IsLapEnd(seq), finish));
            NextSeq++;
            if (finish)
            {
                State = RacerState.Finished;
                FinishMs = ms;
                break;
            }
            // Continue from just past the entry point, so the next gate is only counted further along the move.
            float k = Math.Min(1f, s + 1e-4f);
            a = new RaceVec(a.X + (pos.X - a.X) * k, a.Y + (pos.Y - a.Y) * k, a.Z + (pos.Z - a.Z) * k);
            aTime = at;
        }
    }

    public void Dnf(DnfReason reason)
    {
        if (State != RacerState.Racing) return;
        State = RacerState.Dnf;
        Reason = reason;
    }
}

/// <summary>Split deltas against a best run, and time formats (pure).</summary>
internal static class SplitCompare
{
    /// <summary>Elapsed minus the best run's split at the same gate (negative = ahead), or null without one.</summary>
    public static long? Delta(IReadOnlyList<long> best, int seq, long elapsedMs)
    {
        if (best == null || seq < 0 || seq >= best.Count) return null;
        return elapsedMs - best[seq];
    }

    public static string FormatDelta(long ms)
    {
        string sign = ms < 0 ? "-" : "+";
        return sign + (Math.Abs(ms) / 1000.0).ToString("0.00", CultureInfo.InvariantCulture);
    }

    public static string FormatTime(long ms)
    {
        if (ms < 0) ms = 0;
        long h = ms / 3600000, m = ms / 60000 % 60, s = ms / 1000 % 60, f = ms % 1000;
        return h > 0
            ? string.Format(CultureInfo.InvariantCulture, "{0}:{1:00}:{2:00}.{3:000}", h, m, s, f)
            : string.Format(CultureInfo.InvariantCulture, "{0}:{1:00}.{2:000}", m, s, f);
    }

    public static string DnfText(DnfReason r) => r switch
    {
        DnfReason.GaveUp => "gave up",
        DnfReason.Left => "left",
        DnfReason.TimeLimit => "time limit",
        DnfReason.FinishWindow => "too late",
        DnfReason.Jumped => "moved too far at once",
        DnfReason.Stopped => "race stopped",
        DnfReason.NotAtStart => "not at the start at GO",
        _ => "out",
    };
}
