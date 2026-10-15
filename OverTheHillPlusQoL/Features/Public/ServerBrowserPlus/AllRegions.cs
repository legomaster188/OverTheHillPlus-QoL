using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Configuration;
using FunCore.Managers;
using HarmonyLib;
using OverTheHillPlus.Features.Public.Rejoin;
using OverTheHillPlus.ServerBrowser;
using OverTheHillPlus.UI;
using TMPro;
using UnityEngine;

namespace OverTheHillPlus.Features.Public.ServerBrowserPlus;

/// <summary>"All regions" in the server browser: menu card in PLUS > Servers.</summary>
internal sealed class AllRegionsFeature : IFeature
{
    public void Register()
    {
        AllRegions.Bind(ModConfig.File);
        Registry.Section(Registry.PlusMode, "Servers", DrawMenu, -9);
        Registry.OnTick(AllRegions.Tick);
        Registry.OnGui(DrawJoinNotice);
    }

    private static GUIStyle _notice;

    /// <summary>A small notice over the browser while a join waits for its region (a row click otherwise looks ignored).</summary>
    private static void DrawJoinNotice()
    {
        string text = AllRegions.JoinNotice;
        if (text == null || Time.frameCount - Menu.DrawnFrame <= 2) return;
        Theme.Ensure();
        _notice ??= new GUIStyle(Theme.Label) { alignment = TextAnchor.MiddleCenter, fontSize = 15, richText = true };
        var prev = GUI.matrix;
        try
        {
            float scale = Mathf.Max(1f, Screen.height / 1080f);
            GUI.matrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, new Vector3(scale, scale, 1f));
            float sw = Screen.width / scale, sh = Screen.height / scale;
            var r = new Rect((sw - 560f) / 2f, sh - 120f, 560f, 40f);
            Theme.Fill(r, new Color(Theme.Bg.r, Theme.Bg.g, Theme.Bg.b, 0.92f), 10);
            GUI.Label(r, text, _notice);
        }
        finally { GUI.matrix = prev; }
    }

    private static void DrawMenu()
    {
        Menu.Card("All regions", "Servers from every region in one list", () =>
        {
            Menu.Banner(AllRegions.StatusText(out bool good), good ? Theme.Good : Theme.Info);
            Menu.Switch(AllRegions.Enabled, "Show servers from all regions",
                "Also \"" + AllRegions.OptionLabel + "\" at the bottom of the game's region list", true);
            bool on = AllRegions.Enabled.Value;
            Menu.Switch(AllRegions.ShowRegionTag, "Region tag on each server", "e.g. EU or USW next to the name", on);
            Menu.SliderRow(AllRegions.HopSeconds, "Seconds per region", AllRegions.MinHop, 30f, "0", " s", on);
            Menu.Switch(AllRegions.AddToDropdown, "Add it to the game's region list", null, true);
            string home = string.IsNullOrEmpty(AllRegions.HomeRegion.Value) ? "your region" : AllRegions.HomeRegion.Value.ToUpperInvariant();
            Menu.Small("Photon keeps one server list per region, so the browser visits the regions one after another and " +
                       "keeps what it saw for a couple of minutes. Joining a server in another region switches to that region first. " +
                       $"Servers you create are hosted in {home}, and the browser goes back there when you turn this off.");
        });
    }
}

/// <summary>
/// Shows the servers of every Photon region at once.
///
/// Photon lobbies are per region (see <see cref="LobbyRegion"/>), so while the browser is open with "All regions"
/// picked, the lobby visits the regions the game's dropdown offers one after another (a few seconds each, slower
/// once every region has been seen) and every session list that arrives is kept with the region it came from. The
/// game builds its list in SessionInfoListAdapter.ApplyFilters from OverlandNetworkRunner.SessionList: for the length
/// of that call the runner's list is swapped for the merged one, so the game's own search, hide full/private and
/// area filters, sorting and the PLUS filters all apply to every region. Lists older than a couple of minutes are
/// dropped. Joining a server of another region (row click, submit, quick join) switches the lobby there first, waits
/// for the room to show up in that region's list, then clicks it the normal way so the game does its own checks.
/// </summary>
internal static class AllRegions
{
    public static ConfigEntry<bool> Enabled, ShowRegionTag, AddToDropdown;
    public static ConfigEntry<float> HopSeconds;
    public static ConfigEntry<string> HomeRegion;

