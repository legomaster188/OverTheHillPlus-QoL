using System;
using System.Collections.Generic;
using Fusion;
using UnityEngine;
using Net = OverTheHillPlus.Features.Public.ModNet.ModNet;

namespace OverTheHillPlus.Horns;

// After the namespace line on purpose: the game has its own global ModManager (mod.io).
using OverTheHillPlus.Core.Manager;

/// <summary>
/// Who picked which horn, over the mod network (channel <see cref="Channel"/>, wire format in <see cref="HornWire"/>).
///
/// <para><b>Choices:</b> once connected (host, or a guest whose host runs this mod), you announce your horn to
/// everyone, asking them to answer with theirs; you announce again whenever it changes. Built-in horns go as an id.
/// When to play is not sent: the game already shares every car's horn button with every player, so each game plays
/// the owner's chosen horn when that car honks, in step with what players without the mod hear (the stock horn).</para>
///
/// <para><b>Custom files:</b> a receiver that wants a player's own wav asks that player for it; the owner sends it in
/// 6 KB chunks, two a second, only for its current file, at most 8 times per player per file. The receiver
/// re-asks for missing chunks (5 tries without progress, waiting longer for a busy sender), checks the finished
/// file's hash and validates it (RIFF/WAVE, 256 KB, 10 s)
/// before anything plays it, and keeps it for this sender and this session only. As a guest in a public session your
/// own file is not sent (others hear the game's horn); built-in horns still are.</para>
///
/// <para>Nothing runs unless <see cref="ModManager.ServerModActive"/> says the session runs Custom Horns (solo,
/// hosting, or a host that advertises it). Every message is rate limited by the mod network as well.</para>
/// </summary>
internal static class HornNet
{
    /// <summary>The mod-network channel id given to Custom Horns (core's table, never reuse it).</summary>
    public const ushort Channel = Net.Channels.Horns;

    private const float ChunksPerSecond = 2f, ChunkBurst = 2f;
    private const float RetryAfter = 6f, MaxRetryAfter = 30f, AskBackGap = 3f;
    private const int MaxTries = 5, MaxServesPerPeer = 8, MaxQueue = 16, MaxDownloads = 6, MaxPeers = 64;

    internal sealed class Peer
    {
        public PlayerRef Ref;
        /// <summary>Guest side: this is the host (answer with SendToHost).</summary>
        public bool IsHost;
        public HornChoice Choice = HornChoice.Stock;
        public ChunkAssembler Download;
        /// <summary>When the last new chunk arrived (or the last request went out).</summary>
        public float LastProgress;
        /// <summary>Average seconds between this player's chunks (0 = none yet): a busy sender is waited for longer.</summary>
        public float ChunkGap;
        /// <summary>Requests in a row that brought nothing; reset by every new chunk.</summary>
        public int Tries;
        public ulong FailedHash;
        public float LastAskBackReply = -100f;
        /// <summary>How often we sent our file (this hash) to this player from the start.</summary>
        public int Serves;
        public ulong ServedHash;
        private ulong _keyHash;
        private string _key;

        /// <summary>The sound cache key of this player's file (cached: asked every frame while the car is around).</summary>
        public string SoundKey(ulong hash)
        {
            if (_key == null || _keyHash != hash) { _keyHash = hash; _key = $"peer_{(uint)Ref.RawEncoded:x8}_{hash:x16}"; }
            return _key;
        }
    }

    private sealed class Upload
    {
        public Peer To;
        public ulong Missing;
    }

    private static readonly Dictionary<int, Peer> Peers = new();
    private static readonly List<Upload> Uploads = new();
    private static bool _announced;
    private static HornChoice _announcedChoice = HornChoice.Stock;
    private static float _tokens = ChunkBurst, _tokensAt, _nextSlow, _nextAnnounceTry;

    // Your own horn (set by HornsFeature when the setting or the file changes).
    private static HornChoice _mine = HornChoice.Stock;
    private static byte[] _mineBytes;

    public static int FilesReceived { get; private set; }
    public static int FilesSent { get; private set; }
    public static int Rejected { get; private set; }

    public static void Register()
    {
        Net.Register(Channel, new Net.ChannelOptions
        {
            Name = "horns",
            Version = 1,
            MaxPayload = HornWire.MaxPayload,
            // Chunks go at 2 a second; choices and requests are rare. Room for both without tripping the limit.
            RatePerSecond = 6f,
            Burst = 14,
            AllowRelay = true,   // guests reach each other through the host
            HostOnlySender = false,
        }, OnMessage);
        Net.OnSessionReset(Reset);
        Net.OnPeerGone(p => Forget(p.RawEncoded));
    }

