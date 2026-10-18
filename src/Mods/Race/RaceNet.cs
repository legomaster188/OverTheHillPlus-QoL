using System;
using System.Collections.Generic;
using Fusion;
using OverTheHillPlus.Features.Public.ModNet;
using OverTheHillPlus.Multiplayer;
using Net = OverTheHillPlus.Features.Public.ModNet.ModNet;

namespace OverTheHillPlus.Race;

using OverTheHillPlus.Core.Manager;

/// <summary>
/// Race Mode over the mod network (channel <see cref="Net.Channels.Race"/>). The host runs every race and times every
/// racer from the car positions it simulates, so guests only show what the host tells them:
/// <list type="bullet">
/// <item>host → guests: the track, a countdown (with the racers), each gate time (with the split against the board's
///   best), the results, and the track's leaderboard;</item>
/// <item>guest → host: "count me in / out", "I give up", and "send me the track" after joining.</item>
/// </list>
/// Guests run none of it unless the session advertises Race Mode (<see cref="ModManager.ServerModActive"/>).
/// Names in messages are made safe on the host (HostNotice.SafeName) and escaped again on arrival.
/// </summary>
internal static class RaceNet
{
    public const byte MsgTrack = 1, MsgCountdown = 2, MsgProgress = 3, MsgResults = 4, MsgBoard = 5;
    public const byte MsgJoin = 20, MsgGiveUp = 21, MsgSync = 22;
    public const int MaxRacers = 32;
    private const int NameChars = 24;

    internal static void RegisterChannel()
    {
        Net.Register(Net.Channels.Race, new Net.ChannelOptions
        {
            Name = "race",
            Version = 1,
            MaxPayload = 4096,
            RatePerSecond = 15f,
            Burst = 40,
            HostOnlySender = true,
            Active = () => ModManager.ServerModActive(RacePlugin.ModId),
        }, OnMessage);
        Net.OnSessionReset(() => { RaceHost.SessionReset(); RaceView.Reset(); });
        Net.OnPeerReady(p => { if (Net.IsHost) RaceHost.PeerReady(p); });
        Net.OnPeerGone(p => { if (Net.IsHost) RaceHost.PeerGone(p); });
        Net.OnHostReady(() =>
        {
            if (!GuestActive) return;
            Net.SendToHost(Net.Channels.Race, MsgSync, Array.Empty<byte>());
            SendJoin();
        });
    }

    /// <summary>This game runs the races (solo, or hosting).</summary>
    public static bool IsHostSide => GameState.IsAuthority;

    /// <summary>A guest whose host runs Race Mode (and the mod network is on).</summary>
    public static bool GuestActive => Net.IsGuest && Net.Enabled != null && Net.Enabled.Value && ModManager.ServerModActive(RacePlugin.ModId);

    public static int MyKey
    {
        get { try { return GameState.LocalPlayer?.RawEncoded ?? 0; } catch (Exception ex) { DebugLog.Error("race", ex, "RaceNet.MyKey"); return 0; } }
    }

    // ---------------------------------------------------------------- host -> guests

    public static void BroadcastTrack(RaceTrack t) => Net.Broadcast(Net.Channels.Race, MsgTrack, t?.Encode() ?? Array.Empty<byte>());

    public static void SendTrack(PlayerRef p, RaceTrack t) => Net.SendTo(p, Net.Channels.Race, MsgTrack, t?.Encode() ?? Array.Empty<byte>());

    /// <summary>The countdown; <paramref name="raceSeconds"/> (the time limit, appended last) lets guests give up
    /// waiting for results that never came.</summary>
    public static byte[] EncodeCountdown(int serial, float secondsToGo, IReadOnlyList<(int key, string name)> racers, float raceSeconds)
    {
        var w = new NetWriter(16 + racers.Count * 32).Int(serial).Float(secondsToGo).Byte((byte)Math.Min(racers.Count, MaxRacers));
        for (int i = 0; i < racers.Count && i < MaxRacers; i++) w.Int(racers[i].key).String(racers[i].name, NameChars);
        return w.Float(raceSeconds).ToArray();
    }

    public static byte[] EncodeProgress(int serial, int key, RaceEventKind kind, int seq, long ms, long? delta, DnfReason reason) =>
        new NetWriter(24).Int(serial).Int(key).Byte((byte)kind).UShort((ushort)Math.Clamp(seq, 0, ushort.MaxValue))
            .Int((int)Math.Min(int.MaxValue, ms)).Bool(delta.HasValue).Int((int)Math.Clamp(delta ?? 0, int.MinValue, int.MaxValue))
            .Byte((byte)reason).ToArray();

