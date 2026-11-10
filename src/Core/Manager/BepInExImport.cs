using System;
using System.Collections.Generic;
using System.IO;

namespace OverTheHillPlus.Core.Manager;

/// <summary>
/// One-time import of what the BepInEx build kept in BepInEx/config (car sidecars with parts and odometers, the stash,
/// stats, ghosts, horns, radio, liveries, settings) into the MelonLoader data folder, which every mod reads under the
/// same file names. Copies only what is missing there, never overwrites, never touches the BepInEx folder, and leaves a
/// marker so it happens once. Pure System.IO, so it can be unit tested.
/// </summary>
internal static class BepInExImport
{
    public const string MarkerFile = "oth.core.bepinex-import.done";

    // Big or regenerable, and nothing reads them to keep the game consistent.
    private static readonly string[] SkippedFolders = { "oth.screenshots", };

    /// <summary>Whether a path under BepInEx/config (relative, any slash) is worth carrying over.</summary>
    public static bool Wanted(string relative)
    {
        string p = relative.Replace('\\', '/').TrimStart('/');
        if (p.Length == 0) return false;
        int slash = p.IndexOf('/');
        string top = slash < 0 ? p : p.Substring(0, slash);
        foreach (var s in SkippedFolders) if (string.Equals(top, s, StringComparison.OrdinalIgnoreCase)) return false;
        string name = slash < 0 ? p : p.Substring(p.LastIndexOf('/') + 1);
        if (slash < 0 && string.Equals(name, "BepInEx.cfg", StringComparison.OrdinalIgnoreCase)) return false; // the loader's own
        if (name.EndsWith(ConfigMigration.MigratedSuffix, StringComparison.OrdinalIgnoreCase)) return false;
        if (name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) return false;
        if (slash < 0 && (name.Contains(".bak", StringComparison.OrdinalIgnoreCase) || name.Contains(".before-", StringComparison.OrdinalIgnoreCase)))
            return false; // hand-made backups
        // a settings file with a suffix after .cfg (.cfg.low, .cfg.bak...) is somebody's spare copy, not the file itself
        if (slash < 0 && name.Contains(".cfg.", StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }

    /// <summary>The files to copy: wanted ones the target does not have. Settings files that already exist in the target
    /// (the Melon version) stay as they are, like every other existing file.</summary>
    public static List<string> Plan(IEnumerable<string> sourceFiles, Func<string, bool> targetHas)
    {
        var list = new List<string>();
        foreach (var f in sourceFiles)
            if (Wanted(f) && !targetHas(f)) list.Add(f);
        return list;
    }

    /// <summary>Does the import once. Returns the files copied; the marker is written when nothing failed.</summary>
    public static int Run(string sourceDir, string targetDir, Action<string> log)
    {
        string marker = Path.Combine(targetDir, MarkerFile);
        if (File.Exists(marker)) return 0;
        if (!Directory.Exists(sourceDir))
        {
            File.WriteAllText(marker, "no BepInEx/config folder\n");
            return 0;
        }
        var sources = new List<string>();
        foreach (var f in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
            sources.Add(Path.GetRelativePath(sourceDir, f));
        var plan = Plan(sources, rel => File.Exists(Path.Combine(targetDir, rel)));
        int copied = 0, failed = 0;
        foreach (var rel in plan)
        {
            try
            {
                string to = Path.Combine(targetDir, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(to));
                File.Copy(Path.Combine(sourceDir, rel), to, false);
                copied++;
                log?.Invoke("[Import] copied " + rel.Replace('\\', '/') + " from BepInEx/config");
            }
            catch (Exception e)
            {
                failed++;
                log?.Invoke("[Import] could not copy " + rel.Replace('\\', '/') + ": " + e.Message);
            }
        }
        if (failed == 0) File.WriteAllText(marker, $"{DateTime.UtcNow:O}: {copied} file(s) copied from BepInEx/config\n");
        log?.Invoke($"[Import] BepInEx/config: {copied} file(s) copied, {failed} failed" + (failed > 0 ? " (tried again next start)" : ""));
        return copied;
    }
}
