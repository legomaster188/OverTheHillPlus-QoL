using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Text;
using HarmonyLib;
using UnityEngine;

namespace OverTheHillPlus.ServerBrowser;

/// <summary>Times the server browser's methods and frame spikes while it is open, logging a summary every 5 s.</summary>
internal static class BrowserProfiler
{
    private sealed class Stat { public int Calls; public long Total; public long Max; }

    private static readonly (Type type, string method)[] Targets =
    {
        (typeof(OverlandNetworkRunner), nameof(OverlandNetworkRunner.Fusion_INetworkRunnerCallbacks_OnSessionListUpdated)),
        (typeof(SessionInfoListAdapter), nameof(SessionInfoListAdapter.UpdateSessionList)),
        (typeof(SessionInfoListAdapter), nameof(SessionInfoListAdapter.UpdateViewsHolder)),
        (typeof(SessionInfoListAdapter), nameof(SessionInfoListAdapter.CreateViewsHolder)),
        (typeof(SessionInfoListAdapter), nameof(SessionInfoListAdapter.Sort)),
        (typeof(SessionInfoListAdapter), nameof(SessionInfoListAdapter.ApplyFilters)),
        (typeof(SessionInfoListAdapter), nameof(SessionInfoListAdapter.UpdateNavigation)),
        (typeof(SessionInfoListAdapter), nameof(SessionInfoListAdapter.Update)),
        (typeof(ContextUIServerList), nameof(ContextUIServerList.LateUpdate)),
        (typeof(ContextUIServerList), nameof(ContextUIServerList.RefreshSessionList)),
        (typeof(ContextUIServerList), nameof(ContextUIServerList.UpdateLabels)),
        (typeof(ContextUIServerList), nameof(ContextUIServerList.UpdateHeaders)),
        (typeof(ContextUIServerList), nameof(ContextUIServerList.UpdateEmptySetVisibility)),
        (typeof(ContextUIServerList), nameof(ContextUIServerList.SelectFirstValidTargetInServerList)),
        (typeof(StateServerBrowser), nameof(StateServerBrowser.RefreshSessionList)),
        (typeof(StateServerBrowser), nameof(StateServerBrowser.SessionSelected)),
    };

    private static readonly Dictionary<string, Stat> Stats = new();
    private static readonly Dictionary<MethodBase, string> Names = new();
    private static int _frames, _spikes;
    private static float _worstFrame, _nextReport;
    private static float _openedAt = -1f;

    public static bool BrowserOpen { get; private set; }

    public static void Install(Harmony harmony)
    {
        var prefix = new HarmonyMethod(typeof(BrowserProfiler), nameof(Prefix));
        var postfix = new HarmonyMethod(typeof(BrowserProfiler), nameof(Postfix));
        int ok = 0;
        foreach (var (type, name) in Targets)
        {
            try
            {
                var m = AccessTools.Method(type, name);
                if (m == null) { Plugin.Logger.LogWarning($"Profiler: {type.Name}.{name} not found"); continue; }
                Names[m] = $"{type.Name}.{name}";
                harmony.Patch(m, prefix, postfix);
                ok++;
            }
            catch (Exception e) { Plugin.Logger.LogWarning($"Profiler: could not patch {type.Name}.{name}: {e.Message}"); }
        }
        Plugin.Logger.LogInfo($"Server browser profiler hooked {ok}/{Targets.Length} methods");
    }

    private static void Prefix(out long __state) =>
        __state = BrowserOpen && ModConfig.ProfileServerBrowser.Value ? Stopwatch.GetTimestamp() : 0;

    private static void Postfix(long __state, MethodBase __originalMethod)
    {
        if (__state == 0) return;
        long elapsed = Stopwatch.GetTimestamp() - __state;
        string name = Names.TryGetValue(__originalMethod, out var n) ? n : __originalMethod.Name;
        if (!Stats.TryGetValue(name, out var s)) Stats[name] = s = new Stat();
        s.Calls++;
        s.Total += elapsed;
        if (elapsed > s.Max) s.Max = elapsed;

        if (_openedAt >= 0f && name == "SessionInfoListAdapter.UpdateSessionList")
        {
            Plugin.Logger.LogInfo($"[SB perf] first list shown {(Time.realtimeSinceStartup - _openedAt) * 1000f:F0} ms after opening the browser");
            _openedAt = -1f;
        }
    }

    public static void SetOpen(bool open)
    {
        if (open == BrowserOpen) return;
        BrowserOpen = open;
        if (open)
        {
            _openedAt = Time.realtimeSinceStartup;
            ResetWindow();
            _nextReport = Time.unscaledTime + 5f;
        }
        else if (ModConfig.ProfileServerBrowser.Value) Report();
    }

    public static void Tick()
    {
        if (!BrowserOpen || !ModConfig.ProfileServerBrowser.Value) return;
        float dt = Time.unscaledDeltaTime;
        _frames++;
        if (dt > 0.030f) _spikes++;
        if (dt > _worstFrame) _worstFrame = dt;
        if (Time.unscaledTime >= _nextReport) { Report(); _nextReport = Time.unscaledTime + 5f; }
    }

    private static void Report()
    {
        if (_frames == 0 && Stats.Count == 0) return;
        double msPerTick = 1000.0 / Stopwatch.Frequency;
        var sb = new StringBuilder();
        sb.Append($"[SB perf] {_frames} frames, {_spikes} over 30 ms (worst {_worstFrame * 1000f:F0} ms); ");
        sb.Append($"list updates applied={SessionThrottle.Applied} skipped={SessionThrottle.SkippedIdentical} deferred={SessionThrottle.Deferred}");
        foreach (var kv in Stats.OrderByDescending(k => k.Value.Total).Take(10))
            sb.Append($"\n    {kv.Key}: {kv.Value.Calls} calls, total {kv.Value.Total * msPerTick:F1} ms, max {kv.Value.Max * msPerTick:F1} ms");
        Plugin.Logger.LogInfo(sb.ToString());
        ResetWindow();
    }

    private static void ResetWindow()
    {
        Stats.Clear();
        _frames = _spikes = 0;
        _worstFrame = 0f;
    }
}

[HarmonyPatch]
internal static class BrowserOpenClosePatches
{
    [HarmonyPostfix]
    [HarmonyPatch(typeof(StateServerBrowser), nameof(StateServerBrowser.Open))]
    private static void Opened()
    {
        SessionThrottle.Reset();
        BrowserProfiler.SetOpen(true);
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(StateServerBrowser), nameof(StateServerBrowser.Close))]
    private static void Closed()
    {
        BrowserProfiler.SetOpen(false);
        FirstSeenTracker.SaveIfDue(force: true);
    }
}