    public const string OptionLabel = "All regions";
    public const float MinHop = 5f;
    private const float MaxHop = 60f, MinCacheSeconds = 150f, BadRegionSeconds = 600f, JoinTimeout = 45f, HoldAfterJoin = 30f;

    private sealed class Cache
    {
        public string Region;
        public List<OverlandSessionInfo> Sessions;
        public float At;
    }

    private static readonly Dictionary<string, Cache> Caches = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, float> BadUntil = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, string> RegionOfName = new();
    private static readonly List<OverlandSessionInfo> Merged = new();
    private static Il2CppSystem.Collections.Generic.List<OverlandSessionInfo> _mergedIl2Cpp;
    private static int _version, _mergedVersion = -1, _il2CppVersion = -1;
    private static float _mergedAt = -100f;

    // Visiting the regions.
    private static string _hopRegion, _hopTarget, _pausedWhy;
    private static float _regionSince, _hopStartedAt, _holdUntil, _nextStep;
    private static bool _hopPending;

    // A join waiting for its region; a region picked while a switch was still running; going back home.
    private static string _joinName, _joinRegion, _joinLabel, _deferredPick;
    private static float _joinDeadline, _joinNextSwitch, _homeNextSwitch, _restoreNextSwitch;
    private static bool _restoreHome, _leavingByPick;
    private static int _restoreTries;

    // The runner's own list while ApplyFilters runs on the merged one.
    private static OverlandNetworkRunner _swapOwner;
    private static Il2CppSystem.Collections.Generic.List<OverlandSessionInfo> _swapOriginal;
    private static bool _swapped;

    private static bool _loggedCacheError, _loggedSwapError, _loggedDropdownError, _loggedTickError, _loggedJoinError, _loggedEmptyError;
    private static readonly HashSet<string> LoggedBad = new(StringComparer.OrdinalIgnoreCase);

    public static void Bind(ConfigFile cfg)
    {
        const string s = "ServerBrowserAllRegions";
        Enabled = cfg.Bind(s, "AllRegions", false,
            "Show the servers of every region in the server browser at once (it visits the regions one after another while open).");
        ShowRegionTag = cfg.Bind(s, "ShowRegionTag", true, "With all regions shown: tag each server with its region (EU, USW...).");
        AddToDropdown = cfg.Bind(s, "AddToRegionList", true, $"Add \"{OptionLabel}\" to the game's region list in the server browser.");
        HopSeconds = cfg.Bind(s, "SecondsPerRegion", 6f,
            $"Seconds spent on each region before moving on ({MinHop:0}-{MaxHop:0}); twice that once every region has been seen.");
        HomeRegion = cfg.Bind(s, "HomeRegion", "",
            "Your own region: servers you create are hosted there and the browser goes back to it when all regions is turned off. Set by the mod.");
        Enabled.SettingChanged += OnEnabledChanged;
        ShowRegionTag.SettingChanged += OnTagChanged;
        AddToDropdown.SettingChanged += OnDropdownOptionChanged;
    }

    private static void OnTagChanged(object sender, EventArgs args) => BrowserPlus.RequestRefresh();
    private static void OnDropdownOptionChanged(object sender, EventArgs args) => RefreshDropdown();

    /// <summary>The browser is open and shows every region (merged list, region tags, cross-region joins).</summary>
    public static bool Merging => Enabled != null && Enabled.Value && BrowserPlus.BrowserAlive;

    private static float Hop
    {
        get
        {
            float v = HopSeconds.Value;
            return float.IsNaN(v) ? 6f : Mathf.Clamp(v, MinHop, MaxHop);
        }
    }

    // ---------------------------------------------------------------- switching on and off

    private static void OnEnabledChanged(object sender, EventArgs args)
    {
        if (Enabled.Value)
        {
            if (string.IsNullOrEmpty(HomeRegion.Value) && LobbyRegion.InLobby && LobbyRegion.Connected != null)
                HomeRegion.Value = LobbyRegion.Connected;
            _restoreHome = false;
            _deferredPick = null;
            _hopRegion = null; // counts the current region as just reached
            _hopPending = false;
            Plugin.Logger.LogInfo($"[Browser+] all regions on (home region {HomeRegion.Value})");
        }
        else
        {
            CancelJoin(null);
            // Picking a region in the game's list switches there by itself; turning it off anywhere else goes home.
            if (!_leavingByPick)
            {
                _restoreHome = !string.IsNullOrEmpty(HomeRegion.Value);
                _restoreTries = 0;
                _restoreNextSwitch = 0f;
            }
            Caches.Clear();
            RegionOfName.Clear();
            Merged.Clear();
            _version++;
            Plugin.Logger.LogInfo("[Browser+] all regions off");
        }
        BrowserPlus.RequestRefresh();
        if (!_leavingByPick) RefreshDropdown();
    }

