using System;
using System.Collections.Generic;
using Fusion;
using UnityEngine;
using Net = OverTheHillPlus.Features.Public.ModNet.ModNet;

namespace OverTheHillPlus.Trailers;

// After the namespace line on purpose: the game has its own global ModManager (mod.io).
using OverTheHillPlus.Core.Manager;

/// <summary>
/// The trailers channel (ModNet channel 13): guests ask the host (spawn, hitch, unhitch, reset, remove, load, unload);
/// the host answers the asker and tells every modded guest each trailer's state, reliable, and replays all of them to a
/// guest whose mod network comes up (late joiners). Guests only take states from the host itself. The host and solo
/// run requests straight through <see cref="TrailerHost"/>.
/// </summary>
internal static class TrailerNet
{
    private const float ReplyTimeout = 6f;
    private static float _pendingSince = -1f;
    private static TrailerOp _pendingOp;

    public static string LastMessage { get; private set; } = "";
    public static float LastMessageAt { get; private set; } = -100f;
    public static bool Waiting => _pendingSince >= 0f;

    public static void Register()
    {
        Net.Register(Net.Channels.Trailers, new Net.ChannelOptions
        {
            Name = "trailers",
            Version = 1,
            MaxPayload = 64,
            RatePerSecond = 6f,
            Burst = 24,              // a late joiner gets every trailer's state at once (8 at most)
            HostOnlySender = true,
            Active = () => ModManager.ServerModActive(TrailersPlugin.ModId) && TrailerPrefabs.Ready,
        }, OnMessage);
        Net.OnPeerReady(p =>
        {
            if (!Net.IsHost || !Net.PeerHas(p, Net.Channels.Trailers)) return;
            foreach (var t in TrailerWorld.All)
                Net.SendTo(p, Net.Channels.Trailers, TrailerMessages.State, TrailerMessages.EncodeState(t.State));
        });
        Net.OnPeerGone(TrailerHost.PeerGone);
        Net.OnSessionReset(() =>
        {
            _pendingSince = -1f;
            TrailerWorld.Clear();
        });
    }

    // ---------------------------------------------------------------- asking

    /// <summary>Runs a request: straight away when this game is the host (or solo), otherwise asks the host.
    /// Returns a status line.</summary>
    public static string Ask(TrailerOp op, TrailerKind kind, uint arg = 0)
    {
        string s = AskCore(op, kind, arg);
        Report(s);
        return s;
    }

    private static string AskCore(TrailerOp op, TrailerKind kind, uint arg)
    {
        var runner = GameState.Runner;
        if (runner == null) return "Blocked: load into a world first";
        if (!TrailerPrefabs.Ready) return "Blocked: trailers are not ready (" + (TrailerPrefabs.FailedReason ?? TrailerPrefabs.Waiting ?? "?") + ")";
        if (!ModManager.ServerModActive(TrailersPlugin.ModId)) return "Blocked: this server doesn't run Trailers";
        var req = new TrailerRequest { Op = op, Kind = kind, Arg = arg };
        if (GameState.IsAuthority)
        {
            PlayerRef me;
            try { me = runner.LocalPlayer; } catch (Exception ex) { DebugLog.Error("trailers", ex, "TrailerNet.AskCore"); return "Failed: no local player"; }
            return Describe(op, TrailerHost.Handle(me, req));
        }
        if (Waiting) return "Blocked: waiting for the host's answer";
        if (!Net.Enabled.Value) return "Blocked: turn on the mod network to ask the host";
        if (Net.HostStatus != Net.HostLink.Ready) return "Blocked: " + Net.Describe();
        if (!Net.HostHas(Net.Channels.Trailers)) return "Blocked: the host doesn't run Trailers";
        var res = Net.SendToHost(Net.Channels.Trailers, TrailerMessages.Request, TrailerMessages.EncodeRequest(req));
        switch (res)
        {
            case Net.SendResult.Sent:
                _pendingSince = Time.unscaledTime;
                _pendingOp = op;
                return "Asked the host...";
            case Net.SendResult.RateLimited: return "Blocked: too many requests, wait a moment";
            default: return "Failed: could not reach the host (" + res + ")";
        }
    }

    public static void Tick()
    {
        if (_pendingSince >= 0f && Time.unscaledTime - _pendingSince > ReplyTimeout)
        {
            _pendingSince = -1f;
            Report("Failed: the host didn't answer");
        }
    }

