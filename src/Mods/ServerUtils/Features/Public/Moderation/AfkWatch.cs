using System;
using System.Collections.Generic;
using Fusion;
using OverTheHillPlus.Features.Public.ModNet;
using OverTheHillPlus.Features.Public.PlayerList;
using OverTheHillPlus.Multiplayer;
using UnityEngine;
using UnityEngine.InputSystem;
using Net = OverTheHillPlus.Features.Public.ModNet.ModNet;
using OverTheHillPlus.ServerUtils;

namespace OverTheHillPlus.Features.Public.Moderation;

/// <summary>One guest in the host's AFK table.</summary>
internal sealed class AfkPlayer
{
    public PlayerRef Ref;
    public int Raw;
    public string Name = "";
    public bool HasCar;
    public bool HasAnchor;
    public Vector3 AnchorPos, AnchorFwd;
    public bool HasInput;
    public float Steer, Throttle, Brake;
    public int Buttons;
    public float Idle;            // seconds without activity (only counted while the player has a car)
    public float WarnedAt = -1f;  // when the warning went out (-1 = not warned)
    public float KickAt = -1f;
    public string State = "";
    public bool Modded => Net.IsPeer(Ref);
}

/// <summary>
/// Host AFK watch. In Host mode the host simulates every car, so once a second it compares each guest's car with
/// where it was at the guest's last activity: moving more than 2 m, turning more than 20 degrees, or a change in the
/// controls the guest sends (steering, throttle, brake, buttons) counts as activity. A guest idle for the set time
/// is warned (a pop-up for everyone, and a notice on screen for guests running this mod with the mod network on),
/// then, only if the host turned it on, removed when they are still idle after the response time.
///
/// Time only counts while the guest has a car in the world (not in the lobby or while loading), long host frames
/// are clamped so a stall doesn't add minutes, and the watch can be limited to when the server is full. A warned
/// guest without a car is never removed (the countdown waits until the car is back).
///
/// Modded guests can prove activity the watch can't see (spectating, typing in chat, using menus): while the host's
/// rule is on they send a small "active" message (at most one every 20 s) when they spectate, have the chat box open
/// or touched the keyboard or mouse; a chat message from a guest counts too (<see cref="MarkActive"/>).
/// Off by default.
/// </summary>
internal static class AfkWatch
{
    private const byte MsgRule = 1, MsgNotice = 2, MsgActive = 3;
    private const byte ActiveSpectating = 1, ActiveChatting = 2, ActiveInput = 3;
    private const float ActiveEvery = 20f;
    public const byte NoticeWarning = 1, NoticeRemoving = 2, NoticeCleared = 3;
    private const float MoveMeters = 2f, TurnDegrees = 20f, InputDelta = 0.15f;
    private const float KickNoticeLead = 2f; // lets the "removing" notice arrive before the connection drops

    public static readonly List<AfkPlayer> Players = new();
    private static readonly Dictionary<int, AfkPlayer> ByRaw = new();
    private static float _nextSample, _lastSample = -1f, _nextRoster;
    private static bool _inputFailed;
    private static string _lastRuleSig = "";
    private static float _ruleChangedAt = -1f;

    public static string HostState { get; private set; } = "";
    public static int Warned { get; private set; }
    public static int Removed { get; private set; }

    // Guest: the host's rule as it advertised it, and the last notice it sent.
    public static bool HostRuleKnown { get; private set; }
    public static bool HostRuleOn { get; private set; }
    public static int HostRuleMinutes { get; private set; }
    public static bool HostRuleKicks { get; private set; }
    public static int HostRuleRespond { get; private set; }
    public static string LastNotice { get; private set; } = "";
    public static float LastNoticeAt { get; private set; } = -100f;
    public static bool LastNoticeBad { get; private set; }