    // ---------------------------------------------------------------- session lists per region

    /// <summary>Every session list Photon sends (before the update throttle), kept with its region.</summary>
    internal static void OnRawList(Fusion.NetworkRunner runner, Il2CppSystem.Collections.Generic.List<Fusion.SessionInfo> list)
    {
        if (Enabled == null || !Enabled.Value || list == null) return;
        try
        {
            string region = null;
            try
            {
                var lobby = runner == null ? null : runner.LobbyInfo;
                if (lobby != null && lobby.IsValid) region = lobby.Region;
            }
            catch { }
            if (string.IsNullOrEmpty(region)) region = LobbyRegion.Connected;
            if (string.IsNullOrEmpty(region)) return;

            // What the game does with the list (OnSessionListUpdated): valid sessions, each wrapped once.
            var sessions = new List<OverlandSessionInfo>(list.Count);
            for (int i = 0; i < list.Count; i++)
            {
                var s = list[i];
                if (s == null) continue;
                try
                {
                    if (OverlandSessionInfo.IsSessionValid(s)) sessions.Add(new OverlandSessionInfo(s));
                }
                catch { /* skip an odd entry */ }
            }
            Store(region, sessions);
        }
        catch (Exception e)
        {
            if (_loggedCacheError) return;
            _loggedCacheError = true;
            Plugin.Logger.LogWarning("[Browser+] could not keep a region's server list (further errors suppressed): " + e.Message);
        }
    }

    private static void Store(string region, List<OverlandSessionInfo> sessions)
    {
        Caches[region] = new Cache { Region = region, Sessions = sessions, At = Time.unscaledTime };
        BadUntil.Remove(region);
        _version++;
    }

    /// <summary>Right after switching on: the connected region's list is already there, keep it straight away.</summary>
    private static void SeedFromLive()
    {
        string c = LobbyRegion.Connected;
        if (c == null || Caches.ContainsKey(c) || LobbyRegion.Switching) return;
        var live = BrowserPlus.AllSessions(otherRegions: false);
        if (live.Count > 0) Store(c, live);
    }

    private static float CacheSeconds(int regions) => Mathf.Max(MinCacheSeconds, regions * Mathf.Max(Hop * 2f, 10f) * 1.5f + 30f);

    private static void EnsureMerged()
    {
        float now = Time.unscaledTime;
        if (_mergedVersion == _version && now - _mergedAt < 5f) return;
        _mergedAt = now;

        float maxAge = CacheSeconds(Mathf.Max(1, LobbyRegion.Available().Count));
        List<string> old = null;
        foreach (var kv in Caches)
            if (now - kv.Value.At > maxAge) (old ??= new List<string>()).Add(kv.Key);
        if (old != null)
        {
            foreach (var k in old) Caches.Remove(k);
            _version++;
        }
        _mergedVersion = _version;

        Merged.Clear();
        RegionOfName.Clear();
        // Freshest region first: a room that shows up twice keeps its latest data.
        foreach (var cache in Caches.Values.OrderByDescending(c => c.At))
        {
            foreach (var info in cache.Sessions)
            {
                string name = null;
                try { name = info?.FusionSessionInfo?.Name; } catch { }
                if (string.IsNullOrEmpty(name) || RegionOfName.ContainsKey(name)) continue;
                RegionOfName[name] = cache.Region;
                Merged.Add(info);
            }
        }
    }

    /// <summary>Every remembered session of every region (a copy).</summary>
    public static List<OverlandSessionInfo> MergedSessions()
    {
        EnsureMerged();
        return new List<OverlandSessionInfo>(Merged);
    }

    private static Il2CppSystem.Collections.Generic.List<OverlandSessionInfo> MergedForGame()
    {
        EnsureMerged();
        if (_mergedIl2Cpp == null || _il2CppVersion != _mergedVersion)
        {
            var list = new Il2CppSystem.Collections.Generic.List<OverlandSessionInfo>(Merged.Count);
            foreach (var info in Merged) list.Add(info);
            _mergedIl2Cpp = list;
            _il2CppVersion = _mergedVersion;
        }
        return _mergedIl2Cpp;
    }

