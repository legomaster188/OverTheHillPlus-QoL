using System;
using UnityEngine;

namespace OverTheHillPlus.ControllerFeel;

// After the namespace line on purpose: the game has its own global ModManager (mod.io).
using OverTheHillPlus.Core.Manager;

/// <summary>
/// Controller Feel's per-frame flow.
/// 1. <see cref="Tick"/> (core's after-ticks hook): works out whether the pad may rumble at all (<see cref="FeelGate"/>),
///    reads the car (<see cref="FeelSampler"/>) and mixes this frame's effects (<see cref="FeelMixer"/>).
/// 2. <see cref="AfterGameUpdate"/> (a postfix on the game's <c>ControllerVibrationManager.Update</c>, which has just written
///    the game's own rumble): stacks the game's output (scaled by [Game] GameRumble) with ours, scaled by the game's own
///    Controller Vibration slider, and writes the result to the pad. When that patch is not running, <see cref="Tick"/> writes
///    instead (a frame behind the game's write at worst).
/// The motors go to 0 the moment the gate closes (pause, a menu, a different window, no car), when the car changes, when the
/// mod is switched off and when the game quits; a failure anywhere stops the mod for the session with the motors at 0.
/// Everything is local: nothing is sent to anyone.
/// </summary>
internal static class ControllerFeelCore
{
    private static readonly FeelMixer Mixer = new();
    private static readonly FeelSettings Settings = new();

    private static FeelOutput _ours = FeelOutput.Zero;
    private static IntPtr _car;
    private static bool _failed, _wasOpen, _loggedConfig;
    private static float _lastHooked = -100f;
    private static string _closed = "starting";
    private static float _testStart = -100f;
    private static bool _testing;
    private const float TestLength = 1.5f;

    /// <summary>The mod is working this session (not failed).</summary>
    public static bool Active => !_failed;

    /// <summary>Why the motors are off right now, or null while they may run (the MODS page).</summary>
    public static string ClosedBecause => _failed ? "stopped after an error (see the log)" : _closed;

    /// <summary>Our own output of the last frame (before the game's rumble is stacked), for the MODS page.</summary>
    public static FeelOutput Ours => _ours;

    public static float[] Parts => Mixer.Parts;

    public static void Install()
    {
        CoreBehaviour.OnQuit += () => PadOutput.Stop("quit");
    }

    private static FeelGate.State GateState(ControllerVibrationManager mgr)
    {
        bool usingPad = true;
        bool running = Time.timeScale > 0.01f;
        if (mgr != null)
        {
            try
            {
                if (ControllerFeelConfig.OnlyWithPad.Value) usingPad = ControllerVibrationManager.IsGamepadSchemeActive();
                if (mgr.pauseMenuOpen || mgr.applicationPaused || !mgr.CanAcceptPhysical) running = false;
            }
            catch (Exception ex) { DebugLog.Error(ControllerFeelPlugin.ModId, ex, "ControllerFeelCore.GateState"); }
        }
        return new FeelGate.State(
            modOn: ControllerFeelConfig.Rumble.Value && ModManager.IsLoaded(ControllerFeelPlugin.ModId),
            hasPad: PadOutput.Current() != null,
            usingPad: usingPad,
            driving: CameraView.Driving,
            onTop: CameraView.DrivingOnTop && !CoreBehaviour.MenuOpen,
            focused: Application.isFocused,
            hasCar: GameState.LocalCar != null,
            running: running);
    }

    private static ControllerVibrationManager Manager()
    {
        try { return ControllerVibrationManager.Instance; }
        catch (Exception ex) { DebugLog.Error(ControllerFeelPlugin.ModId, ex, "ControllerFeelCore.Manager"); return null; }
    }

