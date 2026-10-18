using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Text;
using HarmonyLib;
using OverTheHillPlus.CheatsPlus;
using OverTheHillPlus.ServerBrowser;
using UnityEngine;

namespace OverTheHillPlus.Diagnostics;

/// <summary>
/// Times the server browser's methods and frame spikes while it is open, logging a summary every 5 s
/// ([ServerBrowser] Profiling, read at startup). Troubleshooting only, so it lives in Cheats+ (private) and patches the
/// game's server browser types itself; whether the browser is open comes from core's <see cref="BrowserScreen"/>
/// (<see cref="Tick"/> notices it opening and closing).
/// </summary>
internal static class BrowserProfiler
{
    private sealed class Stat { public int Calls; public long Total; public long Max; }

    // Parameter types only where the name alone is ambiguous (null = the one method of that name): the 2026-10-16 game
    // update added ContextUIServerList.RefreshSessionList(CallbackContext) next to the parameterless list refresh.
    private static readonly (Type type, string method, Type[] args)[] Targets =
    {
        (typeof(OverlandNetworkRunner), nameof(OverlandNetworkRunner.Fusion_INetworkRunnerCallbacks_OnSessionListUpdated), null),
        (typeof(SessionInfoListAdapter), nameof(SessionInfoListAdapter.UpdateSessionList), null),
        (typeof(SessionInfoListAdapter), nameof(SessionInfoListAdapter.UpdateViewsHolder), null),
        (typeof(SessionInfoListAdapter), nameof(SessionInfoListAdapter.CreateViewsHolder), null),
        (typeof(SessionInfoListAdapter), nameof(SessionInfoListAdapter.Sort), null),
        (typeof(SessionInfoListAdapter), nameof(SessionInfoListAdapter.ApplyFilters), null),
        (typeof(SessionInfoListAdapter), nameof(SessionInfoListAdapter.UpdateNavigation), null),
        (typeof(SessionInfoListAdapter), nameof(SessionInfoListAdapter.Update), null),
        (typeof(ContextUIServerList), nameof(ContextUIServerList.LateUpdate), null),
        (typeof(ContextUIServerList), nameof(ContextUIServerList.RefreshSessionList), Type.EmptyTypes),
        (typeof(ContextUIServerList), nameof(ContextUIServerList.UpdateLabels), null),
        (typeof(ContextUIServerList), nameof(ContextUIServerList.UpdateHeaders), null),
        (typeof(ContextUIServerList), nameof(ContextUIServerList.UpdateEmptySetVisibility), null),
        (typeof(ContextUIServerList), nameof(ContextUIServerList.SelectFirstValidTargetInServerList), null),
        (typeof(StateServerBrowser), nameof(StateServerBrowser.RefreshSessionList), null),
        (typeof(StateServerBrowser), nameof(StateServerBrowser.SessionSelected), null),
    };

    private static readonly Dictionary<string, Stat> Stats = new();
    private static readonly Dictionary<MethodBase, string> Names = new();
    private static int _frames, _spikes;
    private static float _worstFrame, _nextReport;
    private static float _openedAt = -1f;

    private static bool _open;

    public static void Install(Harmony harmony)
    {
        var prefix = new HarmonyMethod(typeof(BrowserProfiler), nameof(Prefix));
        var postfix = new HarmonyMethod(typeof(BrowserProfiler), nameof(Postfix));
        int ok = 0;
        foreach (var (type, name, args) in Targets)
        {
            try
            {
                var m = AccessTools.Method(type, name, args);
                if (m == null) { CheatsPlugin.Logger.LogWarning($"Profiler: {type.Name}.{name} not found"); continue; }
                Names[m] = $"{type.Name}.{name}";
                harmony.Patch(m, prefix, postfix);
                ok++;
            }
            catch (Exception e) { DebugLog.Error("cheats", e, "BrowserProfiler.Install"); CheatsPlugin.Logger.LogWarning($"Profiler: could not patch {type.Name}.{name}: {e.Message}"); }
        }
        CheatsPlugin.Logger.LogInfo($"Server browser profiler hooked {ok}/{Targets.Length} methods");
    }

    private static void Prefix(out long __state) =>
        __state = _open && CheatsConfig.ProfileServerBrowser.Value ? Stopwatch.GetTimestamp() : 0;

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
            CheatsPlugin.Logger.LogInfo($"[SB perf] first list shown {(Time.realtimeSinceStartup - _openedAt) * 1000f:F0} ms after opening the browser");
            _openedAt = -1f;
        }
    }

    private static void SetOpen(bool open)
    {
        if (open == _open) return;
        _open = open;
        if (open)
        {
            _openedAt = Time.realtimeSinceStartup;
            ResetWindow();
            _nextReport = Time.unscaledTime + 5f;
        }
        else if (CheatsConfig.ProfileServerBrowser.Value) Report();
    }

    /// <summary>Every frame (Cheats+'s frame hook): follows the browser opening and closing, and times the frame.</summary>
    public static void Tick()
    {
        if (CheatsConfig.ProfileServerBrowser == null || !CheatsConfig.ProfileServerBrowser.Value) return;
        SetOpen(BrowserScreen.Open);
        if (!_open) return;
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
        sb.Append($"[SB perf] {_frames} frames, {_spikes} over 30 ms (worst {_worstFrame * 1000f:F0} ms)");
        foreach (var kv in Stats.OrderByDescending(k => k.Value.Total).Take(10))
            sb.Append($"\n    {kv.Key}: {kv.Value.Calls} calls, total {kv.Value.Total * msPerTick:F1} ms, max {kv.Value.Max * msPerTick:F1} ms");
        CheatsPlugin.Logger.LogInfo(sb.ToString());
        ResetWindow();
    }

    private static void ResetWindow()
    {
        Stats.Clear();
        _frames = _spikes = 0;
        _worstFrame = 0f;
    }
}
