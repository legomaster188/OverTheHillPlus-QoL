using System;
using System.Collections.Generic;
using Fusion;
using OverTheHillPlus.Features.Public.ModNet;
using OverTheHillPlus.HostPowers;
using UnityEngine;
using Net = OverTheHillPlus.Features.Public.ModNet.ModNet;

namespace OverTheHillPlus.Features.Cheats.Players;

/// <summary>
/// Teleport requests over the mod network: a guest running this build asks the host to move its car next to a
/// player or to its own map pin; the host gets an approve / deny pop-up (or auto-approves if set) and runs the move
/// with <see cref="PlayerMoves"/> (safe placement, per-player cooldown).
///
/// Wire (channel <see cref="Channel"/>, version 1; fields are only ever appended):
/// - Request (guest -> host): [byte kind][int target RawEncoded][float x][float y][float z]. Kind 0 = to a player,
///   1 = to my map pin (the host reads the guest's pin from the game's networked pin table and only falls back to the
///   coordinates sent along when it can't read it).
/// - Cancel (guest -> host): empty.
/// - Reply (host -> guest): [byte code][float seconds]. No free text is ever sent, so nothing needs the name filter.
/// Vanilla players never receive anything (ModNet only talks to players that announced it).
/// </summary>
internal static class TeleportRequests
{
    /// <summary>Private channel id (public channels count up from 0; private ones start at 100 so they never clash).</summary>
    internal const ushort Channel = 100;

    private const byte MsgRequest = 1, MsgCancel = 2, MsgReply = 3;
    internal const byte KindToPlayer = 0, KindToMyPin = 1;
    private const int MaxPending = 8;
    private const float GuestWaitLimit = 150f; // longer than any host timeout setting

    internal enum Code : byte
    {
        Waiting = 0, Done = 1, Denied = 2, Expired = 3, Cooldown = 4, Disabled = 5, NoCar = 6, NoSpot = 7, Failed = 8,
        Busy = 9, NoTarget = 10, NoPin = 11, Full = 12,
    }

    internal sealed class Pending
    {
        public PlayerRef From;
        public byte Kind;
        public PlayerRef Target;
        public Vector3 SentPin;
        public float At;
    }

    // host
    internal static readonly List<Pending> Queue = new();
    private static readonly Dictionary<int, float> LastDecision = new(); // requester RawEncoded -> time
    public static int Approved { get; private set; }
    public static int Denied { get; private set; }

    // guest
    private static float _askedAt = -1f;
    private static string _askedFor = "";
    public static bool Waiting => _askedAt >= 0f;
    public static string WaitingFor => _askedFor;
    public static bool HostAcknowledged { get; private set; }
    private static float _guestCooldownUntil;

    private static readonly HashSet<string> LoggedOnce = new();

    internal static void RegisterChannel()
    {
        bool ok = Net.Register(Channel, new Net.ChannelOptions
        {
            Name = "teleport requests",
            Version = 1,
            MaxPayload = 32,
            RatePerSecond = 0.5f,
            Burst = 4,
            HostOnlySender = true, // guests only take answers from the host itself
            // A host advertises the channel only while it takes requests; guests always listen for answers.
            Active = () => !Net.IsHost || (PlayersFeature.AcceptRequests != null && PlayersFeature.AcceptRequests.Value),
        }, OnMessage);
        if (!ok) LogOnce("reg", "teleport request channel could not be registered");
        Net.OnSessionReset(ResetSession);
        Net.OnPeerGone(p =>
        {
            Queue.RemoveAll(q => q.From.RawEncoded == p.RawEncoded);
            LastDecision.Remove(p.RawEncoded);
            PlayerMoves.Forget(p);
        });
    }

    private static void ResetSession()
    {
        Queue.Clear();
        LastDecision.Clear();
        _askedAt = -1f;
        HostAcknowledged = false;
        PlayerMoves.ResetSession();
    }

    // ---------------------------------------------------------------- guest side