    public static string RegionOf(string sessionName)
    {
        if (string.IsNullOrEmpty(sessionName)) return null;
        EnsureMerged();
        return RegionOfName.TryGetValue(sessionName, out var r) ? r : null;
    }

    internal static string RowTag(OverlandSessionInfo info, Fusion.SessionInfo session)
    {
        if (!Merging || !ShowRegionTag.Value || session == null) return null;
        string r = RegionOf(session.Name);
        return r == null ? null : $"<color=#9DB8FF><b>{r.ToUpperInvariant()}</b></color>  ";
    }

    // ---------------------------------------------------------------- the game's list build

    internal static void SwapIn()
    {
        SwapOut(); // a swap left over from a call that threw
        if (!Merging) return;
        try
        {
            var o = OverlandNetworkRunner.Instance;
            if (o == null) return;
            var merged = MergedForGame();
            if (merged == null) return;
            _swapOwner = o;
            _swapOriginal = o._SessionList_k__BackingField;
            o._SessionList_k__BackingField = merged;
            _swapped = true;
        }
        catch (Exception e)
        {
            SwapOut();
            if (_loggedSwapError) return;
            _loggedSwapError = true;
            Plugin.Logger.LogWarning("[Browser+] could not show all regions (further errors suppressed): " + e.Message);
        }
    }

    internal static void SwapOut()
    {
        if (!_swapped) return;
        _swapped = false;
        var o = _swapOwner;
        var original = _swapOriginal;
        _swapOwner = null;
        _swapOriginal = null;
        try { if (o != null) o._SessionList_k__BackingField = original; }
        catch (Exception e) { Plugin.Logger.LogError("[Browser+] could not put the server list back: " + e.Message); }
    }

    /// <summary>The game shows "no servers" / "connecting" from the connected region alone: not over a full merged list.</summary>
    internal static void HideEmptyOverRows(ContextUIServerList ui)
    {
        if (!Merging || ui == null) return;
        try
        {
            var data = ui.SessionInfoListAdapter == null ? null : ui.SessionInfoListAdapter.Data;
            if (data == null || data.Count == 0) return;
            if (ui.ListEmptySet != null) ui.ListEmptySet.gameObject.SetActive(false);
            if (ui.ListConnectingToLobbyIndicator != null) ui.ListConnectingToLobbyIndicator.SetActive(false);
        }
        catch (Exception e)
        {
            if (_loggedEmptyError) return;
            _loggedEmptyError = true;
            Plugin.Logger.LogWarning("[Browser+] empty-list notice fix failed (further errors suppressed): " + e.Message);
        }
    }

    internal static void OnLobbyFailed(ContextUIServerList ui, string region)
    {
        LobbyRegion.NoteFailed(region);
        if (Enabled == null || !Enabled.Value || string.IsNullOrEmpty(region)) return;
        BadUntil[region] = Time.unscaledTime + BadRegionSeconds;
        Caches.Remove(region);
        _version++;
        if (LoggedBad.Add(region)) Plugin.Logger.LogInfo($"[Browser+] all regions: could not connect to {region}, skipping it for a while");
        HideEmptyOverRows(ui);
    }

    // ---------------------------------------------------------------- the game's region list

    private static int AvailableCount()
    {
        try
        {
            var list = OverlandNetworkRunner.Instance == null ? null : OverlandNetworkRunner.Instance.AvailableServerRegions;
            return list == null ? 0 : list.Count;
        }
        catch { return 0; }
    }

    /// <summary>After the game filled its region list: add "All regions" at the end (and show it picked when on).</summary>
    internal static void DecorateDropdown(ContextUIServerList ui)
    {
        if (AddToDropdown == null || !AddToDropdown.Value || ui == null) return;
        try
        {
            var dd = ui.RegionSelectionDropdown;
            if (dd == null) return;
            int count = AvailableCount();
            var options = dd.options;
            if (count < 2 || options == null || options.Count != count) return;
            options.Add(new TMP_Dropdown.OptionData(OptionLabel));
            if (Enabled.Value) dd.SetValueWithoutNotify(count);
            dd.RefreshShownValue();
        }
        catch (Exception e)
        {
            if (_loggedDropdownError) return;
            _loggedDropdownError = true;
            Plugin.Logger.LogWarning("[Browser+] could not add all regions to the region list (further errors suppressed): " + e.Message);
        }
    }

