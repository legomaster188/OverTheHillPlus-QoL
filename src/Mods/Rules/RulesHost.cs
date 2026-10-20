using System;
using System.Collections.Generic;
using Fusion;
using HarmonyLib;
using OverTheHillPlus.Features.Public.Filter;
using OverTheHillPlus.Features.Public.PlayerList;
using OverTheHillPlus.Multiplayer;
using UnityEngine;
using Net = OverTheHillPlus.Features.Public.ModNet.ModNet;

namespace OverTheHillPlus.Rules;

/// <summary>
/// The host's side of Rules+: the welcome and rules for each player who joins, timed announcements, and vote
/// kicks. Everyone sees the welcome and announcements on core's host notice pop-up (<see cref="HostNotice"/>, so
/// players without mods see them too). A player with Rules+ gets the rules in a window only they see (over the
/// mod network); one without it gets them as notices, which everyone sees. Only the host acts: every entry point
/// checks that this game hosts a multiplayer session.
/// </summary>
internal static class RulesHost
{
    private sealed class Pending
    {
        public float At;
        public PlayerRef Player;
        /// <summary>The welcome was handled (sent or not), so a retry never repeats it.</summary>
        public bool Welcomed;
        /// <summary>The welcome went out as a notice: the joiner's rules window then leaves it out.</summary>
        public bool WelcomeSent;
        /// <summary>Waited once more for the joiner's mod network handshake.</summary>
        public bool Retried;
    }

    /// <summary>Seconds to wait once more when a joiner has not finished the mod network handshake yet.</summary>
    private const float HandshakeRetry = 6f;

    private static readonly List<Pending> Joins = new();
    private static readonly List<(float at, string text, Color color)> Notices = new();
    private static readonly Announcer Announcer = new();
    private static readonly NoticeWindow RulesNotices = new() { Window = 180f };
    private static readonly Dictionary<int, float> AskedRules = new();
    internal static readonly VoteBook Votes = new();

    private static IntPtr _runner;
    private static float _nextSlow, _othersAt = -100f;
    private static bool _others;
    private static VoteOutcome _lastSent = VoteOutcome.Cancelled;
    private static int _lastSentId, _lastYes = -1, _lastNo = -1, _lastEligible = -1;

    /// <summary>Hosting a multiplayer session (not solo, not a guest).</summary>
    public static bool Hosting
    {
        get
        {
            var r = GameState.Runner;
            try { return r != null && r.IsRunning && r.IsServer && !r.IsSinglePlayer; } catch (Exception ex) { DebugLog.Error("rules", ex, "RulesHost.Hosting"); return false; }
        }
    }

    public static int LocalRaw
    {
        get
        {
            try { return GameState.Runner?.LocalPlayer.RawEncoded ?? 0; } catch (Exception ex) { DebugLog.Error("rules", ex, "RulesHost.LocalRaw"); return 0; }
        }
    }

    // ---------------------------------------------------------------- joins

    public static void OnPlayerJoined(PlayerRef player)
    {
        if (!Hosting || !player.IsRealPlayer) return;
        if (player.RawEncoded == LocalRaw) return;
        if (!RulesConfig.WelcomeEnabled.Value && !RulesConfig.RulesOnJoin.Value) return;
        float delay = Mathf.Clamp(RulesConfig.WelcomeDelay.Value, 2f, 60f);
        DebugLog.Verbose(RulesPlugin.ModId, () => $"player {player.PlayerId} joined: welcome and rules in {delay:0} s");
        Joins.Add(new Pending { At = Time.unscaledTime + delay, Player = player });
    }

    private static void Greet(Pending job)
    {
        var player = job.Player;
        var r = GameState.Runner;
        try { if (r == null || !r.IsPlayerValid(player)) return; } catch (Exception ex) { DebugLog.Error("rules", ex, "RulesHost.Greet"); return; }
        string joiner = HostNotice.SafeName(NameOf(player), "a new player");
        DebugLog.Verbose(RulesPlugin.ModId, () => $"greeting player {player.PlayerId}: welcomed {job.Welcomed}, modded {Net.PeerHas(player, RulesWire.Channel)}, retried {job.Retried}");
        if (!job.Welcomed)
        {
            job.Welcomed = true;
            string welcome = Welcome(joiner);
            job.WelcomeSent = RulesConfig.WelcomeEnabled.Value && welcome.Length > 0 &&
                              HostNotice.Announce(welcome, HostNotice.ColorFor(RulesConfig.WelcomeColor.Value));
        }
        if (!RulesConfig.RulesOnJoin.Value) return;
        if (Net.PeerHas(player, RulesWire.Channel))
        {
            // The welcome already showed as a notice (the joiner sees it too): the window has the rules only.
            SendRules(player, joiner, includeWelcome: !job.WelcomeSent);
            return;
        }
        // A modded joiner's handshake can still be under way: look once more before treating them as vanilla.
        if (!job.Retried && Net.HostAdvertised)
        {
            job.Retried = true;
            job.At = Time.unscaledTime + HandshakeRetry;
            Joins.Add(job);
            return;
        }
        if (!RulesConfig.RulesAsNotices.Value) return;
        // Notices reach everyone: at most one broadcast per window, later joins are merged into the next one.
        if (RulesNotices.Request(Time.unscaledTime)) QueueRuleNotices(Time.unscaledTime + 4f);
    }

