using System;
using System.Collections.Generic;
using System.Text;
using BepInEx.Configuration;
using Fusion;
using HarmonyLib;
using OverTheHillPlus.Features.Public.Filter;
using Net = OverTheHillPlus.Features.Public.ModNet.ModNet;
using UnityEngine;

namespace OverTheHillPlus.Features.Public.Names;

/// <summary>Registers the overflow-name channel (see <see cref="OverflowNames"/>).</summary>
internal sealed class OverflowNamesFeature : IFeature
{
    public void Register()
    {
        OverflowNames.Bind();
        OverflowNames.Install();
        Registry.OnTick(OverflowNames.Tick);
        Registry.Section(Registry.PlusMode, "Lobby", OverflowNames.DrawCard, 40);
    }
}

/// <summary>
/// Names for players past the game's name table.
///
/// The game keeps player names in a networked dictionary with room for 7 entries (OverlandSessionPlayers.
/// PlayerRefToNameDictionary, keyed PlayerId); in a big lobby the 8th player onwards has no name there and
/// GetPlayerName returns "" (decoded with Cpp2IL), which the lobby guards turn into "Player N".
///
/// Carried over the mod network (<see cref="Net"/>, channel <see cref="Net.Channels.Names"/>), which only
/// ever talks to players who proved they run it:
/// - The host records every name passed to AddPlayer (including players with no slot in the name table) and, in
///   lobbies above the stock 4 players, sends the table to every modded guest with this option on, again whenever
///   it changes.
/// - Guests answer GetPlayerName from the received table when the game's own table has nothing.
/// Vanilla players keep the game's behaviour ("Player N" for overflow players).
/// </summary>
internal static class OverflowNames
{
    public static ConfigEntry<bool> Enabled;

    private const byte MsgTable = 2;
    private const int StockMaxPlayers = 4;
    private const int MaxNameLength = 32, MaxEntries = 64, MaxPayload = 8192;
    private const char FieldSep = '\u001F', EntrySep = '\u001E';

    // Host: every name AddPlayer saw this session (PlayerId -> name), plus when a player left (kept briefly so
    // "left" messages and late UI refreshes still find the name).
    private static readonly Dictionary<int, string> HostNames = new();
    private static readonly Dictionary<int, float> LeftAt = new();
    private static readonly Dictionary<int, int> SentVersion = new();    // RawEncoded -> table version sent
    private static int _tableVersion = 1;

    // Guest: the table the host sent us.
    private static readonly Dictionary<int, string> RemoteNames = new();
    private static bool _gotTable;

    private static IntPtr _runnerPtr;
    private static float _nextTick;
    private static bool _installed;
    private static string _lastError;

    private static readonly HashSet<string> LoggedOnce = new();

    public static void Bind()
    {
        Enabled = ModConfig.File.Bind("Big Lobby Names", "ShareOverflowNames", false,
            "Share the names of players 8+ (who don't fit in the game's name table) between players running this mod " +
            "(needs the mod network on). Vanilla players are never sent anything. Experimental.");
        Net.Register(Net.Channels.Names, new Net.ChannelOptions
        {
            Name = "big-lobby names",
            Version = 1,
            MaxPayload = MaxPayload,
            RatePerSecond = 2f,
            Burst = 4,
            HostOnlySender = true,
            Active = () => Enabled.Value,
        }, OnMessage);
        Net.OnPeerGone(p => SentVersion.Remove(p.RawEncoded));
        Net.OnPeerReady(p => SentVersion.Remove(p.RawEncoded)); // (re)send the table on the next tick
        Net.OnSessionReset(ResetSession);
    }

    // ---------------------------------------------------------------- patches

    /// <summary>Patched by hand, each in its own try/catch, so one failing patch can't take the others down.</summary>
    public static void Install()
    {
        if (_installed) return;
        _installed = true;
        var h = new Harmony(Plugin.Guid + ".overflownames");
        // Prefixes, not postfixes: the game's AddPlayer throws for every player past its 4 slots (and RemovePlayer can
        // throw for them too), and a postfix never runs when the original throws - exactly the players this is for.
        TryPatch(h, typeof(OverlandSessionPlayers), nameof(OverlandSessionPlayers.AddPlayer), prefix: nameof(AddPlayerPrefix));
        TryPatch(h, typeof(OverlandSessionPlayers), nameof(OverlandSessionPlayers.RemovePlayer), prefix: nameof(RemovePlayerPrefix));
        TryPatch(h, typeof(OverlandSessionPlayers), nameof(OverlandSessionPlayers.GetPlayerName), postfix: nameof(GetPlayerNamePostfix));
    }

