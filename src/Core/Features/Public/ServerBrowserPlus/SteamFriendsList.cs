using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using OverTheHillPlus.Features.Public.ServerPlayers;
using Steamworks;
using UnityEngine;

namespace OverTheHillPlus.Features.Public.ServerBrowserPlus;

internal enum FriendScope { PlayingThisGame, Online, All }

/// <summary>
/// The local player's Steam friends (read through the game's Steamworks.NET, never sent anywhere), keyed the same way
/// the server browser keys published player names, so a friend's persona name can be found in a server.
///
/// Read on the main thread at most every <see cref="Interval"/> seconds, only while something asks (the browser or
/// the Servers tab is open). If Steam isn't running or the game runs on another platform, every call fails and the
/// list stays empty: <see cref="Available"/> is false and the reason is in <see cref="Problem"/>.
///
/// Also the one friends list of the host's friends-only join rule (Moderation): <see cref="IsFriend"/> by Steam id,
/// so the browser highlight and the join rule always agree and Steam is read once. In core because both the Server
/// Browser+ mod and the join rule use it.
/// </summary>
internal static class SteamFriendsList
{
    internal sealed class Friend
    {
        /// <summary>Persona name as the game shows it in servers (cleaned like published names).</summary>
        public string Name;
        public bool Online;
        public bool PlayingThisGame;
    }

    private const float Interval = 30f;
    private const int MaxFriends = 2000;
    private static readonly Regex Spaces = new(@"\s+", RegexOptions.Compiled);

    private static Dictionary<string, Friend> _byKey = new();
    private static HashSet<ulong> _ids = new();
    private static float _refreshedAt = -1000f;
    private static bool _loggedError;
    private static string _signature;

    public static bool Available { get; private set; }
    public static string Problem { get; private set; } = "not read yet";
    public static int Total { get; private set; }
    public static int OnlineCount { get; private set; }
    public static int InGameCount { get; private set; }
    /// <summary>Bumped whenever the list is re-read, so caches built on it know to rebuild.</summary>
    public static int Version { get; private set; }
    public static float RefreshedAt => _refreshedAt;

    /// <summary>Comparison key for a player name: cleaned like a published name, lower case, single spaces.</summary>
    public static string Key(string name)
    {
        if (string.IsNullOrEmpty(name)) return "";
        string clean = PlayerNamesProperty.Clean(name);
        if (clean == "Player") return ""; // the placeholder for a blocked or empty name never identifies anyone
        return Spaces.Replace(clean.ToLowerInvariant(), " ").Trim();
    }

    /// <summary>Key for a name that is already cleaned (published names are cleaned when they are read).</summary>
    public static string KeyOfCleaned(string clean)
    {
        if (string.IsNullOrEmpty(clean) || clean == "Player") return "";
        return Spaces.Replace(clean.ToLowerInvariant(), " ").Trim();
    }

    /// <summary>The friend whose name has this key and who passes the scope, or null.</summary>
    public static Friend Find(string key, FriendScope scope)
    {
        if (string.IsNullOrEmpty(key) || !_byKey.TryGetValue(key, out var f)) return null;
        return Passes(f, scope) ? f : null;
    }

    public static bool Passes(Friend f, FriendScope scope) => scope switch
    {
        FriendScope.PlayingThisGame => f.PlayingThisGame,
        FriendScope.Online => f.Online,
        _ => true,
    };

    public static int CountIn(FriendScope scope) => scope switch
    {
        FriendScope.PlayingThisGame => InGameCount,
        FriendScope.Online => OnlineCount,
        _ => Total,
    };

    /// <summary>Whether the list can be read (reads it first when due).</summary>
    public static bool AvailableNow { get { RefreshIfDue(); return Available; } }

    /// <summary>True / false when the friends list could be read, null when it could not (reads it first when due).</summary>
    public static bool? IsFriend(ulong steamId)
    {
        RefreshIfDue();
        if (!Available) return null;
        return _ids.Contains(steamId);
    }

    /// <summary>Re-reads the list now if the last read is older than 10 s (a friend just added, a join check).</summary>
    public static void RefreshSoon()
    {
        float now = Time.unscaledTime;
        if (now - _refreshedAt >= 10f || now < _refreshedAt) RefreshIfDue(true);
    }

