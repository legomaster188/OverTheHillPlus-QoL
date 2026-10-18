using System;
using System.Collections.Generic;
using OverTheHillPlus.Shared;
using UnityEngine;

namespace OverTheHillPlus.Race;

/// <summary>
/// What this game shows about the session's racing: the track, the race in progress (countdown, every racer's
/// progress), the last results and the track's leaderboard. The host fills it locally from its own race
/// (<see cref="RaceHost"/>); guests fill it from the host's messages (<see cref="RaceNet"/>), through the same calls.
/// </summary>
internal static class RaceView
{
    internal sealed class Entry
    {
        public int Key;
        public string Name;
        public RacerState State;
        public DnfReason Reason;
        public int NextSeq;
        public long LastMs;
        public long? LastDelta;
        public float LastAt = -100f;
    }

    public static RaceTrack Track { get; private set; }
    /// <summary><see cref="Track"/>'s id, worked out once per track (it hashes every gate).</summary>
    public static string TrackId { get; private set; }
    public static Leaderboard Board { get; private set; }

    public static int Serial { get; private set; }
    /// <summary>A race is counting down or running (until its results arrive).</summary>
    public static bool Live { get; private set; }
    /// <summary>Local unscaled time of GO.</summary>
    public static float GoAt { get; private set; }
    public static readonly Dictionary<int, Entry> Racers = new();

    public static List<Standing> Results { get; private set; }
    public static float ResultsAt { get; private set; } = -100f;
    public static string ResultsTrack { get; private set; }

    /// <summary>A short line about the latest happening ("Ann finished 1:02.345"), and when.</summary>
    public static string Ticker { get; private set; } = "";
    public static float TickerAt { get; private set; } = -100f;

    private static readonly List<long> MySplits = new();

    public static int MyKey => RaceNet.MyKey;

    public static Entry Me => Racers.TryGetValue(MyKey, out var e) ? e : null;

    /// <summary>Guests: when to stop waiting for the results (they may be lost); 0 = no limit known.</summary>
    private static float _liveUntil;

    /// <summary>The local player's run starts at GO (shared with other mods through <see cref="RaceEvents"/>).</summary>
    private static bool _goPending;
    private static string _goTrackId, _goTrackName;

    public static void Reset()
    {
        _goPending = false;
        RaceEvents.Cancel();
        RaceEvents.SetTrack(null, null);
        Track = null;
        TrackId = null;
        Board = null;
        Live = false;
        Racers.Clear();
        Results = null;
        ResultsAt = -100f;
        Ticker = "";
    }

    // ---------------------------------------------------------------- track and board

    public static void SetTrack(RaceTrack t)
    {
        if (t == null) { Track = null; TrackId = null; Board = null; RaceEvents.SetTrack(null, null); return; }
        string id = t.Id;
        bool same = Track != null && TrackId == id;
        Track = t;
        TrackId = id;
        RaceEvents.SetTrack(id, t.Name);
        if (!same) Board = RaceFiles.LoadBoard(id) ?? new Leaderboard { TrackId = id, TrackName = t.Name };
    }

    /// <summary>A board from the host: merged into this game's own file for that track.</summary>
    public static void MergeBoard(Leaderboard b)
    {
        if (b == null || Track == null || b.TrackId != TrackId) return;
        Board ??= new Leaderboard { TrackId = TrackId, TrackName = Track.Name };
        Board.Merge(b.Entries);
        RaceFiles.SaveBoard(Board);
    }

    /// <summary>The host's own board after it added a time.</summary>
    public static void BoardChanged() => RaceFiles.SaveBoard(Board);

    // ---------------------------------------------------------------- a race

    public static void Countdown(int serial, float secondsToGo, IReadOnlyList<(int key, string name)> racers, float raceSeconds)
    {
        RaceEvents.Cancel();
        Serial = serial;
        Live = true;
        GoAt = Time.unscaledTime + Mathf.Clamp(secondsToGo, 0f, 60f);
        // The host ends every race by its time limit; a little later, results that never came are given up on.
        _liveUntil = raceSeconds > 0f ? GoAt + Mathf.Min(raceSeconds, 7200f) + 20f : 0f;
        Racers.Clear();
        MySplits.Clear();
        foreach (var (key, name) in racers) Racers[key] = new Entry { Key = key, Name = name };
        DebugLog.Info(RacePlugin.ModId, $"race {serial}: countdown {secondsToGo:0.#} s on '{Track?.Name ?? "?"}', {racers.Count} racer(s), you {(Racers.ContainsKey(MyKey) ? "are" : "are not")} in it");
        Results = null;
        _goPending = Racers.ContainsKey(MyKey) && !string.IsNullOrEmpty(TrackId);
        _goTrackId = TrackId; // fixed now: the host may edit the draft track before GO
        _goTrackName = Track?.Name;
        Tell(Racers.ContainsKey(MyKey) ? $"Race on {Track?.Name ?? "the track"}: get ready" : $"Race on {Track?.Name ?? "the track"} (you're not in it)");
    }

