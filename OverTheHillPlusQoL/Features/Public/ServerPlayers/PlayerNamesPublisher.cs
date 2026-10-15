using System;
using System.Collections.Generic;
using Fusion;
using HarmonyLib;
using OverTheHillPlus.Features.Public.PlayerList;
using UnityEngine;

namespace OverTheHillPlus.Features.Public.ServerPlayers;

/// <summary>
/// Host side: adds the player-names property when the session starts (Photon only lets a host update keys that
/// existed at creation) and keeps it current while players join and leave, at most once every 5 seconds.
/// </summary>
internal static class PlayerNamesPublisher
{
    private const float MinInterval = 5f;
    private const float MaxInterval = 60f;
    private const float CheckInterval = 1f;
    private const int MinCapBytes = 40; // still room for "p1|*Name|+N"
    private const int MaxRefusalsAtMin = 3;

    /// <summary>The value set at the last StartGame we added the key to (null = the key was not added).</summary>
    private static string _startValue;
    /// <summary>The RoomProperties private flag read at that StartGame (true when it could not be read).</summary>
    private static bool _startPrivate;
    private static IntPtr _runner;
    // Per session: whether it was created hidden/private (sticky, so a stale flag elsewhere can't publish names).
    private static bool _sessionHidden, _sessionPrivate;
    // Per session back-off after Photon refuses an update (size or key): smaller value, slower retries, then stop.
    private static int _capBytes = PlayerNamesProperty.MaxValueBytes;
    private static float _retryInterval = MinInterval;
    private static int _refusalsAtMin;
    private static string _stalledOn;
    private static string _lastSent;
    private static float _lastSentAt = -100f, _nextCheck;
    private static bool _keyMissing, _loggedStartError, _loggedUpdateError;

    public static string LastSent => _lastSent;
    public static float LastSentAt => _lastSentAt;
    public static int Updates { get; private set; }
    public static bool Active { get; private set; }
    public static bool KeyMissing => _keyMissing;

    /// <summary>Adds the key to a hosted session's start arguments (Host/Server only: never to a join).</summary>
    internal static void OnStartGame(StartGameArgs args)
    {
        _startValue = null;
        _startPrivate = false;
        try
        {
            if (!ServerPlayersFeature.Publish.Value) return;
            var mode = args.GameMode;
            if (mode != GameMode.Host && mode != GameMode.Server) return;

            var props = args.SessionProperties;
            if (props == null)
            {
                props = new Il2CppSystem.Collections.Generic.Dictionary<string, SessionProperty>();
                args.SessionProperties = props;
            }
            if (props.Count >= 10) return; // Photon allows 10 custom properties; never take the game's last slot

            bool isPrivate;
            try { isPrivate = props.ContainsKey("RoomProperties") && PlayerNamesProperty.IsPrivateFlags(props["RoomProperties"]); }
            catch { isPrivate = true; } // fail closed
            _startPrivate = isPrivate;
            // Nobody is in the room yet: the first real list goes out as soon as the host's player exists.
            string value = isPrivate && !ServerPlayersFeature.PublishPrivate.Value ? PlayerNamesProperty.HiddenValue : PlayerNamesProperty.Version;
            props[PlayerNamesProperty.Key] = value;
            _startValue = value;
        }
        catch (Exception e)
        {
            if (_loggedStartError) return;
            _loggedStartError = true;
            Plugin.Logger.LogWarning("[ServerPlayers] could not add the player names to the new session (further errors suppressed): " + e.Message);
        }
    }

