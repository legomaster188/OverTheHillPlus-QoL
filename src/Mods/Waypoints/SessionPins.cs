using System;
using System.Collections.Generic;
using Fusion;
using OverTheHillPlus.Features.Public.Filter;
using OverTheHillPlus.Features.Public.ModNet;
using OverTheHillPlus.Features.Public.PlayerList;
using OverTheHillPlus.Multiplayer;
using OverTheHillPlus.Shared;
using UnityEngine;
using Net = OverTheHillPlus.Features.Public.ModNet.ModNet;

namespace OverTheHillPlus.Waypoints;

using OverTheHillPlus.Core.Manager;

/// <summary>
/// The session's shared pins over the mod network (channel <see cref="Net.Channels.Waypoints"/>), on core's
/// <see cref="PinBoard"/>: the host keeps the board and sends every modded guest a snapshot after each change (and on
/// joining); guests ask the host to add or remove a pin. The host checks every request (switch, per-guest limit and
/// cooldown, word filter on the name) and answers with a short result. Guests only run any of this while the session
/// advertises Waypoints+ (<see cref="ModManager.ServerModActive"/>).
/// </summary>
internal static class SessionPins
{
    private const byte MsgSnapshot = 1, MsgAdd = 2, MsgRemove = 3, MsgSync = 4, MsgReply = 6;
    private const float GuestCooldown = 1.5f, SendGap = 0.3f;

    private static PinBoard _board = new();
    private static bool _ownBoard; // true: we are the host and the board is ours
    private static bool _dirty;
    private static float _nextSend;
    private static readonly Dictionary<int, float> LastRequest = new();

    public static PinBoard Board => _board;

    /// <summary>A pin the host pushed that this game should follow now (consumed by the route).</summary>
    public static event Action<BoardPin> Pushed;

    /// <summary>Last result line (toast and menu).</summary>
    public static event Action<string> Report;

    internal static void RegisterChannel()
    {
        Net.Register(Net.Channels.Waypoints, new Net.ChannelOptions
        {
            Name = "waypoints",
            Version = 1,
            MaxPayload = PinBoard.MaxSnapshotBytes + 64,
            RatePerSecond = 4f,
            Burst = 8,
            HostOnlySender = true,
            // Advertised only while this session runs Waypoints+ for us (a host always; a guest when the host has it).
            Active = () => ModManager.ServerModActive(WaypointsPlugin.ModId),
        }, OnMessage);
        Net.OnSessionReset(ResetSession);
        Net.OnPeerReady(p => { if (Net.IsHost) SendSnapshotTo(p); });
        Net.OnPeerGone(p =>
        {
            LastRequest.Remove(p.RawEncoded);
            if (Net.IsHost && _ownBoard && _board.RemoveOwnedBy(p.RawEncoded) > 0) _dirty = true;
        });
        Net.OnHostReady(() => { if (Active) Net.SendToHost(Net.Channels.Waypoints, MsgSync, Array.Empty<byte>()); });
    }

    private static void ResetSession()
    {
        DebugLog.Verbose("waypoints", () => $"session reset: shared board dropped ({_board.Pins.Count} pin(s))");
        _board = new PinBoard();
        _ownBoard = false;
        _dirty = false;
        LastRequest.Clear();
    }

    /// <summary>In a multiplayer session where the shared pins run (host, or a guest of a host with Waypoints+).</summary>
    public static bool Active => (Net.IsHost || Net.IsGuest) && Net.Enabled != null && Net.Enabled.Value && ModManager.ServerModActive(WaypointsPlugin.ModId);

    public static bool IsHost => Net.IsHost;

    /// <summary>Your own key on the board (the host's own pins use 0).</summary>
    public static int MyKey
    {
        get
        {
            if (Net.IsHost) return 0;
            try { return GameState.LocalPlayer?.RawEncoded ?? -1; } catch (Exception ex) { DebugLog.Error("waypoints", ex, "SessionPins.MyKey"); return -1; }
        }
    }

    /// <summary>One line for the MODS page.</summary>
    public static string Status
    {
        get
        {
            if (!Net.IsHost && !Net.IsGuest) return "Not in a multiplayer session: shared pins appear when you host or join.";
            if (Net.Enabled == null || !Net.Enabled.Value) return "The mod network is off (Network settings): shared pins need it.";
            if (Net.IsHost) return $"Hosting: {_board.Pins.Count} shared pin(s), {Net.PeerCount} modded guest(s)." +
                                   (WaypointsConfig.AcceptGuestPins.Value ? "" : " Guests can't add pins.");
            if (!ModManager.ServerModActive(WaypointsPlugin.ModId)) return "Off in this session: the host doesn't run Waypoints+.";
            if (Net.HostStatus != Net.HostLink.Ready) return "Connecting to the host's mod network...";
            return $"{_board.Pins.Count} shared pin(s) from the host.";
        }
    }

