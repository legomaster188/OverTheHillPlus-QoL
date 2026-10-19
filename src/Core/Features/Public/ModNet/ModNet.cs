using System;
using System.Collections.Generic;
using System.Text;
using BepInEx.Configuration;
using Fusion;
using UnityEngine;

namespace OverTheHillPlus.Features.Public.ModNet;

/// <summary>
/// ModNet: a small message layer between players who run this mod, over Fusion reliable data
/// (see <see cref="ModNetTransport"/> for the wire format).
///
/// <para><b>Safety: vanilla players never receive anything.</b> The game's receiver throws on unknown messages, so
/// nothing is sent to a player unless it proved it runs ModNet:</para>
/// <list type="bullet">
/// <item>A modded host (with the mod network on when the server starts) adds the session property
///   <see cref="SessionKey"/> = <see cref="WireProtocol"/>. Vanilla clients ignore unknown keys.</item>
/// <item>A modded guest says HELLO to the host only when that property is there, listing its active channels.
///   The host answers WELCOME with its own. Until then, and with a vanilla host, every send is a no-op that returns
///   <see cref="SendResult.NotConnected"/>.</item>
/// <item>The host sends only to guests that said hello (and forgets a guest the moment it leaves, so a reused
///   player slot is never mistaken for a modded player).</item>
/// </list>
///
/// <para><b>Feature API</b> (all main thread):</para>
/// <code>
/// // In IFeature.Register(): one channel per feature, ids from ModNet.Channels (never reuse one).
/// ModNet.Register(ModNet.Channels.MyFeature, new ModNet.ChannelOptions {
///     Name = "my feature", Version = 1, MaxPayload = 256,
///     RatePerSecond = 2f, Burst = 4,          // per sender, enforced on receive (and on send, so we never trip it)
///     AllowRelay = false,                      // true: guests may send to other guests through the host
///     HostOnlySender = false,                  // true: guests drop anything that did not come from the host itself
///     Active = () => MySetting.Value,          // advertised to the other side only while true
/// }, msg => { var r = new NetReader(msg.Payload); ... });
///
/// ModNet.SendToHost(ch, type, payload);        // guest -> host
/// ModNet.SendTo(player, ch, type, payload);    // host -> guest; guest -> guest via the host (AllowRelay)
/// ModNet.Broadcast(ch, type, payload);         // host -> every modded guest; guest -> everyone via the host
/// ModNet.HostHas(ch) / ModNet.PeerHas(p, ch)   // does the other side run (and currently accept) a channel?
/// ModNet.OnPeerReady / OnPeerGone / OnHostReady / OnSessionReset  // lifecycle callbacks
/// </code>
/// <para>Messages carry a channel id (u16), a type (byte, the feature's own) and the channel's version (byte).
/// Receivers drop messages below their <see cref="ChannelOptions.MinVersion"/>; newer versions are delivered so a
/// feature can read the fields it knows and skip the rest (append fields, never reorder). Payloads are capped per
/// channel (at most <see cref="HardMaxPayload"/> bytes). Every sender is rate limited per channel and overall;
/// excess is dropped and counted. Handlers run from <see cref="Tick"/> (Update), never inside Fusion's callback,
/// and one handler throwing never affects another. Use <see cref="NetWriter"/>/<see cref="NetReader"/> for payloads.
/// Anything a handler shows to other players must still pass the name filter.</para>
/// </summary>
internal static class ModNet
{
    public const int WireProtocol = 2;
    /// <summary>Session property a modded host publishes (value: the wire protocol as text).</summary>
    public const string SessionKey = "othp.net";
    public const int HardMaxPayload = 8192;
    private const int MaxPeers = 64, MaxChannelsAdvertised = 64, MaxQueue = 256;

    /// <summary>Channel ids. Add new ones at the end; never reuse or renumber one.</summary>
    public static class Channels
    {
        public const ushort Control = 0;
        public const ushort Names = 1;
        public const ushort Unstuck = 2;
        public const ushort Chat = 3;
        public const ushort PingMonitor = 4;
        public const ushort Moderation = 5;
        public const ushort Waypoints = 6;
        /// <summary>Race Mode: track, countdown, gate times, results and the track's leaderboard.</summary>
        public const ushort Race = 7;
        public const ushort Tag = 8;
        public const ushort Cargo = 9;
        public const ushort Horns = 10;
        /// <summary>Server Rules (OTH.Rules): the rules card, votes and notices (RulesWire.Channel, kept equal).</summary>
        public const ushort Rules = 11;
        /// <summary>Reserved for the private build: never reuse it.</summary>
        public const ushort ReservedPrivate12 = 12;
        /// <summary>Trailers: spawn / hitch requests and every trailer's hitch state (src/Mods/Trailers).</summary>
        public const ushort Trailers = 13;
        public const ushort CarLook = 15;
        public const ushort AirRide = 24;
        public const ushort OnFoot = 40;
        public const ushort StuntScore = 31;
        public const ushort Flares = 33;
        public const ushort TyrePressure = 42;
        public const ushort Fuel = 43;
        public const ushort Realism = 45;
        public const ushort Gearbox = 44;
        /// <summary>Reserved for the private build: never reuse it.</summary>
        public const ushort Reserved35 = 35;
        /// <summary>Reserved for the private build: never reuse it.</summary>
        public const ushort Reserved114 = 114;
    }

    public static ConfigEntry<bool> Enabled;

    // ---------------------------------------------------------------- public types

    public sealed class ChannelOptions
    {
        public string Name = "?";
        /// <summary>Version this build sends, 1-255.</summary>
        public byte Version = 1;
        /// <summary>Oldest version this build accepts.</summary>
        public byte MinVersion = 1;
        public int MaxPayload = 1024;
        public float RatePerSecond = 5f;
        public int Burst = 10;
        public bool AllowRelay { get; set; }
        public bool HostOnlySender;
        /// <summary>Advertised to the other side (hello / welcome) only while this returns true. Null = always.</summary>
        public Func<bool> Active;
    }

    public sealed class Message
    {
        public ushort Channel;
        public byte Type;
        public byte Version;
        /// <summary>The player who wrote the message (for a relayed message: the original sender).</summary>
        public PlayerRef From;
        /// <summary>Guest side: the message came from the host itself (not relayed from another guest).</summary>
        public bool FromHost;
        public bool Relayed;
        public byte[] Payload;
    }

