using System;
using System.Collections.Generic;
using System.Diagnostics;
using BepInEx.Configuration;
using Fusion;
using GPUInstancerPro;
using OverTheHillPlus.Features.Public.Gfx;
using OverTheHillPlus.Features.Public.NetPerf;
using OverTheHillPlus.Features.Public.PlayerList;
using UnityEngine;
using UnityEngine.Rendering.Universal;
using C = OverTheHillPlus.Features.Public.HostGovernor.HostGovernorFeature;

namespace OverTheHillPlus.Features.Public.HostGovernor;

/// <summary>
/// Measures frame time and simulation health every frame (cheap: one float into a ring buffer, plus one runner
/// property while hosting) and decides once a second whether to step quality down or up.
///
/// Measurements:
/// - Frame time: Time.unscaledDeltaTime. Single frames above 250 ms are counted as hitches (loading a joining
///   player's car, see JoinSmoothing) and left out of the average so one freeze doesn't drop quality for minutes.
/// - Tick health: NetworkRunner.TicksExecuted (forward ticks run in the last update, from UpdateInternal) and the
///   tick counter's advance per real second against NetworkRunner.TickRate. A host that can't keep up runs fewer
///   ticks per second than the tick rate; guests then see everyone's cars hitch.
/// - Fusion statistics (TryGetFusionStatistics -> SimulationSnapshot.Stats): forward ticks, resimulations, time
///   resets, simulation speed. The manager always exists (created in the Simulation constructor).
/// </summary>
internal static class GovernorRuntime
{
    internal const int Levels = 5;
    private static readonly float[] RenderF = { 1f, 0.9f, 0.8f, 0.75f, 0.7f, 0.65f };
    private static readonly float[] ShadowF = { 1f, 0.85f, 0.7f, 0.55f, 0.45f, 0.35f };
    private static readonly float[] LodF = { 1f, 0.9f, 0.8f, 0.7f, 0.6f, 0.5f };

    internal static string LevelName(int level)
    {
        int l = Mathf.Clamp(level, 0, Levels);
        return l == 0 ? "full" : $"{l}: {RenderF[l] * 100f:0}% res, {ShadowF[l] * 100f:0}% shadows";
    }

    private const float HitchSeconds = 0.25f;
    private const float EvalInterval = 1f;

    // ---------------------------------------------------------------- read-out for the menu

    internal sealed class View
    {
        public bool Hosting, Active;
        public int Level, Players, Frames;
        public float AvgMs, TargetMs;
        public string StateLine = "not hosting", FrameLine = "", TickLine = "", FusionLine = "", PriorityLine = "";
        public readonly List<(string, string)> Knobs = new();
    }

    internal static View Snapshot { get; private set; } = new();

    // ---------------------------------------------------------------- frame sampling

    private static readonly float[] Ring = new float[240];
    private static readonly float[] Sorted = new float[240];
    private static int _ringPos, _ringCount;
    private static float _winSum, _winMax;
    private static int _winN, _winHitches;

    private static NetworkRunner _runner;
    private static bool _hosting;
    private static bool _ticksBroken;
    private static int _tpfSum, _tpfFrames, _tpfMax;
    private static int _lastTickRaw = int.MinValue;
    private static float _lastTickAt;
    private static IntPtr _lastTickRunner;

    private static float _nextEval;
    private static readonly HashSet<string> Logged = new();

    private static void LogOnce(string key, Exception e)
    {
        if (Logged.Add(key)) Plugin.Logger.LogWarning($"[HostGovernor] {key} failed (logged once): {e.Message}");
    }

    public static void Tick()
    {
        float dt = Time.unscaledDeltaTime;
        if (dt > 0f)
        {
            Ring[_ringPos] = dt;
            _ringPos = (_ringPos + 1) % Ring.Length;
            if (_ringCount < Ring.Length) _ringCount++;
            if (dt >= HitchSeconds) _winHitches++;
            else
            {
                _winSum += dt;
                _winN++;
                if (dt > _winMax) _winMax = dt;
            }
        }

        if (_hosting && !_ticksBroken && _runner != null)
        {
            try
            {
                int n = _runner.TicksExecuted;
                _tpfSum += n;
                _tpfFrames++;
                if (n > _tpfMax) _tpfMax = n;
            }
            catch (Exception e) { _ticksBroken = true; LogOnce("ticks per frame", e); }
        }

        float now = Time.unscaledTime;
        if (now < _nextEval) return;
        _nextEval = now + EvalInterval;
        try { Evaluate(now); }
        catch (Exception e) { LogOnce("evaluate", e); }
    }

