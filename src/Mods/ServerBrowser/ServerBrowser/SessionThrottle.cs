using System;
using System.Text;
using Fusion;
using HarmonyLib;
using UnityEngine;

namespace OverTheHillPlus.ServerBrowser;

/// <summary>
/// Photon resends the whole session list on every lobby change. This drops updates identical to the
/// last applied one and coalesces bursts so the UI rebuilds at most once per interval.
/// </summary>
[HarmonyPatch(typeof(OverlandNetworkRunner), nameof(OverlandNetworkRunner.Fusion_INetworkRunnerCallbacks_OnSessionListUpdated))]
internal static class SessionThrottle
{
    private const float ForceRefreshSeconds = 10f;

    private static bool _bypass;
    private static string _lastAppliedSig;
    private static float _lastAppliedTime = -100f;

    private static OverlandNetworkRunner _pendingOwner;
    private static NetworkRunner _pendingRunner;
    private static Il2CppSystem.Collections.Generic.List<SessionInfo> _pendingList;
    private static string _pendingSig;

    public static int Applied, SkippedIdentical, Deferred;

    /// <summary>
    /// True while the game handles an update we let through. Applying a list fires the game's SessionListUpdated,
    /// whose handler (StateServerBrowser.RefreshSessionList) calls ContextUIServerList.RefreshSessionList: that must
    /// not reset the throttle, or every update would clear the state that dedupes and spaces out the next one.
    /// </summary>
    internal static bool Applying { get; private set; }

    /// <summary>Set around a list rebuild the mod asks for itself (auto refresh): it must not reset the throttle either.</summary>
    internal static bool QuietRefresh;

    /// <summary>Make the next update go through untouched (browser opened, refresh pressed, region changed).</summary>
    public static void Reset()
    {
        _lastAppliedSig = null;
        _lastAppliedTime = -100f;
    }

    /// <summary>Forgets a held-back update (the lobby is switching region: that list belongs to the old one).</summary>
    public static void DropPending()
    {
        _pendingList = null;
        _pendingOwner = null;
        _pendingRunner = null;
    }

    private static bool Prefix(OverlandNetworkRunner __instance, NetworkRunner runner,
        Il2CppSystem.Collections.Generic.List<SessionInfo> sessionList)
    {
        if (_bypass) return true;
        Applying = false;
        try { LogListOnce(sessionList); } catch { }
        try { FirstSeenTracker.Observe(runner, sessionList); }
        catch (Exception e) { DebugLog.Error("serverbrowser", e, "SessionThrottle.Prefix"); ServerBrowserPlugin.Logger.LogWarning("First-seen observe failed: " + e.Message); }

        // Only the visible browser benefits; when it's closed let the game keep its list current.
        if (!ServerBrowserConfig.ThrottleSessionUpdates.Value || !BrowserState.Open)
        {
            _pendingList = null;
            Applied++;
            Applying = true;
            return true;
        }

        string sig = Signature(sessionList);
        float now = Time.unscaledTime;
        float sinceApplied = now - _lastAppliedTime;

        if (sig == _lastAppliedSig && sinceApplied < ForceRefreshSeconds)
        {
            SkippedIdentical++;
            _pendingList = null;
            return false;
        }

        if (sinceApplied < ServerBrowserConfig.SessionUpdateInterval.Value)
        {
            Deferred++;
            _pendingOwner = __instance;
            _pendingRunner = runner;
            _pendingList = Copy(sessionList);
            _pendingSig = sig;
            return false;
        }

        _pendingList = null;
        MarkApplied(sig);
        Applying = true;
        return true;
    }

    private static void Postfix() { if (!_bypass) Applying = false; }

    private static float _nextListLog;

    /// <summary>At most every 30 s: how many sessions the lobby sent and how many are listed / open / full, to tell a
    /// server missing from the browser apart from one the lobby never sent (Photon caps its room list).</summary>
    private static void LogListOnce(Il2CppSystem.Collections.Generic.List<SessionInfo> list)
    {
        if (list == null || Time.unscaledTime < _nextListLog) return;
        _nextListLog = Time.unscaledTime + 30f;
        int n = list.Count, visible = 0, open = 0, full = 0, big = 0, rejected = 0;
        var regions = new System.Collections.Generic.Dictionary<string, int>();
        for (int i = 0; i < n; i++)
        {
            var s = list[i];
            if (s == null) continue;
            bool ok = false;
            try { ok = OverlandSessionInfo.IsSessionValid(s); } catch { }
            if (!ok) rejected++;
            string rg = s.Region ?? "?";
            regions[rg] = regions.TryGetValue(rg, out int c) ? c + 1 : 1;
            if (s.IsVisible) visible++;
            if (s.IsOpen) open++;
            if (s.MaxPlayers > 0 && s.PlayerCount >= s.MaxPlayers) full++;
            if (s.MaxPlayers > 4) big++;
        }
        var rs = new System.Text.StringBuilder();
        foreach (var kv in regions) rs.Append(rs.Length > 0 ? ", " : "").Append(kv.Key).Append(' ').Append(kv.Value);
        ServerBrowserPlugin.Logger.LogInfo($"[Browser+] lobby sent {n} sessions ({visible} listed, {open} joinable, {full} full, {big} above 4 players, {rejected} the game's browser rejects as invalid); regions: {rs}");
        // The big lobbies (ours among them): does the game's browser accept them, and which properties do they carry?
        int shownBig = 0;
        for (int i = 0; i < n && shownBig < 6; i++)
        {
            var s = list[i];
            if (s == null || s.MaxPlayers <= 4) continue;
            shownBig++;
            bool valid = false;
            try { valid = OverlandSessionInfo.IsSessionValid(s); } catch { }
            // Known keys only: enumerating Fusion's property dictionary through IL2CPP crashed the runtime.
            string keys = "";
            try
            {
                var props = s.Properties;
                if (props != null)
                    foreach (var k in Core.Manager.SessionSlots.KnownKeys)
                        if (props.ContainsKey(k)) keys += (keys.Length > 0 ? "," : "") + k;
            }
            catch (Exception e) { keys = "? " + e.Message; }
            string name = s.Name ?? "";
            if (name.Length > 60) name = name.Substring(0, 60) + "...";
            ServerBrowserPlugin.Logger.LogInfo($"[Browser+]   big lobby '{name}' {s.PlayerCount}/{s.MaxPlayers}, region {s.Region}, game accepts it: {(valid ? "yes" : "NO")}, properties: {keys}");
        }
    }