    internal static void RegisterChannel()
    {
        Net.Register(Net.Channels.Moderation, new Net.ChannelOptions
        {
            Name = "moderation",
            Version = 1,
            MaxPayload = 32,
            RatePerSecond = 1f,
            Burst = 4,
            HostOnlySender = true, // guests only listen to the host itself (the host takes "active" from its guests)
        }, OnMessage);
        Net.OnPeerReady(SendRule);
        Net.OnSessionReset(ResetGuest);
        PlayerActivity.Active += MarkActive; // e.g. the Chat mod received a guest's message
        ModerationCore.SessionChanged += () => { Players.Clear(); ByRaw.Clear(); HostState = ""; ResetGuest(); };
    }

    private static void ResetGuest()
    {
        HostRuleKnown = HostRuleOn = HostRuleKicks = false;
        HostRuleMinutes = HostRuleRespond = 0;
    }

    // ---------------------------------------------------------------- per frame (host)

    public static void Tick()
    {
        if (!ModerationCore.Hosting)
        {
            if (Players.Count > 0) { Players.Clear(); ByRaw.Clear(); }
            HostState = "";
            _lastSample = -1f;
            try { GuestTick(); }
            catch (Exception e) { DebugLog.Error("serverutils", e, "AfkWatch.Tick"); ModerationCore.LogOnce("afkguest", "[Moderation] AFK activity: " + e.Message); }
            return;
        }
        RuleBroadcastTick();
        float now = Time.unscaledTime;
        if (now < _nextSample) return;
        _nextSample = now + 1f;
        float dt = _lastSample < 0f ? 1f : Mathf.Clamp(now - _lastSample, 0f, 2f);
        _lastSample = now;
        try { Sample(now, dt); }
        catch (Exception e) { DebugLog.Error("serverutils", e, "AfkWatch.Tick"); ModerationCore.LogOnce("afk", "[Moderation] AFK watch: " + e); }
    }

    private static void Sample(float now, float dt)
    {
        if (now >= _nextRoster || Players.Count == 0)
        {
            _nextRoster = now + 2f;
            RefreshRoster();
        }
        bool on = ModerationFeature.AfkEnabled.Value;
        if (!on)
        {
            foreach (var e in Players) { e.Idle = 0f; e.WarnedAt = e.KickAt = -1f; e.HasAnchor = false; e.State = ""; }
            HostState = "off";
            return;
        }

        bool fullOnly = ModerationFeature.AfkOnlyWhenFull.Value;
        bool paused = fullOnly && !ServerFull();
        UpdateActivity(now, dt, paused);

        float limit = Mathf.Max(1, ModerationFeature.AfkMinutes.Value) * 60f;
        float respond = Mathf.Max(10, ModerationFeature.AfkRespondSeconds.Value);
        bool kick = ModerationFeature.AfkRemove.Value;
        int watched = 0;
        foreach (var e in Players)
        {
            if (e.HasCar) watched++;
            if (e.KickAt > 0f)
            {
                if (now >= e.KickAt) DoKick(e);
                continue;
            }
            if (e.WarnedAt < 0f)
            {
                if (e.Idle >= limit && !paused) Warn(e, kick, respond, now);
                continue;
            }
            // Warned without a car (garage, vehicle select, respawning): the watch can't see them, so the countdown
            // waits until the car is back.
            if (!e.HasCar)
            {
                e.WarnedAt += dt;
                e.State = "warned (no car)";
                continue;
            }
            // Warned: removed once the response time has passed (still idle), if the host chose that.
            float left = e.WarnedAt + respond - now;
            if (kick && left <= 0f && !paused)
            {
                Notify(e, NoticeRemoving, (int)e.Idle, 0, true);
                e.KickAt = now + KickNoticeLead;
                e.State = "removing...";
            }
            else e.State = kick ? $"warned · {Mathf.Max(0, Mathf.CeilToInt(left))} s" : "warned";
        }
        HostState = paused ? "paused: the server isn't full" : $"watching {watched} guest(s)";
    }