    // ---------------------------------------------------------------- control state

    private static bool _active;
    private static float _activeSince, _fewPlayersSince = -1f;
    private static int _level;
    private static int _pressure, _relief;
    private static float _nextUp, _nextDown;
    private static float _reliefNeeded = 8f, _lastDownAt = -1000f, _lastFlapAt = -1000f;
    private static int _players;
    private static float _nextPlayers;

    private static void Evaluate(float now)
    {
        var r = GameState.Runner;
        _runner = r;
        _hosting = NetPerfRuntime.IsHosting(r);

        // ---- window stats ----
        float avgMs = _winN > 0 ? _winSum / _winN * 1000f : 0f;
        float maxMs = _winMax * 1000f;
        int hitches = _winHitches;
        int frames = _winN + _winHitches;
        _winSum = _winMax = 0f;
        _winN = _winHitches = 0;
        float p95 = Percentile(0.95f) * 1000f;

        // ---- players ----
        if (!_hosting) _players = 0;
        else if (now >= _nextPlayers)
        {
            _nextPlayers = now + 2f;
            try { _players = PlayerListRoster.AllPlayers().Count; }
            catch (Exception e) { LogOnce("player count", e); }
        }

        // ---- tick health ----
        float measuredHz = -1f, nominalHz = -1f, tpfAvg = -1f;
        int tpfMax = _tpfMax;
        if (_tpfFrames > 0) tpfAvg = (float)_tpfSum / _tpfFrames;
        _tpfSum = _tpfFrames = _tpfMax = 0;
        string fusion = "";
        if (_hosting)
        {
            try
            {
                nominalHz = r.TickRate;
                int raw = r.Tick.Raw;
                if (_lastTickRunner == r.Pointer && _lastTickRaw != int.MinValue && now > _lastTickAt && raw >= _lastTickRaw)
                    measuredHz = (raw - _lastTickRaw) / (now - _lastTickAt);
                _lastTickRaw = raw;
                _lastTickAt = now;
                _lastTickRunner = r.Pointer;
            }
            catch (Exception e) { LogOnce("tick rate", e); }
            try { fusion = ReadFusion(r); } catch (Exception e) { LogOnce("fusion stats", e); }
        }
        else
        {
            _lastTickRaw = int.MinValue;
            _lastTickRunner = IntPtr.Zero;
        }

        // ---- target ----
        float limit = FrameLimit();
        float targetFps = Mathf.Clamp(C.TargetFps.Value, 25f, 144f);
        bool capped = limit > 0f && limit <= targetFps + 0.5f;
        if (capped) targetFps = limit * 0.98f;
        float targetMs = 1000f / targetFps;

        // ---- active? ----
        int minPlayers = Mathf.Clamp(C.MinPlayers.Value, 1, 16);
        bool enough = minPlayers <= 1 || _players >= minPlayers;
        if (!C.Enabled.Value || !_hosting) SetActive(false, now);
        else if (enough) { _fewPlayersSince = -1f; SetActive(true, now); }
        else if (_active)
        {
            // Players left: wait a little (someone reconnecting) before putting everything back.
            if (_fewPlayersSince < 0f) _fewPlayersSince = now;
            else if (now - _fewPlayersSince > 10f) SetActive(false, now);
        }

        int maxLevel = Mathf.Clamp(C.MaxLevel.Value, 1, Levels);
        if (_active)
        {
            int floor = C.Preempt.Value && minPlayers > 1 ? Mathf.Clamp((_players - minPlayers) / 6, 0, maxLevel) : 0;
            bool settling = now - _activeSince < 5f; // the first seconds after starting are loading noise
            bool slowTicks = measuredHz > 0f && nominalHz > 0f && measuredHz < nominalHz * 0.93f;
            bool slowFrames = frames > 0 && avgMs > targetMs * 1.12f;
            bool roomy = frames > 0 && !slowTicks &&
                         (capped ? avgMs < targetMs * 1.04f && p95 < targetMs * 1.15f : avgMs < targetMs * 0.85f);

            if (!settling)
            {
                if (slowFrames || slowTicks) { _pressure++; _relief = 0; }
                else { _pressure = 0; if (roomy) _relief++; else _relief = 0; }

                if (_pressure >= 2 && now >= _nextUp && _level < maxLevel)
                {
                    _level++;
                    _pressure = 0;
                    _nextUp = now + 3f;
                    _nextDown = now + 6f;
                    // Stepping back up soon after stepping down: the PC sits on the edge; wait longer next time.
                    if (now - _lastDownAt < 30f)
                    {
                        _reliefNeeded = Mathf.Min(_reliefNeeded * 2f, 120f);
                        _lastFlapAt = now;
                    }
                }
                else if (_relief >= _reliefNeeded && now >= _nextDown && _level > floor)
                {
                    _level--;
                    _relief = 0;
                    _lastDownAt = now;
                    _nextDown = now + 4f;
                }
                if (now - _lastFlapAt > 180f) _reliefNeeded = 8f;
            }
            _level = Mathf.Clamp(Math.Max(_level, floor), 0, maxLevel);
        }
        else _level = 0;

        ApplyKnobs();
        ApplyPriority(now);

        // ---- read-out ----
        var v = new View
        {
            Hosting = _hosting,
            Active = _active,
            Level = _level,
            Players = _players,
            Frames = _ringCount,
            AvgMs = avgMs,
            TargetMs = targetMs,
        };
        v.StateLine = !_hosting ? (C.Enabled.Value ? "waiting: not hosting" : "off (not hosting)")
            : !C.Enabled.Value ? $"auto quality off, {_players} players"
            : !_active ? $"waiting: {_players} players (starts at {minPlayers})"
            : _level == 0 ? $"watching, full quality, {_players} players"
            : $"lowered to step {_level}/{maxLevel} ({LevelName(_level)}), {_players} players";
        v.FrameLine = frames == 0 ? "-"
            : $"{(avgMs > 0f ? 1000f / avgMs : 0f):0} fps avg, p95 {p95:0.0} ms, worst {maxMs:0.0} ms" +
              (hitches > 0 ? $", {hitches} hitch{(hitches > 1 ? "es" : "")}" : "") +
              $", target {1000f / targetMs:0} fps{(capped ? " (frame cap)" : "")}";
        if (_hosting)
        {
            string meas = measuredHz > 0f ? $"{measuredHz:0.0}" : "-";
            string health = measuredHz <= 0f || nominalHz <= 0f ? "" : measuredHz >= nominalHz * 0.97f ? "keeping up" : measuredHz >= nominalHz * 0.9f ? "slightly behind" : "falling behind";
            v.TickLine = $"{meas} / {nominalHz:0} ticks per s" + (health.Length > 0 ? $" ({health})" : "") +
                         (tpfAvg >= 0f ? $", {tpfAvg:0.0} per frame (max {tpfMax})" : "");
            v.FusionLine = fusion;
        }
        v.PriorityLine = _priorityLine;
        v.Knobs.Add(("Render scale", _renderStatus));
        v.Knobs.Add(("Shadow distance", _shadowStatus));
        v.Knobs.Add(("LOD bias", Lod.Status));
        v.Knobs.Add(("FPS cap", Fps.Status));
        Snapshot = v;
    }