    /// <summary>Your horn changed: what you play, and for a custom horn the validated file to send.</summary>
    public static void SetMine(HornChoice choice, byte[] wav)
    {
        _mine = choice;
        _mineBytes = choice.Kind == HornKind.Custom ? wav : null;
        DebugLog.Info(HornsPlugin.ModId, $"own horn: {choice}" + (_mineBytes != null ? $" ({_mineBytes.Length} B file)" : ""));
        // A different file: anything still queued belongs to the old one.
        Uploads.Clear();
    }

    public static HornChoice Mine => _mine;

    /// <summary>What the others are told: your own file only where you may send it.</summary>
    public static HornChoice Announced => _mine.Kind == HornKind.Custom && (_mineBytes == null || GameState.IsPublicGuest) ? HornChoice.Stock : _mine;

    /// <summary>True while this session runs Custom Horns (see the class summary).</summary>
    public static bool Active => ModManager.ServerModActive(HornsPlugin.ModId);

    /// <summary>Talking to the other players is possible right now.</summary>
    public static bool Connected => Active && (Net.IsHost ? Net.HostAdvertised : Net.IsGuest && Net.HostHas(Channel));

    public static Peer PeerOf(int raw) => Peers.TryGetValue(raw, out var p) ? p : null;

    public static int PeerCount
    {
        get
        {
            int n = 0;
            foreach (var p in Peers.Values) if (!p.Choice.IsStock) n++;
            return n;
        }
    }

    // ---------------------------------------------------------------- per frame

    public static void Tick()
    {
        if (!Connected)
        {
            _announced = false;
            return;
        }
        float now = Time.unscaledTime;
        var pub = Announced;
        if ((!_announced || pub != _announcedChoice) && now >= _nextAnnounceTry)
        {
            _nextAnnounceTry = now + 1f;
            int n = Net.Broadcast(Channel, HornWire.MsgSelect, HornWire.EncodeSelect(pub, _announced ? (byte)0 : HornWire.FlagAskBack));
            // A host with nobody here yet is done: whoever joins asks back. A guest needs the host to take it.
            if (n > 0 || Net.IsHost)
            {
                _announced = true;
                _announcedChoice = pub;
            }
        }
        PumpUploads(now);
        if (now >= _nextSlow)
        {
            _nextSlow = now + 1f;
            SlowTick(now);
        }
    }

    private static void SlowTick(float now)
    {
        int downloading = 0;
        foreach (var p in Peers.Values) if (p.Download != null) downloading++;
        foreach (var p in Peers.Values)
        {
            if (p.Download != null)
            {
                // Progress-aware: a sender serving many players sends us a chunk only every few seconds.
                float wait = Mathf.Clamp(3f * p.ChunkGap, RetryAfter, MaxRetryAfter);
                if (now - p.LastProgress < wait) continue;
                if (p.Tries >= MaxTries)
                {
                    LogOnce("timeout" + p.Download.Hash, $"Gave up on a horn file from player {Id(p.Ref)} ({p.Download.Received}/{p.Download.Count} chunks)");
                    p.FailedHash = p.Download.Hash;
                    p.Download = null;
                    downloading--;
                    continue;
                }
                p.Tries++;
                p.LastProgress = now;
                Send(p, HornWire.MsgRequest, HornWire.EncodeRequest(p.Download.Hash, p.Download.HaveMask));
                continue;
            }
            // A custom horn we don't have yet: ask for it (a few at a time).
            if (downloading >= MaxDownloads || !Wants(p)) continue;
            if (!ChunkAssembler.TryCreate(p.Choice.Hash, p.Choice.Length, out var asm, out _)) { p.FailedHash = p.Choice.Hash; continue; }
            p.Download = asm;
            p.Tries = 1;
            p.ChunkGap = 0f;
            p.LastProgress = now;
            downloading++;
            Send(p, HornWire.MsgRequest, HornWire.EncodeRequest(asm.Hash, 0));
        }
    }

    private static bool Wants(Peer p) =>
        p.Choice.Kind == HornKind.Custom && HornsConfig.OthersCustomFiles.Value && HornsConfig.HearOthers.Value
        && p.FailedHash != p.Choice.Hash && !HornSounds.Has(p.SoundKey(p.Choice.Hash));

