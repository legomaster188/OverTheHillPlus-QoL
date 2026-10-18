using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx;

namespace OverTheHillPlus.Ghosts;

/// <summary>
/// The saved ghosts in BepInEx/config/oth.ghosts/: one <c>.ghost</c> file each, named after the ghost. Only files
/// directly in that folder are read (no sub-folders, no links), and every name is cleaned by <see cref="GhostNames"/>
/// so nothing is written anywhere else. A damaged file is skipped with one log line.
/// The folder is read once (first use, or "Reload the list"), headers only (<see cref="MaxListed"/> newest files);
/// save, rename and delete update the list in place, and a ghost's frames are read when it is played.
/// </summary>
internal static class GhostLibrary
{
    /// <summary>Bigger than two hours of driving: anything larger isn't one of ours.</summary>
    private const long MaxFileBytes = 16L * 1024 * 1024;

    /// <summary>Only the newest files are listed (each is read once to check it).</summary>
    internal const int MaxListed = 200;

    private static List<GhostEntry> _entries = new();
    private static readonly Dictionary<string, string> FilePaths = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> Logged = new(StringComparer.OrdinalIgnoreCase);
    private static bool _loaded;

    public static string Folder => Path.Combine(Paths.ConfigPath, "oth.ghosts");

    /// <summary>Newest first.</summary>
    public static IReadOnlyList<GhostEntry> Entries
    {
        get
        {
            if (!_loaded) Refresh();
            return _entries;
        }
    }

    /// <summary>Reads the folder again (headers only).</summary>
    public static void Refresh()
    {
        _loaded = true;
        FilePaths.Clear();
        var files = new List<(string, Func<byte[]>)>();
        try
        {
            Directory.CreateDirectory(Folder);
            var infos = Directory.GetFiles(Folder, "*" + GhostNames.Extension, SearchOption.TopDirectoryOnly)
                .Where(p => string.Equals(Path.GetExtension(p), GhostNames.Extension, StringComparison.OrdinalIgnoreCase))
                .Select(p => new FileInfo(p))
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .ToList();
            if (infos.Count > MaxListed)
                Log($"The ghost folder has {infos.Count} ghosts: only the newest {MaxListed} are listed.");
            foreach (var info in infos.Take(MaxListed))
            {
                string name = Path.GetFileNameWithoutExtension(info.Name);
                // Links could point outside the folder: never followed.
                if ((info.Attributes & FileAttributes.ReparsePoint) != 0) { Log($"Ghost \"{name}\" skipped: it is a link."); continue; }
                if (info.Length > MaxFileBytes) { Log($"Ghost \"{name}\" skipped: it is too big."); continue; }
                FilePaths[name] = info.FullName;
                files.Add((name, Reader(info.FullName)));
            }
        }
        catch (Exception e)
        {
            DebugLog.Error("ghosts", e, "GhostLibrary.Refresh");
            GhostsPlugin.Logger.LogWarning($"Can't list the ghost folder {Folder}: {e.Message}");
        }
        _entries = GhostIndex.Load(files, Log);
        DebugLog.Verbose(GhostsPlugin.ModId, () => $"ghost folder scanned: {files.Count} file(s) listed, {_entries.Count} readable ghost(s), {files.Count - _entries.Count} unreadable");
    }