    private static void SetActive(bool on, float now)
    {
        if (on == _active) return;
        _active = on;
        _pressure = _relief = 0;
        _fewPlayersSince = -1f;
        if (on)
        {
            _activeSince = now;
            _reliefNeeded = 8f;
            Plugin.Logger.LogInfo("[HostGovernor] active");
        }
        else
        {
            _level = 0;
            Plugin.Logger.LogInfo("[HostGovernor] inactive: settings restored");
        }
    }

    private static float Percentile(float q)
    {
        int n = _ringCount;
        if (n == 0) return 0f;
        Array.Copy(Ring, Sorted, n);
        Array.Sort(Sorted, 0, n);
        return Sorted[Mathf.Clamp((int)(q * (n - 1)), 0, n - 1)];
    }

    /// <summary>The FPS the game is limited to right now (VSync or a frame cap), or 0 when uncapped.</summary>
    private static float FrameLimit()
    {
        try
        {
            int vs = QualitySettings.vSyncCount;
            if (vs > 0)
            {
                double hz = 60.0;
                try { hz = Screen.currentResolution.refreshRateRatio.value; } catch { }
                if (hz < 20.0 || double.IsNaN(hz)) hz = 60.0;
                return (float)(hz / vs);
            }
            int t = Application.targetFrameRate;
            return t > 0 ? t : 0f;
        }
        catch { return 0f; }
    }