    private static void PumpUploads(float now)
    {
        _tokens = Mathf.Min(ChunkBurst, _tokens + (now - _tokensAt) * ChunksPerSecond);
        _tokensAt = now;
        while (_tokens >= 1f && Uploads.Count > 0)
        {
            var u = Uploads[0];
            Uploads.RemoveAt(0);
            if (_mineBytes == null || u.Missing == 0) continue;
            int index = 0;
            while ((u.Missing & (1UL << index)) == 0) index++;
            var (off, len) = HornWire.Slice(_mineBytes.Length, index);
            var r = Send(u.To, HornWire.MsgChunk, HornWire.EncodeChunk(_mine.Hash, _mineBytes.Length, index, _mineBytes, off, len));
            if (r == Net.SendResult.RateLimited) { Uploads.Insert(0, u); break; }
            if (r != Net.SendResult.Sent) continue; // gone, or not modded any more
            _tokens -= 1f;
            u.Missing &= ~(1UL << index);
            if (u.Missing != 0) Uploads.Add(u); // round robin between receivers
            else FilesSent++;
        }
    }

    // ---------------------------------------------------------------- receiving

    private static void OnMessage(Net.Message m)
    {
        if (!Active) return;
        var from = m.From;
        bool fromHost = false;
        if (m.FromHost && !m.Relayed)
        {
            // From the host itself: on a guest the transport's sender may not be the host's player ref.
            fromHost = true;
            try { var sp = OverlandSessionPlayers.Instance; if (sp != null && sp.HostPlayerRef.IsRealPlayer) from = sp.HostPlayerRef; } catch (Exception ex) { DebugLog.Error("horns", ex, "HornNet.OnMessage"); }
        }
        try
        {
            var r = GameState.Runner;
            if (r != null && r.LocalPlayer.RawEncoded == from.RawEncoded) return; // our own echo
        }
        catch (Exception ex) { DebugLog.Error("horns", ex, "HornNet.OnMessage"); }
        int raw = from.RawEncoded;
        if (!Peers.TryGetValue(raw, out var peer))
        {
            if (Peers.Count >= MaxPeers) return;
            Peers[raw] = peer = new Peer { Ref = from };
        }
        peer.IsHost = fromHost;

        switch (m.Type)
        {
            case HornWire.MsgSelect: OnSelect(peer, m.Payload); break;
            case HornWire.MsgRequest: OnRequest(peer, m.Payload); break;
            case HornWire.MsgChunk: OnChunk(peer, m.Payload); break;
        }
    }

    private static void OnSelect(Peer p, byte[] payload)
    {
        if (!HornWire.TryDecodeSelect(payload, out var c, out byte flags)) { Rejected++; return; }
        if (c != p.Choice)
        {
            DebugLog.Verbose(HornsPlugin.ModId, () => $"player {Id(p.Ref)} chose horn {c}");
            // A new file: drop the old download (the old sound stays cached for this session).
            if (p.Download != null && p.Download.Hash != c.Hash) p.Download = null;
            p.Choice = c;
        }
        if ((flags & HornWire.FlagAskBack) != 0 && Time.unscaledTime - p.LastAskBackReply > AskBackGap)
        {
            p.LastAskBackReply = Time.unscaledTime;
            Send(p, HornWire.MsgSelect, HornWire.EncodeSelect(Announced, 0));
        }
    }

    private static void OnRequest(Peer p, byte[] payload)
    {
        if (!HornWire.TryDecodeRequest(payload, out ulong hash, out ulong have)) { Rejected++; return; }
        var pub = Announced;
        // Only the file we are announcing right now, and not endlessly.
        if (pub.Kind != HornKind.Custom || hash != pub.Hash || _mineBytes == null) return;
        int count = HornWire.ChunkCount(_mineBytes.Length);
        ulong all = count >= 64 ? ulong.MaxValue : (1UL << count) - 1;
        ulong missing = all & ~have;
        if (missing == 0) return;
        // Already queued for this player (a re-request for missing chunks): update it where it stands in the queue.
        foreach (var u in Uploads)
            if (u.To == p) { u.Missing = missing; return; }
        if (Uploads.Count >= MaxQueue) { DebugLog.Verbose(HornsPlugin.ModId, () => $"upload queue full: player {Id(p.Ref)}'s request refused"); return; }
        // Counted per file, and only for a fresh request (a re-request with a have-mask is the same transfer).
        if (p.ServedHash != hash) { p.ServedHash = hash; p.Serves = 0; }
        if (have == 0 && p.Serves >= MaxServesPerPeer) return;
        if (have == 0) p.Serves++;
        Uploads.Add(new Upload { To = p, Missing = missing });
        DebugLog.Verbose(HornsPlugin.ModId, () => $"upload queued for player {Id(p.Ref)}: {count} chunk(s) of {_mineBytes.Length} B, {Uploads.Count} queued");
    }

