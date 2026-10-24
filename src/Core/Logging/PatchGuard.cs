using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using HarmonyLib;

namespace OverTheHillPlus.Logging;

/// <summary>
/// [Debug] PatchGuards: after a mod's patch classes are applied (PatchReport.PatchAllSafely), each game method the mod
/// patched gets a first prefix (marks the trail "patch Type.Method (mod)", says the mod is running, starts a timer)
/// and a finalizer (stops the timer, notes any exception with the mod's name and passes it on unchanged). Off by
/// default: it adds a little work to every call of a patched method. Methods with struct parameters passed by value
/// are left alone (their IL2CPP trampolines must not be re-patched).
/// </summary>
internal static class PatchGuard
{
    private sealed class Info
    {
        public string Mods;
        public string Trail;
        public string Name;
        public HookStat Stat;
    }

    private const int MaxDepth = 32;
    private static readonly Dictionary<MethodBase, Info> Guarded = new();
    private static readonly string[] PrevMod = new string[MaxDepth];
    private static readonly long[] Started = new long[MaxDepth];
    private static int _depth;
    private static HarmonyMethod _prefix, _finalizer;

    public static bool Enabled => DebugConfig.Get(DebugConfig.PatchGuards, false);

    /// <summary>Guards every method <paramref name="harmony"/> has patched so far. Returns how many got a guard.</summary>
    public static int Add(Harmony harmony, string modId)
    {
        if (harmony == null) return 0;
        int added = 0, skipped = 0;
        try
        {
            _prefix ??= new HarmonyMethod(typeof(PatchGuard).GetMethod(nameof(Prefix), BindingFlags.NonPublic | BindingFlags.Static)) { priority = Priority.First };
            _finalizer ??= new HarmonyMethod(typeof(PatchGuard).GetMethod(nameof(Finalizer), BindingFlags.NonPublic | BindingFlags.Static));
            var originals = new List<MethodBase>(harmony.GetPatchedMethods());
            foreach (var original in originals)
            {
                if (original == null) continue;
                if (Guarded.TryGetValue(original, out var known))
                {
                    if (!known.Mods.Contains(modId)) known.Mods += "+" + modId;
                    continue;
                }
                if (!Guardable(original)) { skipped++; continue; }
                string name = $"{original.DeclaringType?.Name}.{original.Name}";
                var info = new Info { Mods = modId, Name = "patch " + name, Trail = $"patch {name} ({modId})", Stat = HookTiming.NewStat(modId, "patch " + name) };
                Guarded[original] = info;
                try
                {
                    harmony.Patch(original, prefix: _prefix, finalizer: _finalizer);
                    added++;
                }
                catch (Exception e)
                {
                    Guarded.Remove(original);
                    Core.CorePlugin.Logger?.LogWarning($"[Debug] patch guard for {name} ({modId}) failed: {e.GetBaseException().Message}");
                }
            }
        }
        catch (Exception e) { Core.CorePlugin.Logger?.LogWarning($"[Debug] patch guards for {modId}: {e.Message}"); }
        if (added > 0 || skipped > 0)
            Core.CorePlugin.Logger?.LogInfo($"[Debug] patch guards: {added} patched methods of {modId} guarded" + (skipped > 0 ? $", {skipped} left alone (struct parameters)" : ""));
        return added;
    }

    private static bool Guardable(MethodBase m)
    {
        if (m.IsAbstract || m.ContainsGenericParameters) return false;
        foreach (var p in m.GetParameters())
        {
            var t = p.ParameterType;
            if (t.IsByRef) continue;
            if (t.IsValueType && !t.IsPrimitive && !t.IsEnum) return false;
        }
        return true;
    }

    private static void Prefix(MethodBase __originalMethod, out int __state)
    {
        __state = -1;
        if (!CrashTrail.OnMainThread || __originalMethod == null || !Guarded.TryGetValue(__originalMethod, out var info)) return;
        int d = _depth;
        __state = d;
        if (d < MaxDepth)
        {
            PrevMod[d] = CrashTrail.RunningMod;
            Started[d] = DebugLog.On ? Stopwatch.GetTimestamp() : 0;
        }
        _depth = d + 1;
        CrashTrail.Mark(info.Trail);
        CrashTrail.RunningMod = info.Mods;
    }

    private static Exception Finalizer(Exception __exception, MethodBase __originalMethod, int __state)
    {
        int d = __state;
        if (d < 0) return __exception;
        try
        {
            Guarded.TryGetValue(__originalMethod, out var info);
            _depth = d;
            if (d < MaxDepth)
            {
                CrashTrail.RunningMod = PrevMod[d];
                if (Started[d] != 0 && info != null) HookTiming.Record(info.Stat, Stopwatch.GetTimestamp() - Started[d]);
            }
            if (__exception != null && info != null)
            {
                // The patch that threw is in the stack; with several mods on one method, it names the right one.
                var blame = StackText.Classify(__exception.StackTrace ?? "", ModMap.ModOfType, null);
                string mod = blame.Blame == Blame.Ours && blame.ModId != "?" && blame.ModId != "core" ? blame.ModId : info.Mods;
                if (info.Stat != null) { info.Stat.Errors++; info.Stat.LastError = __exception.GetType().Name; }
                DebugLog.Error(mod, __exception, info.Name);
            }
        }
        catch { }
        return __exception; // unchanged: the guard only watches
    }
}