    private static string ReadFusion(NetworkRunner r)
    {
        if (!r.TryGetFusionStatistics(out var mgr) || mgr == null) return "";
        var stats = mgr.SimulationSnapshot?.Stats;
        if (stats == null) return "";
        float Get(Fusion.Statistics.FusionStatType t) => stats.TryGetValue(t, out float x) ? x : float.NaN;
        var parts = new List<string>();
        float fwd = Get(Fusion.Statistics.FusionStatType.ForwardTicks);
        float resim = Get(Fusion.Statistics.FusionStatType.Resimulations);
        float resets = Get(Fusion.Statistics.FusionStatType.TimeResets);
        float speed = Get(Fusion.Statistics.FusionStatType.SimulationSpeed);
        if (!float.IsNaN(fwd)) parts.Add($"forward ticks {fwd:0}");
        if (!float.IsNaN(resim)) parts.Add($"resimulations {resim:0}");
        if (!float.IsNaN(resets) && resets > 0f) parts.Add($"time resets {resets:0}");
        if (!float.IsNaN(speed)) parts.Add($"sim speed {speed:0.00}x");
        try { parts.Add($"tick {r.DeltaTime * 1000f:0.0} ms"); } catch { }
        return parts.Count == 0 ? "" : "Fusion: " + string.Join(", ", parts);
    }

    // ---------------------------------------------------------------- knobs

    private sealed class UrpKnobs
    {
        public UniversalRenderPipelineAsset Asset;
        public GovernorKnob Render, Shadow;
    }

    private static readonly Dictionary<IntPtr, UrpKnobs> Urp = new();
    private static string _renderStatus = "", _shadowStatus = "";

    private static readonly GovernorKnob Lod = new("LOD bias", () => QualitySettings.lodBias, x => QualitySettings.lodBias = x)
    {
        OwnerCheck = () => CfgBool("Graphics.Detail", "Enabled") && CfgBool("Graphics.Detail", "LodOverride")
            ? "Graphics > Model detail (LOD)" : null,
    };

    private static readonly GovernorKnob Fps = new("FPS cap", () => Application.targetFrameRate,
        x => Application.targetFrameRate = Mathf.RoundToInt(x), isCap: true)
    {
        OwnerCheck = () =>
        {
            if (QualitySettings.vSyncCount > 0) return "VSync";
            if (CfgInt("QoL", "FpsCap") > 0) return "the QoL FPS cap";
            if (!Application.isFocused && CfgInt("QoL", "BackgroundFpsCap") > 0) return "the background FPS cap";
            return null;
        },
    };

    private static UrpKnobs KnobsFor(UniversalRenderPipelineAsset a)
    {
        if (Urp.TryGetValue(a.Pointer, out var k)) return k;
        var asset = a;
        k = new UrpKnobs
        {
            Asset = asset,
            Render = new GovernorKnob("Render scale", () => asset.renderScale, x => asset.renderScale = Mathf.Clamp(x, 0.1f, 2f))
            {
                OwnerCheck = () =>
                {
                    if (CfgBool("Graphics.Lighting", "RenderScaleOverride")) return "Graphics > Custom render scale";
                    if (LightingScene.TndActive) return "the DLSS/FSR upscaler";
                    return null;
                },
            },
            Shadow = new GovernorKnob("Shadow distance", () => asset.shadowDistance, x => asset.shadowDistance = Mathf.Max(0f, x))
            {
                OwnerCheck = () => CfgBool("Graphics.Detail", "Enabled") && CfgBool("Graphics.Detail", "ShadowOverride")
                    ? "Graphics > Sun shadows" : null,
                // GPU Instancer culls instanced trees' shadows by this distance; the game refreshes it the same way.
                AfterWrite = RefreshGpui,
            },
        };
        Urp[a.Pointer] = k;
        return k;
    }

    private static void RefreshGpui()
    {
        try { GPUICoreAPI.UpdateParameterBufferData(); }
        catch (Exception e) { LogOnce("GPUI refresh", e); }
    }

