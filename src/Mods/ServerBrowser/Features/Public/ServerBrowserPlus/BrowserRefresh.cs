using System;
using BepInEx.Configuration;
using HarmonyLib;
using OverTheHillPlus.ServerBrowser;
using UnityEngine;
using UnityEngine.InputSystem;

namespace OverTheHillPlus.Features.Public.ServerBrowserPlus;

/// <summary>
/// Keeps the open server list current and remembers how it was sorted.
///
/// Refresh: Photon pushes list changes to the game by itself (SessionThrottle smooths those), so there is nothing to
/// re-query. What goes stale is everything worked out from the list: the PLUS order and filters, friend matches,
/// ages and tags. Every N seconds this re-runs the adapter's own filter + sort (the path the game uses when a search
/// or filter changes), which rebinds the visible rows. It skips a beat while you scroll, click, type in the search
/// box, have the region list open or the on-screen keyboard up, and never resets the update throttle. "Refresh now"
/// does the same, re-reads Steam friends and lets the next list update from Photon through straight away.
///
/// Memory: the game keeps its own filters (regions, areas, hide full/private) but always opens sorted by name. The
/// last sort picked with the column headers (and, if wanted, the search text) is restored when the browser opens.
/// </summary>
internal static class BrowserRefresh
{
    public static ConfigEntry<bool> AutoRefresh, RememberSort, RememberSearch;
    public static ConfigEntry<float> AutoRefreshSeconds;
    public static ConfigEntry<int> SavedSortCriteria, SavedSortDirection;
    public static ConfigEntry<string> SavedSearch;

    private const float MinSeconds = 5f, MaxSeconds = 120f;
    private const int MaxSearchChars = 64;

    private static float _next, _busyUntil;
    public static float LastRefreshAt { get; private set; } = -1f;
    public static int Refreshes { get; private set; }
    private static bool _loggedRefreshError, _loggedRestoreError, _loggedSaveError;

    public static void Bind(ConfigFile cfg)
    {
        const string s = "BrowserRefresh";
        AutoRefresh = cfg.Bind(s, "AutoRefresh", true,
            "While the server browser is open: re-apply filters, sort, friends and ages to the latest list every few seconds.");
        AutoRefreshSeconds = cfg.Bind(s, "AutoRefreshSeconds", 15f, $"Seconds between automatic refreshes ({MinSeconds:0}-{MaxSeconds:0}).");
        RememberSort = cfg.Bind(s, "RememberSort", true, "Open the server browser with the sort you used last (name, area or players; up or down).");
        RememberSearch = cfg.Bind(s, "RememberSearch", false, "Open the server browser with the last search text still filled in.");
        SavedSortCriteria = cfg.Bind(s, "SavedSortCriteria", -1, "Remembered sort column (0 name, 1 area, 2 players; -1 none). Set by the mod.");
        SavedSortDirection = cfg.Bind(s, "SavedSortDirection", 1, "Remembered sort direction (1 or -1). Set by the mod.");
        SavedSearch = cfg.Bind(s, "SavedSearch", "", "Remembered search text. Set by the mod.");
        RememberSearch.SettingChanged += OnRememberSearchChanged;
    }

    // Turning the search memory off forgets the text, so turning it back on later doesn't bring back an old search.
    private static void OnRememberSearchChanged(object sender, EventArgs args)
    {
        if (!RememberSearch.Value && !string.IsNullOrEmpty(SavedSearch.Value)) SavedSearch.Value = "";
    }

    public static float IntervalSeconds
    {
        get
        {
            float v = AutoRefreshSeconds.Value;
            if (float.IsNaN(v)) v = 15f;
            // Never faster than the list update throttle lets updates through.
            float throttle = 0f;
            try { throttle = ServerBrowserConfig.ThrottleSessionUpdates.Value ? ServerBrowserConfig.SessionUpdateInterval.Value : 0f; } catch (Exception ex) { DebugLog.Error("serverbrowser", ex, "BrowserRefresh.IntervalSeconds"); }
            return Mathf.Clamp(Mathf.Max(v, throttle), MinSeconds, MaxSeconds);
        }
    }

    // ---------------------------------------------------------------- auto refresh

