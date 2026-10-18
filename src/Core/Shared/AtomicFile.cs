using System;
using System.IO;
using System.Text;

namespace OverTheHillPlus.Core.Shared;

/// <summary>
/// Writes a file so that a crash, kill or power cut mid-write never leaves it half written: the new content goes to
/// "&lt;path&gt;.tmp" first, which then replaces the file in one step (File.Replace; File.Move when there is no file
/// yet). A reader sees the old file or the new one, never a mix. Pure System.IO (unit-tested).
/// </summary>
internal static class AtomicFile
{
    public static void WriteAllText(string path, string text) => WriteAllBytes(path, new UTF8Encoding(false).GetBytes(text ?? ""));

    public static void WriteAllBytes(string path, byte[] bytes)
    {
        if (string.IsNullOrEmpty(path)) throw new ArgumentException("no path", nameof(path));
        // One temp file per process: several copies of the game on one PC (test copies) share the config folder and
        // must not write the same "<path>.tmp" at once (that failed with "the file is being used by another process").
        string tmp = TempOf(path);
        using (var f = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            f.Write(bytes ?? Array.Empty<byte>(), 0, bytes?.Length ?? 0);
            f.Flush(true); // on disk before the swap, so the swap never exposes an empty file
        }
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                if (File.Exists(path)) File.Replace(tmp, path, null);
                else File.Move(tmp, path);
                // a leftover of the older single temp name (a crash before its swap) is stale now
                try { if (File.Exists(path + ".tmp")) File.Delete(path + ".tmp"); } catch { }
                return;
            }
            catch (IOException) when (attempt < 4)
            {
                System.Threading.Thread.Sleep(15 * (attempt + 1)); // another copy is swapping the same file right now
            }
        }
    }

    /// <summary>This process's temp file for <paramref name="path"/>.</summary>
    internal static string TempOf(string path) => path + "." + Environment.ProcessId + ".tmp";

    /// <summary>
    /// Before reading <paramref name="path"/>: if it is missing but its "&lt;path&gt;.tmp" is there (a write whose final
    /// move failed after the old file was gone), the temp file becomes the file again. Never throws; true when it did.
    /// </summary>
    public static bool Recover(string path)
    {
        try
        {
            if (File.Exists(path)) return false;
            // the older single temp name, then this process's, then any other copy's (newest first)
            var candidates = new System.Collections.Generic.List<string> { path + ".tmp", TempOf(path) };
            try
            {
                string dir = Path.GetDirectoryName(Path.GetFullPath(path)), name = Path.GetFileName(path);
                var others = new DirectoryInfo(dir).GetFiles(name + ".*.tmp");
                Array.Sort(others, (a, b) => b.LastWriteTimeUtc.CompareTo(a.LastWriteTimeUtc));
                foreach (var o in others) candidates.Add(o.FullName);
            }
            catch { }
            foreach (var tmp in candidates)
            {
                if (!File.Exists(tmp) || new FileInfo(tmp).Length == 0) continue;
                File.Move(tmp, path);
                return true;
            }
            return false;
        }
        catch (Exception ex) { DebugLog.Error("core", ex, "AtomicFile.Recover"); return false; }
    }

    /// <summary>
    /// Moves a file that could not be read aside to "&lt;path&gt;.bad" (an older .bad is replaced), so the next write
    /// doesn't overwrite what may still be recovered by hand. The new name, or null when it could not be moved.
    /// </summary>
    public static string SetAside(string path)
    {
        try
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
            string bad = path + ".bad";
            if (File.Exists(bad)) File.Delete(bad);
            File.Move(path, bad);
            return bad;
        }
        catch (Exception ex) { DebugLog.Error("core", ex, "AtomicFile.SetAside"); return null; }
    }
}