    /// <summary>Has the game rebuild its region list (and so ours) to match the current state.</summary>
    private static void RefreshDropdown()
    {
        try
        {
            if (!BrowserPlus.BrowserAlive) return;
            var ui = BrowserPlus.Browser.ui?.ContextUIServerList;
            var dd = ui == null ? null : ui.RegionSelectionDropdown;
            if (dd == null || dd.IsExpanded) return;
            string c = LobbyRegion.Connected;
            if (c == null) return;
            ui.InitRegionSelectionDropdown(c);
        }
        catch { /* cosmetic: the next list update rebuilds it anyway */ }
    }

    /// <summary>A pick in the game's region list. False skips the game's handler.</summary>
    internal static bool OnRegionPicked(int optionIndex)
    {
        try
        {
            int count = AvailableCount();
            if (count <= 0) return true;
            if (optionIndex >= count)
            {
                if (!Enabled.Value)
                {
                    if (LobbyRegion.InLobby && LobbyRegion.Connected != null) HomeRegion.Value = LobbyRegion.Connected;
                    Enabled.Value = true;
                    Menu.SetStatus("Showing servers from all regions");
                }
                return false; // not a region: nothing for the game to switch to
            }

            string code = OverlandNetworkRunner.Instance.AvailableServerRegions[optionIndex].RegionCode;
            _deferredPick = null;
            _restoreHome = false;
            if (Enabled.Value)
            {
                _leavingByPick = true;
                try { Enabled.Value = false; }
                finally { _leavingByPick = false; }
            }
            if (!string.IsNullOrEmpty(code)) HomeRegion.Value = code;
            if (LobbyRegion.Switching)
            {
                // Our own switch is still connecting: two at once would race. Switch once it is done.
                _deferredPick = code;
                return false;
            }
            return true;
        }
        catch { return true; }
    }

    // ---------------------------------------------------------------- joining across regions

    /// <summary>Before the game joins a session. False = it is in another region: switch there first, join after.</summary>
    internal static bool OnJoinSession(OverlandSessionInfo info, bool joiningByCode)
    {
        try
        {
            if (Enabled == null || !Enabled.Value || joiningByCode || info == null) { Hold(); return true; }
            string name = info.FusionSessionInfo?.Name;
            string region = RegionOf(name);
            string connected = LobbyRegion.Connected;
            if (region == null || (LobbyRegion.Same(region, connected) && !LobbyRegion.Switching)) { Hold(); return true; }

            _joinName = name;
            _joinRegion = region;
            _joinLabel = Favourites.PlainName(Favourites.DisplayNameOf(info));
            if (string.IsNullOrEmpty(_joinLabel)) _joinLabel = "that server";
            _joinDeadline = Time.unscaledTime + JoinTimeout;
            _joinNextSwitch = 0f;
            Menu.SetStatus($"Switching to {region.ToUpperInvariant()} to join {_joinLabel}");
            Plugin.Logger.LogInfo($"[Browser+] joining '{_joinLabel}' in region {region}: switching the server list there first");
            return false;
        }
        catch { return true; }
    }

    /// <summary>A join or create is starting: no region visits for a while (switching would pull the runner away).</summary>
    private static void Hold() => _holdUntil = Time.unscaledTime + HoldAfterJoin;

    /// <summary>What a join waiting for its region is doing (null when none is).</summary>
    public static string JoinNotice
    {
        get
        {
            if (_joinName == null || !BrowserPlus.BrowserAlive) return null;
            string where = _joinRegion.ToUpperInvariant();
            return LobbyRegion.Same(LobbyRegion.Connected, _joinRegion) && !LobbyRegion.Switching
                ? $"Looking for {_joinLabel} in {where}..."
                : $"Switching to {where} to join {_joinLabel}...";
        }
    }

    private static void CancelJoin(string why)
    {
        if (_joinName == null) return;
        _joinName = null;
        if (why != null) Menu.SetStatus(why);
    }