    public static void RefreshIfDue(bool force = false)
    {
        float now = Time.unscaledTime;
        if (!force && now - _refreshedAt < Interval && now >= _refreshedAt) return;
        _refreshedAt = now;
        try { Read(); }
        catch (Exception e)
        {
            DebugLog.Error("core", e, "SteamFriendsList.RefreshIfDue");
            // Steam not running, not initialised, or a non-Steam copy of the game.
            Available = false;
            Problem = "Steam friends aren't available (Steam not running, or not the Steam version of the game)";
            _byKey = new Dictionary<string, Friend>();
            _ids = new HashSet<ulong>();
            Total = OnlineCount = InGameCount = 0;
            if (_signature != null) Version++;
            _signature = null;
            if (_loggedError) return;
            _loggedError = true;
            Core.CorePlugin.Logger?.LogInfo("[Steam friends] unavailable: " + e.Message);
        }
    }

    private static void Read()
    {
        const EFriendFlags flags = EFriendFlags.k_EFriendFlagImmediate;
        int count = SteamFriends.GetFriendCount(flags);
        if (count < 0) throw new InvalidOperationException("GetFriendCount returned " + count);

        uint appId = 0;
        bool appKnown = false;
        try { appId = SteamUtils.GetAppID().m_AppId; appKnown = appId != 0; } catch (Exception ex) { DebugLog.Error("core", ex, "SteamFriendsList.Read"); }

        var map = new Dictionary<string, Friend>(Math.Min(count, MaxFriends));
        var ids = new HashSet<ulong>();
        int online = 0, inGame = 0;
        bool gameInfoFailed = false;
        for (int i = 0; i < count && i < MaxFriends; i++)
        {
            CSteamID id;
            string name;
            EPersonaState state;
            try
            {
                id = SteamFriends.GetFriendByIndex(i, flags);
                name = SteamFriends.GetFriendPersonaName(id);
                state = SteamFriends.GetFriendPersonaState(id);
            }
            catch (Exception ex) { DebugLog.Error("core", ex, "SteamFriendsList.Read"); continue; }
            if (id.m_SteamID != 0) ids.Add(id.m_SteamID);
            string clean = PlayerNamesProperty.Clean(name); // the name filter runs once per friend per read
            string key = KeyOfCleaned(clean);
            if (key.Length == 0) continue;

            bool isOnline = state != EPersonaState.k_EPersonaStateOffline;
            bool playing = false;
            if (isOnline && appKnown && !gameInfoFailed)
            {
                try
                {
                    if (SteamFriends.GetFriendGamePlayed(id, out FriendGameInfo_t info))
                        playing = (uint)(info.m_gameID.m_GameID & 0xFFFFFFUL) == appId; // low 24 bits of a game id = app id
                }
                catch (Exception ex) { DebugLog.Error("core", ex, "SteamFriendsList.Read"); gameInfoFailed = true; }
            }

            var f = new Friend
            {
                Name = clean,
                Online = isOnline,
                PlayingThisGame = playing,
            };
            if (isOnline) online++;
            if (playing) inGame++;
            // Two friends with the same name: keep the one most likely to be in a server.
            if (map.TryGetValue(key, out var existing) && Rank(existing) >= Rank(f)) continue;
            map[key] = f;
        }

        // Without game info every online friend counts as possibly playing (better a stray match than none).
        if (gameInfoFailed || !appKnown)
        {
            foreach (var f in map.Values) f.PlayingThisGame = f.Online;
            inGame = online;
        }

        _byKey = map;
        _ids = ids;
        Total = Math.Min(count, MaxFriends);
        OnlineCount = online;
        InGameCount = inGame;
        Available = true;
        Problem = gameInfoFailed || !appKnown ? "Steam didn't say which game friends are playing: online friends are matched" : null;
        // Only a real change (someone came online, started playing, renamed) re-filters the browser.
        var sig = new System.Text.StringBuilder(map.Count * 16);
        sig.Append(ids.Count).Append('|');
        foreach (var kv in map) sig.Append(kv.Key).Append(kv.Value.Online ? '1' : '0').Append(kv.Value.PlayingThisGame ? '1' : '0').Append('|');
        string signature = sig.ToString();
        if (signature != _signature) Version++;
        _signature = signature;
    }

    private static int Rank(Friend f) => f.PlayingThisGame ? 2 : f.Online ? 1 : 0;
}
