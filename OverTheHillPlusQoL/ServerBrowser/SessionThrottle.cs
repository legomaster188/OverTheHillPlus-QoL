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
        try { FirstSeenTracker.Observe(runner, sessionList); }
        catch (Exception e) { Plugin.Logger.LogWarning("First-seen observe failed: " + e.Message); }

        // Only the visible browser benefits; when it's closed let the game keep its list current.
        if (!ModConfig.ThrottleSessionUpdates.Value || !BrowserState.Open)
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

        if (sinceApplied < ModConfig.SessionUpdateInterval.Value)
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

    /// <summary>Called every frame; flushes a deferred update once the interval has passed.</summary>
    public static void Tick()
    {
        Applying = false; // never left set past a frame, even if the game's handler threw
        if (_pendingList == null) return;
        bool due = Time.unscaledTime - _lastAppliedTime >= ModConfig.SessionUpdateInterval.Value;
        if (!due && BrowserState.Open) return;
        var owner = _pendingOwner; var runner = _pendingRunner; var list = _pendingList; var sig = _pendingSig;
        _pendingList = null;
        if (owner == null) return;
        MarkApplied(sig);
        _bypass = true;
        Applying = true;
        try { owner.Fusion_INetworkRunnerCallbacks_OnSessionListUpdated(runner, list); }
        catch (Exception e) { Plugin.Logger.LogWarning("Deferred session update failed: " + e.Message); }
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
            catch { /* signature only */ }
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
    [HarmonyPrefix]
    [HarmonyPatch(typeof(ContextUIServerList), nameof(ContextUIServerList.RefreshSessionList))]
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
