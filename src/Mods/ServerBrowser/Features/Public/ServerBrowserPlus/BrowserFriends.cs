using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;
using OverTheHillPlus.Features.Public.ServerPlayers;
using OverTheHillPlus.ServerBrowser;
using UnityEngine;
using UnityEngine.UI;

namespace OverTheHillPlus.Features.Public.ServerBrowserPlus;

/// <summary>
/// Steam friends in the server browser: servers where a friend is playing are highlighted, tagged, pinned after
/// favourites and can be filtered to. A friend is found by name, in two ways:
/// - the player names modded hosts publish (see <see cref="PlayerNamesProperty"/>), when the host shares them;
/// - the room name, for any host (vanilla rooms are called "&lt;host&gt;'s room").
/// Everything happens locally: the friend list is never sent anywhere. Matching is by name only, so a stranger with
/// a friend's exact name matches too (the default scope, friends playing this game right now, keeps that rare).
/// </summary>
internal static class BrowserFriends
{
    public static ConfigEntry<bool> Highlight, ShowTag, PinFriends, OnlyFriends, MatchHostName;
    public static ConfigEntry<FriendScope> Scope;
    public static ConfigEntry<string> FriendColor;

    internal sealed class Match
    {
        /// <summary>Friends found in the server (cleaned names, host first when known).</summary>
        public readonly List<string> Names = new();
        /// <summary>A friend hosts it.</summary>
        public bool Host;
    }

    private sealed class CacheEntry
    {
        public Match Value;
        public float At;
    }

    private static readonly Dictionary<string, CacheEntry> Cache = new();
    private static int _cacheVersion = -1;
    private static FriendScope _cacheScope;
    private static bool _cacheHostNames;
    private static bool _loggedMatchError;
    private static float _wantFriendsUntil;
    private static int _seenFriendsVersion = -1;

    public static void Bind(ConfigFile cfg)
    {
        const string s = "BrowserFriends";
        Highlight = cfg.Bind(s, "Highlight", true, "Server browser: highlight servers where a Steam friend is playing.");
        ShowTag = cfg.Bind(s, "ShowTag", true, "Server browser: tag those servers with the friend's name.");
        PinFriends = cfg.Bind(s, "PinFriends", true, "Server browser: list servers with friends right after favourites (quick join prefers them too).");
        OnlyFriends = cfg.Bind(s, "OnlyFriends", false, "Server browser: only show servers where a friend is playing.");
        MatchHostName = cfg.Bind(s, "MatchHostName", true, "Also find friends who host a server by its room name (works for servers without this mod).");
        Scope = cfg.Bind(s, "Scope", FriendScope.PlayingThisGame,
            "Which Steam friends to look for: PlayingThisGame (fewest false matches), Online or All.");
        FriendColor = cfg.Bind(s, "Color", "#FF7AC8", "Highlight colour for servers with friends (#RRGGBB).");

        foreach (var e in new[] { Highlight, ShowTag, PinFriends, OnlyFriends, MatchHostName })
            e.SettingChanged += OnSettingChanged;
        Scope.SettingChanged += OnSettingChanged;
        FriendColor.SettingChanged += OnColorChanged;
        ApplyHooks();
    }

    private static void OnSettingChanged(object sender, EventArgs args) => Changed();

    private static void OnColorChanged(object sender, EventArgs args)
    {
        _colorText = null;
        BrowserPlus.RequestRefresh();
    }

    private static void Changed()
    {
        Cache.Clear();
        ApplyHooks();
        BrowserPlus.RequestRefresh();
    }

    /// <summary>Asks for the friend list to be kept fresh for a while (the menu calls this while it shows friends).</summary>
    public static void WantFriends() => _wantFriendsUntil = Time.unscaledTime + 2f;

    /// <summary>Per frame: keeps the Steam friend list fresh while it's needed and re-filters when it changes.</summary>
    internal static void Tick()
    {
        if (BrowserPlus.BrowserAlive || Time.unscaledTime < _wantFriendsUntil) SteamFriendsList.RefreshIfDue();
        if (SteamFriendsList.Version != _seenFriendsVersion)
        {
            _seenFriendsVersion = SteamFriendsList.Version;
            Cache.Clear();
            ApplyHooks();
            BrowserPlus.RequestRefresh();
        }
    }

