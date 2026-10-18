using System;
using BepInEx.Logging;
using Il2CppInterop.Runtime;
using OverTheHillPlus.Logging;
using UnityEngine;

namespace OverTheHillPlus;

/// <summary>
/// More detail on the game's own errors: Unity logs errors and exceptions without a stack trace in this build, so core
/// turns script stack traces on for them and adds one line per distinct error with the trace, whose error it is and
/// what the mods were doing: "ours: camera" (a frame of that mod is in the stack), "ours by trail: camera" (only game
/// frames, but that mod's hook or guarded patch was running, e.g. the game's OnEnable on an object the mod just made)
/// or "the game's own"; the scene, frame and session; the last 5 crash-trail steps.
/// Each distinct error (by blame, type, message without its numbers and top frame) is logged once; repeats are
/// counted and listed in the debug log's summary every 60 s (and in this log every 5 minutes). Capturing a stack trace
/// for every error is costly in IL2CPP, so once one error repeats more than 50 times (an error every frame) the game's
/// own trace settings come back, until 30 s pass without such repeats.
/// </summary>
internal static class ErrorTap
{
    private const int MaxReports = 200;
    private static ManualLogSource _log;
    private static bool _busy, _tracesOn, _canTrace;
    private static int _reported;
    private static StackTraceLogType _error, _exception, _assert;
    private static readonly TraceGate Gate = new();

    internal static void Start(ManualLogSource log)
    {
        _log = log;
        try
        {
            _error = Application.GetStackTraceLogType(LogType.Error);
            _exception = Application.GetStackTraceLogType(LogType.Exception);
            _assert = Application.GetStackTraceLogType(LogType.Assert);
            _canTrace = true;
            TracesOn();
        }
        catch (Exception e) { log.LogWarning("[ErrorTap] stack traces for errors stay off: " + e.Message); }
        try
        {
            Application.add_logMessageReceived(
                DelegateSupport.ConvertDelegate<Application.LogCallback>(new Action<string, string, LogType>(OnLog)));
        }
        catch (Exception e) { log.LogWarning("[ErrorTap] off: " + e.Message); }
    }

    private static void OnLog(string message, string stack, LogType type)
    {
        if (_busy || (type != LogType.Error && type != LogType.Exception && type != LogType.Assert)) return;
        _busy = true;
        try
        {
            var blame = StackText.Classify(stack, ModMap.ModOfType, CrashTrail.RunningMod);
            string norm = StackText.Normalize(message, 160);
            string mod = blame.ModId ?? "game";
            string sig = ErrorBook.Signature(mod, "unity " + type + " " + blame.Blame, norm, blame.TopFrame);
            double now = DebugLog.Now;
            var entry = DebugLog.Book.Note(sig, now, out bool first, mod);
            if (entry == null) return; // the book is full
            if (first) entry.Title = $"{type} ({blame.Describe()}): {StackText.Normalize(message, 100)}" + (blame.TopFrame.Length > 0 ? " at " + blame.TopFrame : "");
            // What the mods were doing, before this error's own step goes into the trail.
            string at = first ? CrashTrail.LastStep : null;
            string recent = first ? CrashTrail.RecentLine(5) : null;
            if (first) entry.TrailText = "error: " + (message?.Length > 80 ? message.Substring(0, 80) : message);
            CrashTrail.Mark(entry.TrailText ?? "error");
            if (Gate.Seen(entry.Count, now) && _tracesOn) TracesOff(entry.Title);
            if (!first) return;
            string line = Line(message, stack, type, blame, at, recent);
            DebugLog.Write("ERROR", mod, line);
            if (_reported >= MaxReports)
            {
                if (_reported++ == MaxReports)
                    _log?.LogWarning($"[ErrorTap] {MaxReports} distinct errors logged: later ones go to the debug log only ({DebugLog.FilePath ?? "Off"}).");
                return;
            }
            _reported++;
            _log?.LogWarning(line);
        }
        catch { /* never throw from the log callback */ }
        finally { _busy = false; }
    }

    private static string Line(string message, string stack, LogType type, Attribution blame, string at, string recent)
    {
        string trace = string.IsNullOrWhiteSpace(stack)
            ? (_tracesOn || !_canTrace ? "  (no stack trace)" : "  (no stack trace: traces are paused while an error repeats every frame)")
            : stack.TrimEnd();
        return $"[ErrorTap] {type} - {blame.Describe()} | scene {FrameWatch.Scene} | frame {Time.frameCount} | {FrameWatch.Session} | " +
               $"mods at \"{at}\": {message}\n{trace}\n  last steps: {recent}";
    }

    /// <summary>Once a second (frame clock): traces come back on after the repeats stopped.</summary>
    internal static void Tick(double now)
    {
        if (_canTrace && Gate.Tick(now) && !_tracesOn)
        {
            TracesOn();
            DebugLog.Write("INFO", "-", "Unity stack traces for errors are on again (no error repeated every frame for 30 s)");
        }
    }

    private static void TracesOn()
    {
        Application.SetStackTraceLogType(LogType.Error, StackTraceLogType.ScriptOnly);
        Application.SetStackTraceLogType(LogType.Exception, StackTraceLogType.ScriptOnly);
        Application.SetStackTraceLogType(LogType.Assert, StackTraceLogType.ScriptOnly);
        _tracesOn = true;
    }

    /// <summary>Gives the game's stack trace settings back (inside the log callback: _busy keeps our own line out).</summary>
    private static void TracesOff(string why)
    {
        _tracesOn = false;
        try
        {
            Application.SetStackTraceLogType(LogType.Error, _error);
            Application.SetStackTraceLogType(LogType.Exception, _exception);
            Application.SetStackTraceLogType(LogType.Assert, _assert);
            string text = $"an error repeats every frame ({why}): Unity stack traces for errors are paused until it stops for 30 s.";
            _log?.LogInfo("[ErrorTap] " + text);
            DebugLog.Write("INFO", "-", text);
        }
        catch (Exception e) { _log?.LogWarning("[ErrorTap] could not switch stack traces off again: " + e.Message); }
    }
}