    private static bool TryPatch(Harmony h, Type type, string method, string prefix = null, string postfix = null)
    {
        try
        {
            var target = AccessTools.Method(type, method);
            if (target == null) { Plugin.Logger.LogWarning($"[Names] {type.Name}.{method} not found; not patched"); return false; }
            var pre = prefix == null ? null : new HarmonyMethod(AccessTools.Method(typeof(OverflowNames), prefix));
            var post = postfix == null ? null : new HarmonyMethod(AccessTools.Method(typeof(OverflowNames), postfix));
            h.Patch(target, prefix: pre, postfix: post);
            return true;
        }
        catch (Exception e)
        {
            _lastError = $"patch {method} failed";
            Plugin.Logger.LogWarning($"[Names] Could not patch {type.Name}.{method}: {e.Message}");
            return false;
        }
    }

    private static void AddPlayerPrefix(OverlandSessionPlayers __instance, PlayerRef playerRef, string runtimePlayerPlayerName)
    {
        try
        {
            try { SyncSession(__instance?.Runner); } catch { }
            if (!playerRef.IsRealPlayer) return;
            int id = playerRef.PlayerId;
            string name = Clean(runtimePlayerPlayerName);
            LeftAt.Remove(id);
            if (HostNames.TryGetValue(id, out var old) && old == name) return;
            HostNames[id] = name;
            _tableVersion++;
            _nextTick = 0f; // push the new name out on the next frame
        }
        catch (Exception e) { LogOnce("add", "[Names] AddPlayer hook: " + e.Message); }
    }

    private static void RemovePlayerPrefix(PlayerRef playerRef)
    {
        try
        {
            SentVersion.Remove(playerRef.RawEncoded);
            if (HostNames.ContainsKey(playerRef.PlayerId)) LeftAt[playerRef.PlayerId] = Time.unscaledTime;
        }
        catch (Exception e) { LogOnce("remove", "[Names] RemovePlayer hook: " + e.Message); }
    }

    /// <summary>Fills in a name only when the game's own table has none (runs before the lobby guards' "Player N").</summary>
    private static void GetPlayerNamePostfix(PlayerRef playerRef, ref string __result)
    {
        if (!string.IsNullOrEmpty(__result) || Enabled == null || !Enabled.Value) return;
        try
        {
            int id = playerRef.PlayerId;
            if (RemoteNames.TryGetValue(id, out var n) && n.Length > 0) { __result = n; return; }
            if (HostNames.TryGetValue(id, out n) && n.Length > 0) __result = n;
        }
        catch { }
    }

    // ---------------------------------------------------------------- per frame

    public static void Tick()
    {
        if (Time.unscaledTime < _nextTick) return;
        _nextTick = Time.unscaledTime + 0.5f;
        try
        {
            var r = GameState.Runner;
            if (r == null) return;
            SyncSession(r);
            if (!Enabled.Value || !Net.IsHost) return;
            HostTick(r);
        }
        catch (Exception e) { LogOnce("tick", "[Names] Tick: " + e.Message); }
    }

    /// <summary>Starts a fresh table when a different session (runner) shows up.</summary>
    private static void SyncSession(NetworkRunner r)
    {
        if (r == null) return;
        IntPtr ptr = r.Pointer;
        if (ptr == IntPtr.Zero || ptr == _runnerPtr) return;
        if (_runnerPtr != IntPtr.Zero) ResetSession();
        _runnerPtr = ptr;
    }

    private static void ResetSession()
    {
        HostNames.Clear();
        LeftAt.Clear();
        SentVersion.Clear();
        RemoteNames.Clear();
        _tableVersion++;
        _gotTable = false;
    }

    private static void HostTick(NetworkRunner r)
    {
        // Forget departed players after a while.
        if (LeftAt.Count > 0)
        {
            var expired = new List<int>();
            foreach (var kv in LeftAt) if (Time.unscaledTime - kv.Value > 30f) expired.Add(kv.Key);
            foreach (int id in expired) { LeftAt.Remove(id); HostNames.Remove(id); }
            if (expired.Count > 0) _tableVersion++;
        }

        if (Net.PeerCount == 0) return;
        // Only big lobbies need it: the stock lobby always fits in the game's name table.
        var info = r.SessionInfo;
        if (info == null || !info.IsValid || info.MaxPlayers <= StockMaxPlayers) return;
        byte[] table = null;
        foreach (var p in Net.Peers)
        {
            int raw = p.Ref.RawEncoded;
            if (!p.Channels.ContainsKey(Net.Channels.Names)) continue;
            if (SentVersion.TryGetValue(raw, out int v) && v == _tableVersion) continue;
            table ??= BuildTable();
            var res = Net.SendTo(p.Ref, Net.Channels.Names, MsgTable, table);
            if (res == Net.SendResult.Sent) SentVersion[raw] = _tableVersion;
            else if (res == Net.SendResult.TooBig) LogOnce("big", "[Names] Name table too big; not sent");
        }
    }

