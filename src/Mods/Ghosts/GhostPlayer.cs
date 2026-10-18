using UnityEngine;

namespace OverTheHillPlus.Ghosts;

/// <summary>Plays one ghost at a time: moves the ghost car along the drive, between samples by interpolation.</summary>
internal static class GhostPlayer
{
    private static GhostCar _car;
    private static GhostRecording _rec;
    private static GhostFrame _buf;
    private static bool _race;

    public static GhostEntry Playing { get; private set; }

    public static float Seconds { get; private set; }

    public static float Duration => _rec?.Duration ?? 0f;

    /// <summary>True when the ghost was started by a race (plays at 1x, doesn't loop).</summary>
    public static bool ForRace => Playing != null && _race;

    /// <summary>Starts <paramref name="entry"/> from the beginning; returns null, or why it can't.</summary>
    public static string Play(GhostEntry entry, bool race = false)
    {
        Stop();
        if (entry == null) return "That ghost is gone.";
        var car = GameState.LocalCar;
        if (car == null) return "Get in a car first: the ghost borrows its look.";
        // The list holds headers only: the frames are read now.
        var rec = GhostLibrary.Load(entry, out string error);
        if (rec == null) return $"Ghost \"{entry.Name}\" is damaged ({error}): skipped.";
        if (rec.Frames.Count == 0) return "That ghost is empty.";
        _car = GhostCar.Build(car);
        if (_car == null) return "Couldn't build the ghost car (see the log).";
        Playing = entry;
        _rec = rec;
        Seconds = 0f;
        _race = race;
        DebugLog.Info(GhostsPlugin.ModId, $"playing '{entry.Name}': {rec.Frames.Count} frames, {rec.Duration:0.0} s, {(race ? "for a race" : "free")}");
        _buf = new GhostFrame(rec.WheelCount);
        Apply();
        return null;
    }

    public static void Stop()
    {
        if (Playing != null) DebugLog.Verbose(GhostsPlugin.ModId, () => $"playback of '{Playing.Name}' stopped at {Seconds:0.0} s");
        _car?.Destroy();
        _car = null;
        Playing = null;
        _rec = null;
        Seconds = 0f;
        _race = false;
    }

    /// <summary>
    /// Every frame while a ghost plays. Returns a line to show when playback ended by itself;
    /// <paramref name="wasRace"/> says whether that ghost was a race's (read before it was cleared).
    /// </summary>
    public static string Tick(out bool wasRace)
    {
        wasRace = _race;
        if (Playing == null) return null;
        if (_car == null || !_car.Alive) { Stop(); return null; } // the scene changed under it
        float speed = _race ? 1f : Mathf.Clamp(GhostsConfig.PlaybackSpeed?.Value ?? 1f, 0.1f, 8f);
        Seconds += Time.deltaTime * speed;
        if (Seconds > _rec.Duration)
        {
            if (!_race && GhostsConfig.Loop != null && GhostsConfig.Loop.Value && _rec.Duration > 0f) Seconds = 0f;
            else
            {
                string name = Playing.Name;
                Stop();
                return $"Ghost \"{name}\" finished.";
            }
        }
        Apply();
        return null;
    }

    private static void Apply()
    {
        GhostSampler.Sample(_rec, Seconds, _buf);
        _car.Apply(_buf, _rec.WheelCount);
    }
}