    private static Func<byte[]> Reader(string path) => () =>
    {
        var info = new FileInfo(path);
        if ((info.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("it is a link");
        if (info.Length > MaxFileBytes) throw new IOException("it is too big");
        return File.ReadAllBytes(path);
    };

    /// <summary>One line per damaged file (or other problem), not one per refresh.</summary>
    private static void Log(string line)
    {
        if (Logged.Add(line))
        {
            DebugLog.Info(GhostsPlugin.ModId, line);
            GhostsPlugin.Logger.LogWarning(line);
        }
    }

    public static GhostEntry Find(string name)
    {
        foreach (var e in Entries)
            if (string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase)) return e;
        return null;
    }

    public static GhostEntry BestFor(string trackId) => GhostIndex.BestFor(Entries, trackId);

    /// <summary>
    /// The whole drive of <paramref name="entry"/> for playback; null with <paramref name="error"/> when the file is
    /// gone or damaged (then it is dropped from the list, with one log line).
    /// </summary>
    public static GhostRecording Load(GhostEntry entry, out string error)
    {
        if (entry.TryLoad(out var rec, out error)) return rec;
        Log($"Ghost \"{entry.Name}\" skipped: {error}.");
        _entries.Remove(entry);
        FilePaths.Remove(entry.Name);
        return null;
    }

    /// <summary>Saves <paramref name="rec"/> under a free name close to <paramref name="wanted"/>; returns that name, or null.</summary>
    public static string Save(GhostRecording rec, string wanted)
    {
        if (!_loaded) Refresh();
        string tmp = null;
        try
        {
            Directory.CreateDirectory(Folder);
            string name = GhostNames.Unique(wanted, n => FilePaths.ContainsKey(n) || File.Exists(GhostNames.PathFor(Folder, n) ?? ""));
            string path = GhostNames.PathFor(Folder, name);
            if (path == null) return null;
            tmp = path + ".tmp";
            File.WriteAllBytes(tmp, GhostCodec.Encode(rec));
            File.Move(tmp, path, true);
            tmp = null;
            FilePaths[name] = path;
            _entries.Add(new GhostEntry(name, rec.HeaderCopy(), Reader(path)));
            GhostIndex.Sort(_entries);
            DebugLog.Info(GhostsPlugin.ModId, $"ghost saved as '{name}': {rec.Frames.Count} frames, {rec.Duration:0.0} s, car {rec.Car}, track '{rec.TrackId}'");
            return name;
        }
        catch (Exception e)
        {
            DebugLog.Error("ghosts", e, "GhostLibrary.Save");
            GhostsPlugin.Logger.LogWarning($"Couldn't save the ghost: {e.Message}");
            return null;
        }
        finally
        {
            if (tmp != null)
            {
                try { File.Delete(tmp); } catch (Exception ex) { DebugLog.Error("ghosts", ex, "GhostLibrary.Save"); }
            }
        }
    }

    public static bool Delete(string name)
    {
        try
        {
            if (!_loaded) Refresh();
            if (!FilePaths.TryGetValue(name, out string path)) return false;
            File.Delete(path);
            FilePaths.Remove(name);
            _entries.RemoveAll(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));
            return true;
        }
        catch (Exception e)
        {
            DebugLog.Error("ghosts", e, "GhostLibrary.Delete");
            GhostsPlugin.Logger.LogWarning($"Couldn't delete ghost \"{name}\": {e.Message}");
            return false;
        }
    }

    /// <summary>Renames a ghost; returns the new name (made safe and unique) or null with <paramref name="error"/>.</summary>
    public static string Rename(string name, string wanted, out string error)
    {
        error = null;
        try
        {
            if (!_loaded) Refresh();
            if (!FilePaths.TryGetValue(name, out string from)) { error = "That ghost is gone."; return null; }
            string clean = GhostNames.Sanitize(wanted);
            if (clean == null) { error = "Use letters or numbers in the name."; return null; }
            if (string.Equals(clean, name, StringComparison.Ordinal)) return name;
            string target = string.Equals(clean, name, StringComparison.OrdinalIgnoreCase)
                ? clean // only the case changes
                : GhostNames.Unique(clean, n => FilePaths.ContainsKey(n) || File.Exists(GhostNames.PathFor(Folder, n) ?? ""));
            string to = GhostNames.PathFor(Folder, target);
            if (to == null) { error = "Use letters or numbers in the name."; return null; }
            File.Move(from, to);
            FilePaths.Remove(name);
            FilePaths[target] = to;
            int i = _entries.FindIndex(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));
            if (i >= 0) _entries[i] = _entries[i].Renamed(target, Reader(to));
            return target;
        }
        catch (Exception e)
        {
            DebugLog.Error("ghosts", e, "GhostLibrary.Rename");
            error = "Couldn't rename it.";
            GhostsPlugin.Logger.LogWarning($"Couldn't rename ghost \"{name}\": {e.Message}");
            return null;
        }
    }
}
