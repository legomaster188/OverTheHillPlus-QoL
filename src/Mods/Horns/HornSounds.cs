using System;
using System.Collections.Generic;
using System.IO;
using OverTheHillPlus.Audio;
using OverTheHillPlus.Shared;
using UnityEngine;

namespace OverTheHillPlus.Horns;

/// <summary>One wav file in the horn folder, and whether it can be used.</summary>
internal sealed class HornFile
{
    public string Name;
    public string Path;
    public bool Ok;
    /// <summary>Why it can't be used (null when <see cref="Ok"/>).</summary>
    public string Problem;
    public double Seconds;
    internal long Length;
    internal DateTime Written;
}

/// <summary>
/// The horn sounds as FMOD sounds: the built-in ones (written once to the cache folder from <see cref="HornSynth"/>),
/// your own file, and the files other players sent (kept for this session only). Every file is validated before
/// FMOD sees it; FMOD only ever opens canonical copies (fmt + data only) in BepInEx/cache/oth.horns that this mod
/// wrote itself.
/// </summary>
internal static class HornSounds
{
    public static string CustomDir { get; private set; }
    public static string CacheDir { get; private set; }

    private static readonly Dictionary<string, IntPtr> Sounds = new();
    private static readonly HashSet<string> Failed = new();
    private static List<HornFile> _files = new();
    private static readonly Dictionary<string, HornFile> Checked = new(StringComparer.OrdinalIgnoreCase);
    private static float _filesAt = -100f;

    public static void Init()
    {
        CustomDir = Path.Combine(BepInEx.Paths.ConfigPath, "oth.horns");
        CacheDir = Path.Combine(BepInEx.Paths.CachePath, "oth.horns");
        try { Directory.CreateDirectory(CustomDir); } catch (Exception e) { DebugLog.Error("horns", e, "HornSounds.Init"); HornsPlugin.Logger.LogWarning("Could not create the horn folder: " + e.Message); }
        try
        {
            Directory.CreateDirectory(CacheDir);
            // Last session's copies (only the names this mod writes, only in its own cache folder).
            foreach (var f in Directory.GetFiles(CacheDir, "*.wav"))
            {
                string n = Path.GetFileName(f);
                if (n.StartsWith("builtin_", StringComparison.Ordinal) || n.StartsWith("own_", StringComparison.Ordinal) || n.StartsWith("peer_", StringComparison.Ordinal))
                    try { File.Delete(f); } catch (Exception ex) { DebugLog.Error("horns", ex, "HornSounds.Init"); /* in use or read-only: overwritten when needed */ }
            }
        }
        catch (Exception e) { DebugLog.Error("horns", e, "HornSounds.Init"); HornsPlugin.Logger.LogWarning("Could not prepare the horn cache folder: " + e.Message); }
    }

    // ---------------------------------------------------------------- sounds

    /// <summary>A built-in horn's sound (zero when FMOD isn't ready or the id is unknown).</summary>
    public static IntPtr BuiltIn(byte id)
    {
        string key = "builtin_" + id;
        if (Sounds.TryGetValue(key, out var s)) return s;
        var wav = HornSynth.Wav(id);
        return wav == null ? IntPtr.Zero : Load(key, wav);
    }

    /// <summary>A validated custom file's sound under <paramref name="key"/> ("own_..." or "peer_...").</summary>
    public static IntPtr Custom(string key, byte[] wav) => Sounds.TryGetValue(key, out var s) ? s : Load(key, wav);

    public static bool Has(string key) => Sounds.TryGetValue(key, out var s) && s != IntPtr.Zero;

    private static IntPtr Load(string key, byte[] wav)
    {
        if (Failed.Contains(key) || !FmodCore.Ready) return IntPtr.Zero;
        // Never hand FMOD anything that didn't pass the checks (also the ones that come from our own synth).
        if (!WavValidator.TryValidate(wav, out var info, out string why)) { Fail(key, "not a valid horn: " + why); return IntPtr.Zero; }
        // A canonical copy (plain fmt + data only): FMOD never parses a chunk the validator didn't check.
        string path = Path.Combine(CacheDir, key + ".wav");
        try { File.WriteAllBytes(path, WavValidator.Canonical(wav, info)); }
        catch (Exception e) { DebugLog.Error("horns", e, "HornSounds.Load"); Fail(key, "could not write " + path + ": " + e.Message); return IntPtr.Zero; }
        var s = FmodCore.CreateSound(path, FmodCore.Mode2D | FmodCore.CreateSample | FmodCore.LoopNormal | FmodCore.IgnoreTags, out int r);
        if (s == IntPtr.Zero) { Fail(key, "FMOD could not open it (error " + r + ")"); return IntPtr.Zero; }
        Sounds[key] = s;
        DebugLog.Verbose(HornsPlugin.ModId, () => $"horn sound '{key}' loaded: {info.Seconds:0.00} s, {wav.Length} B");
        return s;
    }