    private static void StepJoin(float now)
    {
        if (_joinName == null) return;
        if (!BrowserPlus.BrowserAlive) { CancelJoin(null); return; }
        if (AnySession()) { _joinName = null; return; }
        if (now >= _joinDeadline)
        {
            Plugin.Logger.LogInfo($"[Browser+] '{_joinLabel}' was not found in region {_joinRegion}");
            CancelJoin($"Join failed: {_joinLabel} was not found in {_joinRegion.ToUpperInvariant()}");
            return;
        }
        if (LobbyRegion.Switching || !LobbyRegion.InLobby) return;
        if (!LobbyRegion.Ensure(_joinRegion, ref _joinNextSwitch, 12f, "[Browser+]")) return;
        if (!TopIsBrowser()) return; // a notice on top: wait for it to go

        OverlandSessionInfo found = null;
        foreach (var info in BrowserPlus.AllSessions(otherRegions: false))
        {
            string n = null;
            try { n = info.FusionSessionInfo?.Name; } catch { }
            if (n == _joinName) { found = info; break; }
        }
        if (found == null) return; // its list is on the way

        string label = _joinLabel;
        _joinName = null;
        try
        {
            Hold();
            BrowserPlus.Browser.SessionClicked(found); // as if clicked: the game does its own checks and pop-ups
            Menu.SetStatus($"Joining {label}");
        }
        catch (Exception e)
        {
            Menu.SetStatus("Failed to join: " + e.Message);
            if (_loggedJoinError) return;
            _loggedJoinError = true;
            Plugin.Logger.LogWarning("[Browser+] cross-region join failed: " + e);
        }
    }

    /// <summary>Before the game creates a room: with all regions shown, host in the home region, not wherever the list is.</summary>
    internal static bool OnCreateRoom()
    {
        try
        {
            if (Enabled == null || !Enabled.Value) { Hold(); return true; }
            string home = HomeRegion.Value;
            if (string.IsNullOrEmpty(home)) { Hold(); return true; }
            string c = LobbyRegion.Connected;
            if (c != null && LobbyRegion.Same(c, home) && !LobbyRegion.Switching) { Hold(); return true; }
            if (!LobbyRegion.Switching && LobbyRegion.InLobby) LobbyRegion.Switch(home, "[Browser+]");
            Menu.SetStatus($"Going back to your region ({home.ToUpperInvariant()}) to host there: press Create again in a moment");
            return false;
        }
        catch { return true; }
    }

    // ---------------------------------------------------------------- per frame

    internal static void Tick()
    {
        if (Enabled == null) return;
        SwapOut(); // only ever swapped for the length of ApplyFilters
        float now = Time.unscaledTime;
        if (now < _nextStep) return;
        _nextStep = now + 0.25f;
        try { Step(now); }
        catch (Exception e)
        {
            if (_loggedTickError) return;
            _loggedTickError = true;
            Plugin.Logger.LogError("[Browser+] all regions tick failed (further errors suppressed): " + e);
        }
    }

    private static void Step(float now)
    {
        if (!BrowserPlus.BrowserAlive)
        {
            _hopPending = false;
            _hopRegion = null;
            _pausedWhy = null;
            return;
        }

        if (_deferredPick != null)
        {
            if (LobbyRegion.Switching || AnySession()) return;
            string pick = _deferredPick;
            _deferredPick = null;
            if (!LobbyRegion.Same(pick, LobbyRegion.Connected)) LobbyRegion.Switch(pick, "[Browser+]");
            return;
        }

        if (!Enabled.Value)
        {
            StepRestore(now);
            if (!_restoreHome && LobbyRegion.InLobby && !LobbyRegion.Switching)
            {
                // Follow the game's own region while off, so "home" is whatever the player last used.
                string c = LobbyRegion.Connected;
                if (c != null && !LobbyRegion.Same(c, HomeRegion.Value)) HomeRegion.Value = c;
            }
            return;
        }

        StepJoin(now);
        if (CreateTabShown())
        {
            StepHome();
            _pausedWhy = "the create tab is open";
            return;
        }
        StepHop(now);
    }

    private static void StepRestore(float now)
    {
        if (!_restoreHome) return;
        string home = HomeRegion.Value;
        if (string.IsNullOrEmpty(home)) { _restoreHome = false; return; }
        if (AnySession() || RejoinCore.Active || LobbyRegion.Switching || !LobbyRegion.InLobby) return;
        if (LobbyRegion.Same(LobbyRegion.Connected, home)) { _restoreHome = false; return; }
        if (now < _restoreNextSwitch) return;
        if (_restoreTries >= 3)
        {
            _restoreHome = false;
            Plugin.Logger.LogInfo($"[Browser+] could not go back to region {home}; staying in {LobbyRegion.Connected}");
            return;
        }
        _restoreTries++;
        _restoreNextSwitch = now + 15f;
        LobbyRegion.Switch(home, "[Browser+]");
    }

