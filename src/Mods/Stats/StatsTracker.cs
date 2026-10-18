using System;
using System.IO;
using BepInEx;
using UnityEngine;
using VehiclePhysics;

namespace OverTheHillPlus.Stats;

/// <summary>
/// Feeds the trip numbers from your own car (read-only): distance and time driven from its position each frame, top
/// speed from the vehicle physics' speed, jumps and flips from core's <see cref="CarAir"/>. The all-time numbers are
/// saved to BepInEx/config/oth.stats.json every 30 s while they change, at the end of a session and when the game quits.
/// </summary>
internal static class StatsTracker
{
    private const float SaveEvery = 30f;

    internal static TripRecorder Recorder { get; private set; } = new();

    private static string _path;
    private static IntPtr _carPtr;
    private static Vector3 _lastPos;
    private static bool _hasLast;
    private static float _nextSave;
    private static IntPtr _runnerPtr;
    private static bool _inSession;
    private static bool _loggedError, _loggedSave, _readOnly;

    internal static string FilePath => _path ??= Path.Combine(Paths.ConfigPath, "oth.stats.json");

    internal static void Load()
    {
        TripTotals saved = new();
        try
        {
            if (File.Exists(FilePath))
            {
                if (!TripStore.TryParse(File.ReadAllText(FilePath), out saved))
                {
                    // Unreadable (damaged or hand-edited): keep it aside, never overwrite it with fresh numbers.
                    string bad = FilePath + ".bad";
                    if (File.Exists(bad)) bad = FilePath + "." + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".bad";
                    File.Move(FilePath, bad);
                    StatsPlugin.Logger.LogWarning($"Trip Stats: {FilePath} could not be read; moved to {bad}. All-time numbers start again.");
                    saved = new TripTotals();
                }
            }
        }
        catch (Exception e)
        {
            DebugLog.Error("stats", e, "StatsTracker.Load");
            // Could not even read or move it: leave the file alone and don't save over it this session.
            _readOnly = true;
            saved = new TripTotals();
            StatsPlugin.Logger.LogWarning("Trip Stats: could not read " + FilePath + " (all-time numbers are not saved this session): " + e.Message);
        }
        Recorder = new TripRecorder(saved);
        DebugLog.Info(StatsPlugin.ModId, $"all-time numbers loaded from {Path.GetFileName(FilePath)}: {saved.DistanceM / 1000.0:0.0} km, {saved.Jumps} jumps (read-only: {_readOnly})");
    }

    /// <summary>True when the saved file could not be read or set aside: it is not overwritten this session.</summary>
    internal static bool ReadOnly => _readOnly;

    internal static void Save()
    {
        if (!Recorder.Dirty || _readOnly) return;
        try
        {
            // Written next to it first, then swapped in: a crash mid-write never leaves half a file.
            string tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, TripStore.Serialize(Recorder.Lifetime));
            if (File.Exists(FilePath)) File.Replace(tmp, FilePath, null);
            else File.Move(tmp, FilePath);
            Recorder.MarkSaved();
            DebugLog.Verbose(StatsPlugin.ModId, () => $"all-time numbers saved: {Recorder.Lifetime.DistanceM / 1000.0:0.0} km, {Recorder.Lifetime.Jumps} jumps");
        }
        catch (Exception e)
        {
            DebugLog.Error("stats", e, "StatsTracker.Save");
            if (_loggedSave) return;
            _loggedSave = true;
            StatsPlugin.Logger.LogWarning("Trip Stats: could not save " + FilePath + ": " + e.Message);
        }
    }

    internal static void OnLanded(JumpDetector.Landing l) => Recorder.Jump(l.AirTime, l.Distance, l.Flips);

    internal static void Tick()
    {
        try { Sample(); }
        catch (Exception e)
        {
            DebugLog.Error("stats", e, "StatsTracker.Tick");
            _hasLast = false;
            if (!_loggedError)
            {
                _loggedError = true;
                StatsPlugin.Logger.LogWarning("Trip Stats: reading the car failed (further errors not logged): " + e.Message);
            }
        }
        if (Time.unscaledTime >= _nextSave)
        {
            _nextSave = Time.unscaledTime + SaveEvery;
            Save();
        }
    }

    private static void Sample()
    {
        TrackSession();
        var car = GameState.LocalCar;
        if (car == null)
        {
            _carPtr = IntPtr.Zero;
            _hasLast = false;
            return;
        }
        if (car.Pointer != _carPtr)
        {
            // A new car (spawned, changed, respawned): its first position is not a step.
            DebugLog.Verbose(StatsPlugin.ModId, "a car appeared (spawned, changed or respawned): its first position is not counted as a step");
            _carPtr = car.Pointer;
            _hasLast = false;
        }
        var pos = car.transform.position;
        float step = _hasLast ? Vector3.Distance(pos, _lastPos) : 0f;
        _lastPos = pos;
        _hasLast = true;

        float kmh;
        // The data bus is fixed point (x1000), in m/s.
        try { kmh = Mathf.Abs(car.data.Get(Channel.Vehicle, VehicleData.Speed) / 1000f) * 3.6f; }
        catch (Exception ex) { DebugLog.Error("stats", ex, "StatsTracker.Sample"); kmh = 0f; }
        Recorder.Motion(Time.deltaTime, step, kmh);
    }

    /// <summary>A new session (joined, hosted or started solo) starts the session numbers again, if set so.</summary>
    private static void TrackSession()
    {
        var runner = GameState.Runner;
        IntPtr ptr = runner == null ? IntPtr.Zero : runner.Pointer;
        if (ptr == _runnerPtr) return;
        // A session ends when its runner goes away or is replaced straight by another one.
        bool ended = _inSession;
        bool started = ptr != IntPtr.Zero;
        _runnerPtr = ptr;
        _inSession = started;
        if (ended) Save();
        DebugLog.Info(StatsPlugin.ModId, started ? "session started" + (StatsConfig.NewSessionResets.Value ? ": session numbers start again" : "") : "session ended: numbers saved");
        if (started && StatsConfig.NewSessionResets.Value) Recorder.ResetSession();
    }

    internal static void ResetSession() => Recorder.ResetSession();

    internal static void ResetAllTime()
    {
        // Asked for: from now on the file is written again, even if it could not be read at the start.
        _readOnly = false;
        Recorder.ResetLifetime();
        Save();
    }
}
