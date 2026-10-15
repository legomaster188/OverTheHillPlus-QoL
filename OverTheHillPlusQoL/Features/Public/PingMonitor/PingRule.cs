using System;
using System.Collections.Generic;
using Fusion;
using OverTheHillPlus.Features.Public.ModNet;
using OverTheHillPlus.Features.Public.Moderation;
using UnityEngine;
using Net = OverTheHillPlus.Features.Public.ModNet.ModNet;

namespace OverTheHillPlus.Features.Public.PingMonitor;

/// <summary>
/// The host's optional high-ping rule, and the mod network channel of the ping monitor.
///
/// Rule (off by default, host only): a guest whose ping (median of the last three samples) stays above the limit
/// for the set time is warned, or removed from the session if the host chose that. Safeguards:
/// - a grace period after each player joins (loading in spikes the ping);
/// - time above the limit drains back down while the ping is under it, so one good second doesn't reset it but a
///   short spike doesn't add up either;
/// - nothing counts while most guests are over the limit at once (that is the host's own connection or a host
///   stall), within 10 s of any join (the host freezes while it loads the new car), or right after a long frame;
/// - after a warning the player gets 2 minutes before the next one; at most 3 removals a minute (and removals go
///   through <see cref="ModerationCore.Kick"/>: the moderation log, the shared cap of 6 a minute with the AFK watch,
///   and by default the removed player may join again once their connection is better).
///
/// Guests running this mod with the mod network on get a notice before anything happens (at half the time), and
/// when the limit is hit; the host's rule is shown in their session HUD. Only numbers are sent, never text, so
/// nothing here needs the name filter. Vanilla guests get nothing (the host's game can't message them).
///
/// Echo: a modded guest whose game has no ping reading asks the host to echo a timestamp every 2 s.
/// </summary>
internal static class PingRule
{
    private const byte MsgEcho = 1, MsgEchoReply = 2, MsgRule = 3, MsgNotice = 4;
    public const byte NoticeWarning = 1, NoticeLimit = 2, NoticeRemoved = 3;

    private const float JoinPause = 10f;
    private const float WarnCooldown = 120f;
    private const float KickDelay = 2f;           // lets the "removed" notice arrive before the connection drops
    private const int MaxKicksPerMinute = 3;

    private static readonly Queue<float> KickTimes = new();
    private static string _lastRuleSig = "";
    private static float _ruleChangedAt = -1f, _nextEcho;
    private static int _echoSeq;

    /// <summary>Host: one line about what the rule is doing (for the menu).</summary>
    public static string HostState { get; private set; } = "";
    public static int Warned { get; private set; }
    public static int Removed { get; private set; }

    /// <summary>Last thing the rule did (host) or the last notice from the host (guest), for the on-screen notice.</summary>
    public static string LastEvent { get; private set; } = "";
    public static float LastEventAt { get; private set; } = -100f;
    public static bool LastEventBad { get; private set; }

    // Guest: the host's rule as it advertised it.
    public static bool HostRuleKnown { get; private set; }
    public static bool HostRuleOn { get; private set; }
    public static int HostRuleMs { get; private set; }
    public static int HostRuleSeconds { get; private set; }
    public static bool HostRuleKicks { get; private set; }

    internal static void RegisterChannel()
    {
        Net.Register(Net.Channels.PingMonitor, new Net.ChannelOptions
        {
            Name = "ping monitor",
            Version = 1,
            MaxPayload = 32,
            RatePerSecond = 2f,
            Burst = 6,
            HostOnlySender = true, // guests only listen to the host itself
        }, OnMessage);
        Net.OnPeerReady(p => SendRule(p));
        Net.OnSessionReset(ResetGuest);
        PingTracker.SessionReset += () => { ResetGuest(); KickTimes.Clear(); HostState = ""; };
    }

    private static void ResetGuest()
    {
        HostRuleKnown = HostRuleOn = HostRuleKicks = false;
        HostRuleMs = HostRuleSeconds = 0;
    }

