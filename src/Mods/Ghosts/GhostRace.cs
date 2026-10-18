using System;
using OverTheHillPlus.Shared;

namespace OverTheHillPlus.Ghosts;

/// <summary>
/// Racing your ghosts: when a race starts, your best ghost on that track starts with it, and your drive is recorded;
/// a finished race is saved with its time (the fastest one is the next race's ghost). Local only.
/// </summary>
internal static class GhostRace
{
    private static string _trackName = "";

    /// <summary>The track Race Mode has loaded ("" when none or when Race Mode isn't there).</summary>
    public static string ActiveTrackId => RaceEvents.TrackId;

    public static string ActiveTrackName => RaceEvents.TrackName;

    private static bool _hooked;

    /// <summary>Listens to the local player's Race Mode run (core's <see cref="RaceEvents"/>); Race Mode may be absent.</summary>
    internal static void Hook()
    {
        if (_hooked) return;
        _hooked = true;
        RaceEvents.Started += (id, name) => Guard(() => Started(id, name));
        RaceEvents.Finished += (id, seconds) => Guard(() => Finished(id, (int)Math.Round(seconds * 1000.0)));
        RaceEvents.Cancelled += _ => Guard(Cancelled);
    }

    private static void Guard(Action a)
    {
        try { a(); }
        catch (Exception e) { DebugLog.Error("ghosts", e, "GhostRace.Guard"); GhostsPlugin.Logger?.LogWarning("[Ghosts] race hookup failed: " + e.Message); }
    }

    /// <summary>The race on <paramref name="trackId"/> starts now (end of the countdown).</summary>
    internal static void Started(string trackId, string trackName = null)
    {
        if (string.IsNullOrEmpty(trackId) || GhostsConfig.RaceBestGhost == null) return;
        _trackName = string.IsNullOrWhiteSpace(trackName) ? trackId : trackName;
        if (GhostsConfig.RecordRaces.Value)
        {
            // A free drive being recorded is kept, then the race recording starts.
            if (GhostRecorder.Recording) GhostsFrame.SaveDrive(GhostRecorder.Stop(), null);
            string why = GhostRecorder.Start(trackId);
            if (why != null) GhostsFrame.Notice("This race isn't recorded: " + why);
        }
        if (GhostsConfig.RaceBestGhost.Value)
        {
            var best = GhostLibrary.BestFor(trackId);
            string why = best == null ? null : GhostPlayer.Play(best, race: true);
            if (why != null) GhostsFrame.Notice("No ghost this race: " + why);
        }
    }

    /// <summary>You crossed the finish of <paramref name="trackId"/> in <paramref name="ms"/> milliseconds.</summary>
    internal static void Finished(string trackId, int ms)
    {
        if (GhostPlayer.ForRace) GhostPlayer.Stop();
        if (!GhostRecorder.Recording || !string.Equals(GhostRecorder.TrackId, trackId, StringComparison.OrdinalIgnoreCase)) return;
        var rec = GhostRecorder.Stop();
        if (rec == null || ms < 0) return;
        var previous = GhostLibrary.BestFor(trackId);
        rec.RaceTimeMs = ms;
        string time = GhostTime.Race(ms).Replace(':', 'm') + "s"; // file names can't hold ':'
        string saved = GhostLibrary.Save(rec, $"{_trackName} {time}");
        if (saved == null) return;
        bool best = previous == null || ms < previous.Header.RaceTimeMs;
        GhostsFrame.Notice(best ? $"New best ghost on {_trackName}: {GhostTime.Race(ms)}." : $"Race saved as \"{saved}\".");
    }

    /// <summary>The race ended without a finish (left, reset, host stopped it): nothing is saved.</summary>
    internal static void Cancelled()
    {
        if (GhostPlayer.ForRace) GhostPlayer.Stop();
        if (GhostRecorder.Recording && !string.IsNullOrEmpty(GhostRecorder.TrackId)) GhostRecorder.Cancel();
    }
}
