using System;
using System.Collections.Generic;
using System.IO;

namespace OverTheHillPlus.Shared;

/// <summary>One entry of a folder listing.</summary>
internal readonly struct FolderEntry
{
    public readonly string Name;
    public readonly string FullPath;
    public readonly bool IsDirectory;
    /// <summary>A symbolic link, junction or other reparse point: never followed, never opened.</summary>
    public readonly bool IsLink;

    public FolderEntry(string name, string fullPath, bool isDirectory, bool isLink)
    {
        Name = name;
        FullPath = fullPath;
        IsDirectory = isDirectory;
        IsLink = isLink;
    }
}

/// <summary>Lists one folder (the disk in the game, a fake in the tests). May throw; callers catch it.</summary>
internal interface IFolderLister
{
    IEnumerable<FolderEntry> List(string dir);
}

/// <summary>The real disk, one level, hidden and system entries skipped. Reparse points are reported as links.</summary>
internal sealed class DiskFolder : IFolderLister
{
    public IEnumerable<FolderEntry> List(string dir)
    {
        var di = new DirectoryInfo(dir);
        if (!di.Exists) yield break;
        foreach (var fsi in di.EnumerateFileSystemInfos("*", new EnumerationOptions
                 {
                     RecurseSubdirectories = false,
                     AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
                     IgnoreInaccessible = true,
                     ReturnSpecialDirectories = false,
                 }))
        {
            var a = fsi.Attributes;
            yield return new FolderEntry(fsi.Name, fsi.FullName, (a & FileAttributes.Directory) != 0, (a & FileAttributes.ReparsePoint) != 0);
        }
    }
}

/// <summary>
/// Helpers for mods that read files from their own folder only (Radio, Custom Horns): a path must resolve strictly
/// inside the folder, names must be plain, and no link (symlink, junction) is ever followed, on the way in either.
/// Pure (System.IO only), so the tests cover it.
/// </summary>
internal static class ConfinedFolder
{
    private static readonly char[] BadNameChars = { '/', '\\', ':', '\0' };

    /// <summary>True when <paramref name="path"/> resolves to something strictly inside <paramref name="root"/>.</summary>
    public static bool IsInside(string root, string path)
    {
        if (string.IsNullOrEmpty(root) || string.IsNullOrEmpty(path)) return false;
        try
        {
            string r = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
            string p = Path.GetFullPath(path);
            return p.Length > r.Length && p.StartsWith(r, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) { DebugLog.Error("core", ex, "ConfinedFolder.IsInside"); return false; } // invalid characters and the like
    }

    /// <summary>An ordinary name: not "." / "..", no separators, not hidden by a leading dot.</summary>
    public static bool IsPlainName(string name) =>
        !string.IsNullOrEmpty(name) && name[0] != '.' && name.IndexOfAny(BadNameChars) < 0;

    /// <summary>The path is a link (symlink, junction, other reparse point). Unreadable counts as a link.</summary>
    public static bool IsLink(string path)
    {
        try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; }
        catch (Exception ex) { DebugLog.Error("core", ex, "ConfinedFolder.IsLink"); return true; }
    }

    /// <summary>
    /// Checked right before a file is opened: it is a plain existing file strictly inside <paramref name="root"/>, and
    /// neither it, nor any folder between it and the root, nor the root itself is a link.
    /// </summary>
    public static bool IsConfinedFile(string root, string path, out string why)
    {
        why = null;
        if (!IsInside(root, path)) { why = "not in the folder"; return false; }
        string full, top;
        try
        {
            full = Path.GetFullPath(path);
            top = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        }
        catch (Exception ex) { DebugLog.Error("core", ex, "ConfinedFolder.IsConfinedFile"); why = "not a valid path"; return false; }
        try
        {
            var a = File.GetAttributes(full);
            if ((a & FileAttributes.Directory) != 0) { why = "a folder, not a file"; return false; }
            if ((a & FileAttributes.ReparsePoint) != 0) { why = "a link"; return false; }
        }
        catch (Exception ex) { DebugLog.Error("core", ex, "ConfinedFolder.IsConfinedFile"); why = "missing"; return false; }
        // Every folder on the way, up to and including the root.
        for (string dir = Path.GetDirectoryName(full); dir != null; dir = Path.GetDirectoryName(dir))
        {
            if (IsLink(dir)) { why = "inside a linked folder"; return false; }
            if (string.Equals(Path.TrimEndingDirectorySeparator(dir), top, StringComparison.OrdinalIgnoreCase)) return true;
        }
        why = "not in the folder";
        return false;
    }
}