    private static void StepHome()
    {
        string home = HomeRegion.Value;
        if (string.IsNullOrEmpty(home) || AnySession() || RejoinCore.Active || _joinName != null || !TopIsBrowser()) return;
        if (!LobbyRegion.InLobby) return;
        LobbyRegion.Ensure(home, ref _homeNextSwitch, 15f, "[Browser+]");
    }

    private static void StepHop(float now)
    {
        if (!CanHop(now, out _pausedWhy)) return;

        if (LobbyRegion.Switching) return;
        if (!LobbyRegion.InLobby)
        {
            // Our last visit never connected (the game reported it, or it timed out): move on to the next region.
            bool failed = _hopTarget != null && BadUntil.TryGetValue(_hopTarget, out var until) && until > now;
            if (_hopPending && _hopTarget != null && (failed || now - _hopStartedAt > LobbyRegion.SwitchTimeout))
            {
                if (!failed) BadUntil[_hopTarget] = now + BadRegionSeconds;
                _hopPending = false;
                HopTo(PickNext(LobbyRegion.Connected), now);
            }
            return;
        }
        _hopPending = false;

        string c = LobbyRegion.Connected;
        if (c == null) return;
        if (!LobbyRegion.Same(c, _hopRegion))
        {
            _hopRegion = c;
            _regionSince = now;
        }
        SeedFromLive();

        // Its list can land a moment before this notices the region changed (steps are 0.25 s apart).
        bool listIn = Caches.TryGetValue(c, out var cache) && cache.At >= _regionSince - 3f;
        float dwell = SeenEverything() ? Mathf.Max(Hop * 2f, 10f) : Hop;
        if (now < _regionSince + dwell) return;
        if (!listIn && now < _regionSince + dwell + 6f) return; // give its list a moment longer to arrive
        HopTo(PickNext(c), now);
    }

    private static void HopTo(string region, float now)
    {
        if (region == null) return;
        if (!LobbyRegion.Switch(region, null)) return;
        _hopTarget = region;
        _hopStartedAt = now;
        _hopPending = true;
    }

    private static List<string> UsableRegions()
    {
        float now = Time.unscaledTime;
        var regions = LobbyRegion.Available();
        regions.RemoveAll(r => BadUntil.TryGetValue(r, out var until) && until > now);
        return regions;
    }

    private static bool SeenEverything()
    {
        foreach (var r in UsableRegions())
            if (!Caches.ContainsKey(r)) return false;
        return true;
    }

    /// <summary>The region whose list is oldest (never seen first), in the dropdown's order after the current one.</summary>
    private static string PickNext(string current)
    {
        var regions = UsableRegions();
        if (regions.Count == 0) return null;
        int start = Math.Max(0, regions.FindIndex(r => LobbyRegion.Same(r, current)));
        string best = null;
        float bestAt = float.MaxValue;
        for (int k = 1; k <= regions.Count; k++)
        {
            string r = regions[(start + k) % regions.Count];
            if (LobbyRegion.Same(r, current)) continue;
            float at = Caches.TryGetValue(r, out var cache) ? cache.At : float.MinValue;
            if (at < bestAt) { bestAt = at; best = r; }
        }
        return best;
    }

    private static bool CanHop(float now, out string why)
    {
        why = null;
        if (now < _holdUntil) why = "joining";
        else if (_joinName != null) why = $"switching to {_joinRegion.ToUpperInvariant()} to join {_joinLabel}";
        else if (RejoinCore.Active) why = "a rejoin is running";
        else if (AnySession()) why = "in a session";
        else if (!TopIsBrowser()) why = "another screen is open";
        else if (!ListTabShown()) why = "the server list tab is not shown";
        else if (BrowserRefresh.Busy(out string busy)) why = busy;
        return why == null;
    }

    // ---------------------------------------------------------------- game state helpers

    private static bool TopIsBrowser()
    {
        try
        {
            var top = HybridSingleton<GameStateManager>.Instance?.PeekState();
            return top != null && top.TryCast<StateServerBrowser>() != null;
        }
        catch { return false; }
    }

    private static bool ListTabShown()
    {
        try
        {
            var list = BrowserPlus.Browser?.ui?.ContextUIServerList;
            return list != null && list.gameObject.activeInHierarchy;
        }
        catch { return false; }
    }

    private static bool CreateTabShown()
    {
        try
        {
            var create = BrowserPlus.Browser?.ui?.ContextUICreateServer;
            return create != null && create.gameObject.activeInHierarchy;
        }
        catch { return false; }
    }