    public delegate void Handler(Message message);

    public enum SendResult { Sent, Off, NotConnected, NotModded, UnknownChannel, TooBig, RateLimited, NotAllowed, Failed }

    public enum HostLink { None, Vanilla, Asking, Ready, NoAnswer, Paused }

    public enum DropReason { Rate, Size, Unknown, Version, NotPeer, Queue, Relay }

    public sealed class Peer
    {
        public PlayerRef Ref;
        public string ModVersion = "?";
        public readonly Dictionary<ushort, byte> Channels = new();
        public float Since;
        public int Dropped;
    }

    // ---------------------------------------------------------------- state

    private sealed class ChannelDef
    {
        public ushort Id;
        public ChannelOptions Opts;
        public Handler Handler;
        public int Errors;
    }

    private struct Incoming
    {
        public NetworkRunner Runner;
        public PlayerRef From;
        public int K1, K2, K3;
        public byte[] Bytes;
    }

    private sealed class Bucket
    {
        public float Tokens = -1f;
        public float Last;

        public bool Take(float rate, float burst, float cost)
        {
            float now = Time.unscaledTime;
            if (Tokens < 0f) Tokens = burst;
            else Tokens = Mathf.Min(burst, Tokens + (now - Last) * rate);
            Last = now;
            if (Tokens < cost) return false;
            Tokens -= cost;
            return true;
        }
    }

    private static readonly Dictionary<ushort, ChannelDef> Defs = new();
    private static readonly ChannelOptions ControlOpts = new() { Name = "control", MaxPayload = 1024, RatePerSecond = 1f, Burst = 5 };

    // Host side
    private static readonly Dictionary<int, Peer> PeerMap = new(); // RawEncoded -> peer
    // Guest side
    private static readonly Dictionary<ushort, byte> HostChannelMap = new();
    private static HostLink _hostLink;
    private static string _hostModVersion;
    private static int _helloTries;
    private static float _nextHello, _lastHelloAt = -100f;
    private static bool _saidHello;

    private static readonly Queue<Incoming> Inbox = new();
    private static readonly Dictionary<long, Bucket> InBuckets = new();
    private static readonly Dictionary<long, Bucket> OutBuckets = new(); // relays (host side)
    // Our own sends, per receiver: each channel's share and the receiver's totals (ModNetBudget).
    private static readonly Dictionary<long, TokenBucket> SendChannels = new();
    private sealed class SendTotal { public readonly TokenBucket Msgs = new(), Bytes = new(); }
    private static readonly Dictionary<int, SendTotal> SendTotals = new();
    private static readonly Dictionary<int, Bucket> InTotals = new(), InBytes = new();

    private static IntPtr _runnerPtr;
    private static float _runnerGoneSince = -1f, _nextSlowTick;
    private static string _activeSignature = "";
    private static bool _sessionAdvertised; // host: our key is in this session's properties
    private static bool _wasEnabled, _rewelcome;

    private static readonly List<Action<PlayerRef>> PeerReadyHooks = new(), PeerGoneHooks = new();
    private static readonly List<Action> HostReadyHooks = new(), SessionResetHooks = new();
    private static readonly HashSet<string> LoggedOnce = new();

    public static int Sent { get; private set; }
    public static int Received { get; private set; }
    private static readonly int[] Drops = new int[Enum.GetValues(typeof(DropReason)).Length];

    // ---------------------------------------------------------------- registration

    /// <summary>Registers a channel and its handler (once, from IFeature.Register). False if the id is taken.</summary>
    public static bool Register(ushort channel, ChannelOptions options, Handler handler)
    {
        if (channel == Channels.Control || options == null || Defs.ContainsKey(channel))
        {
            Core.CorePlugin.Logger?.LogWarning($"[ModNet] Channel {channel} could not be registered (reserved or taken)");
            return false;
        }
        options.MaxPayload = Mathf.Clamp(options.MaxPayload, 0, HardMaxPayload);
        if (options.Version == 0) options.Version = 1;
        if (options.MinVersion == 0) options.MinVersion = 1;
        options.Burst = Math.Max(1, options.Burst);
        options.RatePerSecond = Mathf.Max(0.01f, options.RatePerSecond);
        Defs[channel] = new ChannelDef { Id = channel, Opts = options, Handler = handler };
        DebugLog.Info(LogId, $"channel {channel} '{options.Name}' registered (version {options.Version}, min {options.MinVersion}, max {options.MaxPayload} B, " +
                             $"{options.RatePerSecond:0.##}/s burst {options.Burst}{(options.AllowRelay ? ", relay" : "")}{(options.HostOnlySender ? ", host only" : "")})");
        return true;
    }

    public static void OnPeerReady(Action<PlayerRef> hook) { if (hook != null) PeerReadyHooks.Add(hook); }
    public static void OnPeerGone(Action<PlayerRef> hook) { if (hook != null) PeerGoneHooks.Add(hook); }
    public static void OnHostReady(Action hook) { if (hook != null) HostReadyHooks.Add(hook); }
    public static void OnSessionReset(Action hook) { if (hook != null) SessionResetHooks.Add(hook); }

    // ---------------------------------------------------------------- queries

    private static NetworkRunner Runner => GameState.Runner;

    /// <summary>Hosting a multiplayer session.</summary>
    public static bool IsHost
    {
        get
        {
            var r = Runner;
            try { return r != null && r.IsRunning && r.IsServer && !r.IsSinglePlayer; } catch (Exception ex) { DebugLog.Error("core", ex, "ModNet.IsHost"); return false; }
        }
    }

    /// <summary>A guest in a multiplayer session.</summary>
    public static bool IsGuest
    {
        get
        {
            var r = Runner;
            try { return r != null && r.IsRunning && !r.IsServer && !r.IsSinglePlayer; } catch (Exception ex) { DebugLog.Error("core", ex, "ModNet.IsGuest"); return false; }
        }
    }

    public static HostLink HostStatus => _hostLink;
    public static string HostModVersion => _hostModVersion;
    public static IReadOnlyCollection<Peer> Peers => PeerMap.Values;
    public static int PeerCount => PeerMap.Count;
    public static int Dropped(DropReason r) => Drops[(int)r];
    public static bool HookInstalled => ModNetTransport.Installed;
    public static bool HostAdvertised => _sessionAdvertised;