    /// <summary>Called every frame; flushes a deferred update once the interval has passed.</summary>
    public static void Tick()
    {
        Applying = false; // never left set past a frame, even if the game's handler threw
        if (_pendingList == null) return;
        bool due = Time.unscaledTime - _lastAppliedTime >= ServerBrowserConfig.SessionUpdateInterval.Value;
        if (!due && BrowserState.Open) return;
        var owner = _pendingOwner; var runner = _pendingRunner; var list = _pendingList; var sig = _pendingSig;
        _pendingList = null;
        if (owner == null) return;
        MarkApplied(sig);
        _bypass = true;
        Applying = true;
        try { owner.Fusion_INetworkRunnerCallbacks_OnSessionListUpdated(runner, list); }
        catch (Exception e) { DebugLog.Error("serverbrowser", e, "SessionThrottle.Tick"); ServerBrowserPlugin.Logger.LogWarning("Deferred session update failed: " + e.Message); }
        finally { _bypass = false; Applying = false; }
    }

    private static void MarkApplied(string sig)
    {
        Applied++;
        _lastAppliedSig = sig;
        _lastAppliedTime = Time.unscaledTime;
    }

    private const string PlayerNamesKey = Features.Public.ServerPlayers.PlayerNamesProperty.Key;

    private static string Signature(Il2CppSystem.Collections.Generic.List<SessionInfo> list)
    {
        if (list == null) return "null";
        var sb = new StringBuilder(list.Count * 48);
        for (int i = 0; i < list.Count; i++)
        {
            var s = list[i];
            if (s == null) continue;
            sb.Append(s.Name).Append('|').Append(s.PlayerCount).Append('/').Append(s.MaxPlayers)
              .Append(s.IsOpen ? 'o' : 'c').Append(s.IsVisible ? 'v' : 'h');
            // Who is in the server (published by modded hosts) changes without the player count changing.
            try
            {
                var props = s.Properties;
                if (props != null && props.ContainsKey(PlayerNamesKey))
                {
                    var p = props[PlayerNamesKey];
                    if (p != null && p.IsString) sb.Append('#').Append((string)p);
                }
            }
            catch (Exception ex) { DebugLog.Error("serverbrowser", ex, "SessionThrottle.Signature"); /* signature only */ }
            sb.Append(';');
        }
        return sb.ToString();
    }

    private static Il2CppSystem.Collections.Generic.List<SessionInfo> Copy(Il2CppSystem.Collections.Generic.List<SessionInfo> src)
    {
        var dst = new Il2CppSystem.Collections.Generic.List<SessionInfo>(src.Count);
        for (int i = 0; i < src.Count; i++) dst.Add(src[i]);
        return dst;
    }
}

[HarmonyPatch]
internal static class SessionThrottleResetPatches
{
    // One target per method: stacked [HarmonyPatch] attributes merge into a single target.
    // The 2026-10-16 game update added RefreshSessionList(CallbackContext) (an input handler) next to the
    // parameterless RefreshSessionList(), which made the name alone ambiguous: name the parameterless one, the list
    // refresh itself.
    [HarmonyPrefix]
    [HarmonyPatch(typeof(ContextUIServerList), nameof(ContextUIServerList.RefreshSessionList), new Type[0])]
    private static void OnRefresh()
    {
        // Only a refresh that isn't the game applying an update (or the mod's own auto refresh) starts afresh.
        if (SessionThrottle.Applying || SessionThrottle.QuietRefresh) return;
        SessionThrottle.Reset();
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(ContextUIServerList), nameof(ContextUIServerList.ServerRegionOnChanged))]
    private static void OnRegionChanged() => SessionThrottle.Reset();
}


/// <summary>Logs the server browser's region choice and searches (diagnostics for servers missing from the list).</summary>
[HarmonyPatch(typeof(ContextUIServerList), nameof(ContextUIServerList.ServerRegionOnChanged))]
internal static class BrowserRegionLogPatch
{
    private static void Postfix(int optionIndex)
    {
        try
        {


            ServerBrowserPlugin.Logger.LogInfo($"[Browser+] region picked in the browser: option {optionIndex}");
        }
        catch { }
    }
}

[HarmonyPatch(typeof(SessionInfoListAdapter), nameof(SessionInfoListAdapter.SetNameFilter))]
internal static class BrowserSearchLogPatch
{
    private static string _last;

    private static void Postfix(string searchTerm)
    {
        try
        {
            if (searchTerm == _last) return;
            _last = searchTerm;
            ServerBrowserPlugin.Logger.LogInfo(string.IsNullOrEmpty(searchTerm) ? "[Browser+] search cleared" : $"[Browser+] searching for '{searchTerm}'");
        }
        catch { }
    }
}