    private static void OnChunk(Peer p, byte[] payload)
    {
        if (!HornWire.TryDecodeChunk(payload, out ulong hash, out int total, out int index, out var data)) { Rejected++; return; }
        var d = p.Download;
        if (d == null || d.Hash != hash || d.Total != total) return; // not asked for (or no longer)
        if (!d.Add(index, data)) return;
        float now = Time.unscaledTime;
        if (d.Received > 1)
        {
            float gap = now - p.LastProgress;
            p.ChunkGap = p.ChunkGap <= 0f ? gap : 0.7f * p.ChunkGap + 0.3f * gap;
        }
        p.LastProgress = now;
        p.Tries = 0;
        if (!d.Complete) return;
        p.Download = null;
        if (!d.TryFinish(out var wav, out string why))
        {
            Rejected++;
            p.FailedHash = hash;
            DebugLog.Error(HornsPlugin.ModId, $"horn file from player {Id(p.Ref)} refused: {why}");
            LogOnce("bad" + hash, $"Refused a horn file from player {Id(p.Ref)}: {why}");
            return;
        }
        // Kept for this sender and this session: at most a couple of files per player.
        HornSounds.Release($"peer_{(uint)p.Ref.RawEncoded:x8}_");
        DebugLog.Info(HornsPlugin.ModId, $"horn file from player {Id(p.Ref)} received and checked: {wav.Length} B");
        if (HornSounds.Custom(p.SoundKey(hash), wav) != IntPtr.Zero) FilesReceived++;
        else p.FailedHash = hash;
    }

    // ---------------------------------------------------------------- helpers

    private static Net.SendResult Send(Peer p, byte type, byte[] payload)
    {
        try
        {
            if (Net.IsHost) return Net.SendTo(p.Ref, Channel, type, payload);
            return p.IsHost ? Net.SendToHost(Channel, type, payload) : Net.SendTo(p.Ref, Channel, type, payload);
        }
        catch (Exception e)
        {
            DebugLog.Error("horns", e, "HornNet.Send");
            LogOnce("send", "Sending on the horn channel failed: " + e.Message);
            return Net.SendResult.Failed;
        }
    }

    private static void Forget(int raw)
    {
        if (!Peers.TryGetValue(raw, out var p)) return;
        Peers.Remove(raw);
        Uploads.RemoveAll(u => u.To == p);
        HornSounds.Release($"peer_{(uint)raw:x8}_");
    }

    private static void Reset()
    {
        Peers.Clear();
        Uploads.Clear();
        _announced = false;
        HornSounds.Release("peer_");
    }

    private static int Id(PlayerRef p)
    {
        try { return p.PlayerId; } catch (Exception ex) { DebugLog.Error("horns", ex, "HornNet.Id"); return 0; }
    }

    private static void LogOnce(string key, string message) => HornsUtil.LogOnce(key, message);

    /// <summary>One line for the MODS page.</summary>
    public static string Describe()
    {
        if (!Active) return "off in this session: the host doesn't run Custom Horns, so everyone hears the game's horn";
        try
        {
            var r = GameState.Runner;
            if (r == null || !r.IsRunning) return "load into a world to use it";
            if (r.IsSinglePlayer) return "solo: only you hear your horn";
        }
        catch (Exception ex) { DebugLog.Error("horns", ex, "HornNet.Describe"); }
        if (Net.Enabled == null || !Net.Enabled.Value) return "the mod network is off: only you hear your horn";
        if (!Connected) return "connecting to the host's mod network";
        string own = _mine.Kind == HornKind.Custom && GameState.IsPublicGuest
            ? " Your own file isn't sent in a public session (others hear the game's horn)." : "";
        return $"{(Net.IsHost ? "hosting" : "guest")}: {PeerCount} other player(s) here picked a horn with this mod." + own;
    }
}