    public static byte[] EncodeResults(int serial, string trackName, List<Standing> list)
    {
        var w = new NetWriter(24 + list.Count * 40).Int(serial).String(trackName, RaceTrack.MaxName).Byte((byte)Math.Min(list.Count, MaxRacers));
        for (int i = 0; i < list.Count && i < MaxRacers; i++)
        {
            var s = list[i];
            w.Int(s.Key).String(s.Name, NameChars).Byte((byte)s.State).Byte((byte)s.Reason).Int((int)Math.Min(int.MaxValue, s.Ms))
             .UShort((ushort)Math.Clamp(s.Gates, 0, ushort.MaxValue)).Byte((byte)Math.Clamp(s.Place, 0, 255));
        }
        return w.ToArray();
    }

    public static void Broadcast(byte type, byte[] payload) => Net.Broadcast(Net.Channels.Race, type, payload);

    public static void SendTo(PlayerRef p, byte type, byte[] payload) => Net.SendTo(p, Net.Channels.Race, type, payload);

    // ---------------------------------------------------------------- guest -> host

    public static void SendJoin()
    {
        if (!GuestActive) return;
        Net.SendToHost(Net.Channels.Race, MsgJoin, new NetWriter(1).Bool(RaceConfig.JoinRaces.Value).ToArray());
    }

    public static string SendGiveUp()
    {
        if (!GuestActive) return "Blocked: the host doesn't run Race Mode.";
        var r = Net.SendToHost(Net.Channels.Race, MsgGiveUp, Array.Empty<byte>());
        return r == Net.SendResult.Sent ? "You gave up the race." : "Failed: could not reach the host (" + r + ").";
    }

    // ---------------------------------------------------------------- receiving

    private static void OnMessage(Net.Message m)
    {
        if (!ModManager.ServerModActive(RacePlugin.ModId)) return;
        if (Net.IsHost)
        {
            switch (m.Type)
            {
                case MsgJoin:
                {
                    var r = new NetReader(m.Payload);
                    bool on = r.Bool();
                    if (r.Ok) RaceHost.GuestJoin(m.From, on);
                    break;
                }
                case MsgGiveUp: RaceHost.GuestGiveUp(m.From); break;
                case MsgSync: RaceHost.PeerReady(m.From); break;
            }
            return;
        }
        if (!m.FromHost || !GuestActive) return;
        switch (m.Type)
        {
            case MsgTrack:
                RaceView.SetTrack(m.Payload == null || m.Payload.Length == 0 ? null : RaceTrack.Decode(m.Payload));
                break;
            case MsgCountdown: ReadCountdown(m.Payload); break;
            case MsgProgress: ReadProgress(m.Payload); break;
            case MsgResults: ReadResults(m.Payload); break;
            case MsgBoard: RaceView.MergeBoard(Leaderboard.Decode(m.Payload)); break;
        }
    }

    private static string SafeText(string s) => HostNotice.Escape(Shared.PinBoard.CleanText(s, NameChars));

    private static void ReadCountdown(byte[] payload)
    {
        var r = new NetReader(payload);
        int serial = r.Int();
        float seconds = r.Float();
        int n = r.Byte();
        if (!r.Ok || n > MaxRacers) return;
        var list = new List<(int, string)>();
        for (int i = 0; i < n; i++) list.Add((r.Int(), SafeText(r.String(NameChars))));
        if (!r.Ok) return;
        float raceSeconds = r.Remaining >= 4 ? r.Float() : 0f;
        RaceView.Countdown(serial, seconds, list, raceSeconds);
    }

    private static void ReadProgress(byte[] payload)
    {
        var r = new NetReader(payload);
        int serial = r.Int(), key = r.Int();
        var kind = (RaceEventKind)r.Byte();
        int seq = r.UShort();
        long ms = Math.Max(0, r.Int());
        bool hasDelta = r.Bool();
        long delta = r.Int();
        var reason = (DnfReason)r.Byte();
        if (!r.Ok || (kind != RaceEventKind.Gate && kind != RaceEventKind.Finish && kind != RaceEventKind.Dnf)) return;
        RaceView.Progress(serial, key, kind, seq, ms, hasDelta ? delta : null, reason);
    }

    private static void ReadResults(byte[] payload)
    {
        var r = new NetReader(payload);
        int serial = r.Int();
        string track = HostNotice.Escape(Shared.PinBoard.CleanText(r.String(RaceTrack.MaxName), RaceTrack.MaxName));
        int n = r.Byte();
        if (!r.Ok || n > MaxRacers) return;
        var list = new List<Standing>();
        for (int i = 0; i < n; i++)
            list.Add(new Standing
            {
                Key = r.Int(), Name = SafeText(r.String(NameChars)), State = (RacerState)r.Byte(), Reason = (DnfReason)r.Byte(),
                Ms = Math.Max(0, r.Int()), Gates = r.UShort(), Place = r.Byte(),
            });
        if (!r.Ok) return;
        RaceView.Done(serial, list, track);
    }
}
