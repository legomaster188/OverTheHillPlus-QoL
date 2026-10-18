using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;

namespace OverTheHillPlus.Logging;

/// <summary>
/// Timing and attribution of the mods' hooks: every feature tick and overlay (Features.Registry), every subscriber of
/// core's static hooks (CoreBehaviour.BeforeFeatureTicks, AfterFeatureTicks, Hotkeys, AfterGuis: each marks the
/// crash trail with its own step now), every Guard site and guarded patch. Each hook has a <see cref="HookStat"/>;
/// one slower than [Debug] SlowHookMs is noted (once per hook per 10 s), and at Verbose the table of all of them goes
/// to the debug log every 60 s. Timing runs only while the debug level is not Off. Main thread only.
/// </summary>
internal static class HookTiming
{
    private sealed class Info
    {
        public string Trail;
        public string Mod;
        public HookStat Stat;
    }

    private static readonly List<HookStat> All = new();
    private static readonly Dictionary<MethodInfo, Info> ByMethod = new();
    private static readonly double Freq = Stopwatch.Frequency;

    /// <summary>A new hook's timing (listed in the table).</summary>
    public static HookStat NewStat(string modId, string name)
    {
        var s = new HookStat(modId, name);
        lock (All) All.Add(s);
        return s;
    }

    /// <summary>Adds one run of <paramref name="ticks"/> and notes it when it was slow.</summary>
    public static void Record(HookStat stat, long ticks)
    {
        if (stat == null) return;
        stat.Add(ticks);
        if (ticks <= FrameWatch.SlowTicks) return;
        double now = DebugLog.Now;
        if (now < stat.NextSlowLogAt) return;
        stat.NextSlowLogAt = now + 10;
        DebugLog.Write("SLOW", stat.ModId,
            $"{stat.Name} took {HookStat.Ms(ticks, Freq)} ms (limit {DebugConfig.Get(DebugConfig.SlowHookMs, 20f):0.#} ms; " +
            $"session max {HookStat.Ms(stat.MaxTicks, Freq)} ms over {stat.Calls} calls)");
    }

    /// <summary>Notes an exception of a hook on its stat (and once in the debug log).</summary>
    public static void Failed(HookStat stat, Exception e, string context)
    {
        if (stat != null)
        {
            stat.Errors++;
            stat.LastError = e?.GetType().Name;
        }
        DebugLog.Error(stat?.ModId, e, context);
    }

    /// <summary>The timing table of the last <paramref name="windowSeconds"/>.</summary>
    public static string Table(double windowSeconds)
    {
        List<HookStat> copy;
        lock (All) copy = new List<HookStat>(All);
        return HookStat.Table(copy, Freq, 15, windowSeconds);
    }

    /// <summary>The session totals of every hook (does not reset the 60 s window): for the diagnostics snapshot.</summary>
    public static string SessionTable()
    {
        List<HookStat> copy;
        lock (All) copy = new List<HookStat>(All);
        return HookStat.SessionTable(copy, Freq, 25);
    }

    /// <summary>
    /// Calls each subscriber of a static hook in its own try/catch (like Hooks.InvokeEach), marking the trail with
    /// "&lt;what&gt; Type.Method (mod)" first and timing it.
    /// </summary>
    public static void InvokeEach(Action hook, string what, Action<Exception> onError)
    {
        if (hook == null) return;
        bool timed = DebugLog.On;
        foreach (var d in hook.GetInvocationList())
        {
            var info = InfoFor(d, what);
            CrashTrail.Mark(info.Trail);
            string prev = CrashTrail.RunningMod;
            CrashTrail.RunningMod = info.Mod;
            long t0 = timed ? Stopwatch.GetTimestamp() : 0;
            try { ((Action)d)(); }
            catch (Exception e)
            {
                Failed(info.Stat, e, info.Trail);
                try { onError?.Invoke(e); } catch { }
            }
            finally
            {
                CrashTrail.RunningMod = prev;
                if (timed) Record(info.Stat, Stopwatch.GetTimestamp() - t0);
            }
        }
    }

    /// <summary>Same as <see cref="InvokeEach(Action, string, Action{Exception})"/> for a hook with an argument.</summary>
    public static void InvokeEach<T>(Action<T> hook, T arg, string what, Action<Exception> onError)
    {
        if (hook == null) return;
        bool timed = DebugLog.On;
        foreach (var d in hook.GetInvocationList())
        {
            var info = InfoFor(d, what);
            CrashTrail.Mark(info.Trail);
            string prev = CrashTrail.RunningMod;
            CrashTrail.RunningMod = info.Mod;
            long t0 = timed ? Stopwatch.GetTimestamp() : 0;
            try { ((Action<T>)d)(arg); }
            catch (Exception e)
            {
                Failed(info.Stat, e, info.Trail);
                try { onError?.Invoke(e); } catch { }
            }
            finally
            {
                CrashTrail.RunningMod = prev;
                if (timed) Record(info.Stat, Stopwatch.GetTimestamp() - t0);
            }
        }
    }

    private static Info InfoFor(Delegate d, string what)
    {
        var m = d.Method;
        if (ByMethod.TryGetValue(m, out var info)) return info;
        string mod = ModMap.ModOfAssembly(m.DeclaringType?.Assembly) ?? "?";
        string name = $"{what} {m.DeclaringType?.Name}.{m.Name}";
        info = new Info { Trail = $"{name} ({mod})", Mod = mod, Stat = NewStat(mod, name) };
        ByMethod[m] = info;
        return info;
    }
}
