using System;
using System.Collections.Generic;
using System.Linq;
using Fusion;
using OverTheHillPlus.Features.Public.ModNet;
using OverTheHillPlus.Multiplayer;
using OverTheHillPlus.UI;
using UnityEngine;
using Net = OverTheHillPlus.Features.Public.ModNet.ModNet;
using Random = System.Random;

namespace OverTheHillPlus.Tag;

// After the namespace line on purpose: the game has its own global ModManager (mod.io).
using OverTheHillPlus.Core.Manager;

/// <summary>
/// The round in this session.
/// <list type="bullet">
/// <item>Host: owns the <see cref="TagRound"/>, feeds it the car contacts it sees (it simulates every car), adds
///   players who join and removes those who leave, announces with the game's pop-up (vanilla players see it too) and
///   broadcasts a snapshot to modded players on change and once a second (channel <see cref="Channel"/>).</item>
/// <item>Everyone: <see cref="View"/> is the latest snapshot (the host's own, or the one it sent) for the HUD and the
///   map; changes between snapshots become short on-screen notices.</item>
/// </list>
/// Guests send nothing on this channel. Networked behaviour only runs while ModManager.ServerModActive("tag").
/// </summary>
internal static class TagGame
{
    /// <summary>ModNet channel 8 (Tag & Hide and Seek). Listed in ModNet.Channels.</summary>
    internal const ushort Channel = Net.Channels.Tag;
    private const byte MsgState = 1;
    private const float BroadcastEvery = 1f, StaleAfter = 6f, ResultsFor = 20f;

    private static TagRound _round;
    private static int _roundNumber;
    private static float _nextBroadcast, _nextCheck;
    private static bool _dirty;
    private static readonly Random Rng = new();
    private static readonly Queue<(int a, int b)> Contacts = new();
    private static readonly HashSet<string> LoggedOnce = new();

    /// <summary>What the HUD and the map show (null: no round known).</summary>
    public static TagSnapshot View { get; private set; }
    /// <summary>When <see cref="View"/> arrived (unscaled time).</summary>
    public static float ViewAt { get; private set; } = -100f;

    /// <summary>Short notices for the HUD (newest last).</summary>
    public static readonly List<(string text, float at, Color color)> Notices = new();

    public static bool Hosting => _round != null && IsHost;

    /// <summary>A round is shown and running (seconds counted down since it arrived).</summary>
    public static bool Running => View != null && View.Running && Time.unscaledTime - ViewAt < StaleAfter;

    internal static float TimeLeft => View == null ? 0f : Math.Max(0f, View.TimeLeft - (Time.unscaledTime - ViewAt));
    internal static float HeadStartLeft => View == null ? 0f : Math.Max(0f, View.HeadStartLeft - (Time.unscaledTime - ViewAt));

    /// <summary>The host side: hosting a multiplayer session (ModNet or not).</summary>
    internal static bool IsHost => Net.IsHost && GameState.IsAuthority;

    internal static bool Active => ModManager.ServerModActive(TagPlugin.ModId);

    // ---------------------------------------------------------------- setup

    internal static void Register()
    {
        Net.Register(Channel, new Net.ChannelOptions
        {
            Name = "tag",
            Version = 1,
            MaxPayload = 1024,
            RatePerSecond = 4f,
            Burst = 8,
            HostOnlySender = true,
            Active = () => Active,
        }, OnMessage);
        Net.OnPeerReady(p => _nextBroadcast = 0f);
        Net.OnSessionReset(Reset);
    }

    private static void Reset()
    {
        _round = null;
        View = null;
        ViewAt = -100f;
        Contacts.Clear();
        Notices.Clear();
        TagHold.Active = false;
    }

    // ---------------------------------------------------------------- host: rounds