    // ---------------------------------------------------------------- per frame

    public static void Tick()
    {
        if (!Net.IsHost) return;
        // Tell modded guests when the rule changes (once the sliders have settled for a second).
        string sig = RuleSignature();
        if (sig != _lastRuleSig)
        {
            if (_ruleChangedAt < 0f) _ruleChangedAt = Time.unscaledTime;
            else if (Time.unscaledTime - _ruleChangedAt > 1f)
            {
                _lastRuleSig = sig;
                _ruleChangedAt = -1f;
                if (Net.PeerCount > 0) Net.Broadcast(Net.Channels.PingMonitor, MsgRule, RulePayload());
            }
        }
    }

    /// <summary>Host, after each sample (once a second).</summary>
    public static void Evaluate()
    {
        var players = PingTracker.Players;
        if (!PingTracker.Hosting || !GameState.IsAuthority)
        {
            HostState = "";
            return;
        }
        if (!PingMonitorFeature.RuleEnabled.Value)
        {
            foreach (var e in players) ClearRule(e);
            HostState = "off";
            return;
        }

        float now = Time.unscaledTime;
        float limit = PingMonitorFeature.RuleMs.Value;
        float need = Math.Max(5, PingMonitorFeature.RuleSeconds.Value);
        float grace = Math.Max(0, PingMonitorFeature.RuleGrace.Value);
        float dt = PingTracker.LastDelta;
        bool kick = PingMonitorFeature.RuleKick.Value;

        int measured = 0, over = 0;
        foreach (var e in players)
        {
            if (e.IsYou || e.Stats.Count == 0 || e.Misses > 0) continue;
            measured++;
            if (e.Stats.Median3() > limit) over++;
        }
        bool everyone = measured >= 2 && over >= Math.Ceiling(measured * 0.6);
        bool joinPause = now - PingTracker.LastJoinAt < JoinPause;
        bool stalled = PingTracker.LastSampleStalled;
        HostState = everyone ? "paused: most players are over the limit at once (your connection?)"
            : joinPause ? "paused while a player joins"
            : stalled ? "paused: your game stalled"
            : $"watching {measured} player(s)";

        foreach (var e in players)
        {
            if (e.IsYou) continue;
            if (e.KickAt > 0f)
            {
                if (now >= e.KickAt) DoKick(e);
                continue;
            }
            float age = now - e.FirstSeen;
            if (age < grace)
            {
                e.AboveFor = 0f;
                e.HalfWarned = false;
                e.RuleState = $"grace {Mathf.CeilToInt(grace - age)} s";
                continue;
            }
            float ms = e.Misses > 0 ? -1f : e.Stats.Median3();
            if (ms < 0f) { e.RuleState = "no reading"; continue; }
            if (ms <= limit)
            {
                e.AboveFor = Math.Max(0f, e.AboveFor - dt * 2f);
                if (e.AboveFor <= 0f) e.HalfWarned = false;
                if (now < e.CooldownUntil) { if (!e.RuleState.StartsWith("remove", StringComparison.Ordinal)) e.RuleState = "warned"; }
                else e.RuleState = e.AboveFor > 0f ? $"{e.AboveFor:0}/{need:0} s" : "";
                continue;
            }
            if (everyone || joinPause || stalled) { e.RuleState = "paused"; continue; }
            if (now < e.CooldownUntil) { if (!e.RuleState.StartsWith("remove", StringComparison.Ordinal)) e.RuleState = "warned"; continue; }

            e.AboveFor += dt;
            e.RuleState = $"{e.AboveFor:0}/{need:0} s";
            if (!e.HalfWarned && e.AboveFor >= need * 0.5f)
            {
                e.HalfWarned = true;
                Notify(e, NoticeWarning, ms, limit, Mathf.CeilToInt(need - e.AboveFor), kick);
            }
            if (e.AboveFor >= need) Act(e, ms, limit, kick, now);
        }
    }