    private static void ApplyKnobs()
    {
        int l = Mathf.Clamp(_level, 0, Levels);
        bool on = _active;

        UniversalRenderPipelineAsset cur = null;
        try { cur = UniversalRenderPipeline.asset; } catch (Exception e) { LogOnce("URP asset", e); }

        // Quality level switched to another URP asset: put the old one back.
        List<IntPtr> dead = null;
        foreach (var kv in Urp)
        {
            if (cur != null && kv.Key == cur.Pointer) continue;
            kv.Value.Render.ReleaseAll();
            kv.Value.Shadow.ReleaseAll();
            (dead ??= new()).Add(kv.Key);
        }
        if (dead != null) foreach (var k in dead) Urp.Remove(k);

        if (cur != null && (on || Urp.Count > 0))
        {
            var k = KnobsFor(cur);
            k.Render.Apply(on && C.ScaleRender.Value, RenderF[l]);
            k.Shadow.Apply(on && C.ScaleShadows.Value, ShadowF[l]);
            _renderStatus = k.Render.Status;
            _shadowStatus = k.Shadow.Status;
        }
        else _renderStatus = _shadowStatus = "";

        Lod.Apply(on && C.ScaleLod.Value, LodF[l]);
        // At least 30: the host's frames drive the session, so a lower cap would lag every guest, not just this PC.
        Fps.Apply(on && C.FpsCap.Value > 0, Math.Max(C.MinFpsCap, C.FpsCap.Value));
    }

    // ---------------------------------------------------------------- process priority

    private static ProcessPriorityClass? _savedPriority;
    private static ProcessPriorityClass _appliedPriority;
    private static float _nextPriority;
    private static string _priorityLine = "";
    private static bool _priorityFailed;

    private static void ApplyPriority(float now)
    {
        bool want = C.Priority.Value && _hosting && !_priorityFailed;
        if (!want && _savedPriority == null) { _priorityLine = C.Priority.Value && !_hosting ? "normal (raised only while hosting)" : ""; return; }
        if (now < _nextPriority) return;
        _nextPriority = now + 3f;
        try
        {
            using var p = Process.GetCurrentProcess();
            if (want)
            {
                var target = C.PriorityLevel.Value == "High" ? ProcessPriorityClass.High : ProcessPriorityClass.AboveNormal;
                var live = p.PriorityClass;
                if (_savedPriority == null) _savedPriority = live;
                if (live != target)
                {
                    p.PriorityClass = target;
                    Plugin.Logger.LogInfo($"[HostGovernor] process priority {live} -> {target} while hosting");
                }
                _appliedPriority = target;
                _priorityLine = $"{Pretty(target)} while hosting (was {Pretty(_savedPriority.Value)})";
            }
            else RestorePriority(p);
        }
        catch (Exception e)
        {
            _priorityFailed = true;
            _savedPriority = null;
            _priorityLine = "could not change the priority (see log)";
            LogOnce("process priority", e);
        }
    }

    private static void RestorePriority(Process p)
    {
        if (_savedPriority == null) return;
        var saved = _savedPriority.Value;
        _savedPriority = null;
        _priorityLine = "";
        // Only put it back if nobody (task manager, another tool) changed it since.
        if (p.PriorityClass == _appliedPriority && saved != _appliedPriority)
        {
            p.PriorityClass = saved;
            Plugin.Logger.LogInfo($"[HostGovernor] process priority restored to {saved}");
        }
    }

    private static string Pretty(ProcessPriorityClass c) => c switch
    {
        ProcessPriorityClass.AboveNormal => "above normal",
        ProcessPriorityClass.BelowNormal => "below normal",
        ProcessPriorityClass.RealTime => "realtime",
        _ => c.ToString().ToLowerInvariant(),
    };

    /// <summary>Game is quitting: put everything back (graphics values and process priority).</summary>
    internal static void Shutdown()
    {
        try
        {
            foreach (var k in Urp.Values) { k.Render.ReleaseAll(); k.Shadow.ReleaseAll(); }
            Urp.Clear();
            Lod.ReleaseAll();
            Fps.ReleaseAll();
        }
        catch (Exception e) { LogOnce("shutdown restore", e); }
        try
        {
            if (_savedPriority != null) { using var p = Process.GetCurrentProcess(); RestorePriority(p); }
        }
        catch (Exception e) { LogOnce("shutdown priority", e); }
    }

    // ---------------------------------------------------------------- other features' settings, read generically

    private static object CfgValue(string section, string key)
    {
        try
        {
            var def = new ConfigDefinition(section, key);
            var f = ModConfig.File;
            return f != null && f.ContainsKey(def) ? f[def].BoxedValue : null;
        }
        catch { return null; }
    }

    private static bool CfgBool(string section, string key) => CfgValue(section, key) is bool b && b;

    private static int CfgInt(string section, string key) => CfgValue(section, key) is int i ? i : 0;
}
