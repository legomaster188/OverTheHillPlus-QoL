using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Configuration;
using HarmonyLib;
using OverTheHillPlus.ServerBrowser;
using UnityEngine;
using UnityEngine.InputSystem;

namespace OverTheHillPlus.Features.Public.ServerBrowserPlus;

internal enum AgeSort { Off, NewestFirst, OldestFirst }

/// <summary>
/// Server browser v2: favourites pinned to the top, extra filters, sort by age and quick join.
///
/// How it hooks the game (from the decompiled SessionInfoListAdapter): ApplyFilters() rebuilds Data.List from
/// OverlandNetworkRunner.SessionList (search, hide full/private, areas) and every caller follows it with Sort(),
/// which sorts Data.List and refreshes the views. A postfix on Sort() therefore sees the final list: it drops
/// rows our filters reject, stable-reorders the rest (favourites first, optional age order) and re-notifies the
/// list view only if something actually changed.
/// </summary>
internal static class BrowserPlus
{
    public static ConfigEntry<bool> PinFavourites, HideIncompatible, OnlyBigLobbies, OnlyFreeSlots, OnlyFavourites;
    public static ConfigEntry<bool> ShowFavTag, ShowBigTag, ShowBar;
    public static ConfigEntry<AgeSort> SortByAge;
    public static ConfigEntry<Key> FavouriteKey;
    public static ConfigEntry<float> BarX, BarY;

    /// <summary>Last list adapter seen (the browser's list view). Unity-null when the browser UI is gone.</summary>
    internal static SessionInfoListAdapter Adapter;
    /// <summary>The open server browser state (null when closed); used for quick join.</summary>
    internal static StateServerBrowser Browser;

    public static int Shown, Hidden;
    private static bool _loggedSortError, _loggedRefreshError, _loggedJoinError;
    private static bool _refreshPending;
    private static OverlandSessionInfo _pendingJoin;

    public static void Bind(ConfigFile cfg)
    {
        const string s = "ServerBrowserPlus";
        PinFavourites = cfg.Bind(s, "PinFavourites", true, "Keep favourite servers at the top of the list.");
        HideIncompatible = cfg.Bind(s, "HideIncompatible", false, "Hide servers running a different game version.");
        OnlyBigLobbies = cfg.Bind(s, "OnlyBigLobbies", false, "Only show modded big lobbies (more than 4 player slots).");
        OnlyFreeSlots = cfg.Bind(s, "OnlyFreeSlots", false, "Only show servers that are open and have a free slot.");
        OnlyFavourites = cfg.Bind(s, "OnlyFavourites", false, "Only show favourite servers.");
        SortByAge = cfg.Bind(s, "SortByAge", AgeSort.Off, "Order servers by how long they have been up (after favourites).");
        ShowFavTag = cfg.Bind(s, "ShowFavTag", true, "Tag favourite servers with FAV in the list.");
        ShowBigTag = cfg.Bind(s, "ShowBigTag", true, "Tag big lobbies (more than 4 slots) with BIG and their size.");
        ShowBar = cfg.Bind(s, "ShowBar", true, "Show the PLUS quick bar (filters, favourite, quick join) while the server browser is open.");
        FavouriteKey = cfg.Bind(s, "FavouriteKey", Key.F5, "While the server browser is open: star/unstar the selected server.");
        BarX = cfg.Bind(s, "BarX", -1f, "Quick bar position (1080p units, -1 = default).");
        BarY = cfg.Bind(s, "BarY", -1f, "Quick bar position (1080p units, -1 = default).");

        // Any filter/sort change re-filters the open list.
        foreach (var e in new[] { PinFavourites, HideIncompatible, OnlyBigLobbies, OnlyFreeSlots, OnlyFavourites, ShowFavTag, ShowBigTag })
            e.SettingChanged += OnSettingChanged;
        SortByAge.SettingChanged += OnSettingChanged;

        RowStyler.ExtraTags = RowTags;
    }

    private static void OnSettingChanged(object sender, EventArgs args) => RequestRefresh();

    public static bool AnyFilter => HideIncompatible.Value || OnlyBigLobbies.Value || OnlyFreeSlots.Value || OnlyFavourites.Value;

    public static bool IsBig(Fusion.SessionInfo s) => s != null && s.MaxPlayers > 4;

    public static bool HasFreeSlot(Fusion.SessionInfo s) => s != null && s.IsOpen && (s.MaxPlayers <= 0 || s.PlayerCount < s.MaxPlayers);

    public static bool BrowserAlive => Browser != null && BrowserState.Open;

    public static bool AdapterAlive
    {
        get
        {
            try { return Adapter != null && Adapter.Pointer != IntPtr.Zero && Adapter.gameObject != null; }
            catch { return false; }
        }
    }

