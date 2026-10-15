using System;
using System.Collections.Generic;
using Fusion;
using OverTheHillPlus.Features.Public.PlayerList;
using UnityEngine;
using Net = OverTheHillPlus.Features.Public.ModNet.ModNet;

namespace OverTheHillPlus.Features.Public.PingMonitor;

/// <summary>The last minute of round-trip samples (one a second) for one connection.</summary>
internal sealed class PingStats
{
    public const int Capacity = 60;
    private readonly float[] _ms = new float[Capacity];
    private int _count, _head; // _head = next write position

    public int Count => _count;

    /// <summary>Newest sample, -1 = none yet.</summary>
    public float Last => _count == 0 ? -1f : _ms[(_head - 1 + Capacity) % Capacity];

    public void Add(float ms)
    {
        _ms[_head] = ms;
        _head = (_head + 1) % Capacity;
        if (_count < Capacity) _count++;
    }

    public void Clear() { _count = 0; _head = 0; }

    /// <summary>Sample <paramref name="i"/> counted back from the newest (0 = newest).</summary>
    private float Back(int i) => _ms[(_head - 1 - i + Capacity * 2) % Capacity];

    public float Average(int n = 10)
    {
        n = Math.Min(n, _count);
        if (n <= 0) return -1f;
        float s = 0f;
        for (int i = 0; i < n; i++) s += Back(i);
        return s / n;
    }

    /// <summary>Mean change between consecutive samples (what players feel as rubber-banding), -1 = not enough data.</summary>
    public float Jitter(int n = 10)
    {
        n = Math.Min(n, _count);
        if (n < 2) return -1f;
        float s = 0f;
        for (int i = 0; i < n - 1; i++) s += Math.Abs(Back(i) - Back(i + 1));
        return s / (n - 1);
    }

    public float Peak(int n = Capacity)
    {
        n = Math.Min(n, _count);
        if (n <= 0) return -1f;
        float m = 0f;
        for (int i = 0; i < n; i++) m = Math.Max(m, Back(i));
        return m;
    }

    /// <summary>Median of the newest three samples: one spike or one dip never decides anything on its own.</summary>
    public float Median3()
    {
        if (_count == 0) return -1f;
        if (_count < 3) return Last;
        float a = Back(0), b = Back(1), c = Back(2);
        return Math.Max(Math.Min(a, b), Math.Min(Math.Max(a, b), c));
    }
}

/// <summary>One player in the host's table.</summary>
internal sealed class PlayerPing
{
    public PlayerRef Ref;
    public int Raw;
    public string Name = "";
    public bool IsYou;
    public float FirstSeen;
    public string Link = "";
    public int Misses;          // consecutive samples without a reading
    public readonly PingStats Stats = new();

    // High-ping rule state (PingRule)
    public float AboveFor;
    public bool HalfWarned;
    public float CooldownUntil;
    public float KickAt = -1f;
    public float KickMs, KickLimit; // the reading and limit that triggered a pending removal (for the log)
    public string RuleState = "";

    public bool Modded => !IsYou && Net.IsPeer(Ref);
}

/// <summary>
/// Samples round-trip times once a second.
///
/// Host: Fusion's <c>NetworkRunner.GetPlayerRtt(player)</c> (seconds) for every guest (the same call the game's own
/// "ping" console command uses on the local player, decoded with Cpp2IL), plus <c>GetPlayerConnectionType</c>.
/// Guest: <c>GetPlayerRtt(LocalPlayer)</c>, the game's own ping figure; if Fusion has no reading and the host runs
/// this mod, an echo over the mod network is used instead (see <see cref="PingRule"/>).
/// Also keeps the session read-outs for the HUD: player count, host name, how long the session has been up.
/// </summary>
internal static class PingTracker
{
    private const float SampleEvery = 1f;
    private const float RosterEvery = 2f;
    private const float MaxValidMs = 20000f;

    public static readonly List<PlayerPing> Players = new();  // host only, sorted by join order
    private static readonly Dictionary<int, PlayerPing> ByRaw = new();

    /// <summary>Guest: own ping to the host.</summary>
    public static readonly PingStats Self = new();
    public static string SelfSource = "";
    private static int _selfMisses;

    public static bool Hosting { get; private set; }
    public static bool InSession { get; private set; }
    public static string HostName { get; private set; } = "";
    public static int PlayerCount { get; private set; }
    public static int MaxPlayers { get; private set; }
    public static float JoinedAt { get; private set; } = -1f;
    /// <summary>How long the host has been running this session (from its tick counter), -1 = unknown.</summary>
    public static float ServerUptime { get; private set; } = -1f;
    /// <summary>Host: when a player last joined (the host stalls while it loads their car, which inflates everyone's ping).</summary>
    public static float LastJoinAt { get; private set; } = -100f;
    /// <summary>Seconds since the previous sample (clamped), for the rule's timers.</summary>
    public static float LastDelta { get; private set; }
    /// <summary>The frame before the latest sample took very long (a host stall): its readings are not trusted.</summary>
    public static bool LastSampleStalled { get; private set; }
    /// <summary>Goes up by one with every sample taken in a session (the rule runs once per sample).</summary>
    public static int SampleCount { get; private set; }