    private static bool ServerFull()
    {
        try
        {
            var info = GameState.Runner?.SessionInfo;
            int max = info != null && info.IsValid ? info.MaxPlayers : 0;
            return max > 0 && PlayerListRoster.AllPlayers().Count >= max;
        }
        catch (Exception ex) { DebugLog.Error("serverutils", ex, "AfkWatch.ServerFull"); return false; }
    }

    private static void RefreshRoster()
    {
        var present = new HashSet<int>();
        foreach (var p in PlayerListRoster.AllPlayers())
        {
            if (ModerationCore.IsLocal(p)) continue;
            int raw = p.RawEncoded;
            present.Add(raw);
            if (!ByRaw.TryGetValue(raw, out var e))
            {
                e = new AfkPlayer { Ref = p, Raw = raw };
                ByRaw[raw] = e;
                Players.Add(e);
            }
            e.Name = ModerationCore.NameOf(p);
        }
        for (int i = Players.Count - 1; i >= 0; i--)
        {
            if (present.Contains(Players[i].Raw)) continue;
            ByRaw.Remove(Players[i].Raw);
            Players.RemoveAt(i);
        }
        Players.Sort((a, b) => a.Raw.CompareTo(b.Raw));
    }

    /// <summary>Host: a player left or a slot was reused: forget them (a new player never inherits idle time).</summary>
    public static void PlayerJoinedOrLeft(PlayerRef p)
    {
        if (!ByRaw.TryGetValue(p.RawEncoded, out var e)) return;
        ByRaw.Remove(p.RawEncoded);
        Players.Remove(e);
        _nextRoster = 0f;
    }

    private static void UpdateActivity(float now, float dt, bool paused)
    {
        foreach (var e in Players) e.HasCar = false;
        foreach (var fp in GameState.FusionPlayers)
        {
            AfkPlayer e;
            Transform t;
            try
            {
                if (fp == null || fp.Object == null) continue;
                if (!ByRaw.TryGetValue(fp.Object.InputAuthority.RawEncoded, out e)) continue;
                var car = fp.vehicleBase;
                t = car != null ? (car.cachedTransform != null ? car.cachedTransform : car.transform) : fp.transform;
                if (t == null) continue;
            }
            catch (Exception ex) { DebugLog.Error("serverutils", ex, "AfkWatch.UpdateActivity"); continue; }
            e.HasCar = true;
            bool active = MovedOrTurned(e, t) | InputChanged(e, fp);
            if (active)
            {
                if (e.WarnedAt >= 0f || e.KickAt > 0f) Cleared(e); // also calls off a removal that is about to happen
                e.Idle = 0f;
                e.State = "";
            }
            else if (!paused) e.Idle += dt;
        }
        foreach (var e in Players)
        {
            if (e.HasCar || e.KickAt > 0f) continue;
            if (e.WarnedAt < 0f) e.State = "no car";
        }
        foreach (var e in Players)
            if (e.HasCar && e.WarnedAt < 0f && e.KickAt < 0f) e.State = e.Idle >= 60f ? $"idle {ModerationCore.Minutes(e.Idle)}" : "";
    }

    private static bool MovedOrTurned(AfkPlayer e, Transform t)
    {
        Vector3 pos, fwd;
        try { pos = t.position; fwd = t.forward; } catch (Exception ex) { DebugLog.Error("serverutils", ex, "AfkWatch.MovedOrTurned"); return false; }
        if (!IsFinite(pos) || !IsFinite(fwd)) return false;
        if (!e.HasAnchor)
        {
            e.HasAnchor = true;
            e.AnchorPos = pos;
            e.AnchorFwd = fwd;
            return false;
        }
        if ((pos - e.AnchorPos).sqrMagnitude > MoveMeters * MoveMeters || Vector3.Angle(fwd, e.AnchorFwd) > TurnDegrees)
        {
            e.AnchorPos = pos;
            e.AnchorFwd = fwd;
            return true;
        }
        return false;
    }

