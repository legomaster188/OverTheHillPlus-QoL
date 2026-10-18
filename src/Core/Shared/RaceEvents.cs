using System;

namespace OverTheHillPlus.Shared;

/// <summary>
/// What a mod that follows races (Ghost Replays) needs from Race Mode, without referencing it: the track being raced
/// and three events about the LOCAL player's own run (host or guest alike). Race Mode calls <see cref="Begin"/>,
/// <see cref="Finish"/> and <see cref="Cancel"/>; each is a no-op when it doesn't fit the run's state, so a run ends
/// exactly once: <see cref="Started"/> then either <see cref="Finished"/> or <see cref="Cancelled"/>.
/// Pure logic (no game types). Main thread only. A subscriber that throws doesn't stop the others.
/// </summary>
internal static class RaceEvents
{
    /// <summary>Your run begins (GO): track id and display name.</summary>
    public static event Action<string, string> Started;

    /// <summary>You crossed the finish: track id and your time in seconds.</summary>
    public static event Action<string, double> Finished;

    /// <summary>Your run ended without a finish (out, left, reset, stopped): track id.</summary>
    public static event Action<string> Cancelled;

    /// <summary>The track Race Mode has loaded (the one raced, or about to be); "" when none.</summary>
    public static string TrackId { get; private set; } = "";

    public static string TrackName { get; private set; } = "";

    /// <summary>Your run is on (between GO and its end).</summary>
    public static bool Active { get; private set; }

    /// <summary>Where a throwing subscriber is reported (core's logger; null in tests). Each event logs once.</summary>
    public static Action<string> LogWarning;

    private static string _runTrackId = "";
    private static readonly System.Collections.Generic.HashSet<string> Logged = new();

    /// <summary>Race Mode: the loaded track changed (null/empty = none).</summary>
    public static void SetTrack(string id, string name)
    {
        TrackId = id ?? "";
        TrackName = name ?? "";
    }

    /// <summary>Race Mode: your run starts. A run still open is cancelled first.</summary>
    public static void Begin(string trackId, string trackName)
    {
        if (string.IsNullOrEmpty(trackId)) return;
        Cancel();
        Active = true;
        _runTrackId = trackId;
        Raise("Started", Started, h => h(trackId, string.IsNullOrWhiteSpace(trackName) ? trackId : trackName));
    }

    /// <summary>Race Mode: you finished in <paramref name="seconds"/>.</summary>
    public static void Finish(double seconds)
    {
        if (!Active) return;
        string id = _runTrackId;
        Active = false;
        Raise("Finished", Finished, h => h(id, seconds));
    }

    /// <summary>Race Mode: your run ended without a finish.</summary>
    public static void Cancel()
    {
        if (!Active) return;
        string id = _runTrackId;
        Active = false;
        Raise("Cancelled", Cancelled, h => h(id));
    }

    private static void Raise<T>(string what, T handlers, Action<T> call) where T : Delegate
    {
        if (handlers == null) return;
        foreach (var d in handlers.GetInvocationList())
        {
            try { call((T)(object)d); }
            catch (Exception e)
            {
                DebugLog.Error("core", e, "RaceEvents.Raise");
                try { if (Logged.Add(what)) LogWarning?.Invoke($"[RaceEvents] a {what} subscriber threw (further errors suppressed): {e.Message}"); }
                catch (Exception ex) { DebugLog.Error("core", ex, "RaceEvents.Raise"); /* logging must never break a race */ }
            }
        }
    }
}
