using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace OverTheHillPlus.Shared;

/// <summary>A skin a modded car can wear: one entry of a livery pack whose car is that car's livery name.</summary>
internal sealed class CarSkin
{
    /// <summary>"&lt;pack folder&gt;/&lt;entry name&gt;" (see <see cref="CarSkinKey"/>): saved in configs, sent to other players.</summary>
    public string Key;
    public string Pack;
    public string Name;
    /// <summary>The car as the manifest names it.</summary>
    public string Car;
    public string PackDir;
    public string TexturePath;
    public long Bytes;
    /// <summary>Width and height (skins are square).</summary>
    public int Side;
}

/// <summary>
/// A skin's key, "&lt;pack folder&gt;/&lt;entry name&gt;": at most <see cref="MaxBytes"/> bytes of UTF-8, exactly one '/',
/// both halves non-empty, no "..", no other path separator or drive colon, no control characters. Keys come from
/// other players too, so a key is only ever compared with the keys of the local scan, never used as a path.
/// Pure (unit-tested).
/// </summary>
internal static class CarSkinKey
{
    public const int MaxBytes = 96;

    /// <summary>The key of an entry, or null when the folder or name can't make a valid key.</summary>
    public static string Make(string pack, string name)
    {
        if (string.IsNullOrEmpty(pack) || string.IsNullOrEmpty(name)) return null;
        string key = pack + "/" + name;
        return IsValid(key) ? key : null;
    }

    public static bool IsValid(string key)
    {
        if (string.IsNullOrEmpty(key)) return false;
        int bytes;
        try { bytes = new UTF8Encoding(false, true).GetByteCount(key); }
        catch (ArgumentException ex) { DebugLog.Error("core", ex, "CarSkinKey.IsValid"); return false; } // lone surrogates
        if (bytes > MaxBytes) return false;
        int slash = key.IndexOf('/');
        if (slash <= 0 || slash == key.Length - 1 || key.IndexOf('/', slash + 1) >= 0) return false;
        if (key.Contains("..")) return false;
        foreach (char c in key)
            if (c == '\\' || c == ':' || char.IsControl(c)) return false;
        string pack = key.Substring(0, slash), name = key.Substring(slash + 1);
        return pack.Trim().Length > 0 && name.Trim().Length > 0;
    }
}

/// <summary>What a skin scan found: the skins by car (case-insensitive) and one line per pack or skin left out.</summary>
internal sealed class CarSkinScanResult
{
    public readonly Dictionary<string, List<CarSkin>> ByCar = new(StringComparer.OrdinalIgnoreCase);
    public readonly List<string> Skipped = new();

    /// <summary>The skins for a car's livery name (an empty list when none).</summary>
    public IReadOnlyList<CarSkin> For(string car) =>
        car != null && ByCar.TryGetValue(car, out var list) ? list : (IReadOnlyList<CarSkin>)Array.Empty<CarSkin>();
}

/// <summary>
/// Finds skins for modded cars in livery pack folders (the game's own format, read by <see cref="LiveryPackScan"/>),
/// for cars the game doesn't have, whose textures the car's mod loads itself. On top of the pack rules: the texture
/// must be a PNG (signature checked) of at most <see cref="MaxBytes"/>, square, <see cref="AllowedSides"/> pixels a
/// side, and the file must be confined to its pack folder (<see cref="ConfinedFolder.IsConfinedFile"/>) right when it
/// is checked; the caller checks again right before reading it. Pure file-system code (unit-tested).
/// </summary>
internal static class CarSkinScan
{
    public const long MaxBytes = 16L * 1024 * 1024;
    public static readonly int[] AllowedSides = { 1024, 2048, 4096 };

    private static readonly byte[] PngSignature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