    internal static void Tick()
    {
        if (!BrowserPlus.BrowserAlive)
        {
            _next = 0f;
            return;
        }
        float now = Time.unscaledTime;
        NoteUserActivity(now);
        if (!AutoRefresh.Value) return;
        if (_next <= 0f || _next - now > MaxSeconds) { _next = now + IntervalSeconds; return; }
        if (now < _next) return;
        if (now < _busyUntil || Busy(out _))
        {
            _next = now + 1f; // try again shortly
            return;
        }
        _next = now + IntervalSeconds;
        Refresh(manual: false);
    }

    private static void NoteUserActivity(float now)
    {
        try
        {
            var m = Mouse.current;
            if (m != null && (m.scroll.ReadValue().sqrMagnitude > 0.01f || m.leftButton.isPressed || m.rightButton.isPressed))
                _busyUntil = now + 2.5f;
        }
        catch (Exception ex) { DebugLog.Error("serverbrowser", ex, "BrowserRefresh.NoteUserActivity"); }
    }

    /// <summary>Whether refreshing now could get in the player's way (typing, a dropdown or keyboard open).</summary>
    public static bool Busy(out string why)
    {
        why = null;
        try
        {
            var ui = BrowserPlus.Browser?.ui?.ContextUIServerList;
            if (ui == null) { why = "browser not ready"; return true; }
            if (BrowserPlus.Browser.IsVirtualKeyboardOpen || ui.IsVirtualKeyboardOpen) { why = "on-screen keyboard open"; return true; }
            var dd = ui.RegionSelectionDropdown;
            if (dd != null && dd.IsExpanded) { why = "region list open"; return true; }
            var search = ui.SearchBarInputField;
            if (search != null && search.isFocused) { why = "typing a search"; return true; }
        }
        catch (Exception ex) { DebugLog.Error("serverbrowser", ex, "BrowserRefresh.Busy"); why = "browser not ready"; return true; }
        return false;
    }

    /// <summary>Re-applies filters and sort to the open list. Manual refreshes also re-read friends and let the next update in.</summary>
    public static bool Refresh(bool manual)
    {
        if (!BrowserPlus.BrowserAlive)
        {
            if (manual) Menu.SetStatus("Blocked: open the server browser (Play online) first");
            return false;
        }
        if (Busy(out string why))
        {
            if (manual) Menu.SetStatus("Blocked: " + why);
            return false;
        }
        var adapter = BrowserPlus.EnsureAdapter();
        if (adapter == null || !BrowserPlus.ListReady)
        {
            if (manual) Menu.SetStatus("Blocked: the server list isn't ready yet");
            return false;
        }
        if (manual)
        {
            SessionThrottle.Reset(); // the next list update from Photon applies at once
            SteamFriendsList.RefreshIfDue(force: true);
        }

        SessionThrottle.QuietRefresh = true;
        try
        {
            adapter.ApplyFilters();
            adapter.Sort();
            try { BrowserPlus.Browser?.ui?.ContextUIServerList?.UpdateEmptySetVisibility(); } catch (Exception ex) { DebugLog.Error("serverbrowser", ex, "BrowserRefresh.Refresh"); }
            LastRefreshAt = Time.unscaledTime;
            Refreshes++;
            if (manual) Menu.SetStatus($"Server list refreshed ({BrowserPlus.Shown} shown)");
            return true;
        }
        catch (Exception e)
        {
            DebugLog.Error("serverbrowser", e, "BrowserRefresh.Refresh");
            if (manual) Menu.SetStatus("Refresh failed: " + e.Message);
            if (!_loggedRefreshError)
            {
                _loggedRefreshError = true;
                ServerBrowserPlugin.Logger.LogWarning("[Browser+] list refresh failed (further errors suppressed): " + e.Message);
            }
            return false;
        }
        finally { SessionThrottle.QuietRefresh = false; }
    }

    /// <summary>Seconds until the next automatic refresh (negative when none is planned).</summary>
    public static float SecondsToNext => _next > 0f ? Mathf.Max(0f, _next - Time.unscaledTime) : -1f;

    // ---------------------------------------------------------------- sort / search memory

    public static string SortText(int criteria, int direction)
    {
        string col = criteria switch { 0 => "Name", 1 => "Area", 2 => "Players", _ => null };
        if (col == null) return "game default (name)";
        return col + (direction < 0 ? ", descending" : ", ascending");
    }

