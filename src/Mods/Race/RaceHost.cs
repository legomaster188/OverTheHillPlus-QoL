using System;
using System.Collections.Generic;
using Fusion;
using OverTheHillPlus.Features.Public.Filter;
using OverTheHillPlus.Features.Public.PlayerList;
using OverTheHillPlus.Multiplayer;
using OverTheHillPlus.Shared;
using UnityEngine;
using Net = OverTheHillPlus.Features.Public.ModNet.ModNet;

namespace OverTheHillPlus.Race;

/// <summary>
/// The host's side (also solo): editing the track, starting and stopping races, timing every racer from the car
/// positions this game simulates (pure <see cref="RaceSession"/>), adding finish times to the track's leaderboard,
/// and telling the guests. Everything here needs <see cref="GameState.IsAuthority"/>.
/// </summary>
internal static class RaceHost
{
    private static RaceTrack _draft = new() { Name = "My track" };
    private static RaceSession _session;
    private static int _serial;
    private static bool _trackDirty;
    private static float _nextTrackSend;
    private static long[] _bestSplits;
    private static readonly HashSet<int> OptedOut = new();
    private static readonly List<RaceEvent> Sendable = new();
    private static readonly List<RaceEvent> Events = new();
    private static readonly Dictionary<int, PlayerRef> Refs = new();
    private static string _lastAreaKey;

    /// <summary>The track being edited (and raced): the same object the host's view shows a copy of.</summary>
    public static RaceTrack Draft => _draft;

    public static bool Racing => _session != null && _session.Phase != RacePhase.Done;

    public static bool CanEdit => RaceNet.IsHostSide && !Racing;

    // ---------------------------------------------------------------- editing

    public static string AddGate(Vector3 pos, string what)
    {
        if (!CanEdit) return Racing ? "Blocked: wait until the race is over." : "Blocked: only the host can edit the track.";
        if (_draft.Gates.Count >= RaceTrack.MaxGates) return $"Failed: {RaceTrack.MaxGates} checkpoints is the most a track can have.";
        if (!PinBoard.ValidCoordinate(pos.x) || !PinBoard.ValidCoordinate(pos.y) || !PinBoard.ValidCoordinate(pos.z)) return "Failed: that spot is off the map.";
        if (_draft.Gates.Count == 0) _draft.Area = GameMap.AreaKey;
        float radius = Mathf.Clamp(RaceConfig.GateRadius.Value, RaceTrack.MinRadius, RaceTrack.MaxRadius);
        _draft.Gates.Add(new RaceGate { X = pos.x, Y = pos.y, Z = pos.z, Radius = radius });
        Changed();
        int n = _draft.Gates.Count;
        DebugLog.Verbose("race", () => $"checkpoint {n} placed at {pos.x:0},{pos.z:0} (radius {radius:0.#}) on '{_draft.Area}'");
        return n == 1 ? $"Start placed at {what}." : $"Checkpoint {n - 1} placed at {what}.";
    }

    public static string AddGateAtCar()
    {
        if (!GameMap.TryCarPosition(out var car)) return "Failed: no car (load into a world first).";
        return AddGate(car, "your car");
    }

    public static string RemoveLast()
    {
        if (!CanEdit || _draft.Gates.Count == 0) return "";
        _draft.Gates.RemoveAt(_draft.Gates.Count - 1);
        DebugLog.Verbose("race", () => $"last checkpoint removed ({_draft.Gates.Count} left)");
        Changed();
        return "Removed the last checkpoint.";
    }

    public static string ClearTrack()
    {
        if (!CanEdit) return "";
        _draft.Gates.Clear();
        Changed();
        return "Cleared the checkpoints.";
    }

    public static void SetName(string name)
    {
        if (!CanEdit) return;
        string clean = PinBoard.CleanText(name, RaceTrack.MaxName);
        if (clean == _draft.Name) return;
        _draft.Name = clean;
        Changed();
    }

    public static void SetKind(RaceKind k)
    {
        if (!CanEdit || _draft.Kind == k) return;
        _draft.Kind = k;
        Changed();
    }

    public static void SetLaps(int laps)
    {
        if (!CanEdit) return;
        laps = Mathf.Clamp(laps, 1, RaceTrack.MaxLaps);
        if (_draft.Laps == laps) return;
        _draft.Laps = laps;
        Changed();
    }

