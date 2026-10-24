using System;
using System.Collections.Generic;

namespace OverTheHillPlus.Logging;

/// <summary>
/// At most <c>burst</c> events per <c>interval</c> seconds for each key (a ModNet channel, a mod, ...); the rest are
/// counted, and the next event that is let through says how many were left out since the last one ("+12 not shown").
/// Keeps a chatty channel from filling the debug log without hiding that it is chatty. Not thread-safe by itself: the
/// caller (main thread) owns it. Pure.
/// </summary>
internal sealed class KeyedRateLimiter<TKey>
{
    private sealed class State
    {
        public double WindowStart = double.MinValue;
        public int Count;
        public int Suppressed;
        public long Total;
    }

    private readonly Dictionary<TKey, State> _states = new();
    private readonly double _interval;
    private readonly int _burst;
    private readonly int _capacity;

    public KeyedRateLimiter(double intervalSeconds, int burst = 1, int capacity = 256)
    {
        _interval = Math.Max(0, intervalSeconds);
        _burst = Math.Max(1, burst);
        _capacity = Math.Max(1, capacity);
    }

    /// <summary>Keys being tracked.</summary>
    public int Keys => _states.Count;

    /// <summary>
    /// True when an event of <paramref name="key"/> at <paramref name="now"/> (seconds) may be logged.
    /// <paramref name="suppressed"/> is how many events of this key were refused since the last one that was let
    /// through (0 when refused or when none were).
    /// </summary>
    public bool Allow(TKey key, double now, out int suppressed)
    {
        suppressed = 0;
        if (!_states.TryGetValue(key, out var s))
        {
            if (_states.Count >= _capacity) _states.Clear();
            _states[key] = s = new State();
        }
        s.Total++;
        if (now >= s.WindowStart + _interval)
        {
            s.WindowStart = now;
            s.Count = 0;
        }
        if (s.Count < _burst)
        {
            s.Count++;
            suppressed = s.Suppressed;
            s.Suppressed = 0;
            return true;
        }
        s.Suppressed++;
        return false;
    }

    /// <summary>Events seen for the key, shown or not.</summary>
    public long Total(TKey key) => _states.TryGetValue(key, out var s) ? s.Total : 0;

    /// <summary>Events of the key refused and not yet reported by an allowed one.</summary>
    public int Pending(TKey key) => _states.TryGetValue(key, out var s) ? s.Suppressed : 0;
}