    private static IntPtr _runnerPtr;
    private static float _nextSample, _nextRoster, _lastSampleAt = -1f, _worstFrame;
    private static readonly HashSet<string> LoggedOnce = new();
    public static event Action SessionReset;

    public static void Tick()
    {
        // A long frame (the host loading a joining car, a scene load) shows up as a ping spike for everyone.
        _worstFrame = Math.Max(_worstFrame, Time.unscaledDeltaTime);
        float now = Time.unscaledTime;
        if (now < _nextSample) return;
        _nextSample = now + SampleEvery;
        try { Sample(now); }
        catch (Exception e) { LogOnce("sample", "[Ping] Sampling failed: " + e.Message); }
        _worstFrame = 0f;
    }

    private static void Sample(float now)
    {
        var r = GameState.Runner;
        bool running = false, single = true, server = false;
        if (r != null)
        {
            try { running = r.IsRunning; single = r.IsSinglePlayer; server = r.IsServer; } catch { running = false; }
        }
        if (r == null || !running || single)
        {
            if (InSession || _runnerPtr != IntPtr.Zero) Reset();
            return;
        }
        if (r.Pointer != _runnerPtr)
        {
            Reset();
            _runnerPtr = r.Pointer;
            JoinedAt = now;
        }
        InSession = true;
        Hosting = server;
        LastDelta = _lastSampleAt < 0f ? SampleEvery : Mathf.Clamp(now - _lastSampleAt, 0f, 2f);
        LastSampleStalled = _worstFrame > 0.5f;
        _lastSampleAt = now;

        if (now >= _nextRoster)
        {
            _nextRoster = now + RosterEvery;
            RefreshSession(r);
            if (Hosting) RefreshRoster(r, now);
        }

        if (Hosting) SampleGuests(r);
        else SampleSelf(r);
        SampleCount++;
    }

    private static void Reset()
    {
        Players.Clear();
        ByRaw.Clear();
        Self.Clear();
        SelfSource = "";
        _selfMisses = 0;
        InSession = Hosting = false;
        HostName = "";
        PlayerCount = MaxPlayers = 0;
        JoinedAt = ServerUptime = -1f;
        LastJoinAt = -100f;
        _runnerPtr = IntPtr.Zero;
        _lastSampleAt = -1f;
        _nextRoster = 0f;
        try { SessionReset?.Invoke(); } catch (Exception e) { LogOnce("reset", "[Ping] Reset hook: " + e.Message); }
    }

    private static void RefreshSession(NetworkRunner r)
    {
        OverlandSessionPlayers sp = null;
        try { sp = OverlandSessionPlayers.Instance; } catch { }

        try { PlayerCount = PlayerListRoster.AllPlayers().Count; } catch { }
        try
        {
            var info = r.SessionInfo;
            MaxPlayers = info != null && info.IsValid ? info.MaxPlayers : 0;
        }
        catch { MaxPlayers = 0; }

        if (Hosting) HostName = "you";
        else
        {
            try
            {
                // Networked property: only readable once the session object has spawned.
                var host = sp != null ? sp.HostPlayerRef : PlayerRef.None;
                HostName = host.IsRealPlayer ? PlayerListRoster.NameOf(sp, host) : "";
            }
            catch { HostName = ""; }
        }

        try
        {
            int rate = r.TickRate;
            int tick = Hosting ? r.Tick.Raw : r.LatestServerTick.Raw;
            float up = rate > 0 ? tick / (float)rate : -1f;
            ServerUptime = up > 0f && up < 30f * 86400f ? up : -1f;
        }
        catch { ServerUptime = -1f; }
    }

