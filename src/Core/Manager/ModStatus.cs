using System;
using System.Collections.Generic;

namespace OverTheHillPlus.Core.Manager;

/// <summary>
/// The words the MODS page shows about a mod's state. Pure logic with no BepInEx or Unity types, so it can be unit
/// tested.
/// </summary>
internal static class ModStatus
{
    /// <summary>The value of a mod's ON/OFF switch; a change waits for a restart, and says so.</summary>
    public static string SwitchText(bool on, bool pendingRestart) =>
        pendingRestart ? (on ? "ON after restart" : "OFF after restart") : on ? "ON" : "OFF";

    /// <summary>The line a mod's page shows when its Load threw.</summary>
    public static string FailedText(string firstErrorLine) => "Failed to start: " + FirstLine(firstErrorLine);

    /// <summary>The line a mod's page shows when some of its game patches could not be applied.</summary>
    public static string UnavailableText(int failedPatches) =>
        failedPatches == 1 ? "1 feature unavailable after the game update" : $"{failedPatches} features unavailable after the game update";

    /// <summary>The startup log line once every mod has applied its patches (PatchReport).</summary>
    public static string PatchSummary(int ok, int failed) => $"[Core] patch report: {ok} ok, {failed} failed";

    private const int MaxLine = 200;

    /// <summary>The first non-empty line of an error (a message or a whole stack trace), shortened for the page.</summary>
    public static string FirstLine(string error)
    {
        if (string.IsNullOrWhiteSpace(error)) return "unknown error";
        foreach (var raw in error.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            return line.Length <= MaxLine ? line : line.Substring(0, MaxLine - 3) + "...";
        }
        return "unknown error";
    }
}

/// <summary>Mods whose Load threw, with the first line of the error. The first failure of a mod is the one kept.</summary>
internal sealed class FailureLog
{
    private readonly Dictionary<string, string> _failed = new(StringComparer.OrdinalIgnoreCase);

    public void Mark(string id, string error)
    {
        if (string.IsNullOrEmpty(id) || _failed.ContainsKey(id)) return;
        _failed[id] = ModStatus.FirstLine(error);
    }

    /// <summary>The first error line, or null when the mod started fine.</summary>
    public string Get(string id) => id != null && _failed.TryGetValue(id, out var line) ? line : null;
}
