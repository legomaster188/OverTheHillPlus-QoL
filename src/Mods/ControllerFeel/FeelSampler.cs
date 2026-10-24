using System;
using UnityEngine;
using VehiclePhysics;

namespace OverTheHillPlus.ControllerFeel;

/// <summary>
/// Reads your own car into a <see cref="FeelInput"/> (read-only: nothing is changed on the car).
/// - Every frame: speed, vertical speed, pedals and rpm from the vehicle physics' data bus (fixed point x1000 for speed and
///   rpm, x10000 for the pedals, as the other mods read them), the wheels (on the ground, what they roll on, the slips,
///   drive and brake torque) from <c>VPVehicleController.wheelState</c>, and the winch from <c>VehicleWinchSystem</c>.
/// - Every physics step (<see cref="Step"/>, from a patch on <c>DoUpdateBlocks</c>): the peak suspension speed and the most
///   suspension travel used, so a hit shorter than a frame is not missed. The peaks are handed over and cleared by <see cref="Read"/>.
/// </summary>
internal static class FeelSampler
{
    private const int MaxWheels = 8;
    /// <summary>The suspension counts as bottomed out from this much of its travel (the game's own number is read when its rumble config is found).</summary>
    private static float _bottomRatio = 0.92f;

    private static bool _ids;
    private static int _chInput, _chVehicle, _throttle, _brake, _rpm, _speed, _engineWorking;
    private static IntPtr _car;
    private static VehicleWinchSystem _winch;
    private static float _nextFind;
    private static int _findTries;
    private static float _peakSusp, _peakBottom;
    private static readonly float[] Share = new float[SurfaceProfiles.Count];
    private static bool _loggedThresholds;

    private static void Ids()
    {
        if (_ids) return;
        _chInput = Channel.Input;
        _chVehicle = Channel.Vehicle;
        _throttle = InputData.Throttle;
        _brake = InputData.Brake;
        _rpm = VehicleData.EngineRpm;
        _speed = VehicleData.Speed;
        _engineWorking = VehicleData.EngineWorking;
        _ids = true;
    }

    /// <summary>Another car (or none): forget what was learnt about the old one.</summary>
    internal static void Forget()
    {
        _car = IntPtr.Zero;
        _winch = null;
        _findTries = 0;
        _nextFind = 0f;
        _peakSusp = _peakBottom = 0f;
    }

    /// <summary>The physics step of the local car: remember the hardest suspension hit since the last frame.</summary>
    internal static void Step(VPVehicleController v)
    {
        var states = v.wheelState;
        int n = states == null ? 0 : Math.Min(Math.Min(v.wheelCount, states.Length), MaxWheels);
        for (int i = 0; i < n; i++)
        {
            var ws = states[i];
            if (ws == null || !ws.enabled) continue;
            var s = ws.suspensionState;
            float speed = Math.Abs(s.contactSpeed);
            if (float.IsFinite(speed) && speed > _peakSusp) _peakSusp = speed;
            float ratio = s.compressionRatio;
            if (float.IsFinite(ratio) && ratio > _peakBottom) _peakBottom = ratio;
        }
    }

    /// <summary>Reads the local car for this frame; false when there is none.</summary>
    internal static bool Read(float dt, out FeelInput input)
    {
        input = default;
        var car = GameState.LocalCar;
        if (car == null)
        {
            if (_car != IntPtr.Zero) Forget();
            return false;
        }
        if (car.Pointer != _car)
        {
            Forget();
            _car = car.Pointer;
        }
        Ids();
        input.Dt = dt;
        var data = car.data;
        if (data == null) return false;
        input.EngineOn = data.Get(_chVehicle, _engineWorking) > 0;
        input.Rpm = data.Get(_chVehicle, _rpm) / 1000f;
        input.Throttle = Mathf.Clamp01(data.Get(_chInput, _throttle) / 10000f);
        input.Brake = Mathf.Clamp01(data.Get(_chInput, _brake) / 10000f);
        input.SpeedMs = Mathf.Abs(data.Get(_chVehicle, _speed) / 1000f);
        var rb = car.cachedRigidbody;
        if (rb != null) input.VerticalSpeed = rb.linearVelocity.y;

        ReadWheels(car, ref input);

        input.SuspensionPeak = _peakSusp;
        input.BottomOut = Mathf.Clamp01((_peakBottom - _bottomRatio) / Mathf.Max(0.02f, 1f - _bottomRatio));
        _peakSusp = _peakBottom = 0f;

        ReadWinch(car, ref input);
        return true;
    }

