using System;
using BepInEx.Logging;

namespace OverTheHillPlus.Logging;

/// <summary>
/// Catches the errors every plugin writes to the BepInEx log (our mods' own LogError lines, Il2CppInterop's
/// "During invoking native->managed trampoline" exceptions from Harmony patches, HarmonyX's patch errors, other
/// creators' plugins) and puts each distinct one once into the debug log, blamed on a mod by its stack frames or its
/// log source, with repeat counts. Unity's own log (source "Unity") is left to ErrorTap. Any thread.
/// </summary>
internal sealed class ErrorListener : ILogListener
{
    [ThreadStatic] private static bool _busy;

    // The mods' own plain log lines (LogInfo, LogMessage and warnings without an exception) go into the debug log too:
    // warnings at Info, the rest at Verbose. At most 20 a second per mod; the next line that gets through says how
    // many were left out.
    private static readonly KeyedRateLimiter<string> LineLimit = new(1.0, 20, 128);

    public LogLevel LogLevelFilter => LogLevel.Fatal | LogLevel.Error | LogLevel.Warning | LogLevel.Message | LogLevel.Info;

    internal static void Install()
    {
        try { Logger.Listeners.Add(new ErrorListener()); }
        catch (Exception e) { Core.CorePlugin.Logger?.LogWarning("[Debug] errors of the BepInEx log are not collected: " + e.Message); }
    }

    public void LogEvent(object sender, LogEventArgs eventArgs)
    {
        if (_busy || !DebugLog.On || eventArgs == null) return;
        var level = eventArgs.Level;
        bool error = (level & (LogLevel.Fatal | LogLevel.Error)) != 0;
        bool warning = (level & LogLevel.Warning) != 0;
        if (!error && !warning && !DebugLog.IsVerbose) return; // plain info lines only at Verbose
        _busy = true;
        try
        {
            string source = eventArgs.Source?.SourceName ?? "?";
            if (source.StartsWith("Unity", StringComparison.OrdinalIgnoreCase)) return; // ErrorTap has those, with their stack
            string text = eventArgs.Data?.ToString();
            if (string.IsNullOrEmpty(text)) return;
            if (text.StartsWith("[ErrorTap]", StringComparison.Ordinal) || text.StartsWith("[Debug]", StringComparison.Ordinal) ||
                text.StartsWith("[CrashTrail]", StringComparison.Ordinal)) return;
            // A warning that carries an exception counts as an error for the book; the plain ones are lines of the mods' own log.
            if (!error && !(warning && text.IndexOf("Exception", StringComparison.Ordinal) >= 0))
            {
                Line(source, warning, text);
                return;
            }
            if (DebugLog.TakeSameAsLast(text)) return; // DebugLog.Error has it already
            Note(source, level, text);
        }
        catch { /* never throw into BepInEx's logger */ }
        finally { _busy = false; }
    }

    /// <summary>A plain line of one of our mods' own log sources (other plugins' lines are not copied).</summary>
    private static void Line(string source, bool warning, string text)
    {
        string mod = ModMap.ModOfSource(source);
        if (mod == null) return;
        if (warning ? !DebugLog.IsInfo : !DebugLog.IsVerbose) return;
        bool allow;
        int more;
        lock (LineLimit) allow = LineLimit.Allow(mod, DebugLog.Now, out more);
        if (!allow) return;
        if (more > 0) text += $" (+{more} more lines of {mod} not shown)";
        DebugLog.Write(warning ? "WARN" : "LOG", mod, text);
    }

    private static void Note(string source, LogLevel level, string text)
    {
        var blame = StackText.Classify(text, ModMap.ModOfType, null);
        string mod = blame.Blame == Blame.Ours && blame.ModId != "?" ? blame.ModId : ModMap.ModOfSource(source) ?? source;
        string type = StackText.ExceptionType(text);
        string what = (type ?? "") + "|" + StackText.Normalize(text, 120);
        string sig = ErrorBook.Signature(mod, "log " + source, what, blame.TopFrame);
        var entry = DebugLog.Book.Note(sig, DebugLog.Now, out bool first, mod);
        if (entry == null || !first) return;
        entry.Title = $"{source}: {StackText.Normalize(text, 120)}";
        string who = blame.Blame == Blame.Ours ? $"blamed on {mod} by its stack ({blame.OurFrame ?? blame.TopFrame})" : $"blamed on {mod} by its log source";
        DebugLog.Write("ERROR", mod, $"logged by {source} ({level}), {who}:\n{text}\n{DebugLog.Where()}");
    }

    public void Dispose() { }
}