    internal static void Tick()
    {
        float now = Time.unscaledTime;
        if (now < _nextCheck) return;
        _nextCheck = now + CheckInterval;

        var r = PlayerListRoster.Runner;
        bool hosting = false;
        try { hosting = r != null && r.IsRunning && r.IsServer && !r.IsSinglePlayer; } catch { }
        if (!hosting)
        {
            Active = false;
            if (r == null) _runner = IntPtr.Zero;
            return;
        }

        if (r.Pointer != _runner)
        {
            // A new session: start from the value it was created with.
            _runner = r.Pointer;
            _lastSent = _startValue;
            _lastSentAt = -100f;
            _keyMissing = false;
            Updates = 0;
            _sessionHidden = _startValue == PlayerNamesProperty.HiddenValue;
            _sessionPrivate = _startPrivate;
            _capBytes = PlayerNamesProperty.MaxValueBytes;
            _retryInterval = MinInterval;
            _refusalsAtMin = 0;
            _stalledOn = null;
        }
        if (_keyMissing) { Active = false; return; }

        SessionInfo info;
        try { info = r.SessionInfo; } catch { return; }
        if (info == null) return;
        try
        {
            if (!info.IsValid) return;
            var props = info.Properties;
            if (props == null) return;
            if (!props.ContainsKey(PlayerNamesProperty.Key))
            {
                // Started without the key (setting was off, or the game started before the mod): it can't be added now.
                _keyMissing = true;
                Active = false;
                return;
            }
        }
        catch { return; }
        Active = true;

        string want = Desired(info);
        if (want == null || want == _lastSent || now - _lastSentAt < _retryInterval) return;
        if (_stalledOn != null)
        {
            if (want == _stalledOn) return; // gave up on this roster: wait until it changes
            _stalledOn = null;
            _refusalsAtMin = 0;
        }
        try
        {
            // Send the game's keys along with ours: harmless if Fusion merges, and keeps the game's AreaInfoName,
            // GameVersion and RoomProperties if it replaces (untested which). Same keys, so the same size budget.
            var update = new Il2CppSystem.Collections.Generic.Dictionary<string, SessionProperty>();
            try
            {
                var current = info.Properties;
                if (current != null)
                    foreach (var k in GameKeys)
                        if (current.ContainsKey(k) && current[k] != null) update[k] = current[k];
            }
            catch
            {
                update.Clear(); // copying failed: send only our key, as before
            }
            update[PlayerNamesProperty.Key] = want;
            bool ok = info.UpdateCustomProperties(update);
            _lastSentAt = now; // also after a refusal: retry no faster than the throttle
            if (ok)
            {
                _lastSent = want;
                Updates++;
                _retryInterval = MinInterval; // the reduced byte cap (if any) stays for this session
                _refusalsAtMin = 0;
            }
            else Refused(want);
        }
        catch (Exception e)
        {
            _lastSentAt = now;
            if (_loggedUpdateError) return;
            _loggedUpdateError = true;
            Plugin.Logger.LogWarning("[ServerPlayers] updating the player names failed (further errors suppressed): " + e.Message);
        }
    }

    /// <summary>Photon refused an update (merged properties over 500 bytes, or a bad key): shrink and slow down.</summary>
    private static void Refused(string want)
    {
        if (!_loggedUpdateError)
        {
            _loggedUpdateError = true;
            Plugin.Logger.LogWarning($"[ServerPlayers] Photon refused the player-name update ({want.Length} chars); sending fewer names, less often.");
        }
        if (_capBytes <= MinCapBytes)
        {
            if (++_refusalsAtMin >= MaxRefusalsAtMin) _stalledOn = want;
        }
        _capBytes = Math.Max(MinCapBytes, _capBytes / 2);
        _retryInterval = Math.Min(MaxInterval, _retryInterval * 2f);
    }

    /// <summary>True when the session is private, read from its own RoomProperties (true when it can't be read).</summary>
    private static bool IsPrivate(SessionInfo info)
    {
        try
        {
            var props = info.Properties;
            if (props == null) return true;
            return props.ContainsKey("RoomProperties") && PlayerNamesProperty.IsPrivateFlags(props["RoomProperties"]);
        }
        catch { return true; }
    }

    private static readonly List<(string, bool)> Roster = new();

    /// <summary>
    /// The game's own session properties (OverlandSessionInfo reads exactly these three), plus the mod network's
    /// advert: if an update replaces the set, guests joining later must still see it.
    /// </summary>
    private static readonly string[] GameKeys = { "AreaInfoName", "GameVersion", "RoomProperties", ModNet.ModNet.SessionKey };

    /// <summary>The value the session should carry now, or null when the roster can't be read yet.</summary>
    private static string Desired(SessionInfo info)
    {
        if (!ServerPlayersFeature.Publish.Value) return PlayerNamesProperty.HiddenValue;
        if (!ServerPlayersFeature.PublishPrivate.Value &&
            (_sessionHidden || _sessionPrivate || IsPrivate(info) || GameState.JoinedPrivateSession))
            return PlayerNamesProperty.HiddenValue;

        var sp = OverlandSessionPlayers.Instance;
        if (sp == null) return null;
        Roster.Clear();
        int hostRaw = 0;
        try { hostRaw = sp.HostPlayerRef.RawEncoded; } catch { }
        foreach (var p in PlayerListRoster.AllPlayers())
        {
            bool host = hostRaw != 0 && p.RawEncoded == hostRaw;
            var entry = (PlayerListRoster.NameOf(sp, p), host);
            if (host) Roster.Insert(0, entry);
            else Roster.Add(entry);
        }
        if (Roster.Count == 0) return null;
        return PlayerNamesProperty.Encode(Roster, _capBytes);
    }
}

[HarmonyPatch(typeof(NetworkRunner), nameof(NetworkRunner.StartGame))]
internal static class PlayerNamesStartGamePatch
{
    private static void Prefix(StartGameArgs args) => PlayerNamesPublisher.OnStartGame(args);
}
