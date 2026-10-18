using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace OverTheHillPlus;

/// <summary>Applies a mod's Harmony patches one class at a time and remembers how many failed, per mod.</summary>
internal static class PatchReport
{
    private static readonly Dictionary<string, int> FailedByMod = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, int> OkByMod = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, List<string>> FailuresByMod = new(StringComparer.OrdinalIgnoreCase);
    private static int _totalOk, _totalFailed;
    private static bool _summaryLogged;

    /// <summary>Patch classes of this mod that were applied this session.</summary>
    public static int Applied(string modId) => modId != null && OkByMod.TryGetValue(modId, out int n) ? n : 0;

    /// <summary>What failed for this mod: "PatchClass: reason", in the order they failed.</summary>
    public static IReadOnlyList<string> Failures(string modId) =>
        modId != null && FailuresByMod.TryGetValue(modId, out var l) ? l : (IReadOnlyList<string>)Array.Empty<string>();

    /// <summary>The one-line self-check of a mod's patches, for the debug log (null when it has none).</summary>
    public static string SummaryLine(string modId)
    {
        int ok = Applied(modId), failed = Failed(modId);
        return ok + failed == 0 ? null : Logging.StartupText.PatchLine(modId, ok, failed, Failures(modId));
    }

    /// <summary>The mods that applied or failed any patch, in the order they first did.</summary>
    public static IEnumerable<string> Mods()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var k in OkByMod.Keys) if (seen.Add(k)) yield return k;
        foreach (var k in FailedByMod.Keys) if (seen.Add(k)) yield return k;
    }

    private static void Add(Dictionary<string, int> map, string key, int n) => map[key] = (map.TryGetValue(key, out int v) ? v : 0) + n;

    /// <summary>Patch classes of this mod that could not be applied this session (0 when all applied).</summary>
    public static int Failed(string modId) => modId != null && FailedByMod.TryGetValue(modId, out int n) ? n : 0;

    /// <summary>
    /// Same as Harmony.PatchAll on <paramref name="asm"/>, one patch class at a time. PatchAll stops at the first
    /// class whose target is missing (e.g. a method renamed by a game update) and throws out of the plugin's Load,
    /// which left the rest of the mod unloaded. Now only that one patch is skipped, logged and counted for
    /// <paramref name="modId"/>. Returns the number of failed classes. <paramref name="include"/>, when given, limits it
    /// to the classes it accepts (core while the old plugin is loaded).
    /// </summary>
    public static int PatchAllSafely(Harmony harmony, Assembly asm, string modId, Func<Type, bool> include = null)
    {
        if (harmony == null || asm == null) return 0;
        modId ??= "?";
        int failed = 0;
        foreach (var type in AccessTools.GetTypesFromAssembly(asm))
        {
            if (include != null && !include(type)) continue;
            try
            {
                // Types without patch attributes patch nothing: only real patch classes count as ok.
                var patched = harmony.CreateClassProcessor(type).Patch();
                if (patched != null && patched.Count > 0)
                {
                    _totalOk++;
                    Add(OkByMod, modId, 1);
                }
            }
            catch (Exception e)
            {
                failed++;
                _totalFailed++;
                var root = e.GetBaseException();
                if (!FailuresByMod.TryGetValue(modId, out var list)) FailuresByMod[modId] = list = new List<string>();
                list.Add($"{type.Name}: {root.Message}");
                // The debug log gets the full exception under the mod's id; the BepInEx line names the type too, so the log
                // listener knows it is the same error and does not write it a second time.
                DebugLog.Error(modId, e, "patch " + type.Name);
                Core.CorePlugin.Logger?.LogError($"Patch {type.FullName} failed and is skipped: {root.GetType().FullName}: {root.Message}");
            }
        }
        FailedByMod[modId] = Failed(modId) + failed;
        string line = SummaryLine(modId);
        if (line != null) DebugLog.Info(modId, "patches: " + line);
        if (failed > 0)
            Core.CorePlugin.Logger?.LogWarning($"[{modId}] {failed} patch class(es) could not be applied; the related features are off this session.");
        // [Debug] PatchGuards: every method the mod patched is timed, marked in the crash trail and its errors blamed on it.
        if (Logging.PatchGuard.Enabled) Logging.PatchGuard.Add(harmony, modId);
        return failed;
    }

    /// <summary>
    /// Records patches applied outside <see cref="PatchAllSafely"/> (core's car framework installs its hooks by hand, once
    /// for every car): <paramref name="failed"/> is added to <paramref name="modId"/>'s count; with
    /// <paramref name="countInTotals"/>, both numbers go into the summary too (once for hooks shared by several mods).
    /// </summary>
    public static void Record(string modId, int ok, int failed, bool countInTotals)
    {
        modId ??= "?";
        FailedByMod[modId] = Failed(modId) + Math.Max(0, failed);
        if (ok > 0) Add(OkByMod, modId, ok);
        if (failed > 0)
        {
            if (!FailuresByMod.TryGetValue(modId, out var list)) FailuresByMod[modId] = list = new List<string>();
            list.Add($"{failed} car hook(s)");
        }
        string line = SummaryLine(modId);
        if (line != null) DebugLog.Info(modId, "patches: " + line);
        if (!countInTotals) return;
        _totalOk += Math.Max(0, ok);
        _totalFailed += Math.Max(0, failed);
    }

    /// <summary>
    /// Logs "[Core] patch report: N ok, M failed" once, for every mod's patch classes together. Called on core's
    /// first frame (CoreBehaviour.Update), when every plugin's Load (and so its patching) has run.
    /// </summary>
    public static void LogSummaryOnce()
    {
        if (_summaryLogged) return;
        _summaryLogged = true;
        string line = Core.Manager.ModStatus.PatchSummary(_totalOk, _totalFailed);
        if (_totalFailed > 0) Core.CorePlugin.Logger?.LogWarning(line);
        else Core.CorePlugin.Logger?.LogInfo(line);
    }
}