    /// <summary>The controls the guest sent (a networked property, readable after Spawned). Off after the first error.</summary>
    private static bool InputChanged(AfkPlayer e, FusionPlayer fp)
    {
        if (_inputFailed) return false;
        float steer, throttle, brake;
        int buttons;
        try
        {
            var input = fp.CurrentFrameInput;
            steer = input.Steering;
            throttle = input.Throttle;
            brake = input.Brake;
            buttons = input.Buttons.Bits;
        }
        catch (Exception ex)
        {
            DebugLog.Error("serverutils", ex, "AfkWatch.InputChanged");
            _inputFailed = true;
            ModerationCore.LogOnce("input", "[Moderation] Can't read guests' controls, using car movement only: " + ex.Message);
            return false;
        }
        if (!float.IsFinite(steer) || !float.IsFinite(throttle) || !float.IsFinite(brake)) return false;
        bool changed = e.HasInput && (Math.Abs(steer - e.Steer) > InputDelta || Math.Abs(throttle - e.Throttle) > InputDelta ||
                                      Math.Abs(brake - e.Brake) > InputDelta || buttons != e.Buttons);
        e.HasInput = true;
        e.Steer = steer;
        e.Throttle = throttle;
        e.Brake = brake;
        e.Buttons = buttons;
        return changed;
    }

    private static bool IsFinite(Vector3 v) => float.IsFinite(v.x) && float.IsFinite(v.y) && float.IsFinite(v.z);

    // ---------------------------------------------------------------- actions

    private static void Warn(AfkPlayer e, bool kick, float respond, float now)
    {
        e.WarnedAt = now;
        e.State = "warned";
        Warned++;
        int idleMin = Mathf.Max(1, Mathf.RoundToInt(e.Idle / 60f));
        Notify(e, NoticeWarning, (int)e.Idle, kick ? Mathf.CeilToInt(respond) : 0, kick);
        if (ModerationFeature.AfkAnnounce.Value)
        {
            string name = HostNotice.SafeName(e.Name);
            ModerationCore.Announce(kick
                ? $"{name} seems to be away ({idleMin} min). Removed in {Mathf.CeilToInt(respond)} s unless they drive."
                : $"{name} seems to be away ({idleMin} min).", HostNotice.ColorFor("Gold"));
        }
        ModerationCore.Add($"{e.Name} idle for {idleMin} min: warned" + (e.Modded ? " (mod notice sent)" : "") +
                           (kick ? $", removed in {Mathf.CeilToInt(respond)} s if still idle" : ""));
    }

    private static void Cleared(AfkPlayer e)
    {
        e.WarnedAt = -1f;
        e.KickAt = -1f;
        Notify(e, NoticeCleared, 0, 0, false);
        ModerationCore.Add($"{e.Name} is back");
    }

    private static void DoKick(AfkPlayer e)
    {
        if (!e.HasCar && ModerationFeature.AfkEnabled.Value && ModerationFeature.AfkRemove.Value)
        {
            // The car went away during the short delay: back to "warned", with 30 s once the car is back.
            float respond = Mathf.Max(10, ModerationFeature.AfkRespondSeconds.Value);
            e.KickAt = -1f;
            e.WarnedAt = Time.unscaledTime - respond + 30f;
            e.State = "warned (no car)";
            Notify(e, NoticeWarning, (int)e.Idle, 30, true);
            return;
        }
        e.KickAt = -1f;
        e.WarnedAt = -1f;
        e.Idle = 0f;
        // Re-check everything: the host may have switched removal off during the delay.
        if (!ModerationFeature.AfkEnabled.Value || !ModerationFeature.AfkRemove.Value || !ModerationCore.Hosting)
        {
            e.State = "";
            return;
        }
        string result = ModerationCore.Kick(e.Ref, "away (no driving or controls)", ModerationFeature.AfkAllowRejoin.Value);
        if (result.StartsWith("Removed", StringComparison.Ordinal))
        {
            Removed++;
            e.State = "removed";
            if (ModerationFeature.AfkAnnounce.Value)
                ModerationCore.Announce($"{HostNotice.SafeName(e.Name)} was removed for being away.", HostNotice.ColorFor("Gold"));
        }
        else e.State = "remove failed";
    }