    /// <summary>The title and each rule as notices, spaced out (the game shows one notice at a time).</summary>
    private static int QueueRuleNotices(float at)
    {
        var lines = RuleLines();
        if (lines.Count == 0) return 0;
        var color = HostNotice.ColorFor(RulesConfig.WelcomeColor.Value);
        string title = Clean(RulesConfig.RulesTitle.Value, RulesWire.MaxTitleChars);
        if (title.Length > 0) { Notices.Add((at, title, color)); at += 4f; }
        for (int i = 0; i < lines.Count; i++, at += 4f) Notices.Add((at, RulesText.RuleNotice(i, lines[i]), color));
        return lines.Count;
    }

    /// <summary>The welcome for <paramref name="joiner"/> (already a safe name), or "" when the template fails the filter.</summary>
    internal static string Welcome(string joiner)
    {
        string template = HostNotice.Escape(RulesConfig.WelcomeMessage.Value ?? "");
        if (NameFilter.IsBlocked(template, out _)) return "";
        string host = HostNotice.SafeName(NameOf(GameState.Runner?.LocalPlayer ?? PlayerRef.None), "the host");
        string text = RulesText.Clip(RulesText.Format(template, joiner, host), RulesWire.MaxWelcomeChars);
        return NameFilter.IsBlocked(text, out _) ? "" : text;
    }

    /// <summary>The rules as they would be sent: markup neutralised, blank and filtered lines left out.</summary>
    internal static List<string> RuleLines()
    {
        var raw = new List<string>();
        foreach (var e in RulesConfig.RuleLines) raw.Add(HostNotice.Escape(e?.Value ?? ""));
        return RulesText.Lines(raw, RulesWire.MaxRuleChars, s => NameFilter.IsBlocked(s, out _));
    }

    internal static RulesPopup Popup(string joiner, bool includeWelcome) => new()
    {
        Title = Clean(RulesConfig.RulesTitle.Value, RulesWire.MaxTitleChars),
        Welcome = includeWelcome && RulesConfig.WelcomeEnabled.Value ? Welcome(joiner) : "",
        Rules = RuleLines(),
    };

    private static string Clean(string s, int max)
    {
        s = RulesText.Clip(HostNotice.Escape(s ?? "").Trim(), max);
        return NameFilter.IsBlocked(s, out _) ? "" : s;
    }

    private static void SendRules(PlayerRef player, string joiner, bool includeWelcome)
    {
        var popup = Popup(joiner, includeWelcome);
        if (popup.Rules.Count == 0 && popup.Welcome.Length == 0) return;
        Net.SendTo(player, RulesWire.Channel, RulesWire.MsgRules, RulesWire.EncodeRules(popup));
    }

    /// <summary>Host button: the rules as notices for everyone now, and the window for every guest with the mod.</summary>
    public static string SendRulesToEveryone()
    {
        if (!Hosting) return "Blocked: host a multiplayer session first.";
        if (RuleLines().Count == 0) return "Blocked: no rules to send (empty, or they failed the word filter).";
        int windows = Net.Broadcast(RulesWire.Channel, RulesWire.MsgRules, RulesWire.EncodeRules(Popup("everyone", false)));
        int n = QueueRuleNotices(Time.unscaledTime);
        return $"Sent: {n} rule(s) as notices, and the rules window to {windows} player(s) with Rules+.";
    }

    // ---------------------------------------------------------------- messages from guests