    private static void ReadWheels(VPVehicleController car, ref FeelInput input)
    {
        Array.Clear(Share, 0, Share.Length);
        // Each read of wheelState makes a new interop wrapper: read it once.
        var states = car.wheelState;
        int n = states == null ? 0 : Math.Min(Math.Min(car.wheelCount, states.Length), MaxWheels);
        int wheels = 0, grounded = 0;
        float drive = 0f, brake = 0f, lateral = 0f;
        for (int i = 0; i < n; i++)
        {
            var ws = states[i];
            if (ws == null || !ws.enabled) continue;
            wheels++;
            if (!ws.grounded) continue;
            grounded++;
            Share[(int)SurfaceOf(ws)] += 1f;
            float slip = Math.Abs(ws.slipRatio);
            if (!float.IsFinite(slip)) slip = 0f;
            if (ws.driveTorque > 1f) drive = Math.Max(drive, slip);
            if (ws.brakeTorque > 1f) brake = Math.Max(brake, slip);
            float side = Math.Abs(ws.normalizedSlip);
            if (float.IsFinite(side)) lateral = Math.Max(lateral, side);
        }
        if (grounded > 0)
            for (int k = 0; k < Share.Length; k++) Share[k] /= grounded;
        input.Wheels = wheels;
        input.Grounded = grounded;
        input.SurfaceShare = Share;
        input.DriveSlip = drive;
        input.BrakeSlip = brake;
        input.LateralSlip = lateral;
    }

    private static Surface SurfaceOf(VehicleBase.WheelState ws)
    {
        MaterialLabel label;
        try
        {
            var cfg = ws.groundMaterial?.Config;
            if (cfg == null) return Surface.Paved;
            label = cfg.Label;
        }
        catch (Exception ex) { DebugLog.Error(ControllerFeelPlugin.ModId, ex, "FeelSampler.SurfaceOf"); return Surface.Paved; }
        return label switch
        {
            MaterialLabel.Grass => Surface.Grass,
            MaterialLabel.Gravel => Surface.Gravel,
            MaterialLabel.Mud or MaterialLabel.DeepMud => Surface.Mud,
            MaterialLabel.Dirt => Surface.Dirt,
            MaterialLabel.Water => Surface.Water,
            MaterialLabel.Rock => Surface.Rock,
            MaterialLabel.LooseRock => Surface.LooseRock,
            MaterialLabel.WoodPlank or MaterialLabel.WoodTrunk => Surface.Wood,
            MaterialLabel.Snow or MaterialLabel.DeepSnow or MaterialLabel.ShallowSnow => Surface.Snow,
            MaterialLabel.Ice => Surface.Ice,
            MaterialLabel.Sand or MaterialLabel.DeepSand or MaterialLabel.QuickSand => Surface.Sand,
            MaterialLabel.Bricks => Surface.Bricks,
            _ => Surface.Paved,
        };
    }

    private static void ReadWinch(VPVehicleController car, ref FeelInput input)
    {
        if (_winch == null && _findTries < 5 && Time.unscaledTime >= _nextFind)
        {
            _findTries++;
            _nextFind = Time.unscaledTime + 2f;
            try
            {
                _winch = car.GetComponent<VehicleWinchSystem>();
                if (_winch == null) _winch = car.GetComponentInChildren<VehicleWinchSystem>();
                if (_winch == null) _winch = car.GetComponentInParent<VehicleWinchSystem>();
            }
            catch (Exception ex) { DebugLog.Error(ControllerFeelPlugin.ModId, ex, "FeelSampler.ReadWinch"); }
        }
        if (_winch == null) return;
        try
        {
            input.WinchTaut = _winch.IsTaut;
            input.WinchTension = Mathf.Clamp01(_winch.NormalizedTension);
        }
        catch (Exception ex)
        {
            DebugLog.Error(ControllerFeelPlugin.ModId, ex, "FeelSampler.ReadWinch");
            _winch = null;
            _findTries = 5;
        }
    }

    /// <summary>Writes the game's own slip thresholds to the log once (Info level), to tune ours against.</summary>
    internal static void LogGameThresholds(ControllerHapticsConfig cfg)
    {
        if (_loggedThresholds || cfg == null) return;
        _loggedThresholds = true;
        try
        {
            if (float.IsFinite(cfg.BottomOutCompressionRatio) && cfg.BottomOutCompressionRatio > 0.5f && cfg.BottomOutCompressionRatio < 1f)
                _bottomRatio = cfg.BottomOutCompressionRatio;
            DebugLog.Info(ControllerFeelPlugin.ModId,
                $"game haptics: longitudinal slip {cfg.LongitudinalSlipDeadZone:0.00}..{cfg.SevereLongitudinalSlip:0.00}, lateral {cfg.LateralSlipDeadZoneDegrees:0.0}..{cfg.SevereLateralSlipDegrees:0.0} deg, " +
                $"suspension speed {cfg.SuspensionMinorCompressionSpeed:0.00}/{cfg.SuspensionStrongCompressionSpeed:0.00}/{cfg.SuspensionMaximumCompressionSpeed:0.00} m/s, bottom-out ratio {cfg.BottomOutCompressionRatio:0.00}");
        }
        catch (Exception ex) { DebugLog.Error(ControllerFeelPlugin.ModId, ex, "FeelSampler.LogGameThresholds"); }
    }
}