    public static void Progress(int serial, int key, RaceEventKind kind, int seq, long ms, long? delta, DnfReason reason)
    {
        if (serial != Serial || !Live || !Racers.TryGetValue(key, out var e)) return;
        e.LastAt = Time.unscaledTime;
        if (kind == RaceEventKind.Dnf)
        {
            e.State = RacerState.Dnf;
            e.Reason = reason;
            if (key == MyKey) EndMyRun(false, 0);
            Tell($"{e.Name}: out ({SplitCompare.DnfText(reason)})");
            return;
        }
        e.NextSeq = Math.Max(e.NextSeq, seq + 1);
        e.LastMs = ms;
        e.LastDelta = delta;
        if (key == MyKey)
        {
            while (MySplits.Count < seq) MySplits.Add(ms);
            if (MySplits.Count == seq) MySplits.Add(ms);
        }
        if (kind == RaceEventKind.Finish)
        {
            e.State = RacerState.Finished;
            if (key == MyKey) MyFinish(e, ms);
            else Tell($"{e.Name} finished in {SplitCompare.FormatTime(ms)}");
        }
    }

    /// <summary>The local run is over: finished in <paramref name="ms"/>, or not (out, no results, race over).</summary>
    private static void EndMyRun(bool finished, long ms)
    {
        _goPending = false;
        if (finished) RaceEvents.Finish(ms / 1000.0);
        else RaceEvents.Cancel();
    }

    private static void MyFinish(Entry e, long ms)
    {
        EndMyRun(true, ms);
        string pb = "";
        if (Board != null && !RaceNet.IsHostSide)
        {
            // Guests keep their own times too (the host's board arrives after the race and is merged).
            var r = Board.Add(e.Name, ms, MySplits.ToArray(), DateTime.Now.ToString("yyyy-MM-dd"));
            if (r.PersonalBest) pb = r.Record ? "  ·  track record!" : "  ·  personal best!";
            RaceFiles.SaveBoard(Board);
        }
        // The host adds its own time in RaceHost and says so there.
        Tell($"You finished in {SplitCompare.FormatTime(ms)}{pb}");
    }

    public static void Done(int serial, List<Standing> standings, string trackName)
    {
        if (serial != Serial) return;
        Live = false;
        EndMyRun(false, 0); // no-op when the run already ended
        Results = standings;
        DebugLog.Info(RacePlugin.ModId, $"race {serial}: results for '{trackName}', {standings?.Count ?? 0} racer(s)");
        ResultsAt = Time.unscaledTime;
        ResultsTrack = trackName;
        var win = standings != null && standings.Count > 0 && standings[0].State == RacerState.Finished ? standings[0] : null;
        Tell(win != null ? $"{win.Name} wins in {SplitCompare.FormatTime(win.Ms)}" : "Race over: nobody finished");
    }

    /// <summary>Guests: a race whose results never arrived (lost, or the host left) stops showing as live.</summary>
    public static void Tick()
    {
        // GO: the local run starts (host and guest alike).
        if (_goPending && Live && Time.unscaledTime >= GoAt)
        {
            _goPending = false;
            RaceEvents.Begin(_goTrackId, _goTrackName);
        }
        if (!Live || RaceNet.IsHostSide || _liveUntil <= 0f || Time.unscaledTime < _liveUntil) return;
        Live = false;
        EndMyRun(false, 0);
        DebugLog.Info(RacePlugin.ModId, $"race {Serial}: the host's results never arrived, giving up on it");
        Tell("The race's results didn't arrive from the host");
    }

    public static void Tell(string text)
    {
        Ticker = text ?? "";
        TickerAt = Time.unscaledTime;
    }

    /// <summary>Racers by progress: finishers by time first, then by gates passed.</summary>
    public static List<Entry> Ordered()
    {
        var list = new List<Entry>(Racers.Values);
        list.Sort((a, b) =>
        {
            bool fa = a.State == RacerState.Finished, fb = b.State == RacerState.Finished;
            if (fa != fb) return fa ? -1 : 1;
            if (fa) return a.LastMs.CompareTo(b.LastMs);
            bool da = a.State == RacerState.Dnf, db = b.State == RacerState.Dnf;
            if (da != db) return da ? 1 : -1;
            if (a.NextSeq != b.NextSeq) return b.NextSeq.CompareTo(a.NextSeq);
            return a.LastMs.CompareTo(b.LastMs);
        });
        return list;
    }
}
