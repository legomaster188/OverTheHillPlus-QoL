using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace OverTheHillPlus.Logging;

/// <summary>One crash-trail step: its number, frame, time and text (ticks: a Stopwatch timestamp, 0 when unknown).</summary>
internal readonly struct TrailRow
{
    public readonly int Seq;
    public readonly int Frame;
    /// <summary>Seconds since the game started (the trail file's time).</summary>
    public readonly float Time;
    public readonly long Ticks;
    public readonly string Text;

    public TrailRow(int seq, int frame, float time, string text, long ticks = 0)
    {
        Seq = seq;
        Frame = frame;
        Time = time;
        Text = text ?? "";
        Ticks = ticks;
    }
}

/// <summary>What a crash report is made of (see <see cref="ReportText.CrashReport"/>).</summary>
internal sealed class CrashReportParts
{
    public string TrailFile;
    public DateTime EndedUtc;
    /// <summary>The crashed session's header from its debug log (null: not found).</summary>
    public List<string> Header;
    /// <summary>This session's header, used when the crashed one's was not found.</summary>
    public string CurrentHeader;
    public List<TrailRow> Trail = new();
    /// <summary>Errors, hangs, spikes and slow hooks from the crashed session's debug log (oldest first).</summary>
    public List<string> Notes = new();
    /// <summary>Why there are no notes (e.g. "the debug log was Off"), or null.</summary>
    public string NotesMissing;
    /// <summary>Matching Windows Application events, or why there are none.</summary>
    public string WindowsEvents;
    public Func<string, string> ModOfStep;
}