    /// <summary>Starts a round of the configured mode with everyone who has a car. Returns a status line.</summary>
    public static string Start()
    {
        if (!Active) return "Blocked: Tag & Hide and Seek is not running in this session";
        if (!IsHost) return "Blocked: only the host starts rounds";
        TagCars.Refresh(force: true);
        var ids = TagCars.All.Select(c => c.Id).ToList();
        if (ids.Count < 2) return "Blocked: needs at least 2 players with a car";
        var s = new TagSettings
        {
            RoundSeconds = TagConfig.RoundMinutes.Value * 60f,
            HeadStartSeconds = TagConfig.HeadStartSeconds.Value,
            TagBackSeconds = TagConfig.TagBackSeconds.Value,
        };
        float now = Time.unscaledTime;
        TagRound r;
        string why;
        if (TagConfig.Mode.Value == TagMode.HideAndSeek)
        {
            int n = Mathf.Clamp(TagConfig.Seekers.Value, 1, ids.Count - 1);
            var seekers = ids.OrderBy(_ => Rng.Next()).Take(n).ToList();
            r = TagRound.StartHideAndSeek(ids, seekers, now, s, out why);
            if (r != null)
                Announce($"Hide and seek! {Names(seekers)} {(seekers.Count == 1 ? "seeks" : "seek")}. Hiders: {TagConfig.HeadStartSeconds.Value} s to hide!", Color.cyan);
        }
        else
        {
            int it = ids[Rng.Next(ids.Count)];
            r = TagRound.StartTag(ids, it, now, s, out why);
            if (r != null) Announce($"Tag! {Safe(it)} is it. {TagConfig.RoundMinutes.Value} min on the clock.", Color.red);
        }
        if (r == null) return "Failed: " + why;
        _round = r;
        _overSince = -1f;
        _roundNumber++;
        Contacts.Clear();
        Missing.Clear();
        _dirty = true;
        Publish(now);
        DebugLog.Info(TagPlugin.ModId, $"round {_roundNumber} started: {r.Mode}, {ids.Count} players, {s.RoundSeconds:0} s, head start {s.HeadStartSeconds:0} s");
        TagPlugin.Logger.LogInfo($"[Tag] Round {_roundNumber} started ({r.Mode}, {ids.Count} players)");
        return r.Mode == TagMode.Tag ? "Tag round started" : "Hide and seek round started";
    }

    public static string Stop()
    {
        if (_round == null || !_round.Running) return "No round running";
        _round.Stop(Time.unscaledTime);
        DebugLog.Info(TagPlugin.ModId, $"round {_roundNumber} stopped by the host");
        Announce("The host ended the round.", Color.white);
        _dirty = true;
        Publish(Time.unscaledTime);
        return "Round stopped";
    }

    /// <summary>Host: two cars touched (from the collision hook, during physics: handled on the next tick).</summary>
    internal static void QueueContact(int a, int b)
    {
        if (_round == null || !_round.Running || a < 0 || b < 0 || a == b || Contacts.Count > 64) return;
        Contacts.Enqueue((a, b));
    }

    // ---------------------------------------------------------------- every frame

    internal static void Tick()
    {
        float now = Time.unscaledTime;
        if (_round != null || Running) TagCars.Refresh(); // once a second at most
        if (_round != null && !IsHost) { _round = null; TagHold.Active = false; }
        if (_round != null) HostTick(now);
        TagHold.Active = Running && View.Phase == TagPhase.HeadStart;
        TagHiding.Tick();
        if (Notices.Count > 0 && now - Notices[0].at > 6f) Notices.RemoveAt(0);
    }

    private static void HostTick(float now)
    {
        if (!Active) { _round = null; return; }
        var events = new List<TagEvent>();
        if (now >= _nextCheck)
        {
            _nextCheck = now + 0.1f;
            TagCars.Refresh();
            if (_round.Running)
            {
                SyncPlayers(now, events);
                DistanceContacts();
            }
        }
        while (Contacts.Count > 0)
        {
            var (a, b) = Contacts.Dequeue();
            events.AddRange(_round.Contact(a, b, now, out _));
        }
        events.AddRange(_round.Tick(now));
        foreach (var e in events) Say(e);
        if (events.Count > 0) _dirty = true;
        Publish(now);
    }

