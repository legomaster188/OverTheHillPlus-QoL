using System;
using System.Collections.Generic;
using Fusion;
using HarmonyLib;
using Il2CppInterop.Runtime;
using UnityEngine;
using Object = UnityEngine.Object;

namespace OverTheHillPlus.Features.Public.PlayerList;

/// <summary>
/// Who is in the session, independent of the game's 4-slot tables (OverlandSessionPlayers.OrderedPlayerRefs /
/// PlayerColors). Players that are not in those tables are "extra" players: the stock pause menu never lists them.
/// Everything here is a local read, cached and throttled.
/// </summary>
internal static class PlayerListRoster
{
    internal struct Entry
    {
        public PlayerRef Ref;
        public string Name;
        public Color Color;
        public bool IsHost, IsLocal, Supporter, Dev;
    }

    private static readonly List<FusionPlayer> Cars = new();
    private static float _carsAt = -10f;

    // Names the host saw in AddPlayer, kept for players past the game's 7-entry name table.
    private static readonly Dictionary<int, string> RememberedNames = new();

    public static NetworkRunner Runner
    {
        get
        {
            try
            {
                var r = OverlandNetworkRunner.Instance?.NetworkRunner;
                if (r != null && r.IsRunning) return r;
            }
            catch { }
            return GameState.Runner;
        }
    }

    /// <summary>In an online session (host or guest), not single-player or the main menu.</summary>
    public static bool InMultiplayerSession
    {
        get
        {
            var r = Runner;
            if (r == null) return false;
            try { return !r.IsSinglePlayer; } catch { return false; }
        }
    }

    /// <summary>All FusionPlayer cars in the world (cached, refreshed at most once a second).</summary>
    public static List<FusionPlayer> FusionPlayers()
    {
        if (Time.unscaledTime - _carsAt < 1f) return Cars;
        _carsAt = Time.unscaledTime;
        Cars.Clear();
        try
        {
            foreach (var o in GameState.FusionPlayers)
            {
                var fp = o.TryCast<FusionPlayer>();
                if (fp != null) Cars.Add(fp);
            }
        }
        catch (Exception e) { LogOnce("cars", "finding cars: " + e.Message); }
        return Cars;
    }

    /// <summary>RawEncoded of every player in the game's 4 player slots.</summary>
    public static HashSet<int> Slotted(OverlandSessionPlayers sp)
    {
        var set = new HashSet<int>();
        if (sp == null) return set;
        try
        {
            var order = sp.OrderedPlayerRefs;
            for (int i = 0; i < order.Length; i++)
            {
                int raw = order.Get(i).RawEncoded;
                if (raw != 0) set.Add(raw);
            }
        }
        catch { }
        return set;
    }

    /// <summary>Index of the player in the game's colour table (-1 = no colour slot, the game's default colour).</summary>
    public static int ColorSlot(OverlandSessionPlayers sp, PlayerRef p)
    {
        if (sp == null) return -1;
        try
        {
            var colors = sp.PlayerColors;
            for (int i = 0; i < colors.Length; i++)
                if (colors.Get(i).RawEncoded == p.RawEncoded) return i;
        }
        catch { }
        return -1;
    }

    /// <summary>Every real player in the session, oldest first: the runner's player list plus every car's owner.</summary>
    public static List<PlayerRef> AllPlayers()
    {
        var seen = new HashSet<int>();
        var list = new List<PlayerRef>();
        void Add(PlayerRef p)
        {
            if (p.IsRealPlayer && seen.Add(p.RawEncoded)) list.Add(p);
        }

        var r = Runner;
        if (r != null)
        {
            try
            {
                var en = r.ActivePlayers?.GetEnumerator();
                var it = en?.TryCast<Il2CppSystem.Collections.IEnumerator>();
                if (it != null)
                    for (int guard = 0; guard < 64 && it.MoveNext(); guard++) Add(en.Current);
            }
            catch (Exception e) { LogOnce("active", "runner player list: " + e.Message); }
        }
        foreach (var fp in FusionPlayers())
        {
            try
            {
                if (fp == null || fp.Object == null) continue;
                Add(fp.Object.InputAuthority);
            }
            catch { }
        }
        var sp = OverlandSessionPlayers.Instance;
        if (sp != null)
        {
            try
            {
                var order = sp.OrderedPlayerRefs;
                for (int i = 0; i < order.Length; i++) Add(order.Get(i));
            }
            catch { }
        }
        list.Sort((a, b) => a.RawEncoded.CompareTo(b.RawEncoded));
        return list;
    }