    internal static void OnGuestMessage(Net.Message msg)
    {
        if (!Hosting) return;
        int from = msg.From.RawEncoded;
        float now = Time.unscaledTime;
        switch (msg.Type)
        {
            case RulesWire.MsgAskRules:
                if (AskedRules.TryGetValue(from, out float at) && now - at < 10f) return;
                if (AskedRules.Count > 128) AskedRules.Clear();
                AskedRules[from] = now;
                SendRules(msg.From, HostNotice.SafeName(NameOf(msg.From), "there"), includeWelcome: false);
                break;
            case RulesWire.MsgStartVote:
                if (!RulesWire.DecodeStartVote(msg.Payload, out int target)) return;
                string why = StartVote(from, target, fromGuest: true);
                if (why != null) Net.SendTo(msg.From, RulesWire.Channel, RulesWire.MsgNotice, RulesWire.EncodeNotice("Vote not started: " + why));
                break;
            case RulesWire.MsgCast:
                if (!RulesWire.DecodeCast(msg.Payload, out int id, out bool yes)) return;
                if (Votes.Current == null || Votes.Current.Id != id) return;
                Votes.Cast(from, yes, now);
                break;
        }
    }

    // ---------------------------------------------------------------- votes

    /// <summary>Everyone who may vote: the guests running Rules+, and the host when it votes.</summary>
    private static List<int> Voters()
    {
        var list = new List<int>();
        if (RulesConfig.HostVotes.Value) list.Add(LocalRaw);
        var r = GameState.Runner;
        foreach (var p in Net.Peers)
        {
            if (!p.Channels.ContainsKey(RulesWire.Channel)) continue;
            try { if (r == null || !r.IsPlayerValid(p.Ref)) continue; } catch (Exception ex) { DebugLog.Error("rules", ex, "RulesHost.Voters"); continue; }
            list.Add(p.Ref.RawEncoded);
        }
        return list;
    }