    /// <summary>
    /// Scans every pack folder in <paramref name="roots"/>, in order: a pack folder name seen in an earlier root wins
    /// (the same pack in two places is read once). Only entries whose car is one of <paramref name="cars"/> (livery car
    /// names, any case; null: every car) are looked at: other cars' liveries are the game's (or Livery Packs')
    /// business, so they are neither checked nor reported. Skin names are unique per car (a pack may use one livery
    /// name for several cars). Refused packs that hold an entry for a wanted car, and bad skins, are each one line in
    /// Skipped.
    /// </summary>
    public static CarSkinScanResult Scan(IEnumerable<string> roots, IEnumerable<string> cars = null, Func<FileSystemInfo, bool> isLink = null)
    {
        var result = new CarSkinScanResult();
        var seenPacks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        HashSet<string> wanted = cars == null ? null : new HashSet<string>(cars, StringComparer.OrdinalIgnoreCase);
        if (roots == null || wanted is { Count: 0 }) return result;
        foreach (string root in roots)
        {
            List<LiveryPack> packs;
            try { packs = LiveryPackScan.ScanRoot(root, isLink); }
            catch (Exception e)
            {
                DebugLog.Error("core", e, "CarSkinScan.Scan");
                result.Skipped.Add($"{root}: could not be read ({e.Message})");
                continue;
            }
            foreach (var pack in packs)
            {
                if (!seenPacks.Add(pack.Folder)) continue;
                if (pack.Problem != null)
                {
                    // Reported only when it would have held a skin for a wanted car.
                    if (wanted == null || pack.Entries.Exists(x => x.Car != null && wanted.Contains(x.Car.Trim())))
                        result.Skipped.Add($"{pack.Folder}: {pack.Problem}");
                    continue;
                }
                foreach (var e in pack.Entries)
                {
                    if (!e.Loadable || string.IsNullOrWhiteSpace(e.Car)) continue;
                    string car = e.Car.Trim();
                    if (wanted != null && !wanted.Contains(car)) continue; // not a modded car's: never checked here
                    string name = string.IsNullOrWhiteSpace(e.Name) ? Path.GetFileNameWithoutExtension(e.Texture) : e.Name.Trim();
                    string key = CarSkinKey.Make(pack.Folder, name);
                    if (key == null) { result.Skipped.Add($"{pack.Folder}: '{name}' can't be a skin name (too long, or it has '/', '\\', ':' or '..')"); continue; }
                    if (!seenKeys.Add(car + "|" + key)) { result.Skipped.Add($"{pack.Folder}: two {car} skins are named '{name}' (the first is used)"); continue; }
                    string problem = CheckFile(pack.Dir, e.TexturePath, out long bytes, out int side);
                    if (problem != null) { result.Skipped.Add($"{pack.Folder}: '{name}': {problem}"); continue; }
                    var skin = new CarSkin
                    {
                        Key = key, Pack = pack.Folder, Name = name, Car = car, PackDir = pack.Dir, TexturePath = e.TexturePath,
                        Bytes = bytes, Side = side,
                    };
                    if (!result.ByCar.TryGetValue(skin.Car, out var list)) result.ByCar[skin.Car] = list = new List<CarSkin>();
                    list.Add(skin);
                }
            }
        }
        return result;
    }

    /// <summary>Why the texture file can't be a skin (outside the pack, a link, too big, not a square PNG of an allowed
    /// size), or null.</summary>
    public static string CheckFile(string packDir, string path, out long bytes, out int side)
    {
        bytes = 0;
        side = 0;
        if (string.IsNullOrEmpty(path)) return "the texture is not a plain file inside the pack";
        if (!ConfinedFolder.IsConfinedFile(packDir, path, out string why)) return $"the texture is not a plain file inside the pack ({why})";
        try
        {
            using var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            bytes = s.Length;
            if (bytes > MaxBytes) return $"the texture is over {MaxBytes / (1024 * 1024)} MB";
            var head = new byte[24];
            int got = 0;
            while (got < head.Length)
            {
                int n = s.Read(head, got, head.Length - got);
                if (n <= 0) break;
                got += n;
            }
            return CheckPngHeader(head, got, out side);
        }
        catch (Exception e) { DebugLog.Error("core", e, "CarSkinScan.CheckFile"); return $"the texture could not be read ({e.Message})"; }
    }

    /// <summary>Why these bytes are not a skin texture (checked again on the bytes actually loaded), or null.</summary>
    public static string CheckPng(byte[] data, out int side)
    {
        side = 0;
        if (data == null) return "no data";
        if (data.LongLength > MaxBytes) return $"the texture is over {MaxBytes / (1024 * 1024)} MB";
        return CheckPngHeader(data, Math.Min(data.Length, 24), out side);
    }

    /// <summary>PNG signature, then the IHDR chunk's width and height: square, one of <see cref="AllowedSides"/>.</summary>
    public static string CheckPngHeader(byte[] head, int length, out int side)
    {
        side = 0;
        if (head == null || length < 24) return "the texture is not a PNG";
        for (int i = 0; i < PngSignature.Length; i++)
            if (head[i] != PngSignature[i]) return "the texture is not a PNG";
        if (head[12] != (byte)'I' || head[13] != (byte)'H' || head[14] != (byte)'D' || head[15] != (byte)'R') return "the texture is not a PNG";
        long w = BigEndian(head, 16), h = BigEndian(head, 20);
        if (w != h) return $"the texture is {w}x{h}: skins are square";
        if (Array.IndexOf(AllowedSides, (int)Math.Min(w, int.MaxValue)) < 0) return $"the texture is {w}x{h}: skins are 1024, 2048 or 4096 pixels a side";
        side = (int)w;
        return null;
    }

    /// <summary>The first 8 bytes of the SHA-256 of a skin's file, as players compare skins with it.</summary>
    public static ulong Hash8(byte[] data)
    {
        var sha = SHA256.HashData(data ?? Array.Empty<byte>());
        ulong v = 0;
        for (int i = 0; i < 8; i++) v = (v << 8) | sha[i];
        return v;
    }

    private static long BigEndian(byte[] b, int at) =>
        ((long)b[at] << 24) | ((long)b[at + 1] << 16) | ((long)b[at + 2] << 8) | b[at + 3];
}