    private static void RefreshRoster(NetworkRunner r, float now)
    {
        List<PlayerRef> all;
        try { all = PlayerListRoster.AllPlayers(); }
        catch (Exception e) { LogOnce("roster", "[Ping] Player list: " + e.Message); return; }

        PlayerRef local = PlayerRef.None;
        try { local = r.LocalPlayer; } catch { }
        OverlandSessionPlayers sp = null;
        try { sp = OverlandSessionPlayers.Instance; } catch { }

        var present = new HashSet<int>();
        foreach (var p in all)
        {
            int raw = p.RawEncoded;
            present.Add(raw);
            if (!ByRaw.TryGetValue(raw, out var e))
            {
                e = new PlayerPing { Ref = p, Raw = raw, FirstSeen = now, IsYou = raw == local.RawEncoded };
                ByRaw[raw] = e;
                Players.Add(e);
                if (!e.IsYou && Players.Count > 1) LastJoinAt = Math.Max(LastJoinAt, now);
            }
            try { e.Name = PlayerListRoster.NameOf(sp, p); } catch { e.Name = "Player " + p.PlayerId; }
        }
        for (int i = Players.Count - 1; i >= 0; i--)
        {
            if (present.Contains(Players[i].Raw)) continue;
            ByRaw.Remove(Players[i].Raw);
            Players.RemoveAt(i);
        }
        Players.Sort((a, b) => a.IsYou != b.IsYou ? (a.IsYou ? -1 : 1) : a.Raw.CompareTo(b.Raw));
    }

    /// <summary>Host: a player joined (runner callback). A reused player slot starts over, with a fresh grace period.</summary>
    public static void PlayerJoined(PlayerRef p)
    {
        if (!Hosting) return;
        LastJoinAt = Time.unscaledTime;
        int raw = p.RawEncoded;
        if (ByRaw.TryGetValue(raw, out var old))
        {
            ByRaw.Remove(raw);
            Players.Remove(old);
        }
        _nextRoster = 0f;
    }

    public static void PlayerLeft(PlayerRef p)
    {
        int raw = p.RawEncoded;
        if (ByRaw.TryGetValue(raw, out var old))
        {
            ByRaw.Remove(raw);
            Players.Remove(old);
        }
    }

    private static void SampleGuests(NetworkRunner r)
    {
        foreach (var e in Players)
        {
            if (e.IsYou) continue;
            float ms = -1f;
            try { ms = (float)(r.GetPlayerRtt(e.Ref) * 1000.0); }
            catch (Exception ex) { LogOnce("rtt", "[Ping] GetPlayerRtt: " + ex.Message); }
            if (ms > 0f && ms < MaxValidMs && !float.IsNaN(ms))
            {
                e.Stats.Add(ms);
                e.Misses = 0;
            }
            else e.Misses++;
            try
            {
                var ct = r.GetPlayerConnectionType(e.Ref);
                e.Link = ct == ConnectionType.Direct ? "direct" : ct == ConnectionType.Relayed ? "relayed" : "";
            }
            catch { }
        }
    }

    private static void SampleSelf(NetworkRunner r)
    {
        float ms = -1f;
        try { ms = (float)(r.GetPlayerRtt(r.LocalPlayer) * 1000.0); }
        catch (Exception ex) { LogOnce("selfrtt", "[Ping] GetPlayerRtt (own): " + ex.Message); }
        if (ms > 0f && ms < MaxValidMs && !float.IsNaN(ms))
        {
            Self.Add(ms);
            SelfSource = "game";
            _selfMisses = 0;
        }
        else
        {
            _selfMisses++;
            // No reading from Fusion: ask a modded host for an echo instead.
            if (_selfMisses >= 3) PingRule.RequestEcho();
        }
    }

    /// <summary>Guest: a round trip measured with the mod network echo.</summary>
    public static void AddEchoSample(float ms)
    {
        if (!InSession || Hosting || _selfMisses < 3) return;
        if (ms <= 0f || ms >= MaxValidMs) return;
        Self.Add(ms);
        SelfSource = "mod echo";
    }

    public static PlayerPing Find(PlayerRef p) => ByRaw.TryGetValue(p.RawEncoded, out var e) ? e : null;

    /// <summary>Highest current ping among guests (host), or null.</summary>
    public static PlayerPing Worst()
    {
        PlayerPing worst = null;
        foreach (var e in Players)
            if (!e.IsYou && e.Stats.Count > 0 && (worst == null || e.Stats.Median3() > worst.Stats.Median3())) worst = e;
        return worst;
    }

    public static void LogOnce(string id, string message)
    {
        if (LoggedOnce.Count < 100 && LoggedOnce.Add(id)) Plugin.Logger.LogWarning(message);
    }

    // ---------------------------------------------------------------- shared formatting

    public static Color ColorFor(float ms)
    {
        if (ms < 0f) return UI.Theme.Muted;
        if (ms < 100f) return UI.Theme.Good;
        if (ms < 200f) return UI.Theme.Warn;
        return UI.Theme.Bad;
    }

    public static string Ms(float ms) => ms < 0f ? "-" : $"{ms:0} ms";

    public static string Duration(float seconds)
    {
        if (seconds < 0f) return "-";
        var t = TimeSpan.FromSeconds(seconds);
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";
    }
}
