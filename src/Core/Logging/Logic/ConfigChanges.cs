using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace OverTheHillPlus.Logging;

/// <summary>The debug log's text for a changed setting, and which mod a config file belongs to. Pure.</summary>
internal static class ConfigLine
{
    private const int MaxValue = 80;

    /// <summary>"[Camera] Mode Drone -> Hood": section, key, old and new value (long or multi-line values are cut).</summary>
    public static string Format(string section, string key, string oldValue, string newValue) =>
        "[" + (section ?? "?") + "] " + (key ?? "?") + " " + Show(oldValue) + " -> " + Show(newValue);

    /// <summary>A value for one log line: "?" for unknown, "(empty)" for "", control characters escaped, cut at 80.</summary>
    public static string Show(string value)
    {
        if (value == null) return "?";
        if (value.Length == 0) return "(empty)";
        var sb = new StringBuilder(Math.Min(value.Length, MaxValue + 1));
        foreach (char c in value)
        {
            if (sb.Length >= MaxValue) { sb.Append('…'); break; }
            if (c == '\n') sb.Append("\\n");
            else if (c == '\r') { }
            else if (c == '\t') sb.Append(' ');
            else if (c < ' ') sb.Append('?');
            else sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>"oth.chat.cfg" gives "chat" (core's "oth.core.cfg" gives "core"); any other file keeps its name without ".cfg".</summary>
    public static string ModOfFile(string path)
    {
        string name = Path.GetFileName(path ?? "") ?? "";
        if (name.EndsWith(".cfg", StringComparison.OrdinalIgnoreCase)) name = name.Substring(0, name.Length - 4);
        if (name.StartsWith("oth.", StringComparison.OrdinalIgnoreCase) && name.Length > 4) name = name.Substring(4);
        return name.Length == 0 ? "?" : name;
    }
}

/// <summary>
/// Setting changes, coalesced: a slider dragged for a second changes its value every frame, so a change only becomes a
/// line once its setting has been quiet for a moment, as "first old value -> last value"; a setting that ended where it
/// started gives no line. Pure; the caller (main thread) owns it.
/// </summary>
internal sealed class ConfigChangeBook
{
    private sealed class Pending
    {
        public string Mod, Section, Key, Old, New;
        public double LastAt;
        public int Changes;
    }

    private readonly Dictionary<string, Pending> _pending = new(StringComparer.Ordinal);
    private readonly List<string> _order = new();
    private readonly int _capacity;

    public ConfigChangeBook(int capacity = 400) => _capacity = Math.Max(1, capacity);

    /// <summary>Settings with a change not yet turned into a line.</summary>
    public int PendingCount => _pending.Count;

    /// <summary>Notes a change at <paramref name="now"/> (seconds). <paramref name="oldValue"/> null = not known.</summary>
    public void Note(string mod, string section, string key, string oldValue, string newValue, double now)
    {
        string id = mod + "\n" + section + "\n" + key;
        if (_pending.TryGetValue(id, out var p))
        {
            p.New = newValue;
            p.LastAt = now;
            p.Changes++;
            return;
        }
        if (_pending.Count >= _capacity) return;
        _pending[id] = new Pending { Mod = mod ?? "?", Section = section, Key = key, Old = oldValue, New = newValue, LastAt = now, Changes = 1 };
        _order.Add(id);
    }

    /// <summary>
    /// The settings quiet for <paramref name="quietSeconds"/> as (mod, line), oldest first, and forgets them. A value
    /// that came back to where it started gives no line. At most <paramref name="max"/> lines; the rest wait.
    /// </summary>
    public List<(string mod, string line)> TakeSettled(double now, double quietSeconds, int max = 60)
    {
        var lines = new List<(string, string)>();
        for (int i = 0; i < _order.Count && lines.Count < max;)
        {
            string id = _order[i];
            var p = _pending[id];
            if (now - p.LastAt < quietSeconds) { i++; continue; }
            _order.RemoveAt(i);
            _pending.Remove(id);
            if (p.Old != null && p.Old == p.New) continue;
            string line = ConfigLine.Format(p.Section, p.Key, p.Old, p.New);
            if (p.Changes > 1) line += " (" + p.Changes + " changes)";
            lines.Add((p.Mod, line));
        }
        return lines;
    }
}

/// <summary>A config file read the way BepInEx does, to find lines and values it could not make sense of. Pure.</summary>
internal static class ConfigFileScan
{
    /// <summary>What a scan found: each section's keys with the text of their value, and the lines that are neither.</summary>
    public sealed class Result
    {
        public readonly Dictionary<string, Dictionary<string, string>> Values = new(StringComparer.Ordinal);
        public readonly List<(int line, string text)> BadLines = new();
    }

    public static Result Parse(IEnumerable<string> lines)
    {
        var r = new Result();
        string section = null;
        int n = 0;
        foreach (var raw in lines)
        {
            n++;
            string line = (raw ?? "").Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            if (line[0] == '[')
            {
                int end = line.IndexOf(']');
                if (end > 1) { section = line.Substring(1, end - 1).Trim(); continue; }
                r.BadLines.Add((n, line));
                continue;
            }
            int eq = line.IndexOf('=');
            if (eq <= 0 || section == null) { r.BadLines.Add((n, line)); continue; }
            string key = line.Substring(0, eq).Trim();
            string value = line.Substring(eq + 1).Trim();
            if (!r.Values.TryGetValue(section, out var keys)) r.Values[section] = keys = new Dictionary<string, string>(StringComparer.Ordinal);
            keys[key] = value;
        }
        return r;
    }

    /// <summary>"3 lines it could not read (first: line 12 'foo bar')", or null.</summary>
    public static string BadLinesText(Result r)
    {
        if (r == null || r.BadLines.Count == 0) return null;
        var first = r.BadLines[0];
        string t = first.text.Length > 60 ? first.text.Substring(0, 60) + "…" : first.text;
        return r.BadLines.Count + (r.BadLines.Count == 1 ? " line" : " lines") + " it could not read (first: line " + first.line + " '" + t + "')";
    }
}
