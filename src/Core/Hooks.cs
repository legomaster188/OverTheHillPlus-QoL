using System;
using System.Collections.Generic;

namespace OverTheHillPlus;

/// <summary>
/// Yes/no questions that several mods answer, e.g. "is the GUI hidden for a screenshot?" or "is the local player
/// spectating?": each mod adds its own check, so core and the other mods ask without referencing that mod.
/// <see cref="Any"/> is true when one check says yes; a check that throws counts as no.
/// </summary>
internal sealed class CheckList
{
    private readonly List<Func<bool>> _checks = new();

    /// <summary>Adds a check (null is ignored).</summary>
    public void Add(Func<bool> check)
    {
        if (check != null) _checks.Add(check);
    }

    public int Count => _checks.Count;

    /// <summary>True when any check is true. A check that throws never makes it true and never stops the others.</summary>
    public bool Any()
    {
        foreach (var check in _checks)
        {
            try { if (check()) return true; }
            catch (Exception ex) { DebugLog.Error("core", ex, "CheckList.Any"); /* a broken check counts as false */ }
        }
        return false;
    }
}

/// <summary>Calling a hook that several mods subscribe to.</summary>
internal static class Hooks
{
    /// <summary>
    /// Calls every subscriber of <paramref name="hook"/> in its own try/catch, so one that throws doesn't skip the
    /// others. Each exception goes to <paramref name="onError"/> (which must not throw itself).
    /// </summary>
    public static void InvokeEach(Action hook, Action<Exception> onError)
    {
        if (hook == null) return;
        foreach (var d in hook.GetInvocationList())
        {
            try { ((Action)d)(); }
            catch (Exception e)
            {
                DebugLog.Error("core", e, "Hooks.InvokeEach");
                try { onError?.Invoke(e); }
                catch (Exception ex) { DebugLog.Error("core", ex, "Hooks.InvokeEach"); /* the error report itself failed: keep going */ }
            }
        }
    }

    /// <summary>Same as <see cref="InvokeEach(Action, Action{Exception})"/> for a hook that takes an argument.</summary>
    public static void InvokeEach<T>(Action<T> hook, T arg, Action<Exception> onError)
    {
        if (hook == null) return;
        foreach (var d in hook.GetInvocationList())
        {
            try { ((Action<T>)d)(arg); }
            catch (Exception e)
            {
                DebugLog.Error("core", e, "Hooks.InvokeEach");
                try { onError?.Invoke(e); }
                catch (Exception ex) { DebugLog.Error("core", ex, "Hooks.InvokeEach"); /* the error report itself failed: keep going */ }
            }
        }
    }
}
