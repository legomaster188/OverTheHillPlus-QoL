using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace OverTheHillPlus.Logging;

internal enum HangEvent
{
    None,
    /// <summary>The main thread has not finished a frame for longer than the next threshold.</summary>
    Stalled,
    /// <summary>The main thread finished a frame again after a reported stall.</summary>
    Recovered,
}

/// <summary>
/// The hang watchdog's decisions (pure): a stall is reported when the main thread's last frame beat is older than the
/// first threshold (default 3 s), again at 10 s and 30 s, then each time the stall doubles (60 s, 120 s, ...); a new
/// beat after a reported stall is a recovery. Used from the watchdog thread only.
/// </summary>
internal sealed class HangWatch
{
    private long _beat;
    private bool _stalled;
    private double _next;

    public HangWatch(double firstSeconds)
    {
        First = firstSeconds;
        _next = First;
    }

    /// <summary>The first threshold in seconds (at least 0.5). Takes effect from the next beat.</summary>
    public double First { get; set; }

    /// <summary>The threshold after <paramref name="current"/>: 10 s, 30 s, then doubling.</summary>
    public static double NextThreshold(double current)
    {
        if (current < 10) return 10;
        if (current < 30) return 30;
        return current * 2;
    }

    /// <summary>
    /// Checks the main thread's last beat (a timestamp, 0 = no frame yet) at <paramref name="now"/>.
    /// <paramref name="seconds"/> is the stall length (Stalled) or how long that frame took (Recovered).
    /// </summary>
    public HangEvent Check(long beat, long now, double ticksPerSecond, out double seconds)
    {
        seconds = 0;
        if (beat == 0 || ticksPerSecond <= 0) return HangEvent.None;
        if (beat != _beat)
        {
            bool was = _stalled;
            long old = _beat;
            _beat = beat;
            _stalled = false;
            _next = Math.Max(0.5, First);
            if (was && old != 0)
            {
                seconds = (beat - old) / ticksPerSecond;
                return HangEvent.Recovered;
            }
            return HangEvent.None;
        }
        seconds = (now - beat) / ticksPerSecond;
        if (seconds < _next) return HangEvent.None;
        _stalled = true;
        while (_next <= seconds) _next = NextThreshold(_next);
        return HangEvent.Stalled;
    }
}

/// <summary>Timing of one hook (a feature's tick or overlay, a static hook, a guarded patch or Guard site).</summary>
internal sealed class HookStat
{
    public readonly string ModId;
    public readonly string Name;
    public long Calls, Ticks, MaxTicks;
    // Since the last timing table (Verbose).
    public long WinCalls, WinTicks, WinMax;
    public int Errors;
    public string LastError;
    /// <summary>Seconds after which the next "slow hook" line may be written (one per hook per 10 s).</summary>
    public double NextSlowLogAt { get; set; }

    public HookStat(string modId, string name)
    {
        ModId = modId ?? "?";
        Name = name ?? "?";
    }

    public void Add(long ticks)
    {
        Calls++;
        Ticks += ticks;
        if (ticks > MaxTicks) MaxTicks = ticks;
        WinCalls++;
        WinTicks += ticks;
        if (ticks > WinMax) WinMax = ticks;
    }

    /// <summary>
    /// The timing table of the hooks that ran since the last one, by total time, <paramref name="top"/> rows; resets
    /// their window. Hooks with errors are listed too.
    /// </summary>
    public static string Table(IEnumerable<HookStat> stats, double ticksPerSecond, int top, double windowSeconds)
    {
        var rows = new List<HookStat>();
        foreach (var s in stats)
            if (s != null && (s.WinCalls > 0 || s.Errors > 0)) rows.Add(s);
        rows.Sort((a, b) => b.WinTicks.CompareTo(a.WinTicks));
        var sb = new StringBuilder();
        double total = 0;
        foreach (var s in rows) total += s.WinTicks;
        sb.Append("hook timing, last ").Append(F0(windowSeconds)).Append(" s: ").Append(rows.Count).Append(" hooks, ")
          .Append(Ms(total, ticksPerSecond)).Append(" ms in all");
        for (int i = 0; i < rows.Count; i++)
        {
            var s = rows[i];
            if (i < top || s.Errors > 0)
            {
                sb.Append('\n').Append("[").Append(s.ModId).Append("] ").Append(s.Name).Append(": ")
                  .Append(s.WinCalls).Append(" calls, ").Append(Ms(s.WinTicks, ticksPerSecond)).Append(" ms, avg ")
                  .Append(s.WinCalls > 0 ? Ms(s.WinTicks / (double)s.WinCalls, ticksPerSecond, "0.000") : "0").Append(" ms, max ")
                  .Append(Ms(s.WinMax, ticksPerSecond)).Append(" ms (session max ").Append(Ms(s.MaxTicks, ticksPerSecond)).Append(" ms)");
                if (s.Errors > 0) sb.Append(", ").Append(s.Errors).Append(" errors, last ").Append(s.LastError ?? "?");
            }
            s.WinCalls = s.WinTicks = s.WinMax = 0;
        }
        return sb.ToString();
    }

    /// <summary>
    /// The session totals of every hook that ran, by total time (<paramref name="top"/> rows, plus any hook with errors);
    /// unlike <see cref="Table"/> it does not reset anything.
    /// </summary>
    public static string SessionTable(IEnumerable<HookStat> stats, double ticksPerSecond, int top)
    {
        var rows = new List<HookStat>();
        foreach (var s in stats)
            if (s != null && (s.Calls > 0 || s.Errors > 0)) rows.Add(s);
        rows.Sort((a, b) => b.Ticks.CompareTo(a.Ticks));
        double total = 0;
        foreach (var s in rows) total += s.Ticks;
        var sb = new StringBuilder();
        sb.Append(rows.Count).Append(" hooks ran, ").Append(Ms(total, ticksPerSecond)).Append(" ms in all");
        for (int i = 0; i < rows.Count; i++)
        {
            var s = rows[i];
            if (i >= top && s.Errors == 0) continue;
            sb.Append('\n').Append("  [").Append(s.ModId).Append("] ").Append(s.Name).Append(": ")
              .Append(s.Calls).Append(" calls, ").Append(Ms(s.Ticks, ticksPerSecond)).Append(" ms, avg ")
              .Append(s.Calls > 0 ? Ms(s.Ticks / (double)s.Calls, ticksPerSecond, "0.000") : "0").Append(" ms, max ")
              .Append(Ms(s.MaxTicks, ticksPerSecond)).Append(" ms");
            if (s.Errors > 0) sb.Append(", ").Append(s.Errors).Append(" errors, last ").Append(s.LastError ?? "?");
        }
        return sb.ToString();
    }

    internal static string Ms(double ticks, double ticksPerSecond, string format = "0.0") =>
        (ticksPerSecond > 0 ? ticks * 1000.0 / ticksPerSecond : 0).ToString(format, CultureInfo.InvariantCulture);

    private static string F0(double v) => v.ToString("0", CultureInfo.InvariantCulture);
}