    /// <summary>A multiplayer session is running (hosting or joined); single-player counts too.</summary>
    private static bool AnySession()
    {
        try
        {
            var overland = OverlandNetworkRunner.Instance;
            var runner = overland == null ? null : overland.NetworkRunner;
            return runner != null && runner.IsRunning && (runner.IsClient || runner.IsServer);
        }
        catch { return false; }
    }

    // ---------------------------------------------------------------- menu

    public static string StatusText(out bool good)
    {
        good = false;
        string home = string.IsNullOrEmpty(HomeRegion.Value) ? "?" : HomeRegion.Value.ToUpperInvariant();
        if (!Enabled.Value)
            return _restoreHome ? $"Off · going back to your region {home}" : $"Off · your region: {home}";
        if (!BrowserPlus.BrowserAlive) return $"On · open the server browser (Play online) · your region: {home}";

        good = true;
        EnsureMerged();
        int total = LobbyRegion.Available().Count;
        int seen = Caches.Count;
        string c = LobbyRegion.Connected;
        string where = LobbyRegion.Switching && LobbyRegion.Target != null
            ? $"switching to {LobbyRegion.Target.ToUpperInvariant()}"
            : c != null ? $"now in {c.ToUpperInvariant()}" : "connecting";
        string text = $"{Merged.Count} servers from {seen} of {total} regions · {where}";
        if (_pausedWhy != null) text += $" · paused: {_pausedWhy}";
        return text;
    }
}

// One patch class per target: a target missing after a game update only disables its own part.

[HarmonyPatch(typeof(OverlandNetworkRunner), nameof(OverlandNetworkRunner.Fusion_INetworkRunnerCallbacks_OnSessionListUpdated))]
internal static class AllRegionsListPatch
{
    // First, so it sees every list, also the ones the update throttle holds back or drops.
    [HarmonyPrefix]
    [HarmonyPriority(Priority.First)]
    private static void Prefix(Fusion.NetworkRunner runner, Il2CppSystem.Collections.Generic.List<Fusion.SessionInfo> sessionList) =>
        AllRegions.OnRawList(runner, sessionList);
}

[HarmonyPatch(typeof(SessionInfoListAdapter), nameof(SessionInfoListAdapter.ApplyFilters))]
internal static class AllRegionsFilterPatch
{
    private static void Prefix() => AllRegions.SwapIn();

    // If the game's method ever throws (no postfix), the next SwapIn or the next tick puts the list back instead.
    private static void Postfix() => AllRegions.SwapOut();
}

[HarmonyPatch]
internal static class AllRegionsDropdownPatches
{
    [HarmonyPostfix]
    [HarmonyPatch(typeof(ContextUIServerList), nameof(ContextUIServerList.InitRegionSelectionDropdown))]
    private static void AfterInit(ContextUIServerList __instance) => AllRegions.DecorateDropdown(__instance);
}

[HarmonyPatch(typeof(ContextUIServerList), nameof(ContextUIServerList.ServerRegionOnChanged))]
internal static class AllRegionsPickPatch
{
    [HarmonyPriority(Priority.First)]
    private static bool Prefix(int optionIndex) => AllRegions.OnRegionPicked(optionIndex);
}

[HarmonyPatch(typeof(StateServerBrowser), nameof(StateServerBrowser.JoinSession))]
internal static class AllRegionsJoinPatch
{
    [HarmonyPriority(Priority.First)]
    private static bool Prefix(OverlandSessionInfo sessionToJoin, bool joiningByCode) =>
        AllRegions.OnJoinSession(sessionToJoin, joiningByCode);
}

[HarmonyPatch(typeof(StateServerBrowser), nameof(StateServerBrowser.CreateRoom))]
internal static class AllRegionsCreatePatch
{
    private static bool Prefix() => AllRegions.OnCreateRoom();
}

[HarmonyPatch(typeof(ContextUIServerList), nameof(ContextUIServerList.UpdateEmptySetVisibility))]
internal static class AllRegionsEmptySetPatch
{
    private static void Postfix(ContextUIServerList __instance) => AllRegions.HideEmptyOverRows(__instance);
}

[HarmonyPatch(typeof(ContextUIServerList), nameof(ContextUIServerList.ShowUnableToConnectToLobby))]
internal static class AllRegionsLobbyFailedPatch
{
    private static void Postfix(ContextUIServerList __instance, string serverRegion) => AllRegions.OnLobbyFailed(__instance, serverRegion);
}