    /// <summary>
    /// Host: the guest did something the watch can't see (a chat message, told by the Chat mod through core's
    /// <see cref="PlayerActivity"/>, or a modded guest's "active" message):
    /// resets their idle time and calls off a warning or a removal about to happen.
    /// </summary>
    public static void MarkActive(PlayerRef p)
    {
        try
        {
            if (!ModerationCore.Hosting || !p.IsRealPlayer) return;
            if (!ByRaw.TryGetValue(p.RawEncoded, out var e)) return;
            if (e.WarnedAt >= 0f || e.KickAt > 0f) Cleared(e);
            e.Idle = 0f;
            if (e.HasCar) e.State = "";
        }
        catch (Exception ex) { DebugLog.Error("serverutils", ex, "AfkWatch.MarkActive"); ModerationCore.LogOnce("markactive", "[Moderation] AFK activity: " + ex.Message); }
    }

    // ---------------------------------------------------------------- guest: proving activity

    private static float _localInputAt = -100f, _nextActiveSend;
    private static bool _inputReadFailed;

    /// <summary>
    /// Guest, per frame: while the host's AFK rule is on, tells the host (at most every 20 s) that we are here when we
    /// spectate, have the chat box open, or touched the keyboard or mouse. Nothing is sent otherwise.
    /// </summary>
    private static void GuestTick()
    {
        if (!HostRuleKnown || !HostRuleOn || !Net.IsGuest) return;
        float now = Time.unscaledTime;
        if (LocalInputThisFrame()) _localInputAt = now;
        if (now < _nextActiveSend || !Net.HostHas(Net.Channels.Moderation)) return;
        byte why = PlayerActivity.Spectating ? ActiveSpectating
            : PlayerActivity.Chatting ? ActiveChatting
            : now - _localInputAt < ActiveEvery ? ActiveInput : (byte)0;
        if (why == 0) return;
        _nextActiveSend = now + ActiveEvery;
        var res = Net.SendToHost(Net.Channels.Moderation, MsgActive, new NetWriter(4).Byte(why).ToArray());
        if (res != Net.SendResult.Sent && res != Net.SendResult.NotModded && res != Net.SendResult.NotConnected && res != Net.SendResult.Off)
            ModerationCore.LogOnce("active" + res, "[Moderation] Could not tell the host we are here: " + res);
    }

    private static bool LocalInputThisFrame()
    {
        if (_inputReadFailed) return false;
        try
        {
            var kb = Keyboard.current;
            if (kb != null && kb.anyKey.wasPressedThisFrame) return true;
            var mouse = Mouse.current;
            if (mouse == null) return false;
            if (mouse.leftButton.wasPressedThisFrame || mouse.rightButton.wasPressedThisFrame) return true;
            Vector2 d = mouse.delta.ReadValue();
            return d.sqrMagnitude > 4f;
        }
        catch (Exception e)
        {
            DebugLog.Error("serverutils", e, "AfkWatch.LocalInputThisFrame");
            _inputReadFailed = true;
            ModerationCore.LogOnce("localinput", "[Moderation] Can't read the keyboard or mouse for the AFK activity: " + e.Message);
            return false;
        }
    }

    // ---------------------------------------------------------------- messages

    private static void Notify(AfkPlayer e, byte kind, int idleSeconds, int secondsLeft, bool kicks)
    {
        if (!e.Modded) return;
        var payload = new NetWriter(16).Byte(kind).Int(idleSeconds).Int(secondsLeft).Bool(kicks).ToArray();
        var res = Net.SendTo(e.Ref, Net.Channels.Moderation, MsgNotice, payload);
        if (res != Net.SendResult.Sent && res != Net.SendResult.NotModded)
            ModerationCore.LogOnce("notice" + res, "[Moderation] Could not send an AFK notice: " + res);
    }

