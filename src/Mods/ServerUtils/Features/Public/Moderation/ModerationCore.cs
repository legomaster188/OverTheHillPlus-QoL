using System;
using System.Collections.Generic;
using System.Text;
using Fusion;
using OverTheHillPlus.Features.Public.PlayerList;
using OverTheHillPlus.Multiplayer;
using UnityEngine;
using OverTheHillPlus.ServerUtils;

namespace OverTheHillPlus.Features.Public.Moderation;

/// <summary>One line of the moderation log.</summary>
internal sealed class ModLogEntry
{
    public DateTime At;
    public string Text;
    public bool Bad;
}

/// <summary>
/// Shared host moderation plumbing: the moderation log, the one place that removes players
/// (<see cref="Kick"/>, always through the game's own OverlandNetworkRunner.KickPlayer and only while hosting), the
/// queue of on-screen notices for everyone, and session bookkeeping.
/// </summary>
internal static class ModerationCore
{
    private const int MaxLog = 100;
    private const int MaxKicksPerMinute = 6;
    // Join-rule removals have their own, higher cap: they can't cascade like the AFK or ping rules (each one is a
    // stranger arriving), and a burst of joins must not use up the cap and let the rest in.
    private const int MaxJoinKicksPerMinute = 20;
    private const float AnnounceGap = 5f;
    private const int MaxAnnounceQueue = 6;

    public static readonly List<ModLogEntry> Log = new();
    private static readonly Queue<float> KickTimes = new(), JoinKickTimes = new();
    private static readonly Dictionary<int, float> CapReported = new();
    private static readonly Queue<(string text, Color color)> Announcements = new();
    private static float _nextAnnounce;
    private static readonly HashSet<string> LoggedOnce = new();

    private static IntPtr _runnerPtr;
    public static event Action SessionChanged;

    // ---------------------------------------------------------------- session

    /// <summary>Hosting a multiplayer session (not single-player, not a guest) with host authority.</summary>
    public static bool Hosting
    {
        get
        {
            var r = GameState.Runner;
            if (r == null || !GameState.IsAuthority) return false;
            try { return r.IsRunning && r.IsServer && !r.IsSinglePlayer; } catch (Exception ex) { DebugLog.Error("serverutils", ex, "ModerationCore.Hosting"); return false; }
        }
    }

    public static bool InMultiplayer
    {
        get
        {
            var r = GameState.Runner;
            try { return r != null && r.IsRunning && !r.IsSinglePlayer; } catch (Exception ex) { DebugLog.Error("serverutils", ex, "ModerationCore.InMultiplayer"); return false; }
        }
    }

    /// <summary>Per frame: notices for everyone, and forgetting per-session state when the session changes.</summary>
    public static void Tick()
    {
        var r = GameState.Runner;
        IntPtr ptr = IntPtr.Zero;
        try { ptr = r == null ? IntPtr.Zero : r.Pointer; } catch (Exception ex) { DebugLog.Error("serverutils", ex, "ModerationCore.Tick"); }
        if (ptr != _runnerPtr)
        {
            _runnerPtr = ptr;
            Announcements.Clear();
            try { SessionChanged?.Invoke(); } catch (Exception e) { DebugLog.Error("serverutils", e, "ModerationCore.Tick"); LogOnce("session", "[Moderation] Session reset: " + e.Message); }
        }
        if (Announcements.Count > 0 && Time.unscaledTime >= _nextAnnounce)
        {
            var (text, color) = Announcements.Dequeue();
            _nextAnnounce = Time.unscaledTime + AnnounceGap;
            if (Hosting && !HostNotice.Announce(text, color))
                LogOnce("announce", "[Moderation] A notice for everyone was not sent (filtered or no session)");
        }
    }

    /// <summary>Host: queue a pop-up for everyone (at most one every 5 s). Names must go through <see cref="HostNotice.SafeName"/>.</summary>
    public static void Announce(string text, Color color)
    {
        if (!Hosting || string.IsNullOrWhiteSpace(text)) return;
        if (Announcements.Count >= MaxAnnounceQueue) return;
        Announcements.Enqueue((text, color));
    }

    // ---------------------------------------------------------------- log

    public static void Add(string text, bool bad = false)
    {
        Log.Add(new ModLogEntry { At = DateTime.Now, Text = text, Bad = bad });
        if (Log.Count > MaxLog) Log.RemoveRange(0, Log.Count - MaxLog);
        DebugLog.Info(ServerUtilsPlugin.ModId, "moderation: " + text);
        ServerUtilsPlugin.Logger.LogInfo("[Moderation] " + text);
    }

    public static void LogOnce(string id, string message)
    {
        if (LoggedOnce.Count < 100 && LoggedOnce.Add(id)) ServerUtilsPlugin.Logger.LogWarning(message);
    }

    // ---------------------------------------------------------------- players

    public static string NameOf(PlayerRef p)
    {
        try { return PlayerListRoster.NameOf(OverlandSessionPlayers.Instance, p); }
        catch (Exception ex) { DebugLog.Error("serverutils", ex, "ModerationCore.NameOf"); return "Player " + p.PlayerId; }
    }

    /// <summary>The name the player joined with, or null while it isn't known yet (never the "Player N" stand-in).</summary>
    public static string RealNameOf(PlayerRef p)
    {
        try { if (PlayerListRoster.TryKnownName(p, out var n) && !string.IsNullOrWhiteSpace(n)) return n; } catch (Exception ex) { DebugLog.Error("serverutils", ex, "ModerationCore.RealNameOf"); }
        return null;
    }

    public static bool IsLocal(PlayerRef p)
    {
        try { var r = GameState.Runner; return r != null && r.LocalPlayer.RawEncoded == p.RawEncoded; } catch (Exception ex) { DebugLog.Error("serverutils", ex, "ModerationCore.IsLocal"); return false; }
    }

