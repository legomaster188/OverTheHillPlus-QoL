using System;
using System.Collections.Generic;
using System.IO;
using OverTheHillPlus.Shared;

namespace OverTheHillPlus.Radio;

/// <summary>One playable file.</summary>
internal sealed class RadioTrack
{
    /// <summary>The file name without its extension, as shown in the "now playing" notice.</summary>
    public readonly string Name;
    public readonly string Path;

    public RadioTrack(string name, string path)
    {
        Name = name;
        Path = path;
    }
}

/// <summary>A station: one sub-folder of the radio folder (or the files loose in it).</summary>
internal sealed class RadioStation
{
    public readonly string Name;
    public readonly List<RadioTrack> Tracks;

    public RadioStation(string name, List<RadioTrack> tracks)
    {
        Name = name;
        Tracks = tracks;
    }
}

/// <summary>
/// Finds the stations under the radio folder, and never anything outside it:
/// <list type="bullet">
/// <item>Files loose in the folder form the first station (<see cref="LooseStationName"/>); each sub-folder is a
///   station of the files directly in it. Deeper folders are not read.</item>
/// <item>Only .mp3, .ogg and .wav files count.</item>
/// <item>Links (symlinks, junctions) are never followed or played, and an entry whose full path is not inside the
///   folder is skipped, so nothing can point the radio at another place on the disk.</item>
/// <item>At most <see cref="MaxStations"/> stations of <see cref="MaxTracksPerStation"/> tracks.</item>
/// </list>
/// Pure (only System.IO through <see cref="IFolderLister"/>), so the tests cover it.
/// </summary>
internal static class RadioLibrary
{
    public const string LooseStationName = "Radio";
    public const int MaxStations = 64;
    public const int MaxTracksPerStation = 1000;
    private static readonly string[] Extensions = { ".mp3", ".ogg", ".wav" };

    public static bool IsAudio(string name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        foreach (var ext in Extensions)
            if (name.Length > ext.Length && name.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    public static List<RadioStation> Scan(string root, IFolderLister fs)
    {
        var stations = new List<RadioStation>();
        List<FolderEntry> top;
        try { top = new List<FolderEntry>(fs.List(root)); }
        catch (Exception ex) { DebugLog.Error("radio", ex, "RadioLibrary.Scan"); return stations; }

        var loose = Tracks(root, top);
        if (loose.Count > 0) stations.Add(new RadioStation(LooseStationName, loose));

        var dirs = new List<FolderEntry>();
        foreach (var e in top)
            if (e.IsDirectory && !e.IsLink && ConfinedFolder.IsPlainName(e.Name) && ConfinedFolder.IsInside(root, e.FullPath)) dirs.Add(e);
        dirs.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));

        foreach (var d in dirs)
        {
            if (stations.Count >= MaxStations) break;
            List<FolderEntry> inner;
            try { inner = new List<FolderEntry>(fs.List(d.FullPath)); }
            catch (Exception ex) { DebugLog.Error("radio", ex, "RadioLibrary.Scan"); continue; }
            var tracks = Tracks(root, inner);
            if (tracks.Count > 0) stations.Add(new RadioStation(d.Name, tracks));
        }
        return stations;
    }

    private static List<RadioTrack> Tracks(string root, List<FolderEntry> entries)
    {
        var list = new List<RadioTrack>();
        foreach (var e in entries)
        {
            if (e.IsDirectory || e.IsLink || !ConfinedFolder.IsPlainName(e.Name) || !IsAudio(e.Name) || !ConfinedFolder.IsInside(root, e.FullPath)) continue;
            list.Add(new RadioTrack(Path.GetFileNameWithoutExtension(e.Name), Path.GetFullPath(e.FullPath)));
        }
        list.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        if (list.Count > MaxTracksPerStation) list.RemoveRange(MaxTracksPerStation, list.Count - MaxTracksPerStation);
        return list;
    }
}