    private static void ClearRule(PlayerPing e)
    {
        e.AboveFor = 0f;
        e.HalfWarned = false;
        e.KickAt = -1f;
        e.RuleState = "";
    }

    private static void Act(PlayerPing e, float ms, float limit, bool kick, float now)
    {
        e.KickMs = ms;
        e.KickLimit = limit;
        while (KickTimes.Count > 0 && now - KickTimes.Peek() > 60f) KickTimes.Dequeue();
        if (kick && KickTimes.Count < MaxKicksPerMinute)
        {
            KickTimes.Enqueue(now);
            Notify(e, NoticeRemoved, ms, limit, 0, true);
            e.KickAt = now + KickDelay;
            e.RuleState = "removing...";
            Report($"Removing {e.Name}: ping {ms:0} ms over the {limit:0} ms limit", true);
            return;
        }
        Notify(e, NoticeLimit, ms, limit, 0, false);
        Warned++;
        e.CooldownUntil = now + WarnCooldown;
        e.AboveFor = 0f;
        e.HalfWarned = false;
        e.RuleState = "warned";
        Report(kick
            ? $"{e.Name} is over the ping limit ({ms:0} ms); not removed: 3 removals in the last minute already"
            : $"{e.Name} is over the ping limit ({ms:0} ms > {limit:0} ms)" + (e.Modded ? ", told them" : " (no mod: can't tell them)"), false);
    }

    private static void DoKick(PlayerPing e)
    {
        e.KickAt = -1f;
        // Whatever happens next, start over: a player who is still listed next second must not be removed twice.
        e.AboveFor = 0f;
        e.HalfWarned = false;
        e.CooldownUntil = Time.unscaledTime + 30f;
        // Re-check everything: the host may have switched the rule off during the delay, or the player left.
        if (!PingMonitorFeature.RuleEnabled.Value || !PingMonitorFeature.RuleKick.Value || !Net.IsHost || !GameState.IsAuthority)
        {
            e.RuleState = "";
            return;
        }
        try
        {
            var r = GameState.Runner;
            if (r == null || !r.IsPlayerValid(e.Ref)) { e.RuleState = ""; return; }
            // The one place that removes players: logged, counted toward the shared cap, and (by default) the game's
            // session ban is lifted so a guest removed for a bad stretch of ping can come back.
            string res = ModerationCore.Kick(e.Ref, $"ping {e.KickMs:0} ms over the {e.KickLimit:0} ms limit",
                PingMonitorFeature.RuleAllowRejoin.Value);
            if (ModerationCore.WasRemoved(res))
            {
                Removed++;
                e.RuleState = "removed";
                Report($"Removed {e.Name} for high ping", true);
            }
            else if (res == ModerationCore.AlreadyLeft) e.RuleState = "";
            else
            {
                e.RuleState = "remove failed";
                Report("Could not remove " + e.Name + ": " + res, true);
            }
        }
        catch (Exception ex)
        {
            e.RuleState = "remove failed";
            PingTracker.LogOnce("kick", "[Ping] Removing a player failed: " + ex.Message);
            Report("Could not remove " + e.Name + ": " + ex.Message, true);
        }
    }

    private static void Report(string text, bool bad)
    {
        LastEvent = text;
        LastEventAt = Time.unscaledTime;
        LastEventBad = bad;
        Plugin.Logger.LogInfo("[Ping] " + text);
        try { Menu.SetStatus(text); } catch { }
    }

    // ---------------------------------------------------------------- messages

    private static void Notify(PlayerPing e, byte kind, float ms, float limit, int secondsLeft, bool kicks)
    {
        if (!e.Modded) return;
        var payload = new NetWriter(16).Byte(kind).Int((int)ms).Int((int)limit).Int(secondsLeft).Bool(kicks).ToArray();
        var res = Net.SendTo(e.Ref, Net.Channels.PingMonitor, MsgNotice, payload);
        if (res != Net.SendResult.Sent && res != Net.SendResult.NotModded)
            PingTracker.LogOnce("notice" + res, "[Ping] Could not send a ping notice: " + res);
    }