    /// <summary>Every frame (core's after-ticks hook).</summary>
    public static void Tick()
    {
        if (_failed || ControllerFeelConfig.Rumble == null) return;
        try
        {
            var mgr = Manager();
            if (!_loggedConfig && mgr != null)
            {
                _loggedConfig = true;
                try { FeelSampler.LogGameThresholds(mgr.Config); } catch (Exception ex) { DebugLog.Error(ControllerFeelPlugin.ModId, ex, "ControllerFeelCore.Tick"); }
            }
            if (RunTest(mgr)) return;
            var gate = GateState(mgr);
            string closed = FeelGate.Closed(gate);
            _closed = closed;
            IntPtr car = GameState.LocalCarPtr;
            if (car != _car)
            {
                // A different car (or none): quiet, and the old car's envelopes must not ring into the new one.
                _car = car;
                Mixer.Reset();
                _ours = FeelOutput.Zero;
                if (PadOutput.Running) PadOutput.Stop("car changed");
            }
            if (closed != null)
            {
                if (_wasOpen || PadOutput.Running) PadOutput.Stop(closed);
                _wasOpen = false;
                Mixer.Reset();
                _ours = FeelOutput.Zero;
                return;
            }
            _wasOpen = true;

            float dt = Mathf.Min(Time.unscaledDeltaTime, 0.1f);
            ControllerFeelConfig.Fill(Settings, PadOutput.TriggersAvailable);
            if (FeelSampler.Read(dt, out var input)) _ours = Mixer.Step(input, Settings, dt);
            else _ours = FeelOutput.Zero;

            // The game's rumble update normally runs the merge (AfterGameUpdate) right after its own write; this is the fallback.
            if (Time.unscaledTime - _lastHooked > 0.25f) Merge(mgr);
        }
        catch (Exception e) { Fail(e, "ControllerFeelCore.Tick"); }
    }

    /// <summary>After the game's <c>ControllerVibrationManager.Update</c> (a Harmony postfix).</summary>
    public static void AfterGameUpdate(ControllerVibrationManager mgr)
    {
        if (_failed || ControllerFeelConfig.Rumble == null) return;
        try
        {
            _lastHooked = Time.unscaledTime;
            if (RunTest(mgr)) return;
            if (_closed != null) return;
            Merge(mgr);
        }
        catch (Exception e) { Fail(e, "ControllerFeelCore.AfterGameUpdate"); }
    }

    private static void Merge(ControllerVibrationManager mgr)
    {
        float keep = Mathf.Clamp01(ControllerFeelConfig.GameRumble.Value);
        // Our rumble follows the game's own Controller Vibration slider (0 = no vibration at all).
        float level = 1f;
        FeelOutput game = FeelOutput.Zero;
        if (mgr != null)
        {
            try
            {
                level = Mathf.Clamp01(mgr.MasterIntensity);
                var last = mgr.lastOutput;
                game = new FeelOutput(last.LF, last.HF);
            }
            catch (Exception ex) { DebugLog.Error(ControllerFeelPlugin.ModId, ex, "ControllerFeelCore.Merge"); }
        }
        var ours = _ours.Scaled(level);
        // Nothing to add and the game's rumble untouched: leave the pad to the game.
        if (ours.IsZero && keep >= 0.999f)
        {
            if (PadOutput.Running) PadOutput.Write(game);
            return;
        }
        PadOutput.Write(FeelOutput.Over(game.Scaled(keep), ours));
    }

    /// <summary>The MODS page's "Test now": the heavy motor, then the light one, then both, about 1.5 s in all.</summary>
    public static void StartTest() => _testStart = Time.unscaledTime;

    /// <summary>Plays the test pattern while it runs (even in the MODS page, where the gate is closed). True while it does.</summary>
    private static bool RunTest(ControllerVibrationManager mgr)
    {
        float t = Time.unscaledTime - _testStart;
        if (t < 0f || t > TestLength)
        {
            if (_testing)
            {
                _testing = false;
                PadOutput.Stop("test over");
            }
            return false;
        }
        _testing = true;
        float level = 1f;
        try { if (mgr != null) level = Mathf.Clamp01(mgr.MasterIntensity); } catch (Exception ex) { DebugLog.Error(ControllerFeelPlugin.ModId, ex, "ControllerFeelCore.RunTest"); }
        level *= Mathf.Clamp01(ControllerFeelConfig.Master.Value);
        var o = t < 0.5f ? new FeelOutput(0.7f, 0f) : t < 1f ? new FeelOutput(0f, 0.7f) : new FeelOutput(0.5f, 0.5f);
        PadOutput.Write(o.Scaled(level));
        return true;
    }

    private static void Fail(Exception e, string where)
    {
        DebugLog.Error(ControllerFeelPlugin.ModId, e, where);
        ControllerFeelPlugin.Logger?.LogError("Controller Feel failed and is off for this session (the motors are stopped): " + e);
        _failed = true;
        PadOutput.Stop("failed");
    }

    /// <summary>The MODS page: puts the game's own rumble to 0 (this mod only) or back to all of it.</summary>
    public static void TakeOver(bool takeOver) => ControllerFeelConfig.GameRumble.Value = takeOver ? 0f : 1f;

    /// <summary>Why nothing is shown or felt, for the MODS page, or null while it works.</summary>
    public static string Problem()
    {
        if (_failed) return "Controller Feel stopped after an error (see the log); the motors are off.";
        if (PadOutput.Current() == null) return "No gamepad found. Plug one in or switch it on.";
        return null;
    }
}