    internal static void SaveSort(ContextUIServerList ui)
    {
        if (!RememberSort.Value || ui == null) return;
        try
        {
            int c = (int)ui.serverSortCriteria;
            int d = ui.serverSortDirection;
            if (c < 0 || c > 2 || (d != 1 && d != -1)) return;
            if (SavedSortCriteria.Value != c) SavedSortCriteria.Value = c;
            if (SavedSortDirection.Value != d) SavedSortDirection.Value = d;
        }
        catch (Exception e)
        {
            DebugLog.Error("serverbrowser", e, "BrowserRefresh.SaveSort");
            if (_loggedSaveError) return;
            _loggedSaveError = true;
            ServerBrowserPlugin.Logger.LogWarning("[Browser+] could not remember the sort (further errors suppressed): " + e.Message);
        }
    }

    internal static void SaveSearch(string term)
    {
        if (!RememberSearch.Value) return;
        term ??= "";
        if (term.Length > MaxSearchChars) term = term.Substring(0, MaxSearchChars);
        if (SavedSearch.Value != term) SavedSearch.Value = term;
    }

    /// <summary>
    /// Right after the game set up the browser (sort reset to name, filters loaded): put the remembered sort and
    /// search back. Only fields and labels are set here; the list itself is sorted when its first update arrives
    /// (and once more next frame, in case it is already there).
    /// </summary>
    internal static void Restore(StateServerBrowser browser)
    {
        try
        {
            var ui = browser?.ui?.ContextUIServerList;
            if (ui == null) return;
            var adapter = ui.SessionInfoListAdapter;
            bool changed = false;

            int c = SavedSortCriteria.Value, d = SavedSortDirection.Value;
            if (RememberSort.Value && c >= 0 && c <= 2 && (d == 1 || d == -1) && ((int)ui.serverSortCriteria != c || ui.serverSortDirection != d))
            {
                var crit = (StateServerBrowser.ServerSortCriteria)c;
                ui.serverSortCriteria = crit;
                ui.serverSortDirection = d;
                if (adapter != null) adapter.SetSortCriteria(crit, d);
                ui.UpdateLabels();
                ui.UpdateHeaders();
                changed = true;
            }

            string term = SavedSearch.Value ?? "";
            if (RememberSearch.Value && term.Length > 0)
            {
                if (term.Length > MaxSearchChars) term = term.Substring(0, MaxSearchChars);
                var input = ui.SearchBarInputField;
                if (input != null) input.SetTextWithoutNotify(term);
                // What SetNameFilter stores, without its immediate filter + sort (the list may not be set up yet).
                if (adapter != null) adapter.currentSearchTerm = term.ToLowerInvariant();
                try { ui.UpdateActiveFiltersLabel(); } catch (Exception ex) { DebugLog.Error("serverbrowser", ex, "BrowserRefresh.Restore"); }
                changed = true;
            }

            if (changed) BrowserPlus.RequestRefresh();
        }
        catch (Exception e)
        {
            DebugLog.Error("serverbrowser", e, "BrowserRefresh.Restore");
            if (_loggedRestoreError) return;
            _loggedRestoreError = true;
            ServerBrowserPlugin.Logger.LogWarning("[Browser+] could not restore the last sort/search (further errors suppressed): " + e.Message);
        }
    }
}

[HarmonyPatch]
internal static class BrowserRefreshPatches
{
    // Every column header click and the sort shortcuts go through SortBy.
    [HarmonyPostfix]
    [HarmonyPatch(typeof(ContextUIServerList), nameof(ContextUIServerList.SortBy))]
    private static void Sorted(ContextUIServerList __instance) => BrowserRefresh.SaveSort(__instance);

    [HarmonyPostfix]
    [HarmonyPatch(typeof(SessionInfoListAdapter), nameof(SessionInfoListAdapter.SetNameFilter))]
    private static void Searched(string searchTerm)
    {
        try { BrowserRefresh.SaveSearch(searchTerm); } catch (Exception ex) { DebugLog.Error("serverbrowser", ex, "BrowserRefreshPatches.Searched"); }
    }

    // StateServerBrowser.Open calls InitContent, where the game resets the sort and loads its saved filters.
    [HarmonyPostfix]
    [HarmonyPatch(typeof(StateServerBrowser), nameof(StateServerBrowser.InitContent))]
    private static void ContentReady(StateServerBrowser __instance) => BrowserRefresh.Restore(__instance);
}
