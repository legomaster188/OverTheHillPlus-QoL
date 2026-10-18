using System;
using System.Collections.Generic;
using System.Diagnostics;
using OverTheHillPlus.Logging;

namespace OverTheHillPlus;

/// <summary>
/// One guarded piece of a mod's code (e.g. a Harmony postfix): <see cref="Run"/> marks the crash trail with
/// "&lt;mod&gt;: &lt;name&gt;", says the mod is running (hangs and the game's errors meanwhile are blamed on it),
/// times it like a feature hook and catches its exception (written once to the debug log, repeats counted).
/// Make it once (<see cref="Guard.Site"/>) and keep it in a static field: running it then allocates nothing.
/// </summary>
public sealed class GuardSite
{
    private readonly HookStat _stat;

    internal GuardSite(string modId, string name)
    {
        ModId = string.IsNullOrEmpty(modId) ? "?" : modId;
        Name = name ?? "?";
        Trail = ModId + ": " + Name;
        _stat = HookTiming.NewStat(ModId, Name);
    }

    public string ModId { get; }
    public string Name { get; }
    /// <summary>The crash-trail step it marks.</summary>
    public string Trail { get; }

    /// <summary>
    /// Runs <paramref name="action"/>; false when it threw (the exception is logged, never rethrown). Pass a cached
    /// delegate (a static readonly Action field) so the call allocates nothing.
    /// </summary>
    public bool Run(Action action)
    {
        if (action == null) return true;
        if (!CrashTrail.OnMainThread)
        {
            try { action(); return true; }
            catch (Exception e) { HookTiming.Failed(_stat, e, Name); return false; }
        }
        CrashTrail.Mark(Trail);
        string prev = CrashTrail.RunningMod;
        CrashTrail.RunningMod = ModId;
        bool timed = DebugLog.On;
        long t0 = timed ? Stopwatch.GetTimestamp() : 0;
        try
        {
            action();
            return true;
        }
        catch (Exception e)
        {
            HookTiming.Failed(_stat, e, Name);
            return false;
        }
        finally
        {
            CrashTrail.RunningMod = prev;
            if (timed) HookTiming.Record(_stat, Stopwatch.GetTimestamp() - t0);
        }
    }
}

/// <summary>Guarding a mod's own code: see <see cref="GuardSite"/>.</summary>
public static class Guard
{
    private static readonly Dictionary<(string, string), GuardSite> Sites = new();

    /// <summary>The guard site for this mod and name (made on first use, the same object afterwards).</summary>
    public static GuardSite Site(string modId, string name)
    {
        lock (Sites)
        {
            if (!Sites.TryGetValue((modId ?? "?", name ?? "?"), out var site))
                Sites[(modId ?? "?", name ?? "?")] = site = new GuardSite(modId, name);
            return site;
        }
    }

    /// <summary>Runs <paramref name="action"/> through <see cref="Site"/> (a lookup per call; keep a GuardSite in a
    /// field for per-frame code). False when it threw.</summary>
    public static bool Run(string modId, string name, Action action) => Site(modId, name).Run(action);
}