    /// <summary>
    /// Players who joined join the round; players whose car is gone for <see cref="LeaveGrace"/> seconds (left the
    /// session, or still in the garage) leave it. The grace covers a car change or a respawn.
    /// </summary>
    private static void SyncPlayers(float now, List<TagEvent> events)
    {
        var present = new HashSet<int>(TagCars.All.Select(c => c.Id));
        foreach (int id in present)
        {
            Missing.Remove(id);
            if (_round.Find(id) == null && _round.AddPlayer(id)) _dirty = true;
        }
        foreach (var p in _round.Players.ToList())
        {
            if (present.Contains(p.Id)) continue;
            if (!Missing.TryGetValue(p.Id, out float since)) { Missing[p.Id] = now; continue; }
            if (now - since < LeaveGrace) continue;
            Missing.Remove(p.Id);
            events.AddRange(_round.PlayerLeft(p.Id, now, ids => ids[Rng.Next(ids.Count)]));
            _dirty = true;
        }
    }

    private const float LeaveGrace = 15f;
    private static readonly Dictionary<int, float> Missing = new();

    /// <summary>Besides real collisions: cars whose centres come within the contact distance.</summary>
    private static void DistanceContacts()
    {
        float d = TagConfig.ContactDistance.Value;
        if (d <= 0f || _round.Phase != TagPhase.Playing) return;
        float d2 = d * d;
        var chasers = _round.Mode == TagMode.Tag
            ? new List<int> { _round.It }
            : _round.Players.Where(p => p.Seeker).Select(p => p.Id).ToList();
        foreach (int c in chasers)
        {
            if (!TagCars.TryPos(c, out var cp)) continue;
            foreach (var p in _round.Players)
            {
                if (p.Id == c || (_round.Mode == TagMode.HideAndSeek && p.Seeker)) continue;
                if (TagCars.TryPos(p.Id, out var pp) && (pp - cp).sqrMagnitude <= d2) QueueContact(c, p.Id);
            }
        }
    }

    private static void Publish(float now)
    {
        if (_round == null) return;
        if (!_dirty && now < _nextBroadcast) return;
        _dirty = false;
        _nextBroadcast = now + BroadcastEvery;
        byte[] bytes = _round.Encode(now, _roundNumber);
        DebugLog.Verbose(TagPlugin.ModId, () => $"snapshot of round {_roundNumber}: {_round.Phase}, {_round.Players.Count} players, {bytes.Length} B to {Net.PeerCount} modded guest(s)");
        Show(TagSnapshot.Decode(bytes));
        if (Net.PeerCount > 0)
        {
            try { Net.Broadcast(Channel, MsgState, bytes); }
            catch (Exception e) { DebugLog.Error("tag", e, "TagGame.Publish"); LogOnce("send", "[Tag] broadcast failed: " + e.Message); }
        }
        // Results stay up for a while, then the round is forgotten.
        if (_round.Phase != TagPhase.Over) _overSince = -1f;
        else if (_overSince < 0f) _overSince = now;
        else if (now - _overSince > ResultsFor) { _round = null; _overSince = -1f; }
    }

    private static float _overSince = -1f;

    // ---------------------------------------------------------------- announcements

    private static void Say(TagEvent e)
    {
        switch (e.Kind)
        {
            case TagEventKind.Tagged: Announce($"{Safe(e.A)} tagged {Safe(e.B)}: {Safe(e.B)} is it!", Color.red); break;
            case TagEventKind.NewIt: Announce($"{Safe(e.B)} is it now.", Color.red); break;
            case TagEventKind.Found:
                int left = _round.Players.Count(p => !p.Seeker);
                Announce($"{Safe(e.A)} found {Safe(e.B)}! {left} {(left == 1 ? "hider" : "hiders")} left.", Color.cyan);
                break;
            case TagEventKind.SeekersReleased: Announce("Ready or not: the seekers are out!", Color.cyan); break;
            case TagEventKind.RoundOver:
                var w = _round.Winners;
                Announce(w.Count == 0 ? "Round over: no winner this time." : $"Round over: {Names(w)} {(w.Count == 1 ? "wins" : "win")}!", new Color(0.95f, 0.70f, 0.24f));
                break;
        }
    }

    private static void Announce(string text, Color color)
    {
        if (!TagConfig.Announce.Value) return;
        HostNotice.Announce(text, color);
    }

    private static string Safe(int id) => HostNotice.SafeName(TagCars.NameOf(id));

