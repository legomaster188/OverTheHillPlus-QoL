using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace OverTheHillPlus.Ghosts;

/// <summary>
/// A saved ghost: its file name (without the extension), its header (<see cref="Header"/>: no frames) and how to read
/// the whole drive when it is played.
/// </summary>
internal sealed class GhostEntry
{
    public readonly string Name;
    public readonly GhostRecording Header;
    private readonly Func<byte[]> _read;

    public GhostEntry(string name, GhostRecording header, Func<byte[]> read = null)
    {
        Name = name;
        Header = header;
        _read = read;
    }

    /// <summary>The same ghost under another name.</summary>
    public GhostEntry Renamed(string name, Func<byte[]> read) => new(name, Header, read);

    /// <summary>Reads the whole drive (frames included); false with a reason when the file is gone or damaged.</summary>
    public bool TryLoad(out GhostRecording rec, out string error)
    {
        rec = null;
        if (Header.Frames.Count > 0) { rec = Header; error = null; return true; }
        if (_read == null) { error = "it has no frames"; return false; }
        byte[] data;
        try { data = _read(); }
        catch (Exception e) { DebugLog.Error("ghosts", e, "GhostEntry.TryLoad"); error = "can't read it (" + e.Message + ")"; return false; }
        return GhostCodec.TryDecode(data, out rec, out error);
    }
}

/// <summary>The list of saved ghosts (pure: the files come in as name + reader).</summary>
internal static class GhostIndex
{
    /// <summary>
    /// Reads each file's header (the checksum covers the whole file); one that can't be read or is damaged is skipped
    /// with one <paramref name="log"/> line, and the others still load. Frames are read later, on play.
    /// </summary>
    public static List<GhostEntry> Load(IEnumerable<(string name, Func<byte[]> read)> files, Action<string> log)
    {
        var list = new List<GhostEntry>();
        foreach (var (name, read) in files)
        {
            byte[] data;
            try { data = read(); }
            catch (Exception e)
            {
                DebugLog.Error("ghosts", e, "GhostIndex.Load");
                log?.Invoke($"Ghost \"{name}\" skipped: can't read it ({e.Message}).");
                continue;
            }
            if (!GhostCodec.TryDecodeHeader(data, out var rec, out string error))
            {
                log?.Invoke($"Ghost \"{name}\" skipped: {error}.");
                continue;
            }
            list.Add(new GhostEntry(name, rec, read));
        }
        Sort(list);
        return list;
    }

    /// <summary>Newest first.</summary>
    public static void Sort(List<GhostEntry> list) =>
        list.Sort((a, b) => b.Header.CreatedUnix.CompareTo(a.Header.CreatedUnix));

    /// <summary>The fastest finished race on <paramref name="trackId"/>, or null.</summary>
    public static GhostEntry BestFor(IEnumerable<GhostEntry> entries, string trackId)
    {
        if (string.IsNullOrEmpty(trackId)) return null;
        GhostEntry best = null;
        foreach (var e in entries)
        {
            var r = e.Header;
            if (r.RaceTimeMs < 0 || !string.Equals(r.TrackId, trackId, StringComparison.OrdinalIgnoreCase)) continue;
            if (best == null || r.RaceTimeMs < best.Header.RaceTimeMs) best = e;
        }
        return best;
    }
}

/// <summary>
/// Ghost file names: kept to letters, digits, spaces, '-', '_', '.' and ',' (no separators, no "..", no device names)
/// so every file stays directly inside the mod's own folder.
/// </summary>
internal static class GhostNames
{
    public const string Extension = ".ghost";
    public const int MaxLength = 48;

    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>A safe file stem for <paramref name="name"/>, or null when nothing usable is left.</summary>
    public static string Sanitize(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var sb = new StringBuilder(name.Length);
        foreach (char c in name)
        {
            if (char.IsLetterOrDigit(c) || c == '-' || c == '_' || c == ',' || c == '(' || c == ')') sb.Append(c);
            else if (c == ' ' || c == '.') sb.Append(c);
        }
        string s = sb.ToString();
        while (s.Contains("..")) s = s.Replace("..", ".");
        s = s.Trim(' ', '.');
        if (s.Length > MaxLength) s = s.Substring(0, MaxLength).TrimEnd(' ', '.');
        if (s.Length == 0) return null;
        // Windows treats "con.txt" and "con .x" as the device too: test the stem without its trailing spaces.
        string stem = s.Split('.')[0].TrimEnd(' ');
        if (Reserved.Contains(stem)) s = "_" + s;
        return s.Length > MaxLength ? s.Substring(0, MaxLength) : s;
    }

    /// <summary>The full path for <paramref name="name"/> in <paramref name="folder"/>, or null when it isn't safe.</summary>
    public static string PathFor(string folder, string name)
    {
        string stem = Sanitize(name);
        if (stem == null) return null;
        string dir = Path.GetFullPath(folder);
        string path = Path.GetFullPath(Path.Combine(dir, stem + Extension));
        return string.Equals(Path.GetDirectoryName(path), dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase) ? path : null;
    }

    /// <summary><paramref name="name"/>, or "name 2", "name 3"… when <paramref name="taken"/> already has it.</summary>
    public static string Unique(string name, Func<string, bool> taken)
    {
        string baseName = Sanitize(name) ?? "Ghost";
        if (!taken(baseName)) return baseName;
        for (int i = 2; i < 10000; i++)
        {
            string suffix = " " + i;
            string stem = baseName.Length + suffix.Length > MaxLength ? baseName.Substring(0, MaxLength - suffix.Length) : baseName;
            string candidate = stem + suffix;
            if (!taken(candidate)) return candidate;
        }
        return baseName + " " + DateTime.UtcNow.Ticks;
    }
}
