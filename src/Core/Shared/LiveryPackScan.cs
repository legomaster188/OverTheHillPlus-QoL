using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace OverTheHillPlus.Shared;

/// <summary>One livery as a pack's manifest.json declares it.</summary>
internal sealed class LiveryPackEntry
{
    public string Car, Name, Texture, Icon;
    /// <summary>The texture file was found inside the pack (the game skips entries whose texture is missing).</summary>
    public bool Loadable;
    /// <summary>Full path of the texture file (set when <see cref="Loadable"/>): the one at the top of the pack when
    /// there is one, otherwise the first file of that name the scan found.</summary>
    public string TexturePath;
}

/// <summary>A pack folder and what the scan found in it.</summary>
internal sealed class LiveryPack
{
    public string Folder;
    public string Dir;
    /// <summary>The id the game gives a local livery folder of this name (see <see cref="LiveryPackScan.PackId"/>).</summary>
    public long Id;
    public readonly List<LiveryPackEntry> Entries = new();
    /// <summary>Why the whole pack is refused (null = not refused).</summary>
    public string Problem;
    /// <summary>Things the game skips or works around (a missing texture or icon, an entry without a car).</summary>
    public readonly List<string> Warnings = new();

    public int Loadable
    {
        get
        {
            int n = 0;
            foreach (var e in Entries) if (e.Loadable) n++;
            return n;
        }
    }

    /// <summary>Safe to hand to the game, and at least one livery would load.</summary>
    public bool Usable => Problem == null && Loadable > 0;
}

/// <summary>
/// Reads livery packs from one folder (pure file-system code; the tests compile this file on its own). Shared by
/// Livery Packs (which hands the packs to the game's own livery list) and the modded-car skins (Api.Cars), which read
/// the textures themselves. A pack is a
/// sub-folder in the format the game's own livery mods use (mod.io uploads and the game's LocalLiveries folder):
/// <c>manifest.json</c> = <c>{"liveries":[{"car","name","texture","icon"}]}</c> next to 2048x2048 PNG textures and
/// optional icons.
///
/// <para><b>Folder-confined.</b> The game reads a pack itself once we hand it the folder
/// (ModLiveryService.RegisterFromDirectory), so a pack is refused as a whole unless everything the game would read
/// stays inside it:</para>
/// <list type="bullet">
/// <item>no links (symlinks, junctions) anywhere in the pack, and they are never followed;</item>
/// <item>exactly one manifest.json, at the top of the pack (the game takes the first one it finds anywhere);</item>
/// <item>textures are plain image file names (the game searches the pack's folders for that name): no folders, no
///   "..", no wildcards;</item>
/// <item>icons are relative image paths inside the pack;</item>
/// <item>every image must be a PNG or JPEG whose header says at most <see cref="MaxTextureSide"/> (textures) or
///   <see cref="MaxIconSide"/> (icons) pixels a side, read before the game decodes anything;</item>
/// <item>images and the manifest have size caps, and a pack has at most <see cref="MaxEntries"/> liveries and
///   <see cref="MaxFiles"/> files.</item>
/// </list>
/// </summary>
internal static class LiveryPackScan
{
    public const string ManifestName = "manifest.json";
    public const int MaxPacks = 200, MaxEntries = 256, MaxFiles = 2000;
    public const long MaxImageBytes = 32L * 1024 * 1024, MaxManifestBytes = 1024 * 1024;
    /// <summary>Largest width / height the game is asked to decode (it loads every image into memory at full size).</summary>
    public const int MaxTextureSide = 4096, MaxIconSide = 1024;

    private static readonly string[] ImageExtensions = { ".png", ".jpg", ".jpeg" };

    /// <summary>A folder or file counts as a link when it is a reparse point (symlink, junction, mount point).</summary>
    public static bool IsLink(FileSystemInfo info)
    {
        try { return (info.Attributes & FileAttributes.ReparsePoint) != 0; }
        catch (Exception ex) { DebugLog.Error("core", ex, "LiveryPackScan.IsLink"); return true; } // unreadable: treat as unsafe
    }

    /// <summary>
    /// The id the game gives a local livery folder (ModLiveryService.RegisterLocalLiveries): FNV-1a 32 over the folder
    /// name's UTF-16 units, then ~|hash|, so always negative. The same pack folder name gives the same id on every PC,
    /// which is how another player with the same pack sees your livery.
    /// </summary>
    public static long PackId(string folderName)
    {
        uint h = 0x811C9DC5;
        foreach (char c in folderName ?? "")
        {
            h ^= c;
            h *= 0x01000193;
        }
        long signed = unchecked((int)h);
        return ~Math.Abs(signed);
    }