    /// <summary>Guest: the host speaks ModNet and currently advertises <paramref name="channel"/>.</summary>
    public static bool HostHas(ushort channel) => HostChannelVersion(channel) > 0;

    /// <summary>Guest: the version of <paramref name="channel"/> the host advertises, 0 = none.</summary>
    public static byte HostChannelVersion(ushort channel) =>
        _hostLink == HostLink.Ready && HostChannelMap.TryGetValue(channel, out var v) ? v : (byte)0;

    /// <summary>Host: the guest runs ModNet and currently advertises <paramref name="channel"/>.</summary>
    public static bool PeerHas(PlayerRef player, ushort channel) =>
        PeerMap.TryGetValue(player.RawEncoded, out var p) && p.Channels.ContainsKey(channel);

    public static bool IsPeer(PlayerRef player) => PeerMap.ContainsKey(player.RawEncoded);

    public static string ChannelName(ushort channel) =>
        channel == Channels.Control ? "control" : Defs.TryGetValue(channel, out var d) ? d.Opts.Name : "#" + channel;

    /// <summary>The channel's name when this game runs it, else null (see ModNetAdvert.ChannelSummary).</summary>
    public static string KnownChannelName(ushort channel) =>
        channel == Channels.Control ? "control" : Defs.TryGetValue(channel, out var d) ? d.Opts.Name : null;

    // ---------------------------------------------------------------- sending

    /// <summary>Guest: sends to the host. Needs a host that advertised the channel.</summary>
    public static SendResult SendToHost(ushort channel, byte type, byte[] payload)
    {
        var pre = Check(channel, payload, out var def, out var r);
        if (pre != SendResult.Sent) return pre;
        if (!IsGuest) return SendResult.NotConnected;
        if (!HostHas(channel)) return _hostLink == HostLink.Ready ? SendResult.NotModded : SendResult.NotConnected;
        return Out(r, PlayerRef.None, true, def, type, 0, 0, payload, HostKey);
    }

    /// <summary>
    /// Host: sends to one modded guest. Guest: sends to another guest through the host (channel must allow relay),
    /// or to the host when <paramref name="to"/> is the host.
    /// </summary>
    public static SendResult SendTo(PlayerRef to, ushort channel, byte type, byte[] payload)
    {
        var pre = Check(channel, payload, out var def, out var r);
        if (pre != SendResult.Sent) return pre;
        if (IsHost)
        {
            if (!PeerHas(to, channel)) return SendResult.NotModded;
            return Out(r, to, false, def, type, 0, 0, payload, to.RawEncoded);
        }
        if (!IsGuest) return SendResult.NotConnected;
        if (!HostHas(channel)) return _hostLink == HostLink.Ready ? SendResult.NotModded : SendResult.NotConnected;
        if (!def.Opts.AllowRelay) return SendResult.NotAllowed;
        return Out(r, PlayerRef.None, true, def, type, ModNetTransport.FlagRelayRequest, to.RawEncoded, payload, HostKey);
    }

    /// <summary>Host: to every modded guest with the channel (returns how many). Guest: to everyone through the host.</summary>
    public static int Broadcast(ushort channel, byte type, byte[] payload)
    {
        if (Check(channel, payload, out var def, out var r) != SendResult.Sent) return 0;
        if (IsHost)
        {
            int n = 0;
            foreach (var p in PeerMap.Values)
                if (p.Channels.ContainsKey(channel) && Out(r, p.Ref, false, def, type, 0, 0, payload, p.Ref.RawEncoded) == SendResult.Sent) n++;
            return n;
        }
        if (IsGuest && HostHas(channel) && def.Opts.AllowRelay)
            return Out(r, PlayerRef.None, true, def, type, ModNetTransport.FlagRelayRequest, 0, payload, HostKey) == SendResult.Sent ? 1 : 0;
        return 0;
    }

    private const int HostKey = int.MinValue; // bucket key for "the host"

    private static SendResult Check(ushort channel, byte[] payload, out ChannelDef def, out NetworkRunner r)
    {
        r = Runner;
        def = null;
        if (Enabled == null || !Enabled.Value) return SendResult.Off;
        if (!Defs.TryGetValue(channel, out def)) return SendResult.UnknownChannel;
        if ((payload?.Length ?? 0) > def.Opts.MaxPayload) return SendResult.TooBig;
        if (r == null || !ModNetTransport.Installed) return SendResult.NotConnected;
        return SendResult.Sent;
    }

    private static SendResult Out(NetworkRunner r, PlayerRef to, bool toServer, ChannelDef def, byte type, int flags, int k3, byte[] payload, int bucketKey)
    {
        var opts = def?.Opts ?? ControlOpts;
        ushort ch = def?.Id ?? Channels.Control;
        // Spend our own tokens: the channel's (at most its share of the total) and the receiver's total, a little under
        // what the receiver accepts from us on all channels together, so a well-behaved sender is never dropped there.
        var total = SendTotalFor(bucketKey);
        if (!ModNetBudget.TrySpend(Time.unscaledTime, SendChannelFor(bucketKey, ch), total.Msgs, total.Bytes,
                opts.RatePerSecond, opts.Burst, ch == Channels.Control, payload?.Length ?? 0))
            return SendResult.RateLimited;
        if (!ModNetTransport.Send(r, to, toServer, PackK1(ch, type, opts.Version), WireProtocol | (flags << 8), k3, payload))
        {
            if (DebugLog.IsInfo) NoteSendFailed(ch, type, payload?.Length ?? 0, to, toServer);
            return SendResult.Failed;
        }
        Sent++;
        if (DebugLog.IsVerbose)
        {
            int size = payload?.Length ?? 0;
            NetVerbose(DirSend | ch, () => $"send {ChannelName(ch)} ({ch}) type {type} {size} B to {PeerText(to, toServer)}" +
                                           (flags != 0 ? $" flags {flags:X}" : ""));
        }
        return SendResult.Sent;
    }

    private static void NoteSendFailed(ushort ch, byte type, int size, PlayerRef to, bool toServer)
    {
        // A failed send is worth an Info line, but never a flood of them.
        NetVerbose(DirSend | ch, () => $"send FAILED {ChannelName(ch)} ({ch}) type {type} {size} B to {PeerText(to, toServer)}: {ModNetTransport.LastError ?? "no reason given"}", info: true);
    }