    private static string RuleSignature() =>
        PingMonitorFeature.RuleEnabled.Value + "|" + PingMonitorFeature.RuleMs.Value + "|" +
        PingMonitorFeature.RuleSeconds.Value + "|" + PingMonitorFeature.RuleKick.Value;

    private static byte[] RulePayload() =>
        new NetWriter(16)
            .Bool(PingMonitorFeature.RuleEnabled.Value)
            .Int(PingMonitorFeature.RuleMs.Value)
            .Int(PingMonitorFeature.RuleSeconds.Value)
            .Bool(PingMonitorFeature.RuleKick.Value)
            .ToArray();

    private static void SendRule(PlayerRef p)
    {
        try
        {
            if (Net.IsHost && Net.PeerHas(p, Net.Channels.PingMonitor))
                Net.SendTo(p, Net.Channels.PingMonitor, MsgRule, RulePayload());
        }
        catch (Exception e) { PingTracker.LogOnce("rule", "[Ping] Could not send the ping rule: " + e.Message); }
    }

    /// <summary>Guest: asks a modded host to echo a timestamp (at most every 2 s).</summary>
    public static void RequestEcho()
    {
        if (Time.unscaledTime < _nextEcho || !Net.IsGuest || !Net.HostHas(Net.Channels.PingMonitor)) return;
        _nextEcho = Time.unscaledTime + 2f;
        _echoSeq++;
        var payload = new NetWriter(8).Int(_echoSeq).Float(Time.realtimeSinceStartup).ToArray();
        Net.SendToHost(Net.Channels.PingMonitor, MsgEcho, payload);
    }

    private static void OnMessage(Net.Message m)
    {
        var rd = new NetReader(m.Payload);
        if (Net.IsHost)
        {
            if (m.Type != MsgEcho || !m.From.IsRealPlayer) return;
            if (m.Payload == null || m.Payload.Length > 16) return;
            Net.SendTo(m.From, Net.Channels.PingMonitor, MsgEchoReply, m.Payload);
            return;
        }
        if (!m.FromHost) return;
        switch (m.Type)
        {
            case MsgEchoReply:
            {
                int seq = rd.Int();
                float sent = rd.Float();
                if (!rd.Ok || seq != _echoSeq) return; // only the newest echo: an old one would read too high
                PingTracker.AddEchoSample((Time.realtimeSinceStartup - sent) * 1000f);
                break;
            }
            case MsgRule:
            {
                bool on = rd.Bool();
                int ms = rd.Int(), secs = rd.Int();
                bool kicks = rd.Bool();
                if (!rd.Ok) return;
                HostRuleKnown = true;
                HostRuleOn = on;
                HostRuleMs = Mathf.Clamp(ms, 0, 100000);
                HostRuleSeconds = Mathf.Clamp(secs, 0, 100000);
                HostRuleKicks = kicks;
                break;
            }
            case MsgNotice:
            {
                byte kind = rd.Byte();
                int ms = rd.Int(), limit = rd.Int(), left = rd.Int();
                bool kicks = rd.Bool();
                if (!rd.Ok) return;
                string text = kind switch
                {
                    NoticeWarning => $"High ping: {ms} ms to the host (its limit is {limit} ms). " +
                                     (kicks ? $"You'll be removed in about {left} s if it stays this high." : $"Warning in about {left} s if it stays this high."),
                    NoticeLimit => $"The host flagged your ping: {ms} ms, over its {limit} ms limit.",
                    NoticeRemoved => $"The host is removing you for high ping ({ms} ms, limit {limit} ms).",
                    _ => null,
                };
                if (text == null) return;
                GuestNotice(text, kind != NoticeWarning);
                break;
            }
        }
    }

    private static void GuestNotice(string text, bool bad)
    {
        LastEvent = text;
        LastEventAt = Time.unscaledTime;
        LastEventBad = bad;
        Plugin.Logger.LogInfo("[Ping] Host notice: " + text);
    }
}