    /// <summary>A texture as the manifest names it: a plain image file name (the game looks it up by name).</summary>
    public static bool SafeTextureName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 200) return false;
        if (name.IndexOfAny(new[] { '/', '\\', ':', '*', '?', '"', '<', '>', '|' }) >= 0) return false;
        if (name == "." || name == ".." || name.Contains("..")) return false;
        foreach (char c in name) if (c < 32) return false;
        return IsImage(name);
    }

    /// <summary>An icon path: relative, inside the pack (no "..", no drive or root), an image.</summary>
    public static bool SafeIconPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 260) return false;
        if (path.IndexOfAny(new[] { ':', '*', '?', '"', '<', '>', '|' }) >= 0) return false;
        foreach (char c in path) if (c < 32) return false;
        string p = path.Replace('\\', '/');
        if (p.StartsWith("/", StringComparison.Ordinal)) return false;
        foreach (string part in p.Split('/'))
            if (part.Length == 0 || part == "." || part == "..") return false;
        return IsImage(p);
    }

    private static bool IsImage(string name)
    {
        string ext = Path.GetExtension(name);
        foreach (string e in ImageExtensions)
            if (string.Equals(ext, e, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>The manifest's entries, or null and <paramref name="error"/> when it is not a livery manifest.</summary>
    public static List<LiveryPackEntry> ParseManifest(string json, out string error)
    {
        error = null;
        try
        {
            using var doc = JsonDocument.Parse((json ?? "").TrimStart('﻿'));
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) { error = "manifest.json is not a JSON object"; return null; }
            if (!root.TryGetProperty("liveries", out var list) || list.ValueKind != JsonValueKind.Array)
            {
                error = "manifest.json has no \"liveries\" list";
                return null;
            }
            var entries = new List<LiveryPackEntry>();
            foreach (var e in list.EnumerateArray())
            {
                if (e.ValueKind != JsonValueKind.Object) { entries.Add(new LiveryPackEntry()); continue; }
                entries.Add(new LiveryPackEntry { Car = Str(e, "car"), Name = Str(e, "name"), Texture = Str(e, "texture"), Icon = Str(e, "icon") });
            }
            return entries;
        }
        catch (JsonException e)
        {
            DebugLog.Error("core", e, "LiveryPackScan.ParseManifest");
            error = "manifest.json is not valid JSON (" + e.Message + ")";
            return null;
        }
    }

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>Every pack folder directly inside <paramref name="root"/> (none when the root is missing or a link).</summary>
    public static List<LiveryPack> ScanRoot(string root, Func<FileSystemInfo, bool> isLink = null)
    {
        isLink ??= IsLink;
        var packs = new List<LiveryPack>();
        if (string.IsNullOrEmpty(root)) return packs;
        var info = new DirectoryInfo(root);
        if (!info.Exists || isLink(info)) return packs;
        DirectoryInfo[] dirs;
        try { dirs = info.GetDirectories(); }
        catch (Exception ex) { DebugLog.Error("core", ex, "LiveryPackScan.ScanRoot"); return packs; }
        Array.Sort(dirs, (a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        foreach (var d in dirs)
        {
            if (packs.Count >= MaxPacks) break;
            packs.Add(ScanPack(d.FullName, isLink));
        }
        return packs;
    }

    /// <summary>Checks one pack folder (see the class summary for the rules).</summary>
    public static LiveryPack ScanPack(string dir, Func<FileSystemInfo, bool> isLink = null)
    {
        isLink ??= IsLink;
        var top = new DirectoryInfo(dir);
        var pack = new LiveryPack { Folder = top.Name, Dir = top.FullName, Id = PackId(top.Name) };
        if (isLink(top)) { pack.Problem = "the pack folder is a link (links are not followed)"; return pack; }

        // Walk the pack without following links.
        var files = new List<FileInfo>();
        var stack = new Stack<DirectoryInfo>();
        stack.Push(top);
        while (stack.Count > 0)
        {
            var d = stack.Pop();
            FileSystemInfo[] items;
            try { items = d.GetFileSystemInfos(); }
            catch (Exception e) { DebugLog.Error("core", e, "LiveryPackScan.ScanPack"); pack.Problem = "a folder could not be read (" + e.Message + ")"; return pack; }
            foreach (var item in items)
            {
                if (isLink(item)) { pack.Problem = "it contains a link (" + Relative(top, item) + "); links are not followed"; return pack; }
                if (item is DirectoryInfo sub) stack.Push(sub);
                else if (item is FileInfo f)
                {
                    files.Add(f);
                    if (files.Count > MaxFiles) { pack.Problem = $"more than {MaxFiles} files"; return pack; }
                }
            }
        }

        FileInfo manifest = null;
        int manifests = 0;
        foreach (var f in files)
        {
            if (!string.Equals(f.Name, ManifestName, StringComparison.OrdinalIgnoreCase)) continue;
            manifests++;
            if (string.Equals(f.DirectoryName?.TrimEnd(Path.DirectorySeparatorChar), top.FullName.TrimEnd(Path.DirectorySeparatorChar),
                    StringComparison.OrdinalIgnoreCase)) manifest = f;
        }
        if (manifests == 0) { pack.Problem = "no manifest.json"; return pack; }
        if (manifest == null) { pack.Problem = "manifest.json must be at the top of the pack folder"; return pack; }
        if (manifests > 1) { pack.Problem = "more than one manifest.json (keep only the one at the top)"; return pack; }
        if (manifest.Length > MaxManifestBytes) { pack.Problem = "manifest.json is too large"; return pack; }

        string json;
        try { json = File.ReadAllText(manifest.FullName); }
        catch (Exception e) { DebugLog.Error("core", e, "LiveryPackScan.ScanPack"); pack.Problem = "manifest.json could not be read (" + e.Message + ")"; return pack; }
        var entries = ParseManifest(json, out string error);
        if (entries == null) { pack.Problem = error; return pack; }
        if (entries.Count == 0) { pack.Problem = "manifest.json lists no liveries"; return pack; }
        if (entries.Count > MaxEntries) { pack.Problem = $"more than {MaxEntries} liveries"; return pack; }

        var byName = new Dictionary<string, FileInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in files) if (!byName.ContainsKey(f.Name)) byName[f.Name] = f;

        foreach (var e in entries)
        {
            pack.Entries.Add(e);
            string label = string.IsNullOrWhiteSpace(e.Name) ? e.Texture ?? "?" : e.Name;
            if (string.IsNullOrWhiteSpace(e.Car) || string.IsNullOrWhiteSpace(e.Texture))
            {
                pack.Warnings.Add($"'{label}' has no car or texture: skipped");
                if (e.Texture == null) continue;
            }
            if (!SafeTextureName(e.Texture))
            {
                pack.Problem = $"texture '{e.Texture}' is not a plain image file name inside the pack";
                return pack;
            }
            if (!string.IsNullOrEmpty(e.Icon))
            {
                if (!SafeIconPath(e.Icon)) { pack.Problem = $"icon '{e.Icon}' is not an image path inside the pack"; return pack; }
                var icon = new FileInfo(Path.Combine(top.FullName, e.Icon.Replace('/', Path.DirectorySeparatorChar)));
                if (!InsideAndListed(top, icon, files)) pack.Warnings.Add($"'{label}': icon '{e.Icon}' not found (the texture is used instead)");
                else
                {
                    string bad = CheckImage(icon, MaxIconSide, $"icon '{e.Icon}'");
                    if (bad != null) { pack.Problem = bad; return pack; }
                }
            }
            if (string.IsNullOrWhiteSpace(e.Car)) continue;
            if (!byName.TryGetValue(e.Texture, out var tex)) { pack.Warnings.Add($"'{label}': texture '{e.Texture}' not found: skipped"); continue; }
            e.Loadable = true;
            var atTop = new FileInfo(Path.Combine(top.FullName, e.Texture));
            e.TexturePath = InsideAndListed(top, atTop, files) ? atTop.FullName : tex.FullName;
        }
        // Every file with a loadable texture's name is a candidate (the game takes the first match): check them all.
        foreach (var e in pack.Entries)
        {
            if (!e.Loadable) continue;
            foreach (var f in files)
            {
                if (!string.Equals(f.Name, e.Texture, StringComparison.OrdinalIgnoreCase)) continue;
                string bad = CheckImage(f, MaxTextureSide, $"texture '{e.Texture}'");
                if (bad != null) { pack.Problem = bad; return pack; }
            }
        }
        return pack;
    }

    /// <summary>Why an image would be refused (file size, not a PNG / JPEG, too many pixels a side), or null.</summary>
    private static string CheckImage(FileInfo f, int maxSide, string what)
    {
        if (f.Length > MaxImageBytes) return $"{what} is over {MaxImageBytes / (1024 * 1024)} MB";
        int w, h;
        try
        {
            using var stream = new FileStream(f.FullName, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (!ImageHeader.TryReadSize(stream, out w, out h)) return $"{what} is not a PNG or JPEG image";
        }
        catch (Exception e) { DebugLog.Error("core", e, "LiveryPackScan.CheckImage"); return $"{what} could not be read ({e.Message})"; }
        if (w <= 0 || h <= 0) return $"{what} has no size";
        if (w > maxSide || h > maxSide) return $"{what} is {w}x{h}, over {maxSide}x{maxSide}";
        return null;
    }

    private static bool InsideAndListed(DirectoryInfo top, FileInfo file, List<FileInfo> files)
    {
        string full;
        try { full = Path.GetFullPath(file.FullName); }
        catch (Exception ex) { DebugLog.Error("core", ex, "LiveryPackScan.InsideAndListed"); return false; }
        string root = top.FullName.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return false;
        foreach (var f in files)
            if (string.Equals(f.FullName, full, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static string Relative(DirectoryInfo top, FileSystemInfo item)
    {
        string root = top.FullName.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return item.FullName.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? item.FullName.Substring(root.Length) : item.Name;
    }
}

/// <summary>
/// Reads an image's width and height from its header only (pure): PNG (signature, then the IHDR chunk) and JPEG (the
/// first SOFn segment). Nothing is decoded, so a huge image is caught before the game tries to load it.
/// </summary>
internal static class ImageHeader
{
    private static readonly byte[] PngSignature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
    /// <summary>How far into a JPEG the first frame header may be (EXIF and colour profiles come first).</summary>
    private const long MaxJpegScan = 4L * 1024 * 1024;

    public static bool TryReadSize(Stream s, out int width, out int height)
    {
        width = height = 0;
        var head = new byte[8];
        if (!ReadExactly(s, head, 2)) return false;
        if (head[0] == 0xFF && head[1] == 0xD8) return Jpeg(s, out width, out height);
        if (head[0] != PngSignature[0] || head[1] != PngSignature[1]) return false;
        if (!ReadExactly(s, head, 6, 2)) return false;
        for (int i = 0; i < 8; i++) if (head[i] != PngSignature[i]) return false;
        var ihdr = new byte[16];
        if (!ReadExactly(s, ihdr, 16)) return false;
        if (ihdr[4] != (byte)'I' || ihdr[5] != (byte)'H' || ihdr[6] != (byte)'D' || ihdr[7] != (byte)'R') return false;
        long w = BigEndian(ihdr, 8, 4), h = BigEndian(ihdr, 12, 4);
        if (w > int.MaxValue || h > int.MaxValue) { width = height = int.MaxValue; return true; }
        width = (int)w;
        height = (int)h;
        return true;
    }

    private static bool Jpeg(Stream s, out int width, out int height)
    {
        width = height = 0;
        long read = 2;
        var buf = new byte[5];
        while (read < MaxJpegScan)
        {
            int b = s.ReadByte();
            read++;
            if (b < 0) return false;
            if (b != 0xFF) return false; // segments must start with a marker
            int marker;
            do { marker = s.ReadByte(); read++; } while (marker == 0xFF);
            if (marker < 0) return false;
            if (marker == 0x01 || (marker >= 0xD0 && marker <= 0xD8)) continue; // no length
            if (marker == 0xD9 || marker == 0xDA) return false;                // end / scan before any frame header
            if (!ReadExactly(s, buf, 2)) return false;
            read += 2;
            int length = (buf[0] << 8) | buf[1];
            if (length < 2) return false;
            bool sof = marker >= 0xC0 && marker <= 0xCF && marker != 0xC4 && marker != 0xC8 && marker != 0xCC;
            if (sof)
            {
                if (length < 7 || !ReadExactly(s, buf, 5)) return false;
                height = (buf[1] << 8) | buf[2];
                width = (buf[3] << 8) | buf[4];
                return true;
            }
            long skip = length - 2;
            if (s.CanSeek) s.Seek(skip, SeekOrigin.Current);
            else for (long i = 0; i < skip; i++) if (s.ReadByte() < 0) return false;
            read += skip;
        }
        return false;
    }

    private static long BigEndian(byte[] b, int at, int n)
    {
        long v = 0;
        for (int i = 0; i < n; i++) v = (v << 8) | b[at + i];
        return v;
    }

    private static bool ReadExactly(Stream s, byte[] buffer, int count, int offset = 0)
    {
        int got = 0;
        while (got < count)
        {
            int n = s.Read(buffer, offset + got, count - got);
            if (n <= 0) return false;
            got += n;
        }
        return true;
    }
}