    public static void Tick()
    {
        bool host = Net.IsHost;
        if (host && !_ownBoard)
        {
            int epoch;
            do { epoch = UnityEngine.Random.Range(1, int.MaxValue); } while (epoch == 0);
            _board = new PinBoard(epoch);
            _ownBoard = true;
            _dirty = true;
            DebugLog.Info("waypoints", $"hosting: shared pin board created (epoch {epoch})");
        }
        else if (!host && _ownBoard)
        {
            _board = new PinBoard();
            _ownBoard = false;
        }
        if (host && _dirty && Time.unscaledTime >= _nextSend)
        {
            _dirty = false;
            _nextSend = Time.unscaledTime + SendGap;
            var snapshot = _board.Encode();
            int sentTo = Net.Broadcast(Net.Channels.Waypoints, MsgSnapshot, snapshot);
            DebugLog.Verbose("waypoints", () => $"snapshot broadcast: {_board.Pins.Count} pin(s), {snapshot.Length} B to {sentTo} guest(s)");
        }
    }

    // ---------------------------------------------------------------- actions (from the menu)

    /// <summary>Shares a spot with the session. Returns a status line.</summary>
    public static string Share(string name, Vector3 pos, int color)
    {
        if (!Active) return "Blocked: shared pins need a session whose host runs Waypoints+ (and the mod network on).";
        name = PinBoard.CleanText(name, PinBoard.MaxName);
        if (Net.IsHost)
        {
            if (NameFilter.IsBlocked(name, out _)) return "Blocked: that name is refused by the word filter.";
            var pin = _board.Add(name, pos.x, pos.y, pos.z, (byte)color, HostName(), 0, out string err);
            if (pin == null) return "Failed: " + err + ".";
            _dirty = true;
            return $"Shared \"{pin.Name}\" with the session.";
        }
        if (GameState.IsPublicGuest) return "Blocked: in a public session only the host can share pins with everyone.";
        if (!Net.HostHas(Net.Channels.Waypoints)) return "Blocked: the host doesn't take shared pins right now.";
        var res = Net.SendToHost(Net.Channels.Waypoints, MsgAdd, PinBoard.EncodeAdd(name, pos.x, pos.y, pos.z, (byte)color));
        return res == Net.SendResult.Sent ? $"Asked the host to share \"{name}\"..." :
            res == Net.SendResult.RateLimited ? "Blocked: too many requests, wait a moment." : "Failed: could not reach the host (" + res + ").";
    }

    public static bool CanRemove(BoardPin p) => p != null && (Net.IsHost || p.OwnerKey == MyKey);

    public static string Remove(BoardPin p)
    {
        if (p == null || !Active) return "";
        if (Net.IsHost)
        {
            if (_board.Remove(p.Id, 0, asHost: true)) { _dirty = true; return $"Removed \"{p.Name}\"."; }
            return "";
        }
        var res = Net.SendToHost(Net.Channels.Waypoints, MsgRemove, PinBoard.EncodeId(p.Id));
        return res == Net.SendResult.Sent ? $"Asked the host to remove \"{p.Name}\"..." : "Failed: could not reach the host (" + res + ").";
    }

    /// <summary>Host: everyone with Waypoints+ follows this pin; a short notice tells everyone else.</summary>
    public static string Push(BoardPin p)
    {
        if (p == null || !Net.IsHost || !_ownBoard) return "Blocked: only the host can push a pin to everyone.";
        if (!_board.Push(p.Id)) return "Failed: that pin is gone.";
        _dirty = true;
        if (WaypointsConfig.AnnouncePush.Value) HostNotice.Announce($"Meet at {p.Name}", HostNotice.ColorFor("Cyan"));
        Pushed?.Invoke(p); // the host follows its own push too
        return $"Pushed \"{p.Name}\" to everyone.";
    }

    /// <summary>Host: shares one of your saved pins and pushes it straight away.</summary>
    public static string ShareAndPush(string name, Vector3 pos, int color)
    {
        if (!Net.IsHost) return "Blocked: only the host can push a pin to everyone.";
        string shared = Share(name, pos, color);
        if (!shared.StartsWith("Shared", StringComparison.Ordinal)) return shared;
        var pin = _board.Pins[_board.Pins.Count - 1];
        return Push(pin);
    }