    private static int PackK1(ushort ch, byte type, byte version) => ch | (type << 16) | (version << 24);

    private static TokenBucket SendChannelFor(int who, ushort ch)
    {
        long key = ((long)who << 16) | ch;
        if (!SendChannels.TryGetValue(key, out var b))
        {
            if (SendChannels.Count > 4096) SendChannels.Clear();
            SendChannels[key] = b = new TokenBucket();
        }
        return b;
    }

    private static SendTotal SendTotalFor(int who)
    {
        if (!SendTotals.TryGetValue(who, out var t))
        {
            if (SendTotals.Count > 256) SendTotals.Clear();
            SendTotals[who] = t = new SendTotal();
        }
        return t;
    }

    private static Bucket BucketFor(Dictionary<long, Bucket> map, int who, ushort ch)
    {
        long key = ((long)who << 16) | ch;
        if (!map.TryGetValue(key, out var b))
        {
            if (map.Count > 4096) map.Clear();
            map[key] = b = new Bucket();
        }
        return b;
    }

    // ---------------------------------------------------------------- receive (called inside Fusion's callback)

    /// <summary>Cheap checks and queueing only; the work happens in <see cref="Tick"/>.</summary>
    internal static void OnWire(NetworkRunner runner, PlayerRef from, int k1, int k2, int k3, byte[] bytes)
    {
        if (Enabled == null || !Enabled.Value) return; // swallowed: the hook only stays to keep our messages from the game
        ushort ch = (ushort)(k1 & 0xFFFF);
        ChannelOptions opts = ch == Channels.Control ? ControlOpts : Defs.TryGetValue(ch, out var d) ? d.Opts : null;
        if (opts == null) { DropAt(DropReason.Unknown, ch, bytes?.Length ?? 0); return; }
        if (bytes.Length > opts.MaxPayload) { DropAt(DropReason.Size, ch, bytes.Length); return; }

        bool server;
        try { server = runner != null && runner.IsServer; } catch (Exception ex) { DebugLog.Error("core", ex, "ModNet.OnWire"); return; }
        int flags = (k2 >> 8) & 0xFF;
        // Rate limit by the original author: the guest who sent it (host side) or, guest side, the relayed author / the host.
        int who = server ? from.RawEncoded : (flags & ModNetTransport.FlagRelayed) != 0 ? k3 : HostKey;
        if (server && !from.IsRealPlayer) return;

        if (!TakeTotal(who, bytes.Length) || !BucketFor(InBuckets, who, ch).Take(opts.RatePerSecond, opts.Burst, 1f))
        {
            DropAt(DropReason.Rate, ch, bytes.Length);
            if (server && PeerMap.TryGetValue(from.RawEncoded, out var p)) p.Dropped++;
            return;
        }
        if (Inbox.Count >= MaxQueue) { DropAt(DropReason.Queue, ch, bytes.Length); return; }
        Inbox.Enqueue(new Incoming { Runner = runner, From = from, K1 = k1, K2 = k2, K3 = k3, Bytes = bytes });
    }

    private static bool TakeTotal(int who, int bytes)
    {
        if (!InTotals.TryGetValue(who, out var msgs)) { if (InTotals.Count > 256) InTotals.Clear(); InTotals[who] = msgs = new Bucket(); }
        if (!InBytes.TryGetValue(who, out var size)) { if (InBytes.Count > 256) InBytes.Clear(); InBytes[who] = size = new Bucket(); }
        // Every sender, all channels together: 40 messages burst / 20 a second, 32 KB burst / 16 KB a second
        // (ModNetBudget: senders keep a little under this per receiver).
        return msgs.Take(ModNetBudget.InRate, ModNetBudget.InBurst, 1f) && size.Take(ModNetBudget.InBytesRate, ModNetBudget.InBytesBurst, Math.Max(1, bytes));
    }

    internal static void CountDrop(DropReason r) => Drops[(int)r]++;

    // ---------------------------------------------------------------- per frame

    public static void Tick()
    {
        var r = Runner;
        try
        {
            if (ModNetTransport.Faulted && ModNetTransport.Installed) ModNetTransport.SetInstalled(false);
            if (Inbox.Count > 0) Pump();
        }
        catch (Exception e) { DebugLog.Error("core", e, "ModNet.Tick"); LogOnce("pump", "[ModNet] Delivering messages failed: " + e); Inbox.Clear(); }

        if (Time.unscaledTime < _nextSlowTick) return;
        _nextSlowTick = Time.unscaledTime + 0.5f;
        try { SlowTick(r); }
        catch (Exception e) { DebugLog.Error("core", e, "ModNet.Tick"); LogOnce("tick", "[ModNet] Tick: " + e.Message); }
    }

    private static void SlowTick(NetworkRunner r)
    {
        if (r != null)
        {
            SyncSession(r);
            _runnerGoneSince = -1f;
        }
        else if (_runnerPtr != IntPtr.Zero || ModNetTransport.Installed)
        {
            // GameState refreshes once a second, so only forget the session once it has really been gone a while.
            if (_runnerGoneSince < 0f) _runnerGoneSince = Time.unscaledTime;
            else if (Time.unscaledTime - _runnerGoneSince > 3f)
            {
                ResetSession();
                _runnerPtr = IntPtr.Zero;
                _runnerGoneSince = -1f;
                ModNetTransport.SetInstalled(false);
            }
            return;
        }
        if (r == null) return;

        bool running, single, server;
        try { running = r.IsRunning; single = r.IsSinglePlayer; server = r.IsServer; }
        catch (Exception ex) { DebugLog.Error("core", ex, "ModNet.SlowTick"); return; }
        bool multi = running && !single;
        bool enabled = Enabled.Value;

        // Sticky for the session (ResetSession clears it): the properties can read back without our key for a moment
        // (or lose it to a property update), and dropping the hook then would hand guests' messages to the game.
        if (server && multi && !_sessionAdvertised) _sessionAdvertised = ReadAdvert(r) > 0;
        int hostProto = !server && multi ? ReadAdvert(r) : 0;

        // The hook stays while anyone may still send to us: as host for the whole advertised session (even if the
        // network is switched off meanwhile, so guests' messages keep being swallowed), as guest once we said hello.
        bool want = multi && (server ? _sessionAdvertised : (enabled && hostProto == WireProtocol) || _saidHello);
        NoteHook(want, multi, server, enabled, hostProto);
        ModNetTransport.SetInstalled(want);
        ModNetTransport.PruneRecent();

        if (_wasEnabled != enabled && multi && ModNetTransport.Installed) EnabledChanged(r, server, enabled);
        _wasEnabled = enabled;
        if (!enabled || !multi || !ModNetTransport.Installed) return;

        if (server) HostTick(r);
        else GuestTick(r, hostProto);
    }

