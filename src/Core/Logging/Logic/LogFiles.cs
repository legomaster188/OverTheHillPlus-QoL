using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace OverTheHillPlus.Logging;

/// <summary>
/// Names of the debug logs (BepInEx/oth-debug/20261017_101500.log) and crash reports
/// (BepInEx/oth-reports/crash-20261017_101500.txt), which old ones to delete, and the debug log's line format. Pure.
/// </summary>
internal static class LogFiles
{
    public const string Stamp = "yyyyMMdd_HHmmss";
    public const string DebugExt = ".log";
    public const string CrashPrefix = "crash-";
    public const string CrashExt = ".txt";

    /// <summary>"20261017_101500.log"; a second game copy started in the same second gets "20261017_101500_2.log".</summary>
    public static string DebugName(DateTime start, int copy = 1) => Name("", start, copy, DebugExt);

    /// <summary>"crash-20261017_101500.txt" (the time the crashed session's trail ended).</summary>
    public static string CrashName(DateTime when, int copy = 1) => Name(CrashPrefix, when, copy, CrashExt);

    private static string Name(string prefix, DateTime t, int copy, string ext) =>
        prefix + t.ToString(Stamp, CultureInfo.InvariantCulture) + (copy > 1 ? "_" + copy.ToString(CultureInfo.InvariantCulture) : "") + ext;

    /// <summary>True for a name <see cref="DebugName"/> / <see cref="CrashName"/> makes with this prefix and extension.</summary>
    public static bool IsOurs(string name, string prefix, string ext)
    {
        if (string.IsNullOrEmpty(name) || !name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
            !name.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) return false;
        string core = name.Substring(prefix.Length, name.Length - prefix.Length - ext.Length);
        if (core.Length < Stamp.Length) return false;
        for (int i = 0; i < Stamp.Length; i++)
        {
            char c = core[i];
            if (i == 8 ? c != '_' : !char.IsDigit(c)) return false;
        }
        if (core.Length == Stamp.Length) return true;
        if (core[Stamp.Length] != '_' || core.Length == Stamp.Length + 1) return false;
        for (int i = Stamp.Length + 1; i < core.Length; i++)
            if (!char.IsDigit(core[i])) return false;
        return true;
    }

    /// <summary>
    /// The files to delete so that only the newest <paramref name="keep"/> of ours stay (their names sort by time).
    /// Names that aren't ours are never listed.
    /// </summary>
    public static List<string> ToDelete(IEnumerable<string> names, int keep, string prefix, string ext)
    {
        var ours = new List<string>();
        if (names != null)
            foreach (var n in names)
                if (IsOurs(n, prefix, ext)) ours.Add(n);
        // Newest first: the stamp sorts by time, and "_2" after the plain name of the same second.
        ours.Sort((a, b) => string.CompareOrdinal(SortKey(b, prefix), SortKey(a, prefix)));
        var delete = new List<string>();
        for (int i = Math.Max(0, keep); i < ours.Count; i++) delete.Add(ours[i]);
        return delete;
    }

    private static string SortKey(string name, string prefix)
    {
        string core = name.Substring(prefix.Length, Stamp.Length);
        int copy = 1;
        int us = name.IndexOf('_', prefix.Length + Stamp.Length);
        if (us >= 0)
        {
            int dot = name.LastIndexOf('.');
            int.TryParse(name.Substring(us + 1, dot - us - 1), NumberStyles.None, CultureInfo.InvariantCulture, out copy);
        }
        return core + copy.ToString("D4", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// One debug log entry: "10:15:02.123 f1234 main  ERROR [camera] text"; further lines of the text are indented, so
    /// every entry starts at the left edge.
    /// </summary>
    public static string Line(DateTime time, int frame, string thread, string level, string modId, string text)
    {
        var sb = new StringBuilder(64 + (text?.Length ?? 0));
        sb.Append(time.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture)).Append(" f").Append(frame.ToString(CultureInfo.InvariantCulture));
        sb.Append(' ').Append((thread ?? "?").PadRight(5)).Append(' ').Append((level ?? "INFO").PadRight(5)).Append(" [").Append(string.IsNullOrEmpty(modId) ? "-" : modId).Append("] ");
        string body = (text ?? "").Replace("\r\n", "\n").TrimEnd('\n', ' ');
        int start = 0;
        bool firstLine = true;
        while (start <= body.Length)
        {
            int nl = body.IndexOf('\n', start);
            if (nl < 0) nl = body.Length;
            if (!firstLine) sb.Append('\n').Append("    ");
            sb.Append(body, start, nl - start);
            firstLine = false;
            start = nl + 1;
        }
        return sb.ToString();
    }

    /// <summary>The level word of a <see cref="Line"/> ("ERROR"), or null for a continuation line or another text.</summary>
    public static string LevelOf(string line)
    {
        if (string.IsNullOrEmpty(line) || line.Length < 20 || line[0] == ' ' || line[2] != ':') return null;
        int f = line.IndexOf(" f", StringComparison.Ordinal);
        if (f < 0) return null;
        int threadEnd = line.IndexOf(' ', line.IndexOf(' ', f + 2) + 1);
        if (threadEnd < 0) return null;
        while (threadEnd < line.Length && line[threadEnd] == ' ') threadEnd++;
        int levelEnd = line.IndexOf(' ', threadEnd);
        return levelEnd > threadEnd ? line.Substring(threadEnd, levelEnd - threadEnd) : null;
    }
}