    /// <summary>Sets every checkpoint's size (and the size of new ones).</summary>
    public static void SetRadius(float r)
    {
        RaceConfig.GateRadius.Value = r;
        if (!CanEdit) return;
        foreach (var g in _draft.Gates) g.Radius = Mathf.Clamp(r, RaceTrack.MinRadius, RaceTrack.MaxRadius);
        Changed();
    }

    public static string Load(RaceTrack t)
    {
        if (!CanEdit) return "Blocked: wait until the race is over.";
        if (t == null) return "";
        _draft = t.Copy();
        Changed();
        DebugLog.Verbose("race", () => $"track '{t.Name}' loaded into the editor: {t.Gates.Count} gates, {t.Kind}, {t.Laps} lap(s), map '{t.Area}'");
        string other = !string.IsNullOrEmpty(t.Area) && t.Area != GameMap.AreaKey ? $" (made on another map: {t.Area})" : "";
        return $"Loaded \"{t.Name}\"{other}.";
    }

    public static string New()
    {
        if (!CanEdit) return "";
        _draft = new RaceTrack { Name = "My track", Area = GameMap.AreaKey, Kind = _draft.Kind, Laps = _draft.Laps };
        Changed();
        return "New track: drive to the start and add checkpoints.";
    }

    private static void Changed()
    {
        _draft.Clean();
        RaceView.SetTrack(_draft.Gates.Count > 0 ? _draft.Copy() : null);
        _trackDirty = true;
    }

    /// <summary>Why the track can't be shared or raced (null = fine).</summary>
    public static string TrackProblem()
    {
        string why = _draft.Validate();
        if (why != null) return why;
        if (NameFilter.IsBlocked(_draft.Name, out _)) return "the track's name is refused by the word filter: rename it";
        return null;
    }

    // ---------------------------------------------------------------- races

    public static string Start()
    {
        if (!RaceNet.IsHostSide) return "Blocked: only the host can start a race.";
        if (Racing) return "Blocked: a race is already on.";
        string why = TrackProblem();
        if (why != null) return "Failed: " + why + ".";
        var track = _draft.Copy();
        var racers = new List<(int key, string name)>();
        Refs.Clear();
        int me = RaceNet.MyKey;
        if (RaceConfig.HostRaces.Value || !Net.IsHost) racers.Add((me, MyName()));
        if (Net.IsHost)
            foreach (var p in Net.Peers)
            {
                if (racers.Count >= RaceNet.MaxRacers) break;
                int key = p.Ref.RawEncoded;
                if (key == me || OptedOut.Contains(key) || !Net.PeerHas(p.Ref, Net.Channels.Race)) continue;
                racers.Add((key, NameOf(p.Ref)));
                Refs[key] = p.Ref;
            }
        if (racers.Count == 0) return "Failed: nobody to race (switch on \"Race myself\", or wait for guests with Race Mode).";

        float countdown = Mathf.Clamp(RaceConfig.Countdown.Value, 3, 30);
        var rules = new RaceRules
        {
            TimeLimitSeconds = Mathf.Clamp(RaceConfig.TimeLimitMinutes.Value, 1f, 120f) * 60.0,
            FinishWindowSeconds = Mathf.Clamp(RaceConfig.FinishWindowSeconds.Value, 10f, 600f),
            StartZoneMeters = Mathf.Clamp(RaceConfig.StartZone.Value, 0f, 1000f),
        };
        _serial++;
        _session = new RaceSession(track, Now, countdown, rules, _serial);
        foreach (var (key, name) in racers) _session.AddRacer(key, name);
        _bestSplits = RaceView.Board?.BestSplits;

        RaceView.SetTrack(track);
        RaceNet.BroadcastTrack(track);
        _trackDirty = false;
        RaceView.Countdown(_serial, countdown, racers, (float)rules.TimeLimitSeconds);
        RaceNet.Broadcast(RaceNet.MsgCountdown, RaceNet.EncodeCountdown(_serial, countdown, racers, (float)rules.TimeLimitSeconds));
        RacePlugin.Logger.LogInfo($"[Race] Race {_serial} on '{track.Name}' with {racers.Count} racer(s)");
        DebugLog.Info(RacePlugin.ModId, $"race {_serial} started on '{track.Name}' ({track.Kind}, {track.Gates.Count} gates, {track.Laps} lap(s)): countdown {countdown:0} s, " +
                                       $"limit {rules.TimeLimitSeconds / 60:0} min, racers: {string.Join(", ", racers.ConvertAll(r => r.name))}");
        return $"Race on \"{track.Name}\": GO in {countdown:0} s.";
    }