    /// <summary>
    /// Switched off: say bye (a guest's host forgets it; a host's guests pause) - anything still sent to us is swallowed.
    /// Switched back on: a host re-welcomes the guests it knows, a guest says hello again.
    /// </summary>
    private static void EnabledChanged(NetworkRunner r, bool server, bool enabled)
    {
        DebugLog.Info(LogId, $"mod network switched {(enabled ? "on" : "off")} during a session ({(server ? "host" : "guest")}): " +
                             (enabled ? (server ? "welcoming the known guests again" : "hello again") : "saying bye"));
        if (server)
        {
            if (!enabled) foreach (var p in PeerMap.Values) SendControl(r, p.Ref, false, CtlBye, Array.Empty<byte>());
            _rewelcome = enabled; // HostTick sends a fresh welcome to every known guest
            return;
        }
        if (!enabled && _hostLink == HostLink.Ready) SendControl(r, PlayerRef.None, true, CtlBye, Array.Empty<byte>());
        HostChannelMap.Clear();
        _hostLink = HostLink.None;
        _helloTries = 0;
    }

    private static void HostTick(NetworkRunner r)
    {
        if (PeerMap.Count > 0)
        {
            List<int> gone = null;
            foreach (var kv in PeerMap)
            {
                bool valid;
                try { valid = r.IsPlayerValid(kv.Value.Ref); } catch (Exception ex) { DebugLog.Error("core", ex, "ModNet.HostTick"); valid = true; }
                if (!valid) (gone ??= new List<int>()).Add(kv.Key);
            }
            if (gone != null) foreach (int raw in gone) ForgetPeer(raw, "left");
        }
        string sig = ActiveSignature();
        if (sig != _activeSignature || _rewelcome)
        {
            _activeSignature = sig;
            _rewelcome = false;
            byte[] welcome = Advert();
            foreach (var p in PeerMap.Values) SendControl(r, p.Ref, false, CtlWelcome, welcome);
        }
    }

    private static void GuestTick(NetworkRunner r, int hostProto)
    {
        if (hostProto != WireProtocol)
        {
            // Not advertised (or a different wire protocol). A link already made is kept: the session properties can
            // read back empty for a moment and the host is still there.
            if (_hostLink != HostLink.Ready && _hostLink != HostLink.Paused)
                SetLink(hostProto == 0 ? HostLink.Vanilla : HostLink.NoAnswer,
                        hostProto == 0 ? "the host's session does not advertise the mod network" : $"the host advertises protocol {hostProto}, this build speaks {WireProtocol}");
            return;
        }
        try { if (!r.LocalPlayer.IsRealPlayer) return; } catch (Exception ex) { DebugLog.Error("core", ex, "ModNet.GuestTick"); return; }
        float now = Time.unscaledTime;
        string sig = ActiveSignature();
        if (_hostLink == HostLink.Ready)
        {
            // Our active channels changed (a setting was flipped): tell the host, at most every 2 s.
            if (sig != _activeSignature && now - _lastHelloAt > 2f) SendHello(r, sig);
            return;
        }
        if (_hostLink == HostLink.NoAnswer || _hostLink == HostLink.Paused) return;
        if (_hostLink != HostLink.Asking) { SetLink(HostLink.Asking, "the host advertises the mod network"); _helloTries = 0; _nextHello = 0f; }
        if (now < _nextHello) return;
        if (_helloTries >= 6) { SetLink(HostLink.NoAnswer, "no welcome after 6 hellos"); Core.CorePlugin.Logger?.LogInfo("[ModNet] The host did not answer; mod messages are off for this session"); return; }
        _helloTries++;
        _nextHello = now + 3f;
        SendHello(r, sig);
    }

    private static void SendHello(NetworkRunner r, string sig)
    {
        _activeSignature = sig;
        _lastHelloAt = Time.unscaledTime;
        if (SendControl(r, PlayerRef.None, true, CtlHello, Advert()))
        {
            DebugLog.Info(LogId, $"hello sent to the host (try {_helloTries}, active channels: {sig})");
            _saidHello = true;
            if (_helloTries <= 1) Core.CorePlugin.Logger?.LogInfo("[ModNet] Said hello to the host");
        }
    }

    /// <summary>The host's advertised protocol (0 = not advertised / unreadable).</summary>
    private static int ReadAdvert(NetworkRunner r)
    {
        try
        {
            var s = GameState.SessionString(r.SessionInfo, SessionKey, out bool found);
            return found && int.TryParse(s, out int v) && v > 0 ? v : 0;
        }
        catch (Exception ex) { DebugLog.Error("core", ex, "ModNet.ReadAdvert"); return 0; }
    }

    private static void SyncSession(NetworkRunner r)
    {
        IntPtr ptr;
        try { ptr = r.Pointer; } catch (Exception ex) { DebugLog.Error("core", ex, "ModNet.SyncSession"); return; }
        if (ptr == IntPtr.Zero || ptr == _runnerPtr) return;
        if (_runnerPtr != IntPtr.Zero) ResetSession();
        _runnerPtr = ptr;
        DebugLog.Info(LogId, "new network runner seen: mod network state starts fresh");
    }

