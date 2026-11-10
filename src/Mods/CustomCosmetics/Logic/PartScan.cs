using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;

namespace OverTheHillPlus.CustomCosmetics;

/// <summary>
/// The pass over the part folders that decides which parts exist: in folder order, an invalid folder name or a part over
/// the limit is left out with its reason, and every other folder is read. Its result (which parts, in which order) decides
/// the parts' bits on every car's list, so it must not depend on timing: it is the same whether it runs on the main thread
/// or in the background. Pure code (unit-tested).
/// </summary>
public static class PartScan
{
    public readonly struct Row<T> where T : class
    {
        public readonly string Id;
        public readonly T Item;
        /// <summary>Why the part is left out (null when <see cref="Item"/> is set).</summary>
        public readonly string Why;

        public Row(string id, T item, string why) { Id = id; Item = item; Why = item == null ? why ?? "could not be read" : null; }
    }

    /// <param name="folders">The part folders, in the order the parts are listed (sorted ordinal).</param>
    /// <param name="validId">Is the folder name a valid part id?</param>
    /// <param name="maxParts">Parts read at most (folders after that are left out).</param>
    /// <param name="read">Reads one part: the part, or null and why not. Must be safe off the main thread.</param>
    public static List<Row<T>> ReadAll<T>(IReadOnlyList<string> folders, Func<string, bool> validId, int maxParts,
        Func<string, (T item, string why)> read) where T : class
    {
        var rows = new List<Row<T>>();
        int kept = 0;
        foreach (var id in folders)
        {
            if (!validId(id)) { rows.Add(new Row<T>(id, null, "its folder name is not a valid part id (lowercase letters, digits, underscores)")); continue; }
            if (kept >= maxParts) { rows.Add(new Row<T>(id, null, $"more than {maxParts} parts")); continue; }
            (T item, string why) r;
            try { r = read(id); }
            catch (Exception e) { r = (null, "it could not be read: " + e.Message); }
            rows.Add(new Row<T>(id, r.item, r.why));
            if (r.item != null) kept++;
        }
        return rows;
    }
}

/// <summary>
/// Work started now that runs on a worker thread, and whose result the main thread takes when it first needs it (waiting
/// for the rest then, if it isn't done). Pure code (unit-tested).
/// </summary>
public sealed class BackgroundJob<T>
{
    private readonly Task<T> _task;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private double _ranMs = -1;

    /// <param name="inBackground">False runs <paramref name="work"/> right here (same result, no thread).</param>
    public BackgroundJob(Func<T> work, bool inBackground = true)
    {
        if (work == null) throw new ArgumentNullException(nameof(work));
        Func<T> timed = () =>
        {
            try { return work(); }
            finally { _ranMs = _clock.Elapsed.TotalMilliseconds; }
        };
        _task = inBackground ? Task.Run(timed) : RunHere(timed);
    }

    private static Task<T> RunHere(Func<T> work)
    {
        try { return Task.FromResult(work()); }
        catch (Exception e) { return Task.FromException<T>(e); }
    }

    public bool IsDone => _task.IsCompleted;

    /// <summary>How long the work ran (milliseconds; -1 while it runs).</summary>
    public double RanMs => _ranMs;

    /// <summary>The result, waiting for it if needed (<paramref name="waitedMs"/>: how long this call waited). The
    /// work's own exception is thrown here.</summary>
    public T Result(out double waitedMs)
    {
        var sw = Stopwatch.StartNew();
        try { return _task.GetAwaiter().GetResult(); }
        finally { waitedMs = _task.IsCompleted && sw.Elapsed.TotalMilliseconds < 0.5 ? 0 : sw.Elapsed.TotalMilliseconds; }
    }
}
