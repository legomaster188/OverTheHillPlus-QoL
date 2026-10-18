using System;
using System.Collections.Generic;

namespace OverTheHillPlus.Race;

internal enum RacePhase : byte { Countdown = 0, Running = 1, Done = 2 }

internal enum RaceEventKind : byte { Go, Gate, Finish, Dnf, Done }

/// <summary>Something the host tells everyone about (see <see cref="RaceSession"/>).</summary>
internal readonly struct RaceEvent
{
    public readonly RaceEventKind Kind;
    public readonly int Key;
    public readonly GateHit Hit;
    public readonly DnfReason Reason;

    public RaceEvent(RaceEventKind kind, int key, GateHit hit = default, DnfReason reason = DnfReason.None)
    {
        Kind = kind; Key = key; Hit = hit; Reason = reason;
    }
}

/// <summary>One line of the results.</summary>
internal sealed class Standing
{
    /// <summary>1-based for finishers, 0 for racers who did not finish.</summary>
    public int Place;
    public int Key;
    public string Name;
    public RacerState State;
    public DnfReason Reason;
    public long Ms;
    public int Gates;
}

/// <summary>
/// One race, run by the host (pure logic): a countdown to GO, every racer's <see cref="RacerRun"/>, and the end rules
/// (time limit, the window after the first finisher, giving up, leaving, the host stopping it). Racers are keyed by
/// their player id; joining is possible during the countdown only.
/// </summary>
internal sealed class RaceSession
{
    public sealed class Racer
    {
        public int Key;
        public string Name;
        public RacerRun Run;
    }

    private readonly RaceRules _rules;
    private readonly List<GateHit> _hits = new();

    public RaceSession(RaceTrack track, double now, double countdownSeconds, RaceRules rules, int serial)
    {
        Track = track;
        _rules = rules ?? new RaceRules();
        GoTime = now + Math.Max(0.0, countdownSeconds);
        Serial = serial;
    }

    public RaceTrack Track { get; }
    public int Serial { get; }
    public double GoTime { get; }
    public RacePhase Phase { get; private set; }
    public double? FirstFinishAt { get; private set; }
    public Dictionary<int, Racer> Racers { get; } = new();

    /// <summary>Adds a racer during the countdown (null once the race runs, or when already in).</summary>
    public Racer AddRacer(int key, string name)
    {
        if (Phase != RacePhase.Countdown || Racers.ContainsKey(key)) return null;
        var r = new Racer { Key = key, Name = string.IsNullOrEmpty(name) ? "Racer" : name, Run = new RacerRun(Track, GoTime, _rules) };
        Racers[key] = r;
        return r;
    }

    /// <summary>A racer's car position at <paramref name="now"/>.</summary>
    public void Sample(int key, double now, RaceVec pos, List<RaceEvent> events)
    {
        if (Phase == RacePhase.Done || !Racers.TryGetValue(key, out var r)) return;
        var before = r.Run.State;
        _hits.Clear();
        r.Run.Sample(now, pos, _hits);
        foreach (var h in _hits)
            events?.Add(new RaceEvent(h.Finish ? RaceEventKind.Finish : RaceEventKind.Gate, key, h));
        if (r.Run.State == RacerState.Finished && before == RacerState.Racing) FirstFinishAt ??= now;
        if (r.Run.State == RacerState.Dnf && before == RacerState.Racing) events?.Add(new RaceEvent(RaceEventKind.Dnf, key, reason: r.Run.Reason));
    }

    /// <summary>Phase changes and the end rules.</summary>
    public void Tick(double now, List<RaceEvent> events)
    {
        if (Phase == RacePhase.Done) return;
        if (Phase == RacePhase.Countdown)
        {
            if (now < GoTime) return;
            Phase = RacePhase.Running;
            events?.Add(new RaceEvent(RaceEventKind.Go, 0));
        }
        if (now - GoTime >= _rules.TimeLimitSeconds) DnfAll(DnfReason.TimeLimit, events);
        else if (FirstFinishAt.HasValue && now - FirstFinishAt.Value >= _rules.FinishWindowSeconds) DnfAll(DnfReason.FinishWindow, events);
        if (StillRacing() == 0) End(events);
    }

    public void GiveUp(int key, List<RaceEvent> events) => Out(key, DnfReason.GaveUp, events);

    public void Left(int key, List<RaceEvent> events) => Out(key, DnfReason.Left, events);

    /// <summary>The host stops the race: everyone still racing is out.</summary>
    public void Stop(List<RaceEvent> events)
    {
        if (Phase == RacePhase.Done) return;
        DnfAll(DnfReason.Stopped, events);
        End(events);
    }

    /// <summary>
    /// The events worth a message of their own: when the race ends in the same batch, the "out" events are left out
    /// (the results carry every racer's outcome), so a mass DNF can't use up the send rate before the results.
    /// </summary>
    public static void ToSend(IReadOnlyList<RaceEvent> events, List<RaceEvent> into)
    {
        bool ends = false;
        foreach (var e in events) if (e.Kind == RaceEventKind.Done) { ends = true; break; }
        foreach (var e in events)
            if (!(ends && e.Kind == RaceEventKind.Dnf)) into.Add(e);
    }

    public int StillRacing()
    {
        int n = 0;
        foreach (var r in Racers.Values) if (r.Run.State == RacerState.Racing) n++;
        return n;
    }

    /// <summary>Finishers by time, then the rest by how far they got (then by when they got there).</summary>
    public List<Standing> Standings()
    {
        var list = new List<Standing>();
        foreach (var r in Racers.Values)
            list.Add(new Standing
            {
                Key = r.Key, Name = r.Name, State = r.Run.State, Reason = r.Run.Reason,
                Ms = r.Run.State == RacerState.Finished ? r.Run.FinishMs : r.Run.LastSplitMs, Gates = r.Run.NextSeq,
            });
        list.Sort((a, b) =>
        {
            bool fa = a.State == RacerState.Finished, fb = b.State == RacerState.Finished;
            if (fa != fb) return fa ? -1 : 1;
            if (fa) return a.Ms != b.Ms ? a.Ms.CompareTo(b.Ms) : a.Key.CompareTo(b.Key);
            if (a.Gates != b.Gates) return b.Gates.CompareTo(a.Gates);
            if (a.Gates > 0 && a.Ms != b.Ms) return a.Ms.CompareTo(b.Ms);
            return a.Key.CompareTo(b.Key);
        });
        int place = 0;
        foreach (var s in list) s.Place = s.State == RacerState.Finished ? ++place : 0;
        return list;
    }

    private void Out(int key, DnfReason reason, List<RaceEvent> events)
    {
        if (Phase == RacePhase.Done || !Racers.TryGetValue(key, out var r) || r.Run.State != RacerState.Racing) return;
        r.Run.Dnf(reason);
        events?.Add(new RaceEvent(RaceEventKind.Dnf, key, reason: reason));
    }

    private void DnfAll(DnfReason reason, List<RaceEvent> events)
    {
        foreach (var r in Racers.Values)
        {
            if (r.Run.State != RacerState.Racing) continue;
            r.Run.Dnf(reason);
            events?.Add(new RaceEvent(RaceEventKind.Dnf, r.Key, reason: reason));
        }
    }

    private void End(List<RaceEvent> events)
    {
        if (Phase == RacePhase.Done) return;
        Phase = RacePhase.Done;
        events?.Add(new RaceEvent(RaceEventKind.Done, 0));
    }
}