    private static void ResetSession()
    {
        DebugLog.Info(LogId, $"session reset ({PeerMap.Count} modded guests, link {_hostLink}, {Inbox.Count} queued messages dropped; sent {Sent}, received {Received} so far)");
        PeerMap.Clear();
        HostChannelMap.Clear();
        _hostLink = HostLink.None;
        _hostModVersion = null;
        _helloTries = 0;
        _nextHello = 0f;
        _saidHello = false;
        _sessionAdvertised = false;
        _activeSignature = "";
        _rewelcome = false;
        Inbox.Clear();
        InBuckets.Clear();
        OutBuckets.Clear();
        SendChannels.Clear();
        SendTotals.Clear();
        InTotals.Clear();
        InBytes.Clear();
        ModNetTransport.ClearRecent();
        foreach (var h in SessionResetHooks)
        {
            try { h(); } catch (Exception e) { DebugLog.Error("core", e, "ModNet.ResetSession"); LogOnce("reset" + h.Method.Name, "[ModNet] Session reset hook: " + e.Message); }
        }
    }

    // ---------------------------------------------------------------- delivery

    private static void Pump()
    {
        int budget = 64; // per frame
        while (Inbox.Count > 0 && budget-- > 0)
        {
            var m = Inbox.Dequeue();
            bool live;
            try { live = m.Runner != null && m.Runner.IsRunning; } catch (Exception ex) { DebugLog.Error("core", ex, "ModNet.Pump"); live = false; }
            if (!live) continue; // from a session we left
            Received++;
            Deliver(m.Runner, m);
        }
    }

    private static void Deliver(NetworkRunner r, Incoming m)
    {
        ushort ch = (ushort)(m.K1 & 0xFFFF);
        byte type = (byte)((m.K1 >> 16) & 0xFF);
        byte version = (byte)((m.K1 >> 24) & 0xFF);
        int flags = (m.K2 >> 8) & 0xFF;
        bool server = r.IsServer;
        if (DebugLog.IsVerbose)
        {
            var from0 = m.From;
            int size = m.Bytes?.Length ?? 0;
            NetVerbose(DirRecv | ch, () => $"recv {ChannelName(ch)} ({ch}) type {type} v{version} {size} B from {PeerText(from0, false)}" +
                                           (flags != 0 ? $" flags {flags:X}" : ""));
        }

        if (ch == Channels.Control) { HandleControl(r, server, m.From, type, m.Bytes); return; }
        if (!Defs.TryGetValue(ch, out var def)) { DropAt(DropReason.Unknown, ch, m.Bytes?.Length ?? 0); return; }
        if (version < def.Opts.MinVersion) { DropAt(DropReason.Version, ch, m.Bytes?.Length ?? 0); return; }

        if (server)
        {
            // Only guests that said hello may use feature channels.
            if (!PeerMap.ContainsKey(m.From.RawEncoded)) { DropAt(DropReason.NotPeer, ch, m.Bytes?.Length ?? 0); return; }
            if ((flags & ModNetTransport.FlagRelayRequest) != 0)
            {
                if (!def.Opts.AllowRelay) { DropAt(DropReason.Relay, ch, m.Bytes?.Length ?? 0); return; }
                Relay(r, def, type, version, m.From, m.K3, m.Bytes);
                int hostRaw;
                try { hostRaw = r.LocalPlayer.RawEncoded; } catch (Exception ex) { DebugLog.Error("core", ex, "ModNet.Deliver"); hostRaw = 0; }
                if (m.K3 != 0 && m.K3 != hostRaw) return; // not addressed to the host too
                Invoke(def, new Message { Channel = ch, Type = type, Version = version, From = m.From, Relayed = true, Payload = m.Bytes });
                return;
            }
            Invoke(def, new Message { Channel = ch, Type = type, Version = version, From = m.From, Payload = m.Bytes });
            return;
        }

        bool relayed = (flags & ModNetTransport.FlagRelayed) != 0;
        if (relayed && (def.Opts.HostOnlySender || !def.Opts.AllowRelay)) { DropAt(DropReason.Relay, ch, m.Bytes?.Length ?? 0); return; }
        if ((flags & ModNetTransport.FlagRelayRequest) != 0) { DropAt(DropReason.Relay, ch, m.Bytes?.Length ?? 0); return; } // only hosts relay
        var from = relayed ? PlayerRef.FromEncoded(m.K3) : m.From;
        Invoke(def, new Message { Channel = ch, Type = type, Version = version, From = from, FromHost = !relayed, Relayed = relayed, Payload = m.Bytes });
    }

    /// <summary>Host: forwards a guest's message to its target (0 = every other modded guest with the channel).</summary>
    private static void Relay(NetworkRunner r, ChannelDef def, byte type, byte version, PlayerRef origin, int target, byte[] bytes)
    {
        int k1 = PackK1(def.Id, type, version);
        int k2 = WireProtocol | (ModNetTransport.FlagRelayed << 8);
        foreach (var p in PeerMap.Values)
        {
            int raw = p.Ref.RawEncoded;
            if (raw == origin.RawEncoded || (target != 0 && raw != target) || !p.Channels.ContainsKey(def.Id)) continue;
            // The origin was rate limited on arrival; this keeps one guest from using the host to flood another.
            if (!BucketFor(OutBuckets, raw ^ (origin.RawEncoded << 8), def.Id).Take(def.Opts.RatePerSecond, def.Opts.Burst, 1f)) { DropAt(DropReason.Rate, def.Id, bytes?.Length ?? 0); continue; }
            if (ModNetTransport.Send(r, p.Ref, false, k1, k2, origin.RawEncoded, bytes))
            {
                Sent++;
                if (DebugLog.IsVerbose)
                {
                    var to = p.Ref;
                    int size = bytes?.Length ?? 0;
                    NetVerbose(DirRelay | def.Id, () => $"relay {ChannelName(def.Id)} ({def.Id}) type {type} {size} B from {PeerText(origin, false)} to {PeerText(to, false)}");
                }
            }
        }
    }

    private static void Invoke(ChannelDef def, Message msg)
    {
        if (def.Handler == null || def.Errors >= 25) return;
        try { def.Handler(msg); }
        catch (Exception e)
        {
            DebugLog.Error("core", e, "ModNet.Invoke");
            def.Errors++;
            if (def.Errors >= 25) DebugLog.Error(LogId, $"channel {def.Id} '{def.Opts.Name}': its handler failed 25 times and is switched off for this session");
            LogOnce("handler" + def.Id, $"[ModNet] Handler for '{def.Opts.Name}' failed{(def.Errors >= 25 ? " (disabled)" : "")}: {e}");
        }
    }

    // ---------------------------------------------------------------- control channel

    private const byte CtlHello = 1, CtlWelcome = 2, CtlBye = 3;