    /// <summary>Can this game ask the host right now? <paramref name="why"/> says why not.</summary>
    public static bool CanAsk(out string why)
    {
        why = null;
        if (Net.Enabled == null || !Net.Enabled.Value) { why = "turn on the mod network (PLUS > Network) to ask the host"; return false; }
        if (!Net.IsGuest) { why = "only guests ask; the host moves players directly"; return false; }
        switch (Net.HostStatus)
        {
            case Net.HostLink.Ready: break;
            case Net.HostLink.Asking: why = "still connecting to the host's mod network"; return false;
            case Net.HostLink.Paused: why = "the host switched its mod network off"; return false;
            default: why = "the host doesn't run this mod"; return false;
        }
        if (!Net.HostHas(Channel)) { why = "the host doesn't take teleport requests"; return false; }
        return true;
    }

    public static float GuestCooldownLeft => Mathf.Max(0f, _guestCooldownUntil - Time.unscaledTime);

    /// <summary>Guest: asks to be moved next to <paramref name="target"/>, or to your own map pin when null.</summary>
    public static string Ask(PlayerMoves.Target target)
    {
        if (!CanAsk(out string why)) return "Blocked: " + why + ".";
        if (Waiting) return $"Blocked: already waiting for the host ({_askedFor}).";
        float cd = GuestCooldownLeft;
        if (cd > 0f) return $"Blocked: wait {Mathf.CeilToInt(cd)} s before asking again.";
        if (GameState.LocalCar == null) return "Failed: you have no car (are you driving?).";

        byte kind;
        Vector3 pin = Vector3.zero;
        int targetRaw = 0;
        if (target == null)
        {
            if (!Teleport.TeleportCore.TryGetPin(out pin)) return "Failed: no map pin. Open the map and place a pin first.";
            kind = KindToMyPin;
            _askedFor = "your map pin";
        }
        else
        {
            if (target.IsMe) return "That's you.";
            kind = KindToPlayer;
            targetRaw = target.Ref.RawEncoded;
            _askedFor = target.Name;
        }
        var payload = new NetWriter(20).Byte(kind).Int(targetRaw).Float(pin.x).Float(pin.y).Float(pin.z).ToArray();
        var res = Net.SendToHost(Channel, MsgRequest, payload);
        switch (res)
        {
            case Net.SendResult.Sent:
                _askedAt = Time.unscaledTime;
                HostAcknowledged = false;
                _guestCooldownUntil = Time.unscaledTime + 5f;
                return $"Asked the host to teleport you to {_askedFor}...";
            case Net.SendResult.RateLimited: return "Blocked: too many requests, wait a moment.";
            default: return $"Failed: could not reach the host ({res}).";
        }
    }

    public static string Cancel()
    {
        if (!Waiting) return "Nothing to cancel.";
        _askedAt = -1f;
        HostAcknowledged = false;
        try { Net.SendToHost(Channel, MsgCancel, Array.Empty<byte>()); } catch (Exception ex) { DebugLog.Error("hostpowers", ex, "TeleportRequests.Cancel"); }
        return "Teleport request cancelled.";
    }

    private static void GuestAnswer(byte[] payload)
    {
        var rd = new NetReader(payload);
        var code = (Code)rd.Byte();
        float secs = Mathf.Clamp(rd.Float(), 0f, 600f);
        if (!rd.Ok) return;
        if (code == Code.Waiting)
        {
            if (!Waiting) return; // cancelled meanwhile
            HostAcknowledged = true;
            PlayersFeature.Report($"The host got your request, waiting for approval (up to {Mathf.CeilToInt(secs)} s)...");
            return;
        }
        bool wasWaiting = Waiting;
        _askedAt = -1f;
        HostAcknowledged = false;
        if (!wasWaiting && code != Code.Done) return; // a late answer to something we cancelled
        string s = code switch
        {
            Code.Done => "The host teleported you.",
            Code.Denied => "Blocked: the host said no.",
            Code.Expired => "Blocked: the host didn't answer in time.",
            Code.Cooldown => $"Blocked: the host says wait {Mathf.CeilToInt(secs)} s.",
            Code.Disabled => "Blocked: the host doesn't take teleport requests.",
            Code.NoCar => "Failed: the host couldn't find your car.",
            Code.NoSpot => "Failed: the host found no safe spot there.",
            Code.Busy => "Blocked: the host is busy moving players, ask again in a moment.",
            Code.NoTarget => "Failed: that player isn't there (or has no car).",
            Code.NoPin => "Failed: the host couldn't see your map pin.",
            Code.Full => "Blocked: the host has too many requests open.",
            _ => "Failed: the host couldn't teleport you.",
        };
        if (code == Code.Cooldown) _guestCooldownUntil = Time.unscaledTime + secs;
        PlayersFeature.Report(s);
    }