    /// <summary>The player's connection token as text (the game's format is "platform_userId", plus any mods' markers
    /// after a '|', see Core.Shared.ConnectionToken), or null. Kept whole: the game's session bans use the whole text.</summary>
    public static string TokenOf(PlayerRef p)
    {
        try
        {
            var r = GameState.Runner;
            var bytes = r?.GetPlayerConnectionToken(p);
            if (bytes == null || bytes.Length == 0 || bytes.Length > 256) return null;
            var managed = new byte[bytes.Length];
            for (int i = 0; i < managed.Length; i++) managed[i] = bytes[i];
            return Encoding.UTF8.GetString(managed);
        }
        catch (Exception e)
        {
            DebugLog.Error("serverutils", e, "ModerationCore.TokenOf");
            LogOnce("token", "[Moderation] Could not read a connection token: " + e.Message);
            return null;
        }
    }

    /// <summary>
    /// The Steam ID in a connection token, 0 = none. The game sends "platform_userId" (see DevTeamMembers.
    /// ContainsConnectionToken); only an id that looks like a Steam individual account is returned. The client
    /// writes its own token, so this identifies honest (vanilla) clients, it does not authenticate anyone.
    /// </summary>
    public static ulong SteamIdFromToken(string token)
    {
        // Mods append markers after the game's part ("platform_userId|othp:..."): read only the game's part.
        token = global::OverTheHillPlus.Core.Shared.ConnectionToken.PlatformPart(token);
        if (string.IsNullOrEmpty(token)) return 0;
        int cut = token.LastIndexOf('_');
        string tail = cut >= 0 ? token.Substring(cut + 1) : token;
        return ulong.TryParse(tail.Trim(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out ulong id)
               && LooksLikeSteamId(id) ? id : 0;
    }

    /// <summary>A Steam64 id of an individual account in the public universe (7656119...).</summary>
    public static bool LooksLikeSteamId(ulong id) => (id >> 56) == 1 && ((id >> 52) & 0xF) == 1 && (id & 0xFFFFFFFF) != 0;

    // ---------------------------------------------------------------- removing players

    /// <summary>What <see cref="Kick"/> returns when the removal cap stopped it (the caller may try again later).</summary>
    public const string RateLimited = "Blocked: too many removals in the last minute.";
    public const string AlreadyLeft = "Failed: the player already left.";

    public static bool WasRemoved(string result) => result != null && result.StartsWith("Removed", StringComparison.Ordinal);

    /// <summary>
    /// Host: removes a player with the game's own kick (OverlandNetworkRunner.KickPlayer). The game also bans the
    /// player's connection token for the rest of the session; <paramref name="allowRejoin"/> takes it off that list
    /// again. At most 6 removals a minute (join-rule removals, <paramref name="joinRule"/>, have their own cap of 20).
    /// Returns a short result for the menu status; a rate-limited call returns <see cref="RateLimited"/>.
    /// </summary>
    public static string Kick(PlayerRef p, string why, bool allowRejoin, bool joinRule = false)
    {
        string name = NameOf(p);
        if (!Hosting) return "Blocked: only the host can remove players.";
        if (!p.IsRealPlayer || IsLocal(p)) return "Blocked: that is you.";
        float now = Time.unscaledTime;
        var times = joinRule ? JoinKickTimes : KickTimes;
        int cap = joinRule ? MaxJoinKicksPerMinute : MaxKicksPerMinute;
        while (times.Count > 0 && now - times.Peek() > 60f) times.Dequeue();
        if (times.Count >= cap)
        {
            // A caller may retry every few seconds: note it in the log at most every 30 s per player.
            if (!CapReported.TryGetValue(p.RawEncoded, out float at) || now - at > 30f)
            {
                if (CapReported.Count > 128) CapReported.Clear();
                CapReported[p.RawEncoded] = now;
                Add($"Not removed {name} ({why}) yet: {cap} removals in the last minute already", true);
            }
            return RateLimited;
        }
        try
        {
            var r = GameState.Runner;
            if (r == null || !r.IsPlayerValid(p)) return AlreadyLeft;
            var runner = OverlandNetworkRunner.Instance;
            if (runner == null) return "Failed: no session runner.";
            string token = allowRejoin ? TokenOf(p) : null;
            times.Enqueue(now);
            CapReported.Remove(p.RawEncoded);
            runner.KickPlayer(p);
            string extra = "";
            if (allowRejoin && token != null)
            {
                try
                {
                    var banned = runner.sessionBannedPlayers;
                    if (banned != null && banned.Contains(token)) { banned.Remove(token); extra = " (may rejoin)"; }
                }
                catch (Exception e) { DebugLog.Error("serverutils", e, "ModerationCore.Kick"); LogOnce("unban", "[Moderation] Could not lift the session ban: " + e.Message); }
            }
            Add($"Removed {name}: {why}{extra}", true);
            return $"Removed {name}.";
        }
        catch (Exception e)
        {
            DebugLog.Error("serverutils", e, "ModerationCore.Kick");
            LogOnce("kick", "[Moderation] KickPlayer failed: " + e);
            Add($"Could not remove {name}: {e.Message}", true);
            return "Failed: " + e.Message;
        }
    }

    // ---------------------------------------------------------------- text

    public static string Clip(string s, int max) =>
        string.IsNullOrEmpty(s) ? "" : s.Length <= max ? s : s.Substring(0, max - 1) + "…";

    public static string Minutes(float seconds)
    {
        if (seconds < 0f) return "-";
        var t = TimeSpan.FromSeconds(seconds);
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";
    }
}