/// <summary>The text of the hang, spike and crash reports (pure).</summary>
internal static class ReportText
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static string Ms(double ms) => ms >= 1000 ? (ms / 1000).ToString("0.00", Inv) + " s" : ms.ToString(ms < 10 ? "0.00" : "0.0", Inv) + " ms";

    /// <summary>
    /// Steps with how long each ran (until the next step; the last one until <paramref name="endTicks"/>):
    /// "  #812 f1234 +3.20 ms  tick CameraFeature (camera)".
    /// </summary>
    public static void AppendSteps(StringBuilder sb, IReadOnlyList<TrailRow> rows, long endTicks, double ticksPerSecond,
                                   Func<string, string> modOfStep, string indent = "  ")
    {
        for (int i = 0; i < rows.Count; i++)
        {
            var r = rows[i];
            long next = i + 1 < rows.Count ? rows[i + 1].Ticks : endTicks;
            sb.Append(indent).Append('#').Append(r.Seq).Append(" f").Append(r.Frame);
            if (r.Ticks != 0 && next != 0 && ticksPerSecond > 0)
                sb.Append(" +").Append(Ms((next - r.Ticks) * 1000.0 / ticksPerSecond));
            sb.Append("  ").Append(r.Text);
            string mod = modOfStep?.Invoke(r.Text);
            if (mod != null && !r.Text.EndsWith("(" + mod + ")", StringComparison.Ordinal)) sb.Append("  [").Append(mod).Append(']');
            sb.Append('\n');
        }
    }

    /// <summary>
    /// The watchdog's note on a stall: how long, where the main thread was (the step it is still in and whether one
    /// of our hooks is running) and the last steps with their durations.
    /// </summary>
    public static string HangText(double seconds, int frame, string runningMod, string currentStep, IReadOnlyList<TrailRow> rows,
                                  long nowTicks, double ticksPerSecond, Func<string, string> modOfStep)
    {
        var sb = new StringBuilder(256 + rows.Count * 64);
        sb.Append("main thread has not finished a frame for ").Append(seconds.ToString("0.0", Inv)).Append(" s (last frame ")
          .Append(frame).Append(").\n");
        string stepMod = modOfStep?.Invoke(currentStep ?? "");
        sb.Append("current step: \"").Append(currentStep ?? "").Append('"');
        if (stepMod != null) sb.Append(" (mod ").Append(stepMod).Append(')');
        sb.Append('\n');
        if (!string.IsNullOrEmpty(runningMod))
            sb.Append("inside a hook of ours: ").Append(runningMod).Append(" (the hang is in that mod's code or in game code it called)\n");
        else
            sb.Append("not inside any hook of ours: the main thread is in the game's own code, physics, rendering or loading, " +
                      "or in a Harmony patch without trail steps (set [Debug] PatchGuards = true to mark those)\n");
        sb.Append("last ").Append(rows.Count).Append(" steps (oldest first, + is how long each ran):\n");
        AppendSteps(sb, rows, nowTicks, ticksPerSecond, modOfStep);
        return sb.ToString().TrimEnd('\n');
    }

    /// <summary>
    /// A frame spike: the frame's length, the steps of ours in it with their durations, and the time before the first
    /// and after the last of them (the game's own work).
    /// </summary>
    public static string SpikeText(double frameMs, double limitMs, int frame, IReadOnlyList<TrailRow> rows, int skipped,
                                   long frameStartTicks, long frameEndTicks, double ticksPerSecond, Func<string, string> modOfStep, int suppressed)
    {
        var sb = new StringBuilder(128 + rows.Count * 64);
        sb.Append("frame ").Append(frame).Append(" took ").Append(Ms(frameMs)).Append(" (limit ").Append(Ms(limitMs)).Append(')');
        if (suppressed > 0) sb.Append("; ").Append(suppressed).Append(" more spikes since the last one shown");
        sb.Append('\n');
        if (rows.Count == 0)
        {
            sb.Append("  no step of ours in that frame: the time went to the game's own work (or a loading screen)");
            return sb.ToString();
        }
        double ours = 0, worst = -1;
        string worstText = null;
        for (int i = 0; i < rows.Count; i++)
        {
            long next = i + 1 < rows.Count ? rows[i + 1].Ticks : frameEndTicks;
            double ms = (next - rows[i].Ticks) * 1000.0 / ticksPerSecond;
            if (i + 1 < rows.Count) ours += ms;
            if (ms > worst) { worst = ms; worstText = rows[i].Text; }
        }
        if (skipped > 0) sb.Append("  (").Append(skipped).Append(" earlier steps of that frame not shown)\n");
        if (frameStartTicks != 0 && skipped == 0)
            sb.Append("  before the first step: ").Append(Ms((rows[0].Ticks - frameStartTicks) * 1000.0 / ticksPerSecond)).Append('\n');
        AppendSteps(sb, rows, frameEndTicks, ticksPerSecond, modOfStep);
        sb.Append("  longest: \"").Append(worstText).Append("\" ").Append(Ms(worst)).Append(" (the last step's time includes the game's work after it)");
        return sb.ToString();
    }

    /// <summary>The last <paramref name="last"/> rows grouped by frame, with the seconds since the previous step.</summary>
    public static string GroupByFrame(IReadOnlyList<TrailRow> rows, int last, Func<string, string> modOfStep = null)
    {
        var sb = new StringBuilder();
        int from = Math.Max(0, rows.Count - Math.Max(0, last));
        int frame = int.MinValue;
        for (int i = from; i < rows.Count; i++)
        {
            var r = rows[i];
            if (r.Frame != frame)
            {
                frame = r.Frame;
                sb.Append("frame ").Append(r.Frame).Append(" (t=").Append(r.Time.ToString("0.00", Inv)).Append(" s)\n");
            }
            sb.Append("  #").Append(r.Seq);
            if (i > from) sb.Append(" +").Append(Ms((r.Time - rows[i - 1].Time) * 1000.0));
            sb.Append("  ").Append(r.Text);
            string mod = modOfStep?.Invoke(r.Text);
            if (mod != null && !r.Text.EndsWith("(" + mod + ")", StringComparison.Ordinal)) sb.Append("  [").Append(mod).Append(']');
            sb.Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>
    /// How the session most likely ended, from its debug log notes: a hang the watchdog saw (and no "back after"
    /// later), else a crash or a kill.
    /// </summary>
    public static string Verdict(IReadOnlyList<string> notes, IReadOnlyList<TrailRow> trail)
    {
        string lastHang = null;
        if (notes != null)
            foreach (var n in notes)
            {
                if (LogFiles.LevelOf(n) != "HANG") continue;
                lastHang = n.Contains("main thread back after") ? null : n;
            }
        string lastStep = trail != null && trail.Count > 0 ? trail[trail.Count - 1].Text : "?";
        if (lastHang != null)
        {
            int at = lastHang.IndexOf("] ", StringComparison.Ordinal);
            return "the game stopped responding (the hang watchdog saw the main thread stuck and it never came back: " +
                   (at >= 0 ? lastHang.Substring(at + 2) : lastHang) + "), then it was closed or killed";
        }
        return "the game crashed or was killed (no hang was noted). The last step was \"" + lastStep + "\": if it is a step of a " +
               "mod, that mod's code (or the game code it called) was running; a native crash (c0000005) has no managed stack, " +
               "so this step is the best pointer";
    }

    /// <summary>
    /// Splits a previous debug log into its header (between "=== session" and "=== end of header") and its notes: every
    /// ERROR, HANG, SPIKE, SLOW and CRASH entry with its continuation lines, the last <paramref name="maxNotes"/> lines.
    /// </summary>
    public static void Extract(IEnumerable<string> lines, int maxNotes, out List<string> header, out List<string> notes)
    {
        header = null;
        notes = new List<string>();
        bool inHeader = false, keep = false;
        foreach (var raw in lines)
        {
            string line = raw ?? "";
            if (line.Contains("=== session header")) { inHeader = true; header = new List<string>(); continue; }
            if (inHeader)
            {
                if (line.Contains("=== end of header")) { inHeader = false; continue; }
                header.Add(line.StartsWith("    ", StringComparison.Ordinal) ? line.Substring(4) : line);
                continue;
            }
            string level = LogFiles.LevelOf(line);
            if (level != null) keep = level is "ERROR" or "HANG" or "SPIKE" or "SLOW" or "CRASH" or "SUMRY";
            else if (!line.StartsWith(" ", StringComparison.Ordinal)) keep = false;
            if (keep) notes.Add(line);
        }
        if (notes.Count > maxNotes) notes.RemoveRange(0, notes.Count - maxNotes);
    }

    /// <summary>The whole crash report.</summary>
    public static string CrashReport(CrashReportParts p)
    {
        var sb = new StringBuilder(8192);
        sb.Append("OTH crash report\n================\n");
        sb.Append("A game session did not close cleanly. Trail file: ").Append(p.TrailFile ?? "?").Append("; its last step was written about ")
          .Append(p.EndedUtc == default ? "?" : p.EndedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", Inv) + " (local time)").Append(".\n\n");
        sb.Append("How it ended: ").Append(Verdict(p.Notes, p.Trail)).Append(".\n\n");

        sb.Append("Session\n-------\n");
        if (p.Header != null && p.Header.Count > 0)
            foreach (var h in p.Header) sb.Append(h).Append('\n');
        else
            sb.Append("(the crashed session's header was not found; this is the current session's)\n").Append(p.CurrentHeader ?? "").Append('\n');
        sb.Append('\n');

        sb.Append("Last ").Append(Math.Min(100, p.Trail.Count)).Append(" trail steps, by frame (oldest first)\n-----------------------------------------\n");
        sb.Append(p.Trail.Count == 0 ? "(none)\n" : GroupByFrame(p.Trail, 100, p.ModOfStep));
        sb.Append('\n');

        sb.Append("Errors, hangs, spikes and slow hooks from that session's debug log\n------------------------------------------------------------------\n");
        if (p.Notes.Count > 0) foreach (var n in p.Notes) sb.Append(n).Append('\n');
        else sb.Append(p.NotesMissing ?? "(none were logged)").Append('\n');
        sb.Append('\n');

        sb.Append("Windows Application events around that time\n-------------------------------------------\n");
        sb.Append(string.IsNullOrEmpty(p.WindowsEvents) ? "(none found)" : p.WindowsEvents.TrimEnd()).Append('\n');
        sb.Append('\n');
        sb.Append("Attach this file and BepInEx/LogOutput.log to a bug report. More detail next time: set [Debug] Level = Verbose " +
                  "(and PatchGuards = true) in BepInEx/config/oth.core.cfg.\n");
        return sb.ToString();
    }

    /// <summary>The wevtutil XPath query for the crash and hang events of Windows' Application log in a time window.</summary>
    public static string EventLogQuery(DateTime fromUtc, DateTime toUtc)
    {
        string f = fromUtc.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ", Inv);
        string t = toUtc.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ", Inv);
        return "*[System[(Provider[@Name='Application Error'] or Provider[@Name='Application Hang'] or " +
               "Provider[@Name='Windows Error Reporting']) and TimeCreated[@SystemTime>='" + f + "' and @SystemTime<='" + t + "']]]";
    }

    /// <summary>
    /// The events of wevtutil's text output ("Event[0]:" blocks) that mention <paramref name="exeName"/>, at most
    /// <paramref name="max"/> of them, each cut to 3000 characters.
    /// </summary>
    public static string FilterEvents(string text, string exeName, int max = 6)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var blocks = new List<string>();
        int at = text.IndexOf("Event[", StringComparison.Ordinal);
        while (at >= 0)
        {
            int next = text.IndexOf("\nEvent[", at + 1, StringComparison.Ordinal);
            string block = (next < 0 ? text.Substring(at) : text.Substring(at, next + 1 - at)).Replace("\r", "").TrimEnd();
            if (string.IsNullOrEmpty(exeName) || block.IndexOf(exeName, StringComparison.OrdinalIgnoreCase) >= 0)
                blocks.Add(block.Length > 3000 ? block.Substring(0, 3000) + "\n  ..." : block);
            at = next < 0 ? -1 : next + 1;
        }
        if (blocks.Count > max) blocks.RemoveRange(max, blocks.Count - max);
        return string.Join("\n\n", blocks);
    }
}