    private static bool SendControl(NetworkRunner r, PlayerRef to, bool toServer, byte type, byte[] payload) =>
        Out(r, to, toServer, null, type, 0, 0, payload, toServer ? HostKey : to.RawEncoded) == SendResult.Sent;

    /// <summary>Hello / welcome payload: mod version, then the channels this side currently runs.</summary>
    private static byte[] Advert()
    {
        var w = new NetWriter(64).String(Core.CorePlugin.Version, 24);
        var active = new List<ChannelDef>();
        foreach (var d in Defs.Values)
            if (IsActive(d)) active.Add(d);
        // Our channels first: other creators' channels (1000 and up) never push one of ours out of the list.
        var list = AdvertOrder.Pick(active, d => d.Id, MaxChannelsAdvertised);
        w.Byte((byte)list.Count);
        foreach (var d in list) w.UShort(d.Id).Byte(d.Opts.Version);
        return w.ToArray();
    }

    private static bool IsActive(ChannelDef d)
    {
        try { return d.Opts.Active == null || d.Opts.Active(); }
        catch (Exception ex) { DebugLog.Error("core", ex, "ModNet.IsActive"); return false; }
    }

    private static string ActiveSignature()
    {
        var sb = new StringBuilder();
        foreach (var d in Defs.Values)
            if (IsActive(d)) sb.Append(d.Id).Append(',');
        return sb.ToString();
    }

    private static string ReadAdvertInto(byte[] bytes, Dictionary<ushort, byte> into)
    {
        var rd = new NetReader(bytes);
        string version = rd.String(24);
        int n = rd.Byte();
        into.Clear();
        for (int i = 0; i < n && i < MaxChannelsAdvertised && rd.Ok; i++)
        {
            ushort ch = rd.UShort();
            byte v = rd.Byte();
            if (rd.Ok && ch != Channels.Control && v > 0) into[ch] = v;
        }
        return string.IsNullOrEmpty(version) ? "?" : version;
    }

    private static void HandleControl(NetworkRunner r, bool server, PlayerRef from, byte type, byte[] bytes)
    {
        if (server)
        {
            if (!from.IsRealPlayer) return;
            int raw = from.RawEncoded;
            if (type == CtlBye) { ForgetPeer(raw, "switched the mod network off"); return; }
            if (type != CtlHello) return;
            if (!PeerMap.TryGetValue(raw, out var peer))
            {
                if (PeerMap.Count >= MaxPeers) { CountDrop(DropReason.Queue); return; }
                PeerMap[raw] = peer = new Peer { Ref = from, Since = Time.unscaledTime };
                Core.CorePlugin.Logger?.LogInfo($"[ModNet] Player {from.PlayerId} runs the mod network");
            }
            peer.ModVersion = ReadAdvertInto(bytes, peer.Channels);
            DebugLog.Info(LogId, $"hello from player {from.PlayerId}: mod {peer.ModVersion}, channels {ModNetAdvert.ChannelSummary(peer.Channels.Keys, KnownChannelName)}; welcoming ({PeerMap.Count} modded guests)");
            SendControl(r, from, false, CtlWelcome, Advert());
            foreach (var h in PeerReadyHooks)
            {
                try { h(from); } catch (Exception e) { DebugLog.Error("core", e, "ModNet.HandleControl"); LogOnce("ready" + h.Method.Name, "[ModNet] Peer hook: " + e.Message); }
            }
            return;
        }

        if (type == CtlBye)
        {
            // The host switched its mod network off: stay quiet until it welcomes us again.
            HostChannelMap.Clear();
            SetLink(HostLink.Paused, "the host said bye (its mod network is off)");
            Core.CorePlugin.Logger?.LogInfo("[ModNet] The host switched the mod network off");
            return;
        }
        if (type != CtlWelcome) return;
        bool first = _hostLink != HostLink.Ready;
        _hostModVersion = ReadAdvertInto(bytes, HostChannelMap);
        if (first || DebugLog.IsVerbose)
            DebugLog.Info(LogId, $"welcome from the host: mod {_hostModVersion}, channels {ModNetAdvert.ChannelSummary(HostChannelMap.Keys, KnownChannelName)}");
        SetLink(HostLink.Ready, "the host welcomed us");
        if (first) Core.CorePlugin.Logger?.LogInfo($"[ModNet] The host runs the mod network ({HostChannelMap.Count} channel(s))");
        foreach (var h in HostReadyHooks)
        {
            try { h(); } catch (Exception e) { DebugLog.Error("core", e, "ModNet.HandleControl"); LogOnce("host" + h.Method.Name, "[ModNet] Host hook: " + e.Message); }
        }
    }

    private static void ForgetPeer(int raw, string why)
    {
        if (!PeerMap.TryGetValue(raw, out var p)) return;
        PeerMap.Remove(raw);
        DebugLog.Info(LogId, $"player {p.Ref.PlayerId} forgotten: {why} ({PeerMap.Count} modded guests left)");
        Core.CorePlugin.Logger?.LogInfo($"[ModNet] Player {p.Ref.PlayerId} {why}");
        foreach (var h in PeerGoneHooks)
        {
            try { h(p.Ref); } catch (Exception e) { DebugLog.Error("core", e, "ModNet.ForgetPeer"); LogOnce("gone" + h.Method.Name, "[ModNet] Peer hook: " + e.Message); }
        }
    }

    /// <summary>Joins and leaves (from the game's runner callbacks): a reused player slot never inherits "modded".</summary>
    internal static void PlayerJoinedOrLeft(PlayerRef p, bool joined)
    {
        try { ForgetPeer(p.RawEncoded, joined ? "slot reused by a new player" : "left"); }
        catch (Exception e) { DebugLog.Error("core", e, "ModNet.PlayerJoinedOrLeft"); LogOnce("joinleave", "[ModNet] join/leave: " + e.Message); }
    }

    // ---------------------------------------------------------------- session property