    public static string Stop()
    {
        if (!Racing) return "";
        Events.Clear();
        DebugLog.Info(RacePlugin.ModId, $"race {_session.Serial} stopped by the host");
        _session.Stop(Events);
        Process();
        return "Race stopped.";
    }

    /// <summary>The local player gives up (host side; guests ask the host).</summary>
    public static string GiveUpSelf()
    {
        if (!Racing) return "";
        Events.Clear();
        _session.GiveUp(RaceNet.MyKey, Events);
        Process();
        return "You gave up the race.";
    }

    // ---------------------------------------------------------------- per frame

    public static void Tick()
    {
        if (!RaceNet.IsHostSide)
        {
            _session = null; // became a guest
            return;
        }

        // Back from a session (or a fresh start): show the track being edited again.
        if (RaceView.Track == null && _draft.Gates.Count > 0 && !Racing) RaceView.SetTrack(_draft.Copy());

        // A new map: the old track's checkpoints are elsewhere.
        string area = GameMap.AreaKey;
        if (_lastAreaKey != null && area != _lastAreaKey && Racing) Stop();
        _lastAreaKey = area;

        if (_trackDirty && Time.unscaledTime >= _nextTrackSend && Net.IsHost && !Racing)
        {
            _trackDirty = false;
            _nextTrackSend = Time.unscaledTime + 1f;
            if (TrackProblem() == null) RaceNet.BroadcastTrack(_draft);
        }

        if (_session == null) return;
        double now = Now;
        Events.Clear();
        foreach (var r in _session.Racers.Values)
        {
            if (r.Run.State != RacerState.Racing) continue;
            Vector3 pos = default;
            bool ok = r.Key == RaceNet.MyKey ? GameMap.TryCarPosition(out pos)
                : Refs.TryGetValue(r.Key, out var pr) && GameMap.TryPlayerCarPosition(pr, out pos);
            if (ok) _session.Sample(r.Key, now, new RaceVec(pos.x, pos.y, pos.z), Events);
        }
        _session.Tick(now, Events);
        Process();
    }

    private static double Now => Time.unscaledTimeAsDouble;

    /// <summary>Turns the session's events into the host's own view, leaderboard entries and messages to guests.</summary>
    private static void Process()
    {
        if (_session == null) return;
        int serial = _session.Serial;
        // When the race ends in this batch, the "out" events ride in the results instead of a message each.
        Sendable.Clear();
        RaceSession.ToSend(Events, Sendable);
        bool foldDnfs = Sendable.Count < Events.Count;
        foreach (var e in Events)
        {
            switch (e.Kind)
            {
                case RaceEventKind.Gate:
                case RaceEventKind.Finish:
                {
                    long? delta = SplitCompare.Delta(_bestSplits, e.Hit.Seq, e.Hit.ElapsedMs);
                    DebugLog.Verbose(RacePlugin.ModId, () => $"race {serial}: racer {e.Key} {(e.Kind == RaceEventKind.Finish ? "finished" : "gate " + e.Hit.Seq)} at {e.Hit.ElapsedMs} ms" +
                                                           (delta.HasValue ? $" (delta {delta.Value} ms)" : ""));
                    RaceView.Progress(serial, e.Key, e.Kind, e.Hit.Seq, e.Hit.ElapsedMs, delta, DnfReason.None);
                    RaceNet.Broadcast(RaceNet.MsgProgress, RaceNet.EncodeProgress(serial, e.Key, e.Kind, e.Hit.Seq, e.Hit.ElapsedMs, delta, DnfReason.None));
                    if (e.Kind == RaceEventKind.Finish) AddToBoard(e.Key, e.Hit.ElapsedMs);
                    break;
                }
                case RaceEventKind.Dnf:
                    DebugLog.Info(RacePlugin.ModId, $"race {serial}: racer {e.Key} out ({e.Reason})");
                    RaceView.Progress(serial, e.Key, RaceEventKind.Dnf, 0, 0, null, e.Reason);
                    if (!foldDnfs) RaceNet.Broadcast(RaceNet.MsgProgress, RaceNet.EncodeProgress(serial, e.Key, RaceEventKind.Dnf, 0, 0, null, e.Reason));
                    break;
                case RaceEventKind.Done:
                    Finish();
                    break;
            }
        }
        Events.Clear();
    }