    // ---------------------------------------------------------------- row tags

    private static string RowTags(OverlandSessionInfo info, Fusion.SessionInfo session)
    {
        string tags = "";
        if (ShowFavTag.Value && Favourites.IsFavourite(info)) tags += "<color=#F2B33D><b>FAV</b></color>  ";
        if (ShowBigTag.Value && IsBig(session)) tags += $"<color=#4FD8F7><b>BIG {session.MaxPlayers}</b></color>  ";
        return tags.Length == 0 ? null : tags;
    }

    // ---------------------------------------------------------------- filter + sort

    private static bool Keep(OverlandSessionInfo info, bool fav)
    {
        var s = info.FusionSessionInfo;
        if (s == null) return true; // let the game decide what to do with odd entries
        if (OnlyFavourites.Value && !fav) return false;
        if (OnlyBigLobbies.Value && !IsBig(s)) return false;
        if (OnlyFreeSlots.Value && !HasFreeSlot(s)) return false;
        if (HideIncompatible.Value && RowStyler.Classify(info, s) == ServerStatus.Incompatible) return false;
        return true;
    }

    /// <summary>Applies our filters and ordering to the adapter's (already game-filtered and sorted) list.</summary>
    internal static void Process(SessionInfoListAdapter adapter)
    {
        Adapter = adapter;
        var data = adapter.Data;
        var list = data?.List;
        if (list == null) return;
        int n = list.Count;

        bool pin = PinFavourites.Value && Favourites.Count > 0;
        var age = SortByAge.Value;
        bool filter = AnyFilter;
        if (!pin && age == AgeSort.Off && !filter)
        {
            Shown = n; Hidden = 0;
            return;
        }

        var rows = new List<(OverlandSessionInfo info, int index, bool fav, long first)>(n);
        for (int i = 0; i < n; i++)
        {
            var info = list[i];
            if (info == null) continue;
            bool fav = false;
            if (pin || OnlyFavourites.Value)
            {
                fav = Favourites.IsFavourite(info);
                if (fav) { var s = info.FusionSessionInfo; if (s != null) Favourites.Seen(Favourites.DisplayNameOf(info), s.PlayerCount, s.MaxPlayers); }
            }
            if (filter && !Keep(info, fav)) continue;
            long first = 0;
            if (age != AgeSort.Off && !FirstSeenTracker.TryGetFirstSeen(info.FusionSessionInfo?.Name, out first))
                first = age == AgeSort.NewestFirst ? long.MinValue : long.MaxValue; // unknown age goes last
            rows.Add((info, i, fav, first));
        }

        IEnumerable<(OverlandSessionInfo info, int index, bool fav, long first)> ordered = rows;
        if (pin || age != AgeSort.Off)
        {
            var o = rows.OrderBy(r => pin && r.fav ? 0 : 1); // LINQ OrderBy is stable: the game's order breaks ties
            if (age == AgeSort.NewestFirst) o = o.ThenByDescending(r => r.first);
            else if (age == AgeSort.OldestFirst) o = o.ThenBy(r => r.first);
            ordered = o.ToList();
        }

        var final = ordered as List<(OverlandSessionInfo info, int index, bool fav, long first)> ?? ordered.ToList();
        Shown = final.Count;
        Hidden = n - final.Count;

        bool changed = final.Count != n;
        for (int i = 0; !changed && i < final.Count; i++)
            if (final[i].index != i) changed = true;
        if (!changed) return;

        list.Clear();
        foreach (var r in final) list.Add(r.info);
        data.NotifyListChangedExternally(false);
    }

    /// <summary>Re-runs the game's filters and sort (and so ours) on the open list, next frame.</summary>
    public static void RequestRefresh() => _refreshPending = true;

    private static void RefreshNow()
    {
        _refreshPending = false;
        if (!AdapterAlive || !BrowserState.Open) return;
        try
        {
            Adapter.ApplyFilters();
            Adapter.Sort();
        }
        catch (Exception e)
        {
            if (_loggedRefreshError) return;
            _loggedRefreshError = true;
            Plugin.Logger.LogWarning("[Browser+] list refresh failed (further errors suppressed): " + e.Message);
        }
    }

    // ---------------------------------------------------------------- favourites / quick join

    public static OverlandSessionInfo SelectedSession()
    {
        try
        {
            if (Browser != null && Browser.currentlySelectedSession != null) return Browser.currentlySelectedSession;
        }
        catch { }
        try { return AdapterAlive ? Adapter.currentlySelectedSessionInfo : null; }
        catch { return null; }
    }

    public static void ToggleFavourite(OverlandSessionInfo info)
    {
        if (info == null) { Menu.SetStatus("Blocked: select a server in the browser first"); return; }
        string name = Favourites.DisplayNameOf(info);
        bool now = Favourites.Toggle(name);
        string plain = Favourites.PlainName(name);
        Menu.SetStatus(now ? $"Starred {plain}" : $"Removed {plain} from favourites");
        RequestRefresh();
    }