    /// <summary>Adds <see cref="SessionKey"/> to a hosted session's start arguments.</summary>
    internal static void OnStartGame(StartGameArgs args)
    {
        try
        {
            if (Enabled == null || !Enabled.Value) { DebugLog.Info(LogId, "starting a game: the mod network is off, the session will not advertise it"); return; }
            // Hook off after an error: don't invite guests to send messages only the game's receiver would see.
            if (ModNetTransport.Faulted) { DebugLog.Info(LogId, "starting a game: the receive hook is faulted, the session will not advertise the mod network"); return; }
            var mode = args.GameMode;
            if (mode != GameMode.Host && mode != GameMode.Server) return;
            var props = args.SessionProperties;
            if (props == null)
            {
                props = new Il2CppSystem.Collections.Generic.Dictionary<string, SessionProperty>();
                args.SessionProperties = props;
            }
            // Photon allows 10 custom properties: a fixed rank among ours decides, whatever order the prefixes run in.
            var p = props;
            if (!Core.Manager.SessionSlots.MayAdd(SessionKey, props.Count, k => p.ContainsKey(k))) { LogOnce("props", "[ModNet] No room for the mod network's session property"); return; }
            props[SessionKey] = WireProtocol.ToString();
            DebugLog.Info(LogId, $"hosting ({mode}): the session advertises mod network protocol {WireProtocol} ({props.Count} session properties)");
        }
        catch (Exception e) { DebugLog.Error("core", e, "ModNet.OnStartGame"); LogOnce("start", "[ModNet] Could not advertise the mod network: " + e.Message); }
    }

    // ---------------------------------------------------------------- debug log

    // Verbose lines for each message, at most 5 a second per channel and direction (the rest are counted and the next
    // line says how many were left out). Info lines are for protocol decisions: hello, welcome, bye, links, hooks.
    private const string LogId = "modnet";
    private const int DirSend = 1 << 16, DirRecv = 2 << 16, DirDrop = 3 << 16, DirRelay = 4 << 16;
    private static readonly Logging.KeyedRateLimiter<int> VerboseLimit = new(1.0, 5, 128);

    /// <summary>One Verbose line for a message, rate limited per channel and direction. Callers check
    /// <see cref="DebugLog.IsVerbose"/> first (the delegate allocates). Any thread.</summary>
    private static void NetVerbose(int channelAndDir, Func<string> line, bool info = false)
    {
        bool allow;
        int more;
        lock (VerboseLimit) allow = VerboseLimit.Allow(channelAndDir, DebugLog.Now, out more);
        if (!allow) return;
        string text = line();
        if (more > 0) text += $" (+{more} like it not shown)";
        if (info) DebugLog.Info(LogId, text);
        else DebugLog.Verbose(LogId, text);
    }

    private static string PeerText(PlayerRef p, bool toServer)
    {
        if (toServer) return "host";
        try { return "player " + p.PlayerId; } catch (Exception ex) { DebugLog.Error("core", ex, "ModNet.PeerText"); return "player ?"; }
    }

    /// <summary>Counts a dropped message and says why at Verbose.</summary>
    private static void DropAt(DropReason reason, ushort channel, int size)
    {
        CountDrop(reason);
        if (DebugLog.IsVerbose)
            NetVerbose(DirDrop | channel, () => $"dropped on channel {ChannelName(channel)} ({channel}): {reason}, {size} B");
    }

    private static void SetLink(HostLink link, string why)
    {
        if (_hostLink == link) return;
        var from = _hostLink;
        _hostLink = link;
        DebugLog.Info(LogId, $"host link {from} -> {link}: {why}");
    }

    private static bool _lastWant;

    private static void NoteHook(bool want, bool multi, bool server, bool enabled, int hostProto)
    {
        if (want == _lastWant) return;
        _lastWant = want;
        if (DebugLog.IsInfo)
            DebugLog.Info(LogId, want
                ? $"receive hook wanted: {(server ? "hosting an advertised session" : "host advertises protocol " + hostProto + " and we are on")}"
                : $"receive hook no longer wanted (multi {multi}, {(server ? "host" : "guest")}, enabled {enabled}, host protocol {hostProto})");
    }

    // ---------------------------------------------------------------- misc

    internal static void LogOnce(string id, string message)
    {
        if (LoggedOnce.Count < 200 && LoggedOnce.Add(id)) Core.CorePlugin.Logger?.LogWarning(message);
    }

    /// <summary>The mod network's state for the diagnostics snapshot (core's Logging/Snapshot).</summary>
    internal static Logging.SnapshotModNet Snapshot()
    {
        var s = new Logging.SnapshotModNet
        {
            Enabled = Enabled != null && Enabled.Value,
            Installed = ModNetTransport.Installed,
            Faulted = ModNetTransport.Faulted,
            LastError = ModNetTransport.LastError,
            Describe = Describe(),
            Role = IsHost ? "host" : IsGuest ? "guest, link " + _hostLink : "not in a multiplayer session",
            Sent = Sent,
            Received = Received,
            Peers = PeerMap.Count,
        };
        foreach (DropReason r in Enum.GetValues(typeof(DropReason)))
            if (Dropped(r) > 0) s.Drops.Add(r + " " + Dropped(r));
        foreach (var p in PeerMap.Values)
            s.PeerLines.Add($"player {p.Ref.PlayerId} mod {p.ModVersion}: {ModNetAdvert.ChannelSummary(p.Channels.Keys, KnownChannelName)}" +
                            (p.Dropped > 0 ? $", {p.Dropped} dropped" : ""));
        if (IsGuest && _hostLink == HostLink.Ready)
            s.PeerLines.Add($"host mod {_hostModVersion}: {ModNetAdvert.ChannelSummary(HostChannelMap.Keys, KnownChannelName)}");
        return s;
    }

    /// <summary>One line for menus: what the mod network is doing right now.</summary>
    public static string Describe()
    {
        if (Enabled == null || !Enabled.Value) return "off";
        if (ModNetTransport.Faulted) return "off after an error (restart the game to retry)";
        if (IsHost)
            return !_sessionAdvertised ? "hosting, but this server started with the mod network off: start a new one"
                : $"hosting: {PeerMap.Count} modded guest(s)";
        if (IsGuest)
            return _hostLink switch
            {
                HostLink.Ready => $"connected to a modded host (mod {_hostModVersion}, {HostChannelMap.Count} channel(s))",
                HostLink.Asking => "asking the host...",
                HostLink.NoAnswer => "the host did not answer (different mod version?)",
                HostLink.Paused => "the host switched its mod network off",
                _ => "the host does not run the mod network",
            };
        return "not in a multiplayer session";
    }
}