    // ---------------------------------------------------------------- messages

    private static void OnMessage(Net.Message m)
    {
        if (!ModManager.ServerModActive(WaypointsPlugin.ModId)) return;
        if (Net.IsHost)
        {
            switch (m.Type)
            {
                case MsgAdd: HostAdd(m.From, m.Payload); break;
                case MsgRemove: HostRemove(m.From, m.Payload); break;
                case MsgSync: SendSnapshotTo(m.From); break;
            }
            return;
        }
        if (!m.FromHost) return;
        switch (m.Type)
        {
            case MsgSnapshot:
            {
                var res = _board.ApplySnapshot(m.Payload);
                DebugLog.Verbose("waypoints", () => $"snapshot from the host: {m.Payload?.Length ?? 0} B, now {_board.Pins.Count} shared pin(s)" + (res.NewPush ? ", a pin was pushed" : ""));
                if (res.NewPush && _board.Find(_board.PushedId) is BoardPin pushed) Pushed?.Invoke(pushed);
                break;
            }
            case MsgReply:
            {
                var r = new NetReader(m.Payload);
                string text = HostNotice.Escape(r.String(120));
                if (r.Ok && text.Length > 0) Report?.Invoke(text);
                break;
            }
        }
    }

    private static void SendSnapshotTo(PlayerRef p)
    {
        if (!Net.IsHost || !_ownBoard) return;
        Net.SendTo(p, Net.Channels.Waypoints, MsgSnapshot, _board.Encode());
    }

    private static void Reply(PlayerRef to, string text) =>
        Net.SendTo(to, Net.Channels.Waypoints, MsgReply, new NetWriter(64).String(text, 120).ToArray());

    private static void HostAdd(PlayerRef from, byte[] payload)
    {
        if (!_ownBoard || !from.IsRealPlayer) return;
        if (!PinBoard.TryDecodeAdd(payload, out string name, out float x, out float y, out float z, out byte color)) return;
        int key = from.RawEncoded;
        if (!WaypointsConfig.AcceptGuestPins.Value) { Reply(from, "The host doesn't take shared pins."); return; }
        if (LastRequest.TryGetValue(key, out float at) && Time.unscaledTime - at < GuestCooldown) { Reply(from, "Too fast: wait a moment."); return; }
        LastRequest[key] = Time.unscaledTime;
        if (_board.CountOwnedBy(key) >= Mathf.Max(0, WaypointsConfig.MaxPinsPerGuest.Value))
        {
            Reply(from, $"You already share {WaypointsConfig.MaxPinsPerGuest.Value} pin(s): remove one first.");
            return;
        }
        if (NameFilter.IsBlocked(name, out _)) { Reply(from, "That pin name is refused by the host's word filter."); return; }
        var pin = _board.Add(name, x, y, z, color, GuestName(from), key, out string err);
        if (pin == null) { Reply(from, "Not shared: " + err + "."); return; }
        _dirty = true;
        Reply(from, $"Shared \"{pin.Name}\" with the session.");
        WaypointsPlugin.Logger.LogInfo($"[Waypoints] {pin.Owner} shared a pin");
    }

    private static void HostRemove(PlayerRef from, byte[] payload)
    {
        if (!_ownBoard || !PinBoard.TryDecodeId(payload, out ushort id)) return;
        var p = _board.Find(id);
        if (p == null) return;
        if (_board.Remove(id, from.RawEncoded, asHost: false)) { _dirty = true; Reply(from, $"Removed \"{p.Name}\"."); }
        else Reply(from, "You can only remove the pins you shared.");
    }

    private static string HostName()
    {
        try
        {
            var me = GameState.LocalPlayer;
            if (me.HasValue) return HostNotice.SafeName(PlayerListRoster.NameOf(OverlandSessionPlayers.Instance, me.Value), "Host");
        }
        catch (Exception ex) { DebugLog.Error("waypoints", ex, "SessionPins.HostName"); }
        return "Host";
    }

    private static string GuestName(PlayerRef p)
    {
        try { return HostNotice.SafeName(PlayerListRoster.NameOf(OverlandSessionPlayers.Instance, p), "Player " + p.PlayerId); }
        catch (Exception ex) { DebugLog.Error("waypoints", ex, "SessionPins.GuestName"); return "Player " + p.PlayerId; }
    }
}