    private static string RuleSignature() =>
        ModerationFeature.AfkEnabled.Value + "|" + ModerationFeature.AfkMinutes.Value + "|" +
        ModerationFeature.AfkRemove.Value + "|" + ModerationFeature.AfkRespondSeconds.Value;

    private static byte[] RulePayload() =>
        new NetWriter(16)
            .Bool(ModerationFeature.AfkEnabled.Value)
            .Int(ModerationFeature.AfkMinutes.Value)
            .Bool(ModerationFeature.AfkRemove.Value)
            .Int(ModerationFeature.AfkRespondSeconds.Value)
            .ToArray();

    /// <summary>Tells modded guests when the AFK rule changes (once the sliders have settled for a second).</summary>
    private static void RuleBroadcastTick()
    {
        string sig = RuleSignature();
        if (sig == _lastRuleSig) { _ruleChangedAt = -1f; return; }
        if (_ruleChangedAt < 0f) { _ruleChangedAt = Time.unscaledTime; return; }
        if (Time.unscaledTime - _ruleChangedAt < 1f) return;
        _lastRuleSig = sig;
        _ruleChangedAt = -1f;
        if (Net.IsHost && Net.PeerCount > 0) Net.Broadcast(Net.Channels.Moderation, MsgRule, RulePayload());
    }

    private static void SendRule(PlayerRef p)
    {
        try
        {
            if (Net.IsHost && GameState.IsAuthority && Net.PeerHas(p, Net.Channels.Moderation))
                Net.SendTo(p, Net.Channels.Moderation, MsgRule, RulePayload());
        }
        catch (Exception e) { DebugLog.Error("serverutils", e, "AfkWatch.SendRule"); ModerationCore.LogOnce("rule", "[Moderation] Could not send the AFK rule: " + e.Message); }
    }

    private static void OnMessage(Net.Message m)
    {
        if (Net.IsHost)
        {
            // Guests only ever send "active", straight to the host (never relayed), and only after saying hello.
            if (m.Type == MsgActive && !m.Relayed && m.From.IsRealPlayer && Net.IsPeer(m.From)) MarkActive(m.From);
            return;
        }
        if (!m.FromHost) return;
        var rd = new NetReader(m.Payload);
        switch (m.Type)
        {
            case MsgRule:
            {
                bool on = rd.Bool();
                int minutes = rd.Int();
                bool kicks = rd.Bool();
                int respond = rd.Int();
                if (!rd.Ok) return;
                HostRuleKnown = true;
                HostRuleOn = on;
                HostRuleMinutes = Mathf.Clamp(minutes, 0, 10000);
                HostRuleKicks = kicks;
                HostRuleRespond = Mathf.Clamp(respond, 0, 100000);
                break;
            }
            case MsgNotice:
            {
                byte kind = rd.Byte();
                int idle = rd.Int(), left = rd.Int();
                bool kicks = rd.Bool();
                if (!rd.Ok) return;
                int idleMin = Mathf.Clamp(Mathf.RoundToInt(idle / 60f), 0, 10000);
                left = Mathf.Clamp(left, 0, 100000);
                string text = kind switch
                {
                    NoticeWarning => $"The host thinks you're away (idle about {idleMin} min). " +
                                     (kicks ? $"Drive or steer within {left} s or you'll be removed from the session." : "Drive or steer to clear this."),
                    NoticeRemoving => "The host is removing you from the session for being away.",
                    NoticeCleared => "Welcome back: the host's away warning is cleared.",
                    _ => null,
                };
                if (text == null) return;
                LastNotice = text;
                LastNoticeAt = Time.unscaledTime;
                LastNoticeBad = kind != NoticeCleared;
                ServerUtilsPlugin.Logger.LogInfo("[Moderation] Host notice: " + text);
                break;
            }
        }
    }
}