    /// <summary>Plugs the friend filter, pin and tags into the browser (only the parts that are switched on).</summary>
    private static void ApplyHooks()
    {
        BrowserPlus.ExtraKeep = OnlyFriends.Value ? KeepRow : null;
        BrowserPlus.ExtraPin = PinFriends.Value && SteamFriendsList.CountIn(Scope.Value) > 0 ? HasFriends : null;
        BrowserPlus.ExtraTags = ShowTag.Value ? Tags : null;
    }

    private static bool KeepRow(OverlandSessionInfo info) => HasFriends(info);

    public static bool HasFriends(OverlandSessionInfo info) => MatchOf(info) != null;

    private static string _colorText;

    private static string ColorText
    {
        get
        {
            if (_colorText != null) return _colorText;
            string c = (FriendColor.Value ?? "").Trim();
            if (!c.StartsWith("#")) c = "#" + c;
            bool ok = c.Length == 7 && uint.TryParse(c.Substring(1), System.Globalization.NumberStyles.HexNumber, null, out _);
            return _colorText = ok ? c.ToUpperInvariant() : "#FF7AC8";
        }
    }

    public static Color RowColor
    {
        get
        {
            uint rgb = uint.Parse(ColorText.Substring(1), System.Globalization.NumberStyles.HexNumber);
            return new Color(((rgb >> 16) & 0xFF) / 255f, ((rgb >> 8) & 0xFF) / 255f, (rgb & 0xFF) / 255f, 1f);
        }
    }

    private static string Tags(OverlandSessionInfo info, Fusion.SessionInfo session)
    {
        var m = MatchOf(info);
        if (m == null) return null;
        // Names are cleaned (no '<' or '>'), so they can't break the rich text around them.
        string first = Short(m.Names[0], 16);
        string label = m.Names.Count == 1 ? $"FRIEND {first}" : $"FRIENDS {first} +{m.Names.Count - 1}";
        return $"<color={ColorText}><b>{label}</b></color>  ";
    }

    internal static string Short(string s, int max)
    {
        s ??= "";
        if (s.Length <= max) return s;
        s = s.Substring(0, max - 1);
        if (char.IsHighSurrogate(s[s.Length - 1])) s = s.Substring(0, s.Length - 1);
        return s + "…";
    }

    // ---------------------------------------------------------------- matching

    /// <summary>The friends in a server, or null when there are none. Cached per server for a second.</summary>
    public static Match MatchOf(OverlandSessionInfo info)
    {
        var s = info?.FusionSessionInfo;
        if (s == null) return null;
        var scope = Scope.Value;
        bool hostNames = MatchHostName.Value;
        if (_cacheVersion != SteamFriendsList.Version || _cacheScope != scope || _cacheHostNames != hostNames)
        {
            Cache.Clear();
            _cacheVersion = SteamFriendsList.Version;
            _cacheScope = scope;
            _cacheHostNames = hostNames;
        }
        if (SteamFriendsList.CountIn(scope) == 0) return null;

        string id;
        try { id = s.Name ?? ""; } catch (Exception ex) { DebugLog.Error("serverbrowser", ex, "BrowserFriends.MatchOf"); return null; }
        float now = Time.unscaledTime;
        if (Cache.TryGetValue(id, out var c) && now - c.At < 1f && now >= c.At) return c.Value;
        if (Cache.Count > 512) Cache.Clear();

        Match m = null;
        try { m = Compute(info, s, scope, hostNames, now); }
        catch (Exception e)
        {
            DebugLog.Error("serverbrowser", e, "BrowserFriends.MatchOf");
            if (!_loggedMatchError)
            {
                _loggedMatchError = true;
                ServerBrowserPlugin.Logger.LogWarning("[Browser+] friend matching failed (further errors suppressed): " + e.Message);
            }
        }
        Cache[id] = new CacheEntry { Value = m, At = now };
        return m;
    }

    private static Match Compute(OverlandSessionInfo info, Fusion.SessionInfo s, FriendScope scope, bool hostNames, float now)
    {
        Match m = null;
        void Add(string name, bool host)
        {
            m ??= new Match();
            if (host) m.Host = true;
            if (m.Names.Contains(name)) return;
            if (host) m.Names.Insert(0, name);
            else m.Names.Add(name);
        }

        if (hostNames)
        {
            string host = HostFromRoomName(Favourites.PlainName(Favourites.DisplayNameOf(info)));
            var f = SteamFriendsList.Find(SteamFriendsList.Key(host), scope);
            if (f != null) Add(f.Name, true);
        }

        var names = PlayerNamesProperty.For(s, now, out bool present);
        if (present && names != null && !names.Hidden)
        {
            for (int i = 0; i < names.List.Count; i++)
            {
                var f = SteamFriendsList.Find(SteamFriendsList.KeyOfCleaned(names.List[i]), scope);
                if (f != null) Add(f.Name, i == names.HostIndex);
            }
        }
        return m;
    }