    private static void Fail(string key, string why)
    {
        Failed.Add(key);
        DebugLog.Error(HornsPlugin.ModId, $"horn sound '{key}': {why}");
        HornsUtil.LogOnce("sound" + key, $"Horn sound {key}: {why}");
    }

    /// <summary>Frees the sounds whose key starts with <paramref name="prefix"/> and deletes their cache copies.</summary>
    public static void Release(string prefix)
    {
        var gone = new List<string>();
        foreach (var kv in Sounds)
            if (kv.Key.StartsWith(prefix, StringComparison.Ordinal)) gone.Add(kv.Key);
        foreach (var k in gone)
        {
            try { FmodCore.Release(Sounds[k]); } catch (Exception ex) { DebugLog.Error("horns", ex, "HornSounds.Release"); }
            Sounds.Remove(k);
            try { File.Delete(Path.Combine(CacheDir, k + ".wav")); } catch (Exception ex) { DebugLog.Error("horns", ex, "HornSounds.Release"); }
        }
        Failed.RemoveWhere(k => k.StartsWith(prefix, StringComparison.Ordinal));
    }

    // ---------------------------------------------------------------- your own files

    /// <summary>The horn folder itself is a link (symlink, junction): refused, like Radio's.</summary>
    public static bool FolderIsLink => Directory.Exists(CustomDir) && ConfinedFolder.IsLink(CustomDir);

    /// <summary><see cref="FolderIsLink"/> as of the last <see cref="Files"/> listing (for the page, no disk access).</summary>
    public static bool FolderLinked { get; private set; }

    /// <summary>
    /// The wav files directly in the horn folder (no links, nothing outside it), checked. Listed at most every 2 s;
    /// a file is only read and validated again when its size or write time changed.
    /// </summary>
    public static List<HornFile> Files(bool fresh = false)
    {
        if (!fresh && Time.unscaledTime - _filesAt < 2f && Time.unscaledTime >= _filesAt) return _files;
        _filesAt = Time.unscaledTime;
        var list = new List<HornFile>();
        FolderLinked = FolderIsLink;
        if (FolderLinked) { _files = list; Checked.Clear(); return list; }
        try
        {
            foreach (var e in new DiskFolder().List(CustomDir))
            {
                if (e.IsDirectory || e.IsLink || !e.Name.EndsWith(".wav", StringComparison.OrdinalIgnoreCase)) continue;
                if (!ConfinedFolder.IsPlainName(e.Name) || !ConfinedFolder.IsInside(CustomDir, e.FullPath)) continue;
                long length;
                DateTime written;
                try { var fi = new FileInfo(e.FullPath); length = fi.Length; written = fi.LastWriteTimeUtc; }
                catch (Exception ex) { DebugLog.Error("horns", ex, "HornSounds.Files"); continue; }
                if (fresh || !Checked.TryGetValue(e.Name, out var f) || f.Length != length || f.Written != written)
                {
                    f = new HornFile { Name = e.Name, Path = e.FullPath, Length = length, Written = written };
                    f.Ok = WavValidator.TryReadFile(e.FullPath, out _, out var info, out f.Problem);
                    if (f.Ok) f.Seconds = info.Seconds;
                    DebugLog.Verbose(HornsPlugin.ModId, () => f.Ok ? $"horn file {e.Name} checked: ok, {f.Seconds:0.00} s, {length} B" : $"horn file {e.Name} checked: refused ({f.Problem})");
                    Checked[e.Name] = f;
                }
                list.Add(f);
                if (list.Count >= 100) break;
            }
        }
        catch (Exception e) { DebugLog.Error("horns", e, "HornSounds.Files"); HornsPlugin.Logger.LogDebug("Horn folder: " + e.Message); }
        list.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        _files = list;
        return list;
    }

    /// <summary>Reads your own file by name: only a plain file directly in the horn folder, and only a valid wav.</summary>
    public static bool ReadOwn(string name, out byte[] wav, out string problem)
    {
        wav = null;
        if (!ConfinedFolder.IsPlainName(name))
        {
            problem = "not a file name in the horn folder";
            return false;
        }
        if (FolderIsLink) { problem = "the horn folder is a link: put the files in the folder itself"; return false; }
        string path = Path.Combine(CustomDir, name);
        if (!ConfinedFolder.IsConfinedFile(CustomDir, path, out problem)) return false;
        return WavValidator.TryReadFile(path, out wav, out _, out problem);
    }
}