    /// <summary>Starts a vote (host or a guest's request); null when it started, otherwise why not.</summary>
    internal static string StartVote(int starter, int target, bool fromGuest)
    {
        if (!Hosting) return "only the host runs votes";
        if (!RulesConfig.VoteKickEnabled.Value) return "vote kick is off on this server";
        // P10 is enforced here, on the host: a guest's own check can be skipped by a modified client.
        if (fromGuest)
        {
            string refusal = VoteBook.GuestStartRefusal(GameState.JoinedPrivateSession);
            if (refusal != null) return refusal;
        }
        var r = GameState.Runner;
        var targetRef = PlayerRef.FromEncoded(target);
        try { if (r == null || !targetRef.IsRealPlayer || !r.IsPlayerValid(targetRef)) return "that player is not in the session"; }
        catch (Exception ex) { DebugLog.Error("rules", ex, "RulesHost.StartVote"); return "that player is not in the session"; }
        Votes.MinYes = Math.Max(2, RulesConfig.MinYes.Value);
        string why = Votes.TryStart(starter, target, LocalRaw, Voters(), Time.unscaledTime);
        if (why != null) return why;
        DebugLog.Info(RulesPlugin.ModId, $"vote to remove player {targetRef.PlayerId} started by player {PlayerRef.FromEncoded(starter).PlayerId}{(fromGuest ? " (a guest's request)" : "")}; needs {Votes.MinYes}+ yes");
        RulesPlugin.Logger.LogInfo($"[Rules] Vote to remove {NameOf(targetRef)} started by {NameOf(PlayerRef.FromEncoded(starter))}.");
        Publish(true);
        return null;
    }

    /// <summary>The host's own ballot.</summary>
    internal static string CastAsHost(bool yes)
    {
        var v = Votes.Current;
        if (v == null || v.Outcome != VoteOutcome.Open) return v != null && v.Live ? "the vote is already decided" : "no vote is running";
        string why = Votes.Cast(LocalRaw, yes, Time.unscaledTime);
        if (why == null) Publish(true);
        return why;
    }

    internal static string Veto()
    {
        var v = Votes.Current;
        if (!Hosting || v == null || !v.Live) return "Blocked: no vote is running.";
        Votes.Veto();
        DebugLog.Info(RulesPlugin.ModId, $"vote to remove player {PlayerRef.FromEncoded(v.Target).PlayerId} vetoed by the host");
        RulesPlugin.Logger.LogInfo($"[Rules] Vote to remove {NameOf(PlayerRef.FromEncoded(v.Target))} vetoed by the host.");
        Publish(true);
        Votes.Clear();
        return "Vetoed: the vote is over.";
    }

    private static VoteOutcome _lastLoggedOutcome = VoteOutcome.Open;

    private static void VoteTick()
    {
        var v = Votes.Current;
        if (v == null) return;
        if (!RulesConfig.VoteKickEnabled.Value && v.Live) Votes.Veto();
        var r = GameState.Runner;
        bool targetHere;
        try { targetHere = r != null && r.IsPlayerValid(PlayerRef.FromEncoded(v.Target)); } catch (Exception ex) { DebugLog.Error("rules", ex, "RulesHost.VoteTick"); targetHere = false; }
        if (!targetHere) Votes.TargetLeft();
        float now = Time.unscaledTime;
        // A voter whose game stopped answering on the channel no longer counts.
        foreach (int e in new List<int>(v.Eligible))
        {
            if (e == LocalRaw) { if (!RulesConfig.HostVotes.Value) Votes.VoterLeft(e, now); continue; }
            if (!Net.PeerHas(PlayerRef.FromEncoded(e), RulesWire.Channel)) Votes.VoterLeft(e, now);
        }
        var outcome = Votes.Evaluate(now);
        if (outcome != _lastLoggedOutcome)
        {
            _lastLoggedOutcome = outcome;
            DebugLog.Info(RulesPlugin.ModId, $"vote {v.Id} on player {PlayerRef.FromEncoded(v.Target).PlayerId}: {outcome} ({v.Yes} yes, {v.No} no, {v.Eligible.Count} eligible)");
        }
        if (outcome == VoteOutcome.Passed) Kick(v);
        Publish(false);
        if (outcome != VoteOutcome.Open && outcome != VoteOutcome.Passing) Votes.Clear();
    }

    private static void Kick(VoteBook.Vote v)
    {
        var target = PlayerRef.FromEncoded(v.Target);
        string name = HostNotice.SafeName(NameOf(target));
        try
        {
            // Never the host: the book refuses it, and this checks the live session too.
            var sp = OverlandSessionPlayers.Instance;
            if (v.Target == LocalRaw || (sp != null && sp.HostPlayerRef.RawEncoded == v.Target)) return;
            var runner = OverlandNetworkRunner.Instance;
            if (runner == null || !Hosting) return;
            runner.KickPlayer(target);
            DebugLog.Info(RulesPlugin.ModId, $"player {target.PlayerId} kicked by a vote ({v.Yes} of {v.Eligible.Count})");
            Votes.NoteKick(Time.unscaledTime);
            RulesPlugin.Logger.LogInfo($"[Rules] {NameOf(target)} removed by a vote ({v.Yes} of {v.Eligible.Count}).");
            HostNotice.Announce($"{name} was removed by a vote ({v.Yes} of {v.Eligible.Count})", HostNotice.ColorFor("Red"));
        }
        catch (Exception e) { DebugLog.Error("rules", e, "RulesHost.Kick"); RulesPlugin.Logger.LogWarning("[Rules] Vote kick failed: " + e.Message); }
    }

    /// <summary>Sends the vote's state to the voters with the mod when it changed (and shows it here).</summary>
    private static void Publish(bool force)
    {
        var v = Votes.Current;
        if (v == null) return;
        int yes = v.Yes, no = v.No, eligible = v.Eligible.Count;
        if (!force && v.Id == _lastSentId && v.Outcome == _lastSent && yes == _lastYes && no == _lastNo && eligible == _lastEligible) return;
        _lastSentId = v.Id;
        _lastSent = v.Outcome;
        _lastYes = yes;
        _lastNo = no;
        _lastEligible = eligible;
        var view = new VoteView
        {
            Id = v.Id, Target = v.Target, Yes = yes, No = no, Needed = v.NeededNow, Eligible = eligible,
            TargetName = HostNotice.SafeName(NameOf(PlayerRef.FromEncoded(v.Target))),
            StarterName = HostNotice.SafeName(NameOf(PlayerRef.FromEncoded(v.Starter))),
            SecondsLeft = v.SecondsLeft(Time.unscaledTime), Outcome = v.Outcome,
        };
        byte[] payload = RulesWire.EncodeVoteState(view);
        foreach (int e in v.Eligible)
        {
            if (e == LocalRaw) continue;
            Net.SendTo(PlayerRef.FromEncoded(e), RulesWire.Channel, RulesWire.MsgVoteState, payload);
        }
        // The target sees the result only (not a running count of votes against them).
        if (!v.Live && Net.PeerHas(PlayerRef.FromEncoded(v.Target), RulesWire.Channel))
            Net.SendTo(PlayerRef.FromEncoded(v.Target), RulesWire.Channel, RulesWire.MsgVoteState, payload);
        RulesClient.OnVoteState(view, v.Eligible.Contains(LocalRaw), v.Ballots.TryGetValue(LocalRaw, out bool mine) ? mine : null);
    }

    internal static void PeerGone(PlayerRef p)
    {
        if (Votes.Current != null) Votes.VoterLeft(p.RawEncoded, Time.unscaledTime);
    }

    // ---------------------------------------------------------------- per frame

    internal static void Tick()
    {
        try { TickInner(); }
        catch (Exception e) { DebugLog.Error("rules", e, "RulesHost.Tick"); LogOnce("tick", "[Rules] Tick: " + e); }
    }

    private static void TickInner()
    {
        var r = GameState.Runner;
        IntPtr ptr = IntPtr.Zero;
        try { ptr = r == null ? IntPtr.Zero : r.Pointer; } catch (Exception ex) { DebugLog.Error("rules", ex, "RulesHost.TickInner"); }
        if (ptr != _runner)
        {
            // A new session (or none): forget everything from the last one.
            _runner = ptr;
            Joins.Clear();
            Notices.Clear();
            AskedRules.Clear();
            RulesNotices.Reset();
            Votes.Reset();
            RulesClient.Reset();
            _lastSentId = 0;
            Announcer.Reset(Time.unscaledTime);
        }
        if (!Hosting) return;
        float now = Time.unscaledTime;
        for (int i = Joins.Count - 1; i >= 0; i--)
        {
            if (now < Joins[i].At) continue;
            var job = Joins[i];
            Joins.RemoveAt(i);
            try { Greet(job); } catch (Exception e) { DebugLog.Error("rules", e, "RulesHost.TickInner"); RulesPlugin.Logger.LogWarning("[Rules] Welcome failed: " + e.Message); }
        }
        for (int i = 0; i < Notices.Count; i++)
        {
            if (now < Notices[i].at) continue;
            var n = Notices[i];
            Notices.RemoveAt(i);
            HostNotice.Announce(n.text, n.color);
            break; // one per frame at most
        }
        if (RulesNotices.Due(now) && RulesConfig.RulesOnJoin.Value && RulesConfig.RulesAsNotices.Value) QueueRuleNotices(now);
        if (now < _nextSlow) return;
        _nextSlow = now + 0.5f;
        // Caught here: a tick that throws is switched off by core for the rest of the session.
        try { VoteTick(); }
        catch (Exception e) { DebugLog.Error("rules", e, "RulesHost.TickInner"); LogOnce("vote", "[Rules] Vote: " + e); }
        try { AnnounceTick(now); }
        catch (Exception e) { DebugLog.Error("rules", e, "RulesHost.TickInner"); LogOnce("announce", "[Rules] Announcement: " + e); }
    }

    private static readonly HashSet<string> Logged = new();

    private static void LogOnce(string key, string message)
    {
        if (Logged.Add(key)) RulesPlugin.Logger.LogWarning(message);
    }

    private static void AnnounceTick(float now)
    {
        if (!RulesConfig.AnnounceEnabled.Value) { Announcer.Reset(now); return; }
        if (now - _othersAt > 5f)
        {
            _othersAt = now;
            try { _others = PlayerListRoster.AllPlayers().Count > 1; } catch (Exception ex) { DebugLog.Error("rules", ex, "RulesHost.AnnounceTick"); _others = false; }
        }
        if (!_others) return;
        var messages = new List<string>();
        foreach (var e in RulesConfig.Announcements) messages.Add(e?.Value);
        string text = Announcer.Next(now, Mathf.Max(1, RulesConfig.AnnounceMinutes.Value) * 60f, messages);
        if (text == null) return;
        if (!HostNotice.Announce(RulesText.Clip(text, RulesWire.MaxWelcomeChars), HostNotice.ColorFor(RulesConfig.AnnounceColor.Value)))
            RulesPlugin.Logger.LogInfo("[Rules] Announcement not sent (empty, or it would contain a blocked word).");
    }

    internal static string NameOf(PlayerRef p)
    {
        try { return PlayerListRoster.NameOf(OverlandSessionPlayers.Instance, p); }
        catch (Exception ex) { DebugLog.Error("rules", ex, "RulesHost.NameOf"); return $"Player {p.PlayerId}"; }
    }
}

[HarmonyPatch(typeof(OverlandNetworkRunner), nameof(OverlandNetworkRunner.Fusion_INetworkRunnerCallbacks_OnPlayerJoined))]
internal static class RulesPlayerJoinedPatch
{
    private static void Postfix(PlayerRef player)
    {
        try { RulesHost.OnPlayerJoined(player); }
        catch (Exception e) { DebugLog.Error("rules", e, "RulesPlayerJoinedPatch.Postfix"); RulesPlugin.Logger.LogWarning("[Rules] Join: " + e.Message); }
    }
}