    private static void Report(string s)
    {
        if (string.IsNullOrEmpty(s)) return;
        LastMessage = s;
        LastMessageAt = Time.unscaledTime;
        try { Menu.SetStatus(s); } catch (Exception ex) { DebugLog.Error("trailers", ex, "TrailerNet.Report"); }
        TrailersPlugin.Logger.LogInfo("[Trailers] " + s);
    }

    public static string Describe(TrailerOp op, TrailerReply code)
    {
        switch (code)
        {
            case TrailerReply.Done:
                switch (op)
                {
                    case TrailerOp.Spawn: return "Done: your trailer is hitched behind your car";
                    case TrailerOp.Remove: return "Done: your trailer is gone";
                    case TrailerOp.Hitch: return "Done: hitched";
                    case TrailerOp.Unhitch: return "Done: unhitched (its parking brake is on)";
                    case TrailerOp.Reset: return "Done: your trailer is back behind your car, hitched";
                    case TrailerOp.Load: return "Done: the car is on the deck";
                    case TrailerOp.Unload: return "Done: the deck is empty";
                }
                return "Done";
            case TrailerReply.Off: return "Blocked: the host doesn't let guests tow trailers";
            case TrailerReply.NoCar: return "Failed: no car found (are you driving?)";
            case TrailerReply.TooFast: return "Blocked: stop first";
            case TrailerReply.PlayerLimit: return "Blocked: you have as many trailers as this server allows (remove one first)";
            case TrailerReply.SessionLimit: return "Blocked: this server has as many trailers as it allows";
            case TrailerReply.NoTrailer: return "Blocked: you have no trailer for that";
            case TrailerReply.TooFar: return "Blocked: back your car up to the trailer's coupler first (within 2.5 m)";
            case TrailerReply.NotAHauler: return "Blocked: you need the car hauler for that";
            case TrailerReply.NotACar: return "Failed: that is not a car";
            case TrailerReply.NotReady: return "Blocked: trailers are not ready yet";
            case TrailerReply.Busy: return "Blocked: busy, try again";
            case TrailerReply.BallTaken: return "Blocked: your car already tows a trailer (unhitch it first)";
            case TrailerReply.NoTowHitch: return "Blocked: " + TowHitch.Need;
            default: return "Failed: the host couldn't do it";
        }
    }

    // ---------------------------------------------------------------- the host's messages

    public static void BroadcastState(TrailerState s)
    {
        if (!Net.IsHost) return;
        var bytes = TrailerMessages.EncodeState(s);
        foreach (var p in Net.Peers)
            if (Net.PeerHas(p.Ref, Net.Channels.Trailers)) Net.SendTo(p.Ref, Net.Channels.Trailers, TrailerMessages.State, bytes);
    }

    public static void BroadcastRemoved(uint netId, DespawnReason why)
    {
        if (!Net.IsHost) return;
        var bytes = TrailerMessages.EncodeRemoved(netId, why);
        foreach (var p in Net.Peers)
            if (Net.PeerHas(p.Ref, Net.Channels.Trailers)) Net.SendTo(p.Ref, Net.Channels.Trailers, TrailerMessages.Removed, bytes);
    }

    private static void OnMessage(Net.Message m)
    {
        switch (m.Type)
        {
            case TrailerMessages.Request:
                if (!Net.IsHost || !m.From.IsRealPlayer || m.Relayed) return;
                if (!TrailerMessages.DecodeRequest(m.Payload, out var req)) return;
                var code = TrailerHost.Handle(m.From, req);
                Net.SendTo(m.From, Net.Channels.Trailers, TrailerMessages.Reply, TrailerMessages.EncodeReply(req.Op, code));
                break;
            case TrailerMessages.Reply:
                if (Net.IsHost || !m.FromHost) return;
                if (!TrailerMessages.DecodeReply(m.Payload, out var op, out var c)) return;
                // only the answer to the request still waiting (a late one after the timeout is dropped)
                if (!Waiting || op != _pendingOp) return;
                _pendingSince = -1f;
                Report(Describe(op, c));
                break;
            case TrailerMessages.State:
                if (Net.IsHost || !m.FromHost || !ModManager.ServerModActive(TrailersPlugin.ModId) || !TrailerPrefabs.Ready) return;
                var s = TrailerMessages.DecodeState(m.Payload);
                if (s != null) TrailerWorld.Apply(s);
                break;
            case TrailerMessages.Removed:
                if (Net.IsHost || !m.FromHost) return;
                if (TrailerMessages.DecodeRemoved(m.Payload, out uint id, out _)) TrailerWorld.Remove(id);
                break;
        }
    }
}
