using System;
using System.Collections.Generic;
using System.Globalization;

namespace OverTheHillPlus.Logging;

/// <summary>
/// Every distinct error of the session, by signature (mod, hook, exception type or message, top frame): the first
/// sighting is logged in full, repeats only count ("x37 since 10:15:02"), and <see cref="TakeSummary"/> lists the ones
/// that repeated since the last summary. Thread-safe. Pure: no game types.
/// </summary>
internal sealed class ErrorBook
{
    internal sealed class Entry
    {
        public string Signature;
        public string ModId;
        /// <summary>One short line naming the error (for summaries).</summary>
        public string Title;
        /// <summary>The crash-trail line for this error, made once (repeats mark the trail without allocating).</summary>
        public string TrailText { get; set; }
        public int Count;
        public int CountAtSummary;
        public double FirstAt, LastAt;
    }

    private readonly Dictionary<string, Entry> _bySignature = new(StringComparer.Ordinal);
    private readonly int _capacity;
    private readonly object _lock = new();

    /// <summary>New errors refused because the book was full.</summary>
    public int Overflow { get; private set; }

    public int Count
    {
        get { lock (_lock) return _bySignature.Count; }
    }

    public ErrorBook(int capacity = 600) => _capacity = Math.Max(1, capacity);

    /// <summary>The signature of an error: who, where, what and the top frame.</summary>
    public static string Signature(string modId, string hook, string type, string topFrame) =>
        (modId ?? "?") + "|" + (hook ?? "") + "|" + (type ?? "") + "|" + (topFrame ?? "");

    /// <summary>
    /// Counts one sighting at <paramref name="now"/> (seconds). <paramref name="first"/> is true on the signature's
    /// first sighting (log it in full then). Returns null, counting it in <see cref="Overflow"/>, for a new signature
    /// once the book is full.
    /// </summary>
    public Entry Note(string signature, double now, out bool first, string modId = null, string title = null)
    {
        first = false;
        signature ??= "";
        lock (_lock)
        {
            if (_bySignature.TryGetValue(signature, out var e))
            {
                e.Count++;
                e.LastAt = now;
                return e;
            }
            if (_bySignature.Count >= _capacity)
            {
                Overflow++;
                return null;
            }
            e = new Entry { Signature = signature, ModId = modId ?? "?", Title = title ?? signature, Count = 1, CountAtSummary = 1, FirstAt = now, LastAt = now };
            _bySignature[signature] = e;
            first = true;
            return e;
        }
    }

    /// <summary>Copies of the entries, the most recently seen first (at most <paramref name="max"/>).</summary>
    public List<Entry> Recent(int max)
    {
        var list = new List<Entry>();
        lock (_lock)
        {
            foreach (var e in _bySignature.Values)
                list.Add(new Entry { Signature = e.Signature, ModId = e.ModId, Title = e.Title, Count = e.Count, FirstAt = e.FirstAt, LastAt = e.LastAt });
        }
        list.Sort((a, b) => b.LastAt.CompareTo(a.LastAt));
        if (list.Count > max) list.RemoveRange(max, list.Count - max);
        return list;
    }

    /// <summary>Counts one more sighting of a known entry (the cheap path for a catch block that fails every frame).</summary>
    public void Repeat(Entry entry, double now)
    {
        if (entry == null) return;
        lock (_lock)
        {
            entry.Count++;
            entry.LastAt = now;
        }
    }

    /// <summary>
    /// One line per error that repeated since the last summary, most repeats first: "[camera] NullReferenceException
    /// in ... x37 in the last 60 s (x120 since 10:15:02)". <paramref name="clock"/> turns a time into a label (null:
    /// seconds). Empty when nothing repeated.
    /// </summary>
    public List<string> TakeSummary(double now, double windowSeconds, Func<double, string> clock = null, int max = 20)
    {
        var rows = new List<(int delta, Entry e)>();
        lock (_lock)
        {
            foreach (var e in _bySignature.Values)
            {
                int delta = e.Count - e.CountAtSummary;
                if (delta <= 0) continue;
                rows.Add((delta, e));
                e.CountAtSummary = e.Count;
            }
        }
        rows.Sort((a, b) => b.delta != a.delta ? b.delta.CompareTo(a.delta) : string.CompareOrdinal(a.e.Signature, b.e.Signature));
        var lines = new List<string>(Math.Min(rows.Count, max) + 1);
        for (int i = 0; i < rows.Count && i < max; i++)
        {
            var (delta, e) = rows[i];
            string since = clock != null ? clock(e.FirstAt) : e.FirstAt.ToString("0.0", CultureInfo.InvariantCulture) + " s";
            lines.Add($"[{e.ModId}] {e.Title} x{delta} in the last {windowSeconds.ToString("0", CultureInfo.InvariantCulture)} s (x{e.Count} since {since})");
        }
        if (rows.Count > max) lines.Add($"... and {rows.Count - max} more repeating errors");
        return lines;
    }
}

/// <summary>
/// When Unity's stack traces for errors go off and back on (capturing one costs a lot in IL2CPP): off once one error
/// signature was seen more than <see cref="PerSignature"/> times (an error every frame), back on after
/// <see cref="QuietSeconds"/> without any such over-budget repeat, so a new distinct error later still gets its trace.
/// </summary>
internal sealed class TraceGate
{
    public int PerSignature = 50;
    public double QuietSeconds = 30;
    private double _lastNoisy;

    public bool On { get; private set; } = true;

    /// <summary>A sighting of a signature now seen <paramref name="count"/> times. True when traces must go off now.</summary>
    public bool Seen(int count, double now)
    {
        if (count <= PerSignature) return false;
        _lastNoisy = now;
        if (!On) return false;
        On = false;
        return true;
    }

    /// <summary>Called now and then. True when traces may come back on now.</summary>
    public bool Tick(double now)
    {
        if (On || now - _lastNoisy < QuietSeconds) return false;
        On = true;
        return true;
    }
}

/// <summary>At most one event per <c>interval</c> seconds and <c>max</c> in all; the rest are counted.</summary>
internal sealed class RateLimiter
{
    private readonly double _interval;
    private readonly int _max;
    private double _next = double.MinValue;
    private int _allowed;

    public RateLimiter(double intervalSeconds, int max = int.MaxValue)
    {
        _interval = Math.Max(0, intervalSeconds);
        _max = Math.Max(0, max);
    }

    /// <summary>Events refused since the last <see cref="TakeSuppressed"/>.</summary>
    public int Suppressed { get; private set; }

    /// <summary>True when an event at <paramref name="now"/> (seconds) may be logged.</summary>
    public bool Allow(double now)
    {
        if (_allowed >= _max || now < _next)
        {
            Suppressed++;
            return false;
        }
        _allowed++;
        _next = now + _interval;
        return true;
    }

    /// <summary>The refused count, reset to 0.</summary>
    public int TakeSuppressed()
    {
        int s = Suppressed;
        Suppressed = 0;
        return s;
    }
}
