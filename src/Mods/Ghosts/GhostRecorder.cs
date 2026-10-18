using System;
using UnityEngine;
using VehiclePhysics;

namespace OverTheHillPlus.Ghosts;

/// <summary>
/// Records your own car at <see cref="GhostCodec.Hz"/>: its root position and rotation and each wheel's steering,
/// spin and suspension. Only reads the car (nothing is changed or sent).
/// </summary>
internal static class GhostRecorder
{
    private static GhostRecording _rec;
    private static VPVehicleController _car;
    private static Transform _root;
    private static GhostRig.WheelVisual[] _wheels = Array.Empty<GhostRig.WheelVisual>();
    private static double[] _spin = Array.Empty<double>();
    private static float[] _steer = Array.Empty<float>();
    // The wheel states, fetched once at the start: every wheelState read builds a new interop array.
    private static VehicleBase.WheelState[] _states = Array.Empty<VehicleBase.WheelState>();
    private static float _next;
    private static bool _invertSpin;

    public static bool Recording => _rec != null;

    /// <summary>The Race Mode track this recording is for ("" for a free drive).</summary>
    public static string TrackId => _rec?.TrackId ?? "";

    public static float Seconds => _rec == null ? 0f : _rec.Duration;

    /// <summary>Starts recording your car; returns null, or why it can't.</summary>
    public static string Start(string trackId = "")
    {
        var car = GameState.LocalCar;
        if (car == null) return "Get in a car first.";
        try
        {
            _car = car;
            _root = car.transform;
            _wheels = GhostRig.Wheels(car);
            _spin = new double[_wheels.Length];
            _steer = new float[_wheels.Length];
            _states = new VehicleBase.WheelState[_wheels.Length];
            var states = car.wheelState;
            for (int i = 0; i < _states.Length && states != null && i < states.Length; i++) _states[i] = states[i];
            try { _invertSpin = car.invertVisualWheelSpinDirection; } catch (Exception ex) { DebugLog.Error("ghosts", ex, "GhostRecorder.Start"); _invertSpin = false; }
            _rec = new GhostRecording
            {
                Car = GhostRig.CarName(car),
                TrackId = trackId ?? "",
                CreatedUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                WheelCount = _wheels.Length,
            };
            _next = Time.time;
            for (int i = 0; i < _states.Length; i++) _steer[i] = _states[i]?.steeringAngle ?? 0f;
            Sample();
            DebugLog.Info(GhostsPlugin.ModId, $"recording started: {_rec.Car}, {_wheels.Length} wheels, track '{_rec.TrackId}'");
            return null;
        }
        catch (Exception e)
        {
            DebugLog.Error("ghosts", e, "GhostRecorder.Start");
            Clear();
            GhostsPlugin.Logger.LogWarning("Couldn't start recording: " + e.Message);
            return "Couldn't start recording.";
        }
    }

    /// <summary>Stops and hands back the drive (null when nothing was recording).</summary>
    public static GhostRecording Stop()
    {
        var rec = _rec;
        if (rec != null) DebugLog.Info(GhostsPlugin.ModId, $"recording stopped: {rec.Frames.Count} frames, {rec.Duration:0.0} s");
        Clear();
        return rec;
    }

    public static void Cancel() => Clear();

    private static void Clear()
    {
        _rec = null;
        _car = null;
        _root = null;
        _wheels = Array.Empty<GhostRig.WheelVisual>();
        _states = Array.Empty<VehicleBase.WheelState>();
    }

    /// <summary>
    /// Every frame: wheel spin is integrated each frame (a fast wheel turns a lot between samples), and a sample is
    /// taken whenever one is due. Returns a finished drive when the car went away or the length limit was reached.
    /// </summary>
    public static GhostRecording Tick(out string why)
    {
        why = null;
        if (_rec == null) return null;
        if (_car == null || _root == null || GameState.LocalCar == null || GameState.LocalCar.Pointer != _car.Pointer)
        {
            why = "Recording stopped: you left the car.";
            return Stop();
        }

        float dt = Time.deltaTime;
        try
        {
            for (int i = 0; i < _states.Length; i++)
            {
                var s = _states[i];
                if (s == null) continue;
                double degPerSec = s.angularVelocity * Mathf.Rad2Deg;
                _spin[i] += (_invertSpin ? -degPerSec : degPerSec) * dt;
                _steer[i] = s.steeringAngle;
            }
        }
        catch (Exception ex) { DebugLog.Error("ghosts", ex, "GhostRecorder.Tick"); }

        float step = 1f / _rec.Hz;
        // A long hitch would add one stretched sample per missed slot: cap the catch-up.
        int due = 0;
        while (Time.time >= _next && due < 10)
        {
            Sample();
            due++;
        }
        if (due == 10) _next = Time.time + step;

        int max = Mathf.Clamp(GhostsConfig.MaxMinutes?.Value ?? 30, 1, 120) * 60 * _rec.Hz;
        if (_rec.Frames.Count >= Math.Min(max, GhostCodec.MaxFrames))
        {
            why = "Recording stopped at its length limit.";
            return Stop();
        }
        return null;
    }

    private static void Sample()
    {
        _next += 1f / _rec.Hz;
        var f = new GhostFrame(_rec.WheelCount);
        var p = _root.position;
        var q = _root.rotation;
        f.Px = p.x; f.Py = p.y; f.Pz = p.z;
        f.Qx = q.x; f.Qy = q.y; f.Qz = q.z; f.Qw = q.w;
        for (int i = 0; i < _rec.WheelCount; i++)
        {
            f.Wheels[i] = new GhostWheel
            {
                Steer = _steer[i],
                Spin = (float)_spin[i],
                Suspension = GhostRig.Height(_root, _wheels[i].Wheel),
            };
        }
        _rec.Frames.Add(f);
    }
}
