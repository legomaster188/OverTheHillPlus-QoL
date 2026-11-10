using System;

namespace OverTheHillPlus.Core.Shared;

/// <summary>
/// When a search of the whole world is worth running. Object.FindObjectsOfType walks every object the game has loaded,
/// whatever type it looks for: 20 ms and more with the cars' templates in memory, and seconds while the game loads something
/// in the background, because it waits for the loader's lock. What it finds only changes when something comes or goes, so
/// a search runs at its usual pace only for <see cref="SettleSeconds"/> after the world last changed (<see cref="WorldScan.Stamp"/>),
/// and then not at all until the next change, though every change gets at least one search, however late it comes round.
/// <see cref="Again"/> asks for one more round. Pure code (unit-tested).
/// </summary>
internal sealed class ScanPacer
{
    /// <summary>How long after a change the searches go on: a new object can wake up later than the change that made it
    /// (Fusion activates some copies a moment after they are made, a scene's objects come up over its first seconds).</summary>
    public const float SettleSeconds = 30f;

    private readonly float _every;
    private readonly float _settle;
    private int _stamp;
    private bool _started, _owed;
    private float _next, _until;

    /// <summary>What is looked for (for the report).</summary>
    public string Name { get; }

    /// <summary>Searches run so far.</summary>
    public int Runs { get; private set; }

    /// <param name="name">What it looks for.</param>
    /// <param name="everySeconds">The pace while the world is settling.</param>
    /// <param name="settleSeconds">How long that lasts after a change (0: <see cref="SettleSeconds"/>).</param>
    public ScanPacer(string name, float everySeconds, float settleSeconds = 0f)
    {
        Name = name ?? "?";
        _every = Math.Max(0f, everySeconds);
        _settle = settleSeconds > 0f ? settleSeconds : SettleSeconds;
    }

    /// <summary>A search should run now (the caller then says so with <see cref="Ran"/>).</summary>
    public bool Due(int stamp, float now)
    {
        if (!_started || stamp != _stamp)
        {
            _started = true;
            _stamp = stamp;
            _until = now + _settle;
            _owed = true;
        }
        return (now <= _until || _owed) && now >= _next;
    }

    /// <summary>The search ran.</summary>
    public void Ran(float now)
    {
        _next = now + _every;
        _owed = false;
        Runs++;
    }

    /// <summary>One more round, as after a change: what the last search found is gone, or a setting asks for it. Due at once.</summary>
    public void Again()
    {
        _started = false;
        _next = 0f;
    }
}

/// <summary>
/// Spreads something many objects do every so often over the frames between: each has its own beat within the period and comes
/// back to it, so a long frame that made them all late doesn't leave them all on the same frame from then on. Pure code
/// (unit-tested).
/// </summary>
internal static class Beats
{
    /// <summary>The next time after <paramref name="now"/> on the beat <paramref name="beat"/> (0 to 1 of the period).</summary>
    public static float Next(float now, float period, float beat)
    {
        float at = beat * period;
        return ((float)Math.Floor((now - at) / period) + 1f) * period + at;
    }
}