    private static string Names(IList<int> ids)
    {
        var names = ids.Take(4).Select(Safe).ToList();
        string s = string.Join(", ", names);
        return ids.Count > 4 ? s + $" and {ids.Count - 4} more" : s;
    }

    // ---------------------------------------------------------------- guests

    private static void OnMessage(Net.Message m)
    {
        if (m.Type != MsgState || !m.FromHost || Net.IsHost || !Active) return;
        var snap = TagSnapshot.Decode(m.Payload);
        if (snap == null) { LogOnce("bad", "[Tag] dropped a snapshot that didn't decode"); return; }
        Show(snap);
    }

    /// <summary>Takes a new snapshot and turns what changed into notices.</summary>
    private static void Show(TagSnapshot next)
    {
        if (next == null) return;
        var prev = View;
        View = next;
        ViewAt = Time.unscaledTime;
        int me = TagCars.Me;
        bool newRound = prev == null || prev.Round != next.Round;
        if (newRound || prev.Phase != next.Phase)
            DebugLog.Info(TagPlugin.ModId, $"round {next.Round}: {next.Mode} {next.Phase}, {next.Players.Count} players, it/seeker {next.It}, you are {(next.Find(me)?.Seeker == true ? "a seeker" : "playing")}");
        if (newRound && next.Running)
        {
            if (next.Mode == TagMode.Tag) Notice(next.It == me ? "Tag! You are it!" : $"Tag! {TagCars.ShortName(next.It)} is it.", Theme.Bad);
            else Notice(next.Find(me)?.Seeker == true ? "Hide and seek: you seek. Wait for the head start..." : "Hide and seek: hide!", Theme.Info);
            return;
        }
        if (prev == null) return;
        if (next.Mode == TagMode.Tag && next.It != prev.It && next.Running)
            Notice(next.It == me ? "You are it!" : $"{TagCars.ShortName(next.It)} is it.", Theme.Bad);
        if (next.Mode == TagMode.HideAndSeek)
        {
            if (prev.Phase == TagPhase.HeadStart && next.Phase == TagPhase.Playing) Notice("Ready or not: the seekers are out!", Theme.Info);
            foreach (var p in next.Players)
            {
                var was = prev.Find(p.Id);
                if (was != null && !was.Found && p.Found) Notice(p.Id == me ? "You were found! Now you seek." : $"{TagCars.ShortName(p.Id)} was found.", Theme.Info);
            }
        }
        if (prev.Running && next.Phase == TagPhase.Over)
        {
            var winners = next.Players.Where(p => p.Winner).Select(p => p.Id).ToList();
            Notice(winners.Count == 0 ? "Round over." : winners.Contains(me) ? "Round over: you win!" : "Round over: " + string.Join(", ", winners.Take(3).Select(TagCars.ShortName)) + (winners.Count == 1 ? " wins" : " win"), Theme.Warn);
        }
    }

    private static void Notice(string text, Color color)
    {
        Notices.Add((text, Time.unscaledTime, color));
        if (Notices.Count > 4) Notices.RemoveAt(0);
    }

    /// <summary>One line for the MODS page.</summary>
    internal static string Status()
    {
        if (!Active) return GameState.Runner == null ? "Load into a world" : "Not running in this session";
        if (!Running)
        {
            if (View != null && View.Phase == TagPhase.Over && Time.unscaledTime - ViewAt < ResultsFor) return "Round over";
            return IsHost ? "No round: start one" : "No round (the host starts them)";
        }
        string t = Clock(TimeLeft);
        if (View.Mode == TagMode.Tag) return $"Tag · {TagCars.ShortName(View.It)} is it · {t} left";
        return View.Phase == TagPhase.HeadStart ? $"Hide and seek · head start {Clock(HeadStartLeft)}" : $"Hide and seek · {View.Players.Count(p => !p.Seeker)} hiders left · {t}";
    }

    internal static string Clock(float seconds)
    {
        int s = Mathf.Max(0, Mathf.CeilToInt(seconds));
        return $"{s / 60}:{s % 60:00}";
    }

    internal static void LogOnce(string id, string message)
    {
        if (LoggedOnce.Add(id)) TagPlugin.Logger.LogWarning(message);
    }
}
