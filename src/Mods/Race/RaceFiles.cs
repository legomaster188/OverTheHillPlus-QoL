using System;
using System.Collections.Generic;
using System.IO;
using BepInEx;

namespace OverTheHillPlus.Race;

/// <summary>
/// Race Mode's files under BepInEx/config/oth.race/: tracks/&lt;name&gt;.json and boards/&lt;track id&gt;.json. File names
/// are always built with <see cref="RaceTrack.FileNameFor"/> (letters, digits and dashes), so nothing typed or received
/// can point outside these folders. Writes go through a temporary file.
/// </summary>
internal static class RaceFiles
{
    internal sealed class TrackFile
    {
        /// <summary>The file name without .json, as found on disk.</summary>
        public string File;
        /// <summary>The full path that was listed (deleting removes exactly this file).</summary>
        public string Path;
        public RaceTrack Track;
    }

    private static string Root => Path.Combine(Paths.ConfigPath, "oth.race");
    private static string Tracks => Path.Combine(Root, "tracks");
    private static string Boards => Path.Combine(Root, "boards");

    private static List<TrackFile> _tracks;
    private static float _tracksAt = -100f;

    /// <summary>The saved tracks (re-read at most every 5 s, and after a save or delete).</summary>
    public static IReadOnlyList<TrackFile> List()
    {
        float now = UnityEngine.Time.unscaledTime;
        if (_tracks != null && now - _tracksAt < 5f && now >= _tracksAt) return _tracks;
        _tracksAt = now;
        var list = new List<TrackFile>();
        int unreadable = 0;
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            if (Directory.Exists(Tracks))
                foreach (var path in Directory.GetFiles(Tracks, "*.json"))
                {
                    var t = RaceTrack.FromJson(ReadSmall(path));
                    if (t != null) list.Add(new TrackFile { File = Path.GetFileNameWithoutExtension(path), Path = path, Track = t });
                    else
                    {
                        unreadable++;
                        DebugLog.Verbose(RacePlugin.ModId, () => $"{Path.GetFileName(path)} is not a readable track (bad JSON, too big or empty)");
                    }
                    if (list.Count >= 200) break;
                }
            DebugLog.Verbose(RacePlugin.ModId, () => $"track list: {list.Count} track(s), {unreadable} unreadable, {(System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency:0.0} ms");
        }
        catch (Exception e) { DebugLog.Error("race", e, "RaceFiles.List"); RacePlugin.Logger.LogWarning("[Race] listing tracks: " + e.Message); }
        list.Sort((a, b) => string.Compare(a.Track.Name, b.Track.Name, StringComparison.OrdinalIgnoreCase));
        return _tracks = list;
    }

    public static string SaveTrack(RaceTrack t)
    {
        string why = t.Validate();
        if (why != null) return "Failed: " + why + ".";
        string file = FileFor(t);
        if (!Write(Path.Combine(Tracks, file + ".json"), t.ToJson())) return "Failed: could not write the track file.";
        _tracks = null;
        DebugLog.Info(RacePlugin.ModId, $"track '{t.Name}' saved as {file}.json ({t.Gates.Count} gates)");
        return $"Saved the track \"{t.Name}\" ({file}.json).";
    }

    /// <summary>The file a track saves to: its own name, or name-2, -3, ... when a different track has that file.</summary>
    public static string FileFor(RaceTrack t) => RaceTrack.FileFor(t.Name, NameInFile);

    private static string NameInFile(string file)
    {
        try
        {
            string path = Path.Combine(Tracks, file + ".json");
            if (!File.Exists(path)) return null;
            // A file that doesn't read as a track still counts as taken: "<>" never matches a track name.
            return RaceTrack.FromJson(ReadSmall(path))?.Name ?? "<>";
        }
        catch (Exception ex) { DebugLog.Error("race", ex, "RaceFiles.NameInFile"); return "<>"; }
    }

    /// <summary>Deletes the listed file itself (only inside the tracks folder).</summary>
    public static void DeleteTrack(TrackFile f)
    {
        if (f?.Path == null) return;
        try
        {
            string dir = Path.GetFullPath(Tracks).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string full = Path.GetFullPath(f.Path);
            if (!string.Equals(Path.GetDirectoryName(full), dir, StringComparison.OrdinalIgnoreCase)) return;
            if (File.Exists(full)) File.Delete(full);
            DebugLog.Info(RacePlugin.ModId, $"track file deleted: {Path.GetFileName(full)}");
        }
        catch (Exception e) { DebugLog.Error("race", e, "RaceFiles.DeleteTrack"); RacePlugin.Logger.LogWarning("[Race] deleting a track: " + e.Message); }
        _tracks = null;
    }

    public static Leaderboard LoadBoard(string trackId)
    {
        try
        {
            string path = Path.Combine(Boards, RaceTrack.FileNameFor(trackId, 64) + ".json");
            if (!File.Exists(path)) return null;
            var board = Leaderboard.FromJson(ReadSmall(path));
            DebugLog.Verbose(RacePlugin.ModId, () => $"leaderboard of '{trackId}' read: {board?.Entries.Count ?? 0} entries");
            return board;
        }
        catch (Exception ex) { DebugLog.Error("race", ex, "RaceFiles.LoadBoard"); return null; }
    }

    public static void SaveBoard(Leaderboard b)
    {
        if (b == null || string.IsNullOrEmpty(b.TrackId)) return;
        DebugLog.Verbose(RacePlugin.ModId, () => $"leaderboard of '{b.TrackId}' saved: {b.Entries.Count} entries");
        Write(Path.Combine(Boards, RaceTrack.FileNameFor(b.TrackId, 64) + ".json"), b.ToJson());
    }

    private static string ReadSmall(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length > 512 * 1024) return null;
        return File.ReadAllText(path);
    }

    private static bool Write(string path, string text)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, text);
            File.Copy(tmp, path, overwrite: true);
            File.Delete(tmp);
            return true;
        }
        catch (Exception e)
        {
            DebugLog.Error("race", e, "RaceFiles.Write");
            RacePlugin.Logger.LogWarning("[Race] writing " + Path.GetFileName(path) + ": " + e.Message);
            return false;
        }
    }
}
