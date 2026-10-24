using System;
using System.Collections.Generic;
using Fusion;
using HarmonyLib;
using OverTheHillPlus.Features.Public.PlayerList;
using OverTheHillPlus.ServerCreator;
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
            // Photon allows 10 custom properties: core's fixed ranks decide (the names come last), whatever order the
            // StartGame prefixes run in.
            var present = props;
            if (!Core.Manager.SessionSlots.MayAdd(PlayerNamesProperty.Key, props.Count, k => present.ContainsKey(k))) return;

            bool isPrivate;
            try { isPrivate = props.ContainsKey("RoomProperties") && PlayerNamesProperty.IsPrivateFlags(props["RoomProperties"]); }
            catch (Exception ex) { DebugLog.Error("servercreator", ex, "PlayerNamesPublisher.OnStartGame"); isPrivate = true; } // fail closed
            _startPrivate = isPrivate;
            // Nobody is in the room yet: the first real list goes out as soon as the host's player exists.
            string value = isPrivate && !ServerPlayersFeature.PublishPrivate.Value ? PlayerNamesProperty.HiddenValue : PlayerNamesProperty.Version;
            props[PlayerNamesProperty.Key] = value;
            _startValue = value;
            DebugLog.Info(ServerCreatorPlugin.ModId, $"hosting ({mode}): player names are published in the session properties ({(isPrivate ? "private session" : "public session")}, {(value == PlayerNamesProperty.HiddenValue ? "names hidden" : "names visible")})");
        }
        catch (Exception e)
        {
            DebugLog.Error("servercreator", e, "PlayerNamesPublisher.OnStartGame");
            if (_loggedStartError) return;
            _loggedStartError = true;
            ServerCreatorPlugin.Logger.LogWarning("[ServerPlayers] could not add the player names to the new session (further errors suppressed): " + e.Message);
        }
    }

    internal static void Tick()
    {
        float now = Time.unscaledTime;
        if (now < _nextCheck) return;
        _nextCheck = now + CheckInterval;

        var r = PlayerListRoster.Runner;
        bool hosting = false;
        try { hosting = r != null && r.IsRunning && r.IsServer && !r.IsSinglePlayer; } catch (Exception ex) { DebugLog.Error("servercreator", ex, "PlayerNamesPublisher.Tick"); }
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
        try { info = r.SessionInfo; } catch (Exception ex) { DebugLog.Error("servercreator", ex, "PlayerNamesPublisher.Tick"); return; }
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
        catch (Exception ex) { DebugLog.Error("servercreator", ex, "PlayerNamesPublisher.Tick"); return; }
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
            // Fusion's UpdateCustomProperties may replace the whole set, so every current key goes along with ours.
            // The game's browser hides a session without AreaInfoName, GameVersion and RoomProperties (it stays
            // joinable by code), so never send an update that would lose one of them: skip it and say why.
            var update = new Il2CppSystem.Collections.Generic.Dictionary<string, SessionProperty>();
            string missing = null;
            try
            {
                var current = info.Properties;
                if (current != null)
                    // Every key the game and these mods use (enumerating Fusion's dictionary through IL2CPP crashed the
                    // runtime, so the known keys are copied one by one).
                    foreach (var k in Core.Manager.SessionSlots.KnownKeys)
                        if (current.TryGetValue(k, out var v) && v != null) update[k] = v;
                foreach (var k in GameKeys)
                    if (k != null && !update.ContainsKey(k) && Array.IndexOf(RequiredGameKeys, k) >= 0) missing = missing == null ? k : missing + ", " + k;
            }
            catch (Exception ex)
            {
                DebugLog.Error("servercreator", ex, "PlayerNamesPublisher.Tick");
                missing = "(could not read the current properties)";
            }
            if (missing != null)
            {
                _lastSentAt = now;
                _stalledOn = want;
                if (!_loggedMissing)
                {
                    _loggedMissing = true;
                    ServerCreatorPlugin.Logger.LogWarning("[Players] Not publishing player names: the session's current properties lack " + missing +
                        ", and an update without them would hide this server from the browser.");
                }
                return;
            }
            update[PlayerNamesProperty.Key] = want;
            if (!_loggedKeys)
            {
                _loggedKeys = true;
                var keys = new List<string>();
                foreach (var kv in update) keys.Add(kv.Key);
                ServerCreatorPlugin.Logger.LogInfo("[Players] publishing player names with every session property kept: " + string.Join(", ", keys));
            }
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
            DebugLog.Error("servercreator", e, "PlayerNamesPublisher.Tick");
            _lastSentAt = now;
            if (_loggedUpdateError) return;
            _loggedUpdateError = true;
            ServerCreatorPlugin.Logger.LogWarning("[ServerPlayers] updating the player names failed (further errors suppressed): " + e.Message);
        }
    }

    /// <summary>Photon refused an update (merged properties over 500 bytes, or a bad key): shrink and slow down.</summary>
    private static void Refused(string want)
    {
        if (!_loggedUpdateError)
        {
            _loggedUpdateError = true;
            ServerCreatorPlugin.Logger.LogWarning($"[ServerPlayers] Photon refused the player-name update ({want.Length} chars); sending fewer names, less often.");
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
        catch (Exception ex) { DebugLog.Error("servercreator", ex, "PlayerNamesPublisher.IsPrivate"); return true; }
    }

    private static readonly List<(string, bool)> Roster = new();

    /// <summary>
    /// The game's own session properties (OverlandSessionInfo reads exactly these three), plus the mod network's
    /// and the server mods' adverts: if an update replaces the set, guests joining later must still see them.
    /// </summary>
    /// <summary>The properties the game's browser needs to list a session (OverlandSessionInfo.IsSessionValid).</summary>
    private static readonly string[] RequiredGameKeys = { "AreaInfoName", "GameVersion", "RoomProperties" };
    private static bool _loggedMissing, _loggedKeys;

    private static readonly string[] GameKeys = { "AreaInfoName", "GameVersion", "RoomProperties", ModNet.ModNet.SessionKey, Core.Manager.RequiredMods.Key };

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
        try { hostRaw = sp.HostPlayerRef.RawEncoded; } catch (Exception ex) { DebugLog.Error("servercreator", ex, "PlayerNamesPublisher.Desired"); }
        foreach (var p in PlayerListRoster.AllPlayers())
        {
            bool host = hostRaw != 0 && p.RawEncoded == hostRaw;
            var entry = (PlayerListRoster.NameOf(sp, p), host);
            if (host) Roster.Insert(0, entry);
            else Roster.Add(entry);
        }
        if (Roster.Count == 0) return null;
        // The server mods' list (othp.req) shares Photon's 500 bytes: give up what it takes.
        int cap = Math.Min(_capBytes, PlayerNamesProperty.MaxValueBytes - ModTypes.RequiredModsSession.BytesIn(info));
        return PlayerNamesProperty.Encode(Roster, Math.Max(MinCapBytes, cap));
    }
}

[HarmonyPatch(typeof(NetworkRunner), nameof(NetworkRunner.StartGame))]
internal static class PlayerNamesStartGamePatch
{
    private static void Prefix(StartGameArgs args) => PlayerNamesPublisher.OnStartGame(args);
}
