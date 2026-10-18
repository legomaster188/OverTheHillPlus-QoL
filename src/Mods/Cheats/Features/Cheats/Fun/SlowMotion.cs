using System;
using Fusion.Addons.Physics;
using OverTheHillPlus.CheatsPlus;
using UnityEngine;

namespace OverTheHillPlus.Features.Cheats.Fun;

/// <summary>
/// Single-player slow motion. Mirrors what the game's own TimeManager.RPC_SetTimeScale does (read from its
/// decompiled body): it sets OverlandNetworkRunner.Instance.RunnerSimulatePhysics3D.DeltaTimeMultiplier (the Fusion
/// physics step length) and Time.timeScale (animation, VFX, audio timing). Both are session-wide, which is why this is
/// only applied in a single-player game; anywhere else (menu, multiplayer) the values are put back to 1.
/// Only values this class wrote are ever restored, and a pause that sets Time.timeScale to 0 is left alone.
/// </summary>
internal static class SlowMotion
{
    private static float _applied = 1f;              // the scale we last wrote (1 = nothing applied)
    private static RunnerSimulatePhysics3D _sim;     // the physics runner we changed
    private static float _nextCheck;
    private static bool _loggedError;

    public static bool Applied => _applied < 0.999f;

    public static string Status => Applied ? $"{_applied:0.00}x" : "normal speed";

    public static bool IsSolo
    {
        get
        {
            var r = GameState.Runner;
            if (r == null) return false;
            try { return r.IsSinglePlayer; } catch (Exception ex) { DebugLog.Error("cheats", ex, "SlowMotion.IsSolo"); return false; }
        }
    }

    public static void Tick()
    {
        try
        {
            float want = FunPhysics.SlowMo.Value && IsSolo ? Mathf.Clamp(FunPhysics.SlowMoScale.Value, 0.05f, 1f) : 1f;

            if (want >= 0.999f)
            {
                if (Applied) Restore();
                return;
            }

            // Re-assert a few times a second: a scene change replaces the runner, an unpause resets timeScale to 1.
            if (Mathf.Abs(want - _applied) < 0.0001f && Time.unscaledTime < _nextCheck) return;
            _nextCheck = Time.unscaledTime + 0.5f;
            Apply(want);
        }
        catch (Exception e)
        {
            if (_loggedError) return;
            _loggedError = true;
            CheatsPlugin.Logger.LogError("Fun: slow motion failed (further errors suppressed): " + e);
        }
    }

    private static void Apply(float scale)
    {
        var sim = FindSim();
        if (sim != null)
        {
            if (_sim != null && _sim.Pointer != sim.Pointer) RestoreSim();
            if (Mathf.Abs(sim.DeltaTimeMultiplier - scale) > 0.0001f) sim.DeltaTimeMultiplier = scale;
            _sim = sim;
        }

        float ts = Time.timeScale;
        // 0 = the game is paused; leave it, we re-apply once it unpauses.
        if (ts > 0f && Mathf.Abs(ts - scale) > 0.0001f) Time.timeScale = scale;

        if (Mathf.Abs(_applied - scale) > 0.0001f)
        {
            OverTheHillPlus.AdvantageFlag.MarkUsed();
            CheatsPlugin.Logger.LogInfo($"Fun: slow motion {scale:0.00}x");
        }
        _applied = scale;
    }

    private static void Restore()
    {
        try
        {
            // Only undo our own value (the game's own SetTimeScale command may have set something else since).
            if (Mathf.Abs(Time.timeScale - _applied) < 0.0001f) Time.timeScale = 1f;
        }
        catch (Exception ex) { DebugLog.Error("cheats", ex, "SlowMotion.Restore"); }
        RestoreSim();
        CheatsPlugin.Logger.LogInfo("Fun: slow motion off");
        _applied = 1f;
    }

    private static void RestoreSim()
    {
        try
        {
            if (_sim != null && Mathf.Abs(_sim.DeltaTimeMultiplier - _applied) < 0.0001f) _sim.DeltaTimeMultiplier = 1f;
        }
        catch (Exception ex) { DebugLog.Error("cheats", ex, "SlowMotion.RestoreSim"); } // runner destroyed with its scene: nothing left to restore
        _sim = null;
    }

    private static RunnerSimulatePhysics3D FindSim()
    {
        try
        {
            var onr = OverlandNetworkRunner.Instance;
            if (onr == null) return null;
            return onr.RunnerSimulatePhysics3D;
        }
        catch (Exception ex) { DebugLog.Error("cheats", ex, "SlowMotion.FindSim"); return null; }
    }
}