    /// <summary>Players in the session that are not in the game's 4 player slots.</summary>
    public static List<PlayerRef> Extras()
    {
        var result = new List<PlayerRef>();
        if (!InMultiplayerSession) return result;
        var sp = OverlandSessionPlayers.Instance;
        if (sp == null) return result;
        var slotted = Slotted(sp);
        foreach (var p in AllPlayers())
            if (!slotted.Contains(p.RawEncoded)) result.Add(p);
        return result;
    }

    public static Entry Describe(OverlandSessionPlayers sp, PlayerRef p)
    {
        var e = new Entry { Ref = p, Name = NameOf(sp, p), Color = Color.white };
        try { e.Color = UIManager.GetPlayerColor(p); } catch { }
        try { e.IsHost = sp != null && sp.HostPlayerRef.RawEncoded == p.RawEncoded; } catch { }
        try { var r = Runner; e.IsLocal = r != null && r.LocalPlayer.RawEncoded == p.RawEncoded; } catch { }
        try { e.Supporter = sp != null && sp.OwnsSupporterEdition(p); } catch { }
        try { e.Dev = sp != null && sp.IsDevTeamMember(p); } catch { }
        return e;
    }

    public static string NameOf(OverlandSessionPlayers sp, PlayerRef p)
    {
        string n = null;
        try { n = sp?.GetPlayerName(p); } catch { }
        if (string.IsNullOrEmpty(n) && TryKnownName(p, out var known)) n = known;
        return string.IsNullOrEmpty(n) ? $"Player {p.PlayerId}" : n;
    }

    // ---- real names for players past the name table (host only knows them) ----

    public static void Remember(PlayerRef p, string name)
    {
        if (string.IsNullOrEmpty(name)) return;
        try { RememberedNames[p.PlayerId] = name; } catch { }
    }

    public static bool TryKnownName(PlayerRef p, out string name)
    {
        name = null;
        try { if (RememberedNames.TryGetValue(p.PlayerId, out name) && !string.IsNullOrEmpty(name)) return true; } catch { }
        try
        {
            var info = OverlandNetworkRunner.Instance?.spawnedCharactersInfo;
            if (info != null && info.ContainsKey(p))
            {
                name = info[p]?.PlayerName;
                if (!string.IsNullOrEmpty(name)) return true;
            }
        }
        catch { }
        name = null;
        return false;
    }

    // ---- logging ----

    private static readonly HashSet<string> Logged = new();

    public static void LogOnce(string key, string message)
    {
        if (Logged.Add(key)) Plugin.Logger.LogWarning("[PlayerList] " + message + " (logged once)");
    }
}

/// <summary>Host: remember the name every joining player sent, even when the game's name table is already full.</summary>
[HarmonyPatch(typeof(OverlandSessionPlayers), nameof(OverlandSessionPlayers.AddPlayer))]
internal static class PlayerListRememberNamePatch
{
    private static void Postfix(PlayerRef playerRef, string runtimePlayerPlayerName)
    {
        try { PlayerListRoster.Remember(playerRef, runtimePlayerPlayerName); } catch { }
    }
}

/// <summary>
/// The game's name table holds 7 players; past that GetPlayerName returns "". On the host the real name is still known,
/// so use it (runs before the lobby guard's "Player N" fallback, which only applies when the result is still empty).
/// </summary>
[HarmonyPatch(typeof(OverlandSessionPlayers), nameof(OverlandSessionPlayers.GetPlayerName))]
internal static class PlayerListNameFallbackPatch
{
    [HarmonyPriority(Priority.Low)]
    private static void Postfix(PlayerRef playerRef, ref string __result)
    {
        if (!string.IsNullOrEmpty(__result)) return;
        try
        {
            if (PlayerListFeature.HostNames == null || !PlayerListFeature.HostNames.Value) return;
            if (PlayerListRoster.TryKnownName(playerRef, out var name)) __result = name;
        }
        catch { }
    }
}
