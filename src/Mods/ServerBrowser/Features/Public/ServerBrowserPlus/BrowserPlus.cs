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
    public static ConfigEntry<bool> PinFavourites, PinModded, HideIncompatible, OnlyBigLobbies, OnlyFreeSlots, OnlyFavourites;
    public static ConfigEntry<bool> ShowFavTag, ShowBigTag, ShowBar, GameLookBar;
    public static ConfigEntry<AgeSort> SortByAge;
    public static ConfigEntry<Key> FavouriteKey, QuickJoinKey;
    public static ConfigEntry<float> BarX, BarY;

    /// <summary>Last list adapter seen (the browser's list view). Unity-null when the browser UI is gone.</summary>
    internal static SessionInfoListAdapter Adapter;
    /// <summary>The open server browser state (null when closed); used for quick join. Tracked by core (BrowserScreen).</summary>
    internal static StateServerBrowser Browser => BrowserScreen.Current;

    public static int Shown, Hidden;

    // Hooks for other server browser features (friends): all optional, null = not used.
    /// <summary>Extra filter: return false to hide a row. Set it only while that filter is switched on.</summary>
    internal static Func<OverlandSessionInfo, bool> ExtraKeep;
    /// <summary>Rows this returns true for are pinned right after favourites (and preferred by quick join).</summary>
    internal static Func<OverlandSessionInfo, bool> ExtraPin;
    /// <summary>Extra rich-text tags shown before FAV/BIG.</summary>
    internal static Func<OverlandSessionInfo, Fusion.SessionInfo, string> ExtraTags;
    private static bool _loggedSortError, _loggedRefreshError, _loggedJoinError, _loggedNeverReady;
    private static bool _refreshPending;
    // A refresh waiting for the list view: retried 4 times a second, given up after 5 s (a list that never gets ready,
    // e.g. after a game update broke the check, must not cost an exception every frame).
    private static float _deferredSince = -1f, _nextRefreshTry;
    private static OverlandSessionInfo _pendingJoin;

    public static void Bind(ConfigFile cfg)
    {
        const string s = "ServerBrowserPlus";
        PinFavourites = cfg.Bind(s, "PinFavourites", true, "Keep favourite servers at the top of the list.");
        PinModded = cfg.Bind(s, "PinModded", true, "Servers running these mods come right after favourites and friends' servers.");
        HideIncompatible = cfg.Bind(s, "HideIncompatible", false, "Hide servers running a different game version.");
        OnlyBigLobbies = cfg.Bind(s, "OnlyBigLobbies", false, "Only show modded big lobbies (more than 4 player slots).");
        OnlyFreeSlots = cfg.Bind(s, "OnlyFreeSlots", false, "Only show servers that are open and have a free slot.");
        OnlyFavourites = cfg.Bind(s, "OnlyFavourites", false, "Only show favourite servers.");
        SortByAge = cfg.Bind(s, "SortByAge", AgeSort.Off, "Order servers by how long they have been up (after favourites).");
        ShowFavTag = cfg.Bind(s, "ShowFavTag", true, "Tag favourite servers with FAV in the list.");
        ShowBigTag = cfg.Bind(s, "ShowBigTag", true, "Tag big lobbies (more than 4 slots) with BIG and their size.");
        ShowBar = cfg.Bind(s, "ShowBar", true, "Show the PLUS quick bar (filters, favourite, quick join) while the server browser is open.");
        GameLookBar = cfg.Bind(s, "GameLookBar", true,
            "Build the quick bar from the game's own UI (its font, flat rows and key prompts). Off = the older overlay bar.");
        FavouriteKey = cfg.Bind(s, "FavouriteKey", Key.F5, "While the server browser is open: star/unstar the selected server.");
        QuickJoinKey = cfg.Bind(s, "QuickJoinKey", Key.F3, "While the server browser is open: quick join the best server (None = no hotkey).");
        BarX = cfg.Bind(s, "BarX", -1f, "Quick bar position (1080p units, -1 = default).");
        BarY = cfg.Bind(s, "BarY", -1f, "Quick bar position (1080p units, -1 = default).");

        // Any filter/sort change re-filters the open list.
        foreach (var e in new[] { PinFavourites, PinModded, HideIncompatible, OnlyBigLobbies, OnlyFreeSlots, OnlyFavourites, ShowFavTag, ShowBigTag })
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
            catch (Exception ex) { DebugLog.Error("serverbrowser", ex, "BrowserPlus.AdapterAlive"); return false; }
        }
    }

    // ---------------------------------------------------------------- row tags

    private static string RowTags(OverlandSessionInfo info, Fusion.SessionInfo session)
    {
        string tags = "";
        try { tags = AllRegions.RowTag(info, session) ?? ""; } catch (Exception ex) { DebugLog.Error("serverbrowser", ex, "BrowserPlus.RowTags"); tags = ""; }
        try { tags += ExtraTags?.Invoke(info, session) ?? ""; } catch (Exception ex) { DebugLog.Error("serverbrowser", ex, "BrowserPlus.RowTags"); }
        try { tags += ModTypes.RequiredModsJoin.RowTag(session) ?? ""; } catch (Exception ex) { DebugLog.Error("serverbrowser", ex, "BrowserPlus.RowTags"); }
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
        var extra = ExtraKeep;
        if (extra != null && !extra(info)) return false;
        return true;
    }

    private static float _nextPlacementLog;

    /// <summary>At most every 15 s: how many rows the game's own filters left, how many we show, and where each server
    /// running these mods ended up (diagnostics for "my server is not in the list").</summary>
    private static void LogPlacement(int afterGame, List<OverlandSessionInfo> shown)
    {
        if (UnityEngine.Time.unscaledTime < _nextPlacementLog) return;
        _nextPlacementLog = UnityEngine.Time.unscaledTime + 15f;
        var sb = new System.Text.StringBuilder();
        int found = 0;
        for (int i = 0; i < shown.Count; i++)
        {
            var info = shown[i];
            if (info == null || !RunsOurMods(info)) continue;
            found++;
            string name = "";
            try { name = Favourites.DisplayNameOf(info) ?? ""; } catch { }
            if (name.Length > 50) name = name.Substring(0, 50) + "...";
            sb.Append($" #{i + 1} '{name}'");
        }
        string filters = "";
        int big = 0;
        try
        {
            foreach (var info in shown)
                if (info?.FusionSessionInfo != null && info.FusionSessionInfo.MaxPlayers > 4) big++;
            var ad = Adapter;
            if (ad != null)
            {
                string term = ad.currentSearchTerm;
                var f = ad.currentActiveFilters;
                filters = $"; search '{term ?? ""}'" + (f == null ? "" :
                    $", hide full {f.HideFullServers}, hide private {f.HidePrivateServers}, areas {f.AreasFilter?.Count ?? 0}, regions {f.RegionsFilter?.Count ?? 0}");
            }
        }
        catch (Exception e) { filters = "; filters ?(" + e.Message + ")"; }
        ServerBrowserPlugin.Logger.LogInfo($"[Browser+] list shows {shown.Count} rows (the game's own filters left {afterGame}, {big} above 4 slots){filters}; servers running these mods: {found}" + (found > 0 ? " at" + sb : ""));
    }

    /// <summary>The session advertises these mods (the server-mods key or the mod network key).</summary>
    internal static bool RunsOurMods(OverlandSessionInfo info)
    {
        try
        {
            var props = info?.FusionSessionInfo?.Properties;
            return props != null && (props.ContainsKey(Core.Manager.RequiredMods.Key) || props.ContainsKey(Features.Public.ModNet.ModNet.SessionKey));
        }
        catch { return false; }
    }

    /// <summary>Applies our filters and ordering to the adapter's (already game-filtered and sorted) list.</summary>
    private static int _loggedShown = -1, _loggedHidden = -1;

    internal static void Process(SessionInfoListAdapter adapter)
    {
        Adapter = adapter;
        var data = adapter.Data;
        var list = data?.List;
        if (list == null) return;
        int n = list.Count;

        bool pin = PinFavourites.Value && Favourites.Count > 0;
        var age = SortByAge.Value;
        var extraPin = ExtraPin;
        bool filter = AnyFilter || ExtraKeep != null;
        bool modded = PinModded.Value;
        if (!pin && !modded && extraPin == null && age == AgeSort.Off && !filter)
        {
            Shown = n; Hidden = 0;
            return;
        }

        var rows = new List<(OverlandSessionInfo info, int index, bool fav, long first)>(n);
        HashSet<IntPtr> extraPinned = null, moddedPinned = null;
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
            if (extraPin != null)
            {
                bool p2 = false;
                try { p2 = extraPin(info); } catch (Exception ex) { DebugLog.Error("serverbrowser", ex, "BrowserPlus.Process"); }
                if (p2) (extraPinned ??= new HashSet<IntPtr>()).Add(info.Pointer);
            }
            if (modded && RunsOurMods(info)) (moddedPinned ??= new HashSet<IntPtr>()).Add(info.Pointer);
            rows.Add((info, i, fav, first));
        }

        IEnumerable<(OverlandSessionInfo info, int index, bool fav, long first)> ordered = rows;
        if (pin || extraPinned != null || moddedPinned != null || age != AgeSort.Off)
        {
            // LINQ OrderBy is stable: the game's order breaks ties. Favourites, friends, servers running these mods, the rest.
            var o = rows.OrderBy(r => pin && r.fav ? 0
                : extraPinned != null && extraPinned.Contains(r.info.Pointer) ? 1
                : moddedPinned != null && moddedPinned.Contains(r.info.Pointer) ? 2 : 3);
            if (age == AgeSort.NewestFirst) o = o.ThenByDescending(r => r.first);
            else if (age == AgeSort.OldestFirst) o = o.ThenBy(r => r.first);
            ordered = o.ToList();
        }

        var final = ordered as List<(OverlandSessionInfo info, int index, bool fav, long first)> ?? ordered.ToList();
        try { LogPlacement(n, final.Select(r => r.info).ToList()); } catch { }
        Shown = final.Count;
        Hidden = n - final.Count;
        if (Shown != _loggedShown || Hidden != _loggedHidden)
        {
            _loggedShown = Shown;
            _loggedHidden = Hidden;
            DebugLog.Verbose(ServerBrowserPlugin.ModId, () => $"server list processed: {Shown} shown, {Hidden} hidden by filters (pin favourites {pin}, age sort {age})");
        }

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
        if (!BrowserState.Open || EnsureAdapter() == null) { _deferredSince = -1f; return; }
        if (!ListReady)
        {
            // The list view sets itself up a frame or two after opening.
            float now = Time.unscaledTime;
            if (_deferredSince < 0f) _deferredSince = now;
            if (now - _deferredSince > 5f)
            {
                _deferredSince = -1f;
                if (!_loggedNeverReady)
                {
                    _loggedNeverReady = true;
                    ServerBrowserPlugin.Logger.LogWarning("[Browser+] the server list never became ready; skipped a refresh (further ones skipped quietly)");
                }
                return;
            }
            _refreshPending = true;
            _nextRefreshTry = now + 0.25f;
            return;
        }
        _deferredSince = -1f;
        try
        {
            Adapter.ApplyFilters();
            Adapter.Sort();
        }
        catch (Exception e)
        {
            DebugLog.Error("serverbrowser", e, "BrowserPlus.RefreshNow");
            if (_loggedRefreshError) return;
            _loggedRefreshError = true;
            ServerBrowserPlugin.Logger.LogWarning("[Browser+] list refresh failed (further errors suppressed): " + e.Message);
        }
    }

    // ---------------------------------------------------------------- favourites / quick join

    public static OverlandSessionInfo SelectedSession()
    {
        try
        {
            if (Browser != null && Browser.currentlySelectedSession != null) return Browser.currentlySelectedSession;
        }
        catch (Exception ex) { DebugLog.Error("serverbrowser", ex, "BrowserPlus.SelectedSession"); }
        try { return AdapterAlive ? Adapter.currentlySelectedSessionInfo : null; }
        catch (Exception ex) { DebugLog.Error("serverbrowser", ex, "BrowserPlus.SelectedSession"); return null; }
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

    /// <summary>
    /// All sessions the game knows about (before any filtering). With "All regions" on this includes the rooms
    /// remembered from the other regions, unless <paramref name="otherRegions"/> is false (connected region only).
    /// </summary>
    public static List<OverlandSessionInfo> AllSessions(bool otherRegions = true)
    {
        if (otherRegions && AllRegions.Merging) return AllRegions.MergedSessions();
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
        catch (Exception ex) { DebugLog.Error("serverbrowser", ex, "BrowserPlus.AllSessions"); }
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
            try { status = RowStyler.Classify(info, s); } catch (Exception ex) { DebugLog.Error("serverbrowser", ex, "BrowserPlus.BestServer"); continue; }
            if (status != ServerStatus.Public) continue; // private (needs a code), full or other version
            if (OnlyBigLobbies.Value && !IsBig(s)) continue;
            candidates++;
            bool extra = false;
            try { extra = ExtraPin != null && ExtraPin(info); } catch (Exception ex) { DebugLog.Error("serverbrowser", ex, "BrowserPlus.BestServer"); }
            long score = (Favourites.IsFavourite(info) ? 1_000_000L : 0L)
                         + (extra ? 500_000L : 0L)
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
        Join(best);
    }

    /// <summary>Joins a server the same way as clicking its row (next frame, from Update).</summary>
    public static void Join(OverlandSessionInfo info)
    {
        if (!BrowserAlive) { Menu.SetStatus("Blocked: open the server browser (Play online) first"); return; }
        var s = info?.FusionSessionInfo;
        if (s == null) { Menu.SetStatus("Blocked: that server is gone"); return; }
        Menu.SetStatus($"Joining {Favourites.PlainName(Favourites.DisplayNameOf(info))} ({s.PlayerCount}/{s.MaxPlayers})");
        _pendingJoin = info; // joined from Update, not from inside OnGUI
    }

    /// <summary>Whether the list view has finished setting up (sorting before that throws).</summary>
    public static bool ListReady
    {
        get
        {
            try { return AdapterAlive && Adapter.IsInitialized; }
            catch (Exception ex) { DebugLog.Error("serverbrowser", ex, "BrowserPlus.ListReady"); return false; }
        }
    }

    /// <summary>The browser's list adapter (taken from the open browser if no sort has been seen yet).</summary>
    public static SessionInfoListAdapter EnsureAdapter()
    {
        if (AdapterAlive) return Adapter;
        try
        {
            var a = BrowserAlive ? Browser.ui?.ContextUIServerList?.SessionInfoListAdapter : null;
            if (a != null) Adapter = a;
        }
        catch (Exception ex) { DebugLog.Error("serverbrowser", ex, "BrowserPlus.EnsureAdapter"); }
        return AdapterAlive ? Adapter : null;
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
            DebugLog.Error("serverbrowser", e, "BrowserPlus.JoinNow");
            Menu.SetStatus("Failed to join: " + e.Message);
            if (_loggedJoinError) return;
            _loggedJoinError = true;
            ServerBrowserPlugin.Logger.LogWarning("[Browser+] quick join failed: " + e);
        }
    }

    // ---------------------------------------------------------------- per frame

    internal static void Tick()
    {
        if (_pendingJoin != null) JoinNow();
        if (_refreshPending && Time.unscaledTime >= _nextRefreshTry) RefreshNow();
        Favourites.SaveIfDue();

        if (!BrowserState.Open) return;
        var kb = Keyboard.current;
        if (kb == null) return;
        var key = FavouriteKey.Value;
        if (key != Key.None)
        {
            try { if (kb[key].wasPressedThisFrame) ToggleFavourite(SelectedSession()); }
            catch (Exception ex) { DebugLog.Error("serverbrowser", ex, "BrowserPlus.Tick"); /* invalid key in config */ }
        }
        key = QuickJoinKey.Value;
        if (key != Key.None && key != FavouriteKey.Value)
        {
            // Not while a pop-up (filters, join dialog) is on top of the browser.
            try { if (kb[key].wasPressedThisFrame && Browser != null && Browser.isFocused) QuickJoin(); }
            catch (Exception ex) { DebugLog.Error("serverbrowser", ex, "BrowserPlus.Tick"); /* invalid key in config */ }
        }
    }

    internal static void OnSortDone(SessionInfoListAdapter adapter)
    {
        try { Process(adapter); }
        catch (Exception e)
        {
            if (_loggedSortError) return;
            _loggedSortError = true;
            ServerBrowserPlugin.Logger.LogError("[Browser+] filter/sort failed (further errors suppressed): " + e);
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
    [HarmonyPatch(typeof(StateServerBrowser), nameof(StateServerBrowser.Close))]
    private static void Closed() => Favourites.SaveIfDue(force: true);
}