    // ---------------------------------------------------------------- host side

    private static void HostReceive(Net.Message m)
    {
        var from = m.From;
        if (!from.IsRealPlayer || m.Relayed) return;
        if (m.Type == MsgCancel)
        {
            Queue.RemoveAll(q => q.From.RawEncoded == from.RawEncoded);
            return;
        }
        if (m.Type != MsgRequest) return;

        var rd = new NetReader(m.Payload);
        byte kind = rd.Byte();
        int targetRaw = rd.Int();
        var pin = new Vector3(rd.Float(), rd.Float(), rd.Float());
        if (!rd.Ok || (kind != KindToPlayer && kind != KindToMyPin)) return;

        if (PlayersFeature.AcceptRequests == null || !PlayersFeature.AcceptRequests.Value || PlayerMoves.NotAllowedReason != null)
        {
            Reply(from, Code.Disabled);
            return;
        }
        float cd = PlayersFeature.RequestCooldown?.Value ?? 10f;
        if (LastDecision.TryGetValue(from.RawEncoded, out float at) && Time.unscaledTime - at < cd)
        {
            Reply(from, Code.Cooldown, cd - (Time.unscaledTime - at));
            return;
        }
        var req = new Pending { From = from, Kind = kind, Target = PlayerRef.FromEncoded(targetRaw), SentPin = pin, At = Time.unscaledTime };
        if (kind == KindToPlayer && (targetRaw == 0 || targetRaw == from.RawEncoded))
        {
            Reply(from, Code.NoTarget);
            return;
        }

        // One open request per player: a new one replaces the old.
        Queue.RemoveAll(q => q.From.RawEncoded == from.RawEncoded);
        if (Queue.Count >= MaxPending) { Reply(from, Code.Full); return; }

        if (PlayersFeature.AutoApprove != null && PlayersFeature.AutoApprove.Value)
        {
            Decide(req, true);
            return;
        }
        Queue.Add(req);
        Reply(from, Code.Waiting, Timeout);
        PlayersFeature.Report($"Teleport request from {NameOf(from)}: {Describe(req)}. Approve or deny it.");
    }

    private static float Timeout => PlayersFeature.RequestTimeout?.Value ?? 30f;

    /// <summary>Host: expires old requests.</summary>
    public static void Tick()
    {
        if (Waiting && Time.unscaledTime - _askedAt > GuestWaitLimit)
        {
            _askedAt = -1f;
            HostAcknowledged = false;
            PlayersFeature.Report("Blocked: the host never answered your teleport request.");
        }
        if (Queue.Count == 0) return;
        if (!Net.IsHost)
        {
            Queue.Clear();
            return;
        }
        float timeout = Timeout;
        for (int i = Queue.Count - 1; i >= 0; i--)
        {
            var q = Queue[i];
            if (Time.unscaledTime - q.At <= timeout) continue;
            Queue.RemoveAt(i);
            Reply(q.From, Code.Expired);
            PlayersFeature.Report($"Teleport request from {NameOf(q.From)} expired.");
        }
    }

    public static float SecondsLeft(Pending q) => Mathf.Max(0f, q.At + Timeout - Time.unscaledTime);