    // ---------------------------------------------------------------- messages

    private static void OnMessage(Net.Message m)
    {
        if (m.Type != MsgTable || !m.FromHost || m.Payload == null || !Enabled.Value) return;
        if (Net.IsHost) return;
        ParseTable(m.Payload);
        if (!_gotTable) Plugin.Logger.LogInfo($"[Names] Host shares big-lobby names ({RemoteNames.Count} known)");
        _gotTable = true;
        try { OverlandSessionPlayers.InvokePlayerDataUpdated(); } catch { } // let name labels refresh
    }

    private static byte[] BuildTable()
    {
        var sb = new StringBuilder(HostNames.Count * 24);
        int n = 0;
        foreach (var kv in HostNames)
        {
            // Names the mod sends to other players must pass the filter; a guest then shows the game's "Player N".
            if (Blocked(kv.Value)) continue;
            if (n++ >= MaxEntries) break;
            if (sb.Length > 0) sb.Append(EntrySep);
            sb.Append(kv.Key).Append(FieldSep).Append(kv.Value);
        }
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    private static void ParseTable(byte[] bytes)
    {
        RemoteNames.Clear();
        string text = Encoding.UTF8.GetString(bytes, 0, Math.Min(bytes.Length, MaxPayload));
        int count = 0;
        foreach (var entry in text.Split(EntrySep))
        {
            if (count >= MaxEntries) break;
            int cut = entry.IndexOf(FieldSep);
            if (cut <= 0 || !int.TryParse(entry.Substring(0, cut), out int id) || id < 0) continue;
            string name = Clean(entry.Substring(cut + 1));
            if (name.Length == 0 || Blocked(name)) continue; // also guards against an older or modified host
            RemoteNames[id] = name;
            count++;
        }
    }

    // ---------------------------------------------------------------- misc

    private static bool Blocked(string name)
    {
        try { return NameFilter.IsBlockedStrict(name, out _); }
        catch (Exception e)
        {
            LogOnce("filter", "[Names] Name filter failed, leaving the name out: " + e.Message);
            return true;
        }
    }

    /// <summary>Plain text, at most 32 characters (the game's name slot size); no control characters or rich-text tags.</summary>
    private static string Clean(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var sb = new StringBuilder(Math.Min(s.Length, MaxNameLength));
        foreach (char c in s)
        {
            if (sb.Length >= MaxNameLength) break;
            if (char.IsControl(c) || c == '<' || c == '>') continue;
            sb.Append(c);
        }
        return sb.ToString().Trim();
    }

    private static void LogOnce(string id, string message)
    {
        if (LoggedOnce.Add(id)) Plugin.Logger.LogWarning(message);
    }

    // ---------------------------------------------------------------- menu

    public static void DrawCard()
    {
        Menu.Card("Big lobby names", "The game has name slots for 7 players. Players running this mod share the names of everyone past that; vanilla players see \"Player N\".", () =>
        {
            Menu.Switch(Enabled, "Share overflow names (experimental)", "Host: send names to modded players. Guest: take them from a modded host. Needs the mod network", true);
            Menu.Switch(Net.Enabled, "Mod network", "Shared with other features (Network tab)", true);
            string role;
            try
            {
                var r = GameState.Runner;
                if (!Net.Enabled.Value) role = "mod network off";
                else if (r == null || !r.IsRunning) role = "not in a session";
                else if (r.IsSinglePlayer) role = "single-player (nothing to share)";
                else if (r.IsServer)
                {
                    int with = 0;
                    foreach (var p in Net.Peers) if (p.Channels.ContainsKey(Net.Channels.Names)) with++;
                    role = $"hosting: {with} modded guest(s) take names, {HostNames.Count} name(s) known";
                }
                else if (_gotTable) role = $"guest: host shares names ({RemoteNames.Count} known)";
                else if (Net.HostStatus == Net.HostLink.Ready)
                    role = Net.HostHas(Net.Channels.Names) ? "guest: waiting for names (only sent in lobbies above 4)" : "guest: the host does not share names";
                else role = "guest: " + Net.Describe();
            }
            catch { role = "?"; }
            Menu.KeyValue("Status", role);
            if (_lastError != null) Menu.KeyValue("Problem", _lastError);
        });
    }
}