    /// <summary>
    /// The host's name from a room name like "Bob's room" (also "Bob’s Room"): the part before the last "'s".
    /// A name without that pattern is returned whole (a room simply called after its host).
    /// </summary>
    internal static string HostFromRoomName(string room)
    {
        if (string.IsNullOrWhiteSpace(room)) return "";
        room = room.Trim();
        int best = -1;
        for (int i = room.Length - 2; i > 0; i--)
        {
            char a = room[i], b = room[i + 1];
            if ((a == '\'' || a == '’') && (b == 's' || b == 'S') && (i + 2 == room.Length || char.IsWhiteSpace(room[i + 2])))
            {
                best = i;
                break;
            }
        }
        return best > 0 ? room.Substring(0, best).Trim() : room;
    }

    /// <summary>Friends found in all the servers the game knows about (for the menu).</summary>
    public static List<(OverlandSessionInfo info, Match match)> AllMatches()
    {
        var result = new List<(OverlandSessionInfo, Match)>();
        foreach (var info in BrowserPlus.AllSessions())
        {
            var m = MatchOf(info);
            if (m != null) result.Add((info, m));
        }
        return result;
    }

    // ---------------------------------------------------------------- row highlight

    private const string GlowName = "OTHPlus_FriendGlow";
    private const string EdgeName = "OTHPlus_FriendEdge";
    private static bool _loggedRowError;

    internal static void StyleRow(SessionInfoListAdapter adapter, SessionInfoItemViewsHolder vh)
    {
        try
        {
            var root = vh?.root;
            if (root == null) return;
            var data = adapter?.Data;
            int idx = vh.ItemIndex;
            Match m = null;
            if (Highlight.Value && data != null && idx >= 0 && idx < data.Count) m = MatchOf(data[idx]);

            var glow = root.Find(GlowName);
            var edge = root.Find(EdgeName);
            if (m == null)
            {
                // Recycled views: switch off a highlight left from the server this view showed before.
                if (glow != null) glow.gameObject.SetActive(false);
                if (edge != null) edge.gameObject.SetActive(false);
                return;
            }
            var color = RowColor;
            var g = glow != null ? glow.GetComponent<Image>() : Create(root, GlowName, edge: false);
            var e = edge != null ? edge.GetComponent<Image>() : Create(root, EdgeName, edge: true);
            if (g != null)
            {
                g.gameObject.SetActive(true);
                g.color = new Color(color.r, color.g, color.b, 0.16f);
            }
            if (e != null)
            {
                e.gameObject.SetActive(true);
                e.color = color;
            }
        }
        catch (Exception ex)
        {
            DebugLog.Error("serverbrowser", ex, "BrowserFriends.StyleRow");
            if (_loggedRowError) return;
            _loggedRowError = true;
            ServerBrowserPlugin.Logger.LogWarning("[Browser+] friend highlight failed (further errors suppressed): " + ex.Message);
        }
    }

    private static Image Create(RectTransform root, string name, bool edge)
    {
        var go = new GameObject(name);
        go.transform.SetParent(root, false);
        var img = go.AddComponent<Image>();
        img.raycastTarget = false;
        go.AddComponent<LayoutElement>().ignoreLayout = true;
        var rt = go.GetComponent<RectTransform>();
        if (edge)
        {
            // Right edge, so it doesn't cover the status stripe on the left.
            rt.anchorMin = new Vector2(1f, 0f);
            rt.anchorMax = new Vector2(1f, 1f);
            rt.pivot = new Vector2(1f, 0.5f);
            rt.sizeDelta = new Vector2(6f, 0f);
        }
        else
        {
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.sizeDelta = Vector2.zero;
        }
        rt.anchoredPosition = Vector2.zero;
        go.transform.SetAsLastSibling();
        return img;
    }
}

[HarmonyPatch]
internal static class BrowserFriendsPatches
{
    // After the row styler (default priority) so the highlight sits on top of its status tint.
    [HarmonyPostfix]
    [HarmonyPriority(Priority.Low)]
    [HarmonyPatch(typeof(SessionInfoListAdapter), nameof(SessionInfoListAdapter.UpdateViewsHolder))]
    private static void RowBound(SessionInfoListAdapter __instance, SessionInfoItemViewsHolder newOrRecycled) =>
        BrowserFriends.StyleRow(__instance, newOrRecycled);
}