    private static void AddToBoard(int key, long ms)
    {
        var board = RaceView.Board;
        if (board == null || !_session.Racers.TryGetValue(key, out var r)) return;
        var res = board.Add(r.Name, ms, r.Run.Splits.ToArray(), DateTime.Now.ToString("yyyy-MM-dd"));
        RaceView.BoardChanged();
        if (key == RaceNet.MyKey && res.PersonalBest)
            RaceView.Tell($"You finished in {SplitCompare.FormatTime(ms)}  ·  {(res.Record ? "track record!" : "personal best!")}");
        else if (res.Record && key != RaceNet.MyKey)
            RaceView.Tell($"{r.Name}: new track record, {SplitCompare.FormatTime(ms)}");
    }

    private static void Finish()
    {
        var standings = _session.Standings();
        string trackName = _session.Track.Name;
        RaceView.Done(_session.Serial, standings, trackName);
        RaceNet.Broadcast(RaceNet.MsgResults, RaceNet.EncodeResults(_session.Serial, trackName, standings));
        if (RaceView.Board != null) RaceNet.Broadcast(RaceNet.MsgBoard, RaceView.Board.Encode(10));
        var win = standings.Count > 0 && standings[0].State == RacerState.Finished ? standings[0] : null;
        if (win != null && RaceConfig.AnnounceResults.Value && Net.IsHost)
            HostNotice.Announce($"Race {trackName}: {win.Name} wins in {SplitCompare.FormatTime(win.Ms)}", HostNotice.ColorFor("Green"));
        RacePlugin.Logger.LogInfo($"[Race] Race {_session.Serial} done: " + (win != null ? $"{win.Name} {SplitCompare.FormatTime(win.Ms)}" : "nobody finished"));
        DebugLog.Info(RacePlugin.ModId, $"race {_session.Serial} over on '{trackName}': " + (win != null ? $"{win.Name} won in {SplitCompare.FormatTime(win.Ms)}" : "nobody finished") +
                                       $"; {standings.Count} racer(s) in the results");
        _session = null;
    }

    // ---------------------------------------------------------------- guests

    public static void PeerReady(PlayerRef p)
    {
        if (!Net.IsHost) return;
        if (TrackProblem() == null) RaceNet.SendTrack(p, _draft);
        if (RaceView.Board != null && RaceView.Board.Entries.Count > 0) RaceNet.SendTo(p, RaceNet.MsgBoard, RaceView.Board.Encode(10));
    }

    public static void PeerGone(PlayerRef p)
    {
        OptedOut.Remove(p.RawEncoded);
        if (!Racing) return;
        Events.Clear();
        _session.Left(p.RawEncoded, Events);
        Process();
    }

    public static void GuestJoin(PlayerRef p, bool on)
    {
        if (on) OptedOut.Remove(p.RawEncoded);
        else OptedOut.Add(p.RawEncoded);
    }

    public static void GuestGiveUp(PlayerRef p)
    {
        if (!Racing) return;
        Events.Clear();
        _session.GiveUp(p.RawEncoded, Events);
        Process();
    }

    public static void SessionReset()
    {
        if (_session != null) DebugLog.Info(RacePlugin.ModId, $"session reset: race {_session.Serial} dropped");
        _session = null;
        OptedOut.Clear();
        Refs.Clear();
        _trackDirty = true;
    }

    /// <summary>One line for the MODS page.</summary>
    public static string Status()
    {
        if (!RaceNet.IsHostSide) return null;
        int guests = 0;
        if (Net.IsHost) foreach (var p in Net.Peers) if (Net.PeerHas(p.Ref, Net.Channels.Race) && !OptedOut.Contains(p.Ref.RawEncoded)) guests++;
        string who = Net.IsHost ? $"{guests} guest(s) with Race Mode ready" : "solo: time trials";
        return Racing ? $"Race on: {_session.StillRacing()} still racing." : who + ".";
    }

    // ---------------------------------------------------------------- names

    private static string MyName()
    {
        var me = GameState.LocalPlayer;
        if (me.HasValue && me.Value.IsRealPlayer) return NameOf(me.Value);
        return "You";
    }

    private static string NameOf(PlayerRef p)
    {
        try { return HostNotice.SafeName(PinBoard.CleanText(PlayerListRoster.NameOf(OverlandSessionPlayers.Instance, p), 24), "Player " + p.PlayerId); }
        catch (Exception ex) { DebugLog.Error("race", ex, "RaceHost.NameOf"); return "Player " + p.PlayerId; }
    }
}