    /// <summary>All sessions the game knows about (before any filtering).</summary>
    public static List<OverlandSessionInfo> AllSessions()
    {
        var result = new List<OverlandSessionInfo>();
        try
        {
            var runner = OverlandNetworkRunner.Instance;
            var src = runner == null ? null : runner.SessionList;
            if (src == null) return result;
            for (int i = 0; i < src.Count; i++)
            {
                var s = src[i];
                if (s != null && s.FusionSessionInfo != null) result.Add(s);
            }
        }
        catch { }
        return result;
    }

    /// <summary>
    /// The best server to drop into: joinable (open, free slot, public, same game version), favourites first,
    /// then the liveliest lobby, then the one with the most room.
    /// </summary>
    public static OverlandSessionInfo BestServer(out int candidates)
    {
        OverlandSessionInfo best = null;
        long bestScore = long.MinValue;
        candidates = 0;
        foreach (var info in AllSessions())
        {
            var s = info.FusionSessionInfo;
            if (!HasFreeSlot(s)) continue;
            ServerStatus status;
            try { status = RowStyler.Classify(info, s); } catch { continue; }
            if (status != ServerStatus.Public) continue; // private (needs a code), full or other version
            if (OnlyBigLobbies.Value && !IsBig(s)) continue;
            candidates++;
            long score = (Favourites.IsFavourite(info) ? 1_000_000L : 0L)
                         + Math.Min(s.PlayerCount, 64) * 1_000L
                         + Math.Min(Math.Max(0, s.MaxPlayers - s.PlayerCount), 64);
            if (score > bestScore) { bestScore = score; best = info; }
        }
        return best;
    }

    public static void QuickJoin()
    {
        if (!BrowserAlive) { Menu.SetStatus("Blocked: open the server browser (Play online) first"); return; }
        var best = BestServer(out int candidates);
        if (best == null)
        {
            Menu.SetStatus(candidates == 0 ? "Blocked: no joinable public server right now" : "Blocked: nothing suitable to join");
            return;
        }
        var s = best.FusionSessionInfo;
        Menu.SetStatus($"Joining {Favourites.PlainName(Favourites.DisplayNameOf(best))} ({s.PlayerCount}/{s.MaxPlayers})");
        _pendingJoin = best; // joined from Update, not from inside OnGUI
    }

    private static void JoinNow()
    {
        var target = _pendingJoin;
        _pendingJoin = null;
        if (target == null || !BrowserAlive) return;
        try
        {
            Browser.SessionClicked(target); // same path as clicking the row: the game does its own checks/pop-ups
            Menu.CloseRequested = true;
        }
        catch (Exception e)
        {
            Menu.SetStatus("Failed to join: " + e.Message);
            if (_loggedJoinError) return;
            _loggedJoinError = true;
            Plugin.Logger.LogWarning("[Browser+] quick join failed: " + e);
        }
    }

    // ---------------------------------------------------------------- per frame

    internal static void Tick()
    {
        if (_pendingJoin != null) JoinNow();
        if (_refreshPending) RefreshNow();
        Favourites.SaveIfDue();

        if (!BrowserState.Open) return;
        var kb = Keyboard.current;
        if (kb == null) return;
        var key = FavouriteKey.Value;
        if (key == Key.None) return;
        try { if (kb[key].wasPressedThisFrame) ToggleFavourite(SelectedSession()); }
        catch { /* invalid key in config */ }
    }

    internal static void OnSortDone(SessionInfoListAdapter adapter)
    {
        try { Process(adapter); }
        catch (Exception e)
        {
            if (_loggedSortError) return;
            _loggedSortError = true;
            Plugin.Logger.LogError("[Browser+] filter/sort failed (further errors suppressed): " + e);
        }
    }
}

[HarmonyPatch]
internal static class BrowserPlusPatches
{
    [HarmonyPostfix]
    [HarmonyPatch(typeof(SessionInfoListAdapter), nameof(SessionInfoListAdapter.Sort))]
    private static void AfterSort(SessionInfoListAdapter __instance) => BrowserPlus.OnSortDone(__instance);

    [HarmonyPostfix]
    [HarmonyPatch(typeof(StateServerBrowser), nameof(StateServerBrowser.Open))]
    private static void Opened(StateServerBrowser __instance) => BrowserPlus.Browser = __instance;

    [HarmonyPostfix]
    [HarmonyPatch(typeof(StateServerBrowser), nameof(StateServerBrowser.Close))]
    private static void Closed()
    {
        BrowserPlus.Browser = null;
        Favourites.SaveIfDue(force: true);
    }
}