    /// <summary>Host: approve or deny a request (from the pop-up, a hotkey or the menu).</summary>
    public static string Decide(Pending q, bool approve)
    {
        if (q == null) return "Nothing to decide.";
        Queue.Remove(q);
        string name = NameOf(q.From);
        if (PlayerMoves.Busy && approve) { Reply(q.From, Code.Busy); return $"Blocked: still moving players, {name} was told to ask again."; }
        // The guest's "ask again" cooldown starts with every real answer (not with "busy, ask again").
        LastDecision[q.From.RawEncoded] = Time.unscaledTime;
        if (!approve)
        {
            Denied++;
            Reply(q.From, Code.Denied);
            return $"Denied {name}'s teleport request.";
        }

        if (PlayerMoves.NotAllowedReason != null) { Reply(q.From, Code.Disabled); return PlayerMoves.NotAllowedReason; }
        PlayerMoves.InvalidateRoster();
        var who = PlayerMoves.Find(q.From);
        if (who == null || !who.HasCar) { Reply(q.From, Code.NoCar); return $"Failed: {name} has no car."; }

        string result;
        if (q.Kind == KindToPlayer)
        {
            var target = PlayerMoves.Find(q.Target);
            if (target == null || !target.HasCar) { Reply(q.From, Code.NoTarget); return $"Failed: the player {name} asked for is gone."; }
            result = PlayerMoves.MoveRequester(who, target, null);
        }
        else
        {
            // Their pin as the game shows it to everyone; the coordinates they sent only if that can't be read.
            Vector3 pin;
            if (!PlayerMoves.TryGetPinOf(q.From, out pin))
            {
                pin = q.SentPin;
                if (!SafeSpot.IsFinite(pin) || pin.sqrMagnitude < 0.01f || Mathf.Abs(pin.x) > 100000f || Mathf.Abs(pin.z) > 100000f || Mathf.Abs(pin.y) > 20000f)
                {
                    Reply(q.From, Code.NoPin);
                    return $"Failed: couldn't read {name}'s map pin.";
                }
            }
            result = PlayerMoves.MoveRequester(who, null, pin);
        }

        bool ok = !result.StartsWith("Failed") && !result.StartsWith("Blocked");
        if (ok) Approved++;
        Reply(q.From, ok ? Code.Done : result.Contains("no safe spot") ? Code.NoSpot : Code.Failed);
        return result;
    }

    public static string DecideFirst(bool approve) => Queue.Count == 0 ? null : Decide(Queue[0], approve);

    /// <summary>"to Bob (1.2 km)" / "to their map pin (300 m)" for the pop-up.</summary>
    public static string Describe(Pending q)
    {
        try
        {
            var who = PlayerMoves.Find(q.From);
            Vector3? dest = null;
            string label;
            if (q.Kind == KindToPlayer)
            {
                var t = PlayerMoves.Find(q.Target);
                label = "go to " + (t?.Name ?? "a player who left");
                if (t != null && t.HasCar) dest = t.Position;
            }
            else
            {
                label = "go to their map pin";
                if (PlayerMoves.TryGetPinOf(q.From, out var pin)) dest = pin;
                else if (SafeSpot.IsFinite(q.SentPin) && q.SentPin.sqrMagnitude > 0.01f) dest = q.SentPin;
            }
            if (dest.HasValue && who != null && who.HasCar)
                label += $" ({Teleport.TeleportCore.Distance(Vector3.Distance(who.Position, dest.Value))})";
            return label;
        }
        catch (Exception ex) { DebugLog.Error("hostpowers", ex, "TeleportRequests.Describe"); return "teleport"; }
    }

    public static string NameOf(PlayerRef p)
    {
        var t = PlayerMoves.Find(p);
        return t != null ? t.Name : "Player " + p.PlayerId;
    }

    private static void Reply(PlayerRef to, Code code, float seconds = 0f)
    {
        try
        {
            var res = Net.SendTo(to, Channel, MsgReply, new NetWriter(8).Byte((byte)code).Float(seconds).ToArray());
            if (res != Net.SendResult.Sent && res != Net.SendResult.RateLimited)
                LogOnce("reply" + res, $"could not answer a teleport request ({res})");
        }
        catch (Exception e) { DebugLog.Error("hostpowers", e, "TeleportRequests.Reply"); LogOnce("replyx", "answering a teleport request: " + e.Message); }
    }

    // ---------------------------------------------------------------- dispatch

    private static void OnMessage(Net.Message m)
    {
        if (Net.IsHost) HostReceive(m);
        else if (m.Type == MsgReply && m.FromHost) GuestAnswer(m.Payload);
    }

    private static void LogOnce(string key, string message)
    {
        if (LoggedOnce.Add(key)) HostPowersPlugin.Logger.LogWarning("[Players] " + message + " (logged once)");
    }
}
