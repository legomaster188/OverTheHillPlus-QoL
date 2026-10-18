using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace OverTheHillPlus.Bookmarks;

/// <summary>
/// Named spots per map, kept in one JSON file (BepInEx/config/oth.bookmarks.json in the game). Pure: no game or
/// Unity types, so the tests compile this file on its own. The file is written through a temporary file, an
/// unreadable file is kept as "*.bad" and the store starts empty, and entries with a broken position are dropped.
/// </summary>
internal sealed class BookmarkStore
{
    public const int MaxPerMap = 200;
    public const int MaxNameLength = 40;
    public const int FileVersion = 1;

    public sealed class Bookmark
    {
        public string Name { get; set; }
        public float X { get; set; }
        public float Y { get; set; }
        public float Z { get; set; }
        public float Yaw { get; set; }
        /// <summary>When it was saved or last moved, "yyyy-MM-dd HH:mm" (shown on the page).</summary>
        public string Saved { get; set; }
    }

    /// <summary>The file's shape: a version and the bookmarks per map key.</summary>
    public sealed class FileData
    {
        public int Version { get; set; } = FileVersion;
        public Dictionary<string, List<Bookmark>> Maps { get; set; } = new();
    }

    private readonly string _path;
    private Dictionary<string, List<Bookmark>> _maps;

    /// <summary>Warnings (unreadable file, failed save). Null = silent.</summary>
    public Action<string> Log;

    public BookmarkStore(string path) => _path = path;

    public string FilePath => _path;

    private Dictionary<string, List<Bookmark>> Maps => _maps ??= Load();

    // ---------------------------------------------------------------- reading

    /// <summary>Bookmarks of one map, in the user's order (a live list: change it only through this class).</summary>
    public IReadOnlyList<Bookmark> For(string map) =>
        Maps.TryGetValue(map ?? "", out var list) ? list : (IReadOnlyList<Bookmark>)Array.Empty<Bookmark>();

    public int Total => Maps.Values.Sum(l => l.Count);

    /// <summary>Maps that have at least one bookmark.</summary>
    public int MapCount => Maps.Count(kv => kv.Value.Count > 0);

    public int CountOtherMaps(string map) => Maps.Where(kv => kv.Key != (map ?? "")).Sum(kv => kv.Value.Count);

    // ---------------------------------------------------------------- changing

    /// <summary>
    /// Saves a spot. An empty name becomes "Spot N"; a name already used on that map gets " (2)", " (3)"...
    /// Returns a message for the page; <paramref name="added"/> is the new entry (null when refused).
    /// </summary>
    public string Add(string map, string name, float x, float y, float z, float yaw, DateTime now, out Bookmark added)
    {
        added = null;
        map ??= "";
        if (!Finite(x, y, z, yaw)) return "Failed: the car's position is invalid.";
        if (!Maps.TryGetValue(map, out var list)) Maps[map] = list = new List<Bookmark>();
        if (list.Count >= MaxPerMap) return $"Failed: {MaxPerMap} bookmarks is the limit per map.";
        name = CleanName(name);
        if (name.Length == 0) name = $"Spot {list.Count + 1}";
        name = UniqueName(list.Select(b => b.Name), name);
        added = new Bookmark { Name = name, X = x, Y = y, Z = z, Yaw = NormalizeYaw(yaw), Saved = Stamp(now) };
        list.Add(added);
        Persist();
        return $"Saved \"{name}\".";
    }

    /// <summary>Moves a bookmark to a new spot (keeps its name and place in the list).</summary>
    public bool Update(string map, Bookmark b, float x, float y, float z, float yaw, DateTime now)
    {
        if (!Has(map, b, out _) || !Finite(x, y, z, yaw)) return false;
        b.X = x; b.Y = y; b.Z = z; b.Yaw = NormalizeYaw(yaw);
        b.Saved = Stamp(now);
        Persist();
        return true;
    }

    /// <summary>Renames a bookmark; an empty name is refused, a taken one gets a number.</summary>
    public bool Rename(string map, Bookmark b, string name)
    {
        name = CleanName(name);
        if (name.Length == 0 || !Has(map, b, out var list)) return false;
        if (string.Equals(name, b.Name, StringComparison.Ordinal)) return false;
        name = UniqueName(list.Where(o => !ReferenceEquals(o, b)).Select(o => o.Name), name);
        b.Name = name;
        Persist();
        return true;
    }

    public bool Remove(string map, Bookmark b)
    {
        if (!Has(map, b, out var list)) return false;
        list.Remove(b);
        if (list.Count == 0) Maps.Remove(map ?? "");
        Persist();
        return true;
    }

    /// <summary>Moves a bookmark up (-1) or down (+1) in its map's list.</summary>
    public bool Move(string map, Bookmark b, int delta)
    {
        if (!Has(map, b, out var list)) return false;
        int i = list.IndexOf(b), j = i + delta;
        if (j < 0 || j >= list.Count || j == i) return false;
        (list[i], list[j]) = (list[j], list[i]);
        Persist();
        return true;
    }

    private bool Has(string map, Bookmark b, out List<Bookmark> list)
    {
        list = null;
        return b != null && Maps.TryGetValue(map ?? "", out list) && list.Contains(b);
    }

    // ---------------------------------------------------------------- names

    /// <summary>No control characters or rich-text brackets, trimmed, at most <see cref="MaxNameLength"/> characters.</summary>
    public static string CleanName(string name)
    {
        if (string.IsNullOrEmpty(name)) return "";
        var chars = name.Where(c => !char.IsControl(c) && c != '<' && c != '>').ToArray();
        string s = new string(chars).Trim();
        return s.Length > MaxNameLength ? s.Substring(0, MaxNameLength).TrimEnd() : s;
    }

    /// <summary><paramref name="wanted"/>, or "wanted (2)", "wanted (3)"... whichever is not taken (case-insensitive).</summary>
    public static string UniqueName(IEnumerable<string> taken, string wanted)
    {
        var set = new HashSet<string>(taken.Where(t => t != null), StringComparer.OrdinalIgnoreCase);
        if (!set.Contains(wanted)) return wanted;
        for (int n = 2; ; n++)
        {
            string candidate = $"{wanted} ({n})";
            if (!set.Contains(candidate)) return candidate;
        }
    }

    // ---------------------------------------------------------------- file

    private static string Stamp(DateTime t) => t.ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture);

    private static float NormalizeYaw(float yaw)
    {
        yaw %= 360f;
        return yaw < 0f ? yaw + 360f : yaw;
    }

    private static bool Finite(params float[] v) => v.All(float.IsFinite);

    private static bool Valid(Bookmark b) => b != null && Finite(b.X, b.Y, b.Z, b.Yaw);

    private Dictionary<string, List<Bookmark>> Load()
    {
        var result = new Dictionary<string, List<Bookmark>>(StringComparer.Ordinal);
        try
        {
            if (string.IsNullOrEmpty(_path) || !File.Exists(_path)) return result;
            var data = JsonSerializer.Deserialize<FileData>(File.ReadAllText(_path));
            if (data?.Maps == null) return result;
            foreach (var kv in data.Maps)
            {
                if (kv.Key == null || kv.Value == null) continue;
                var list = new List<Bookmark>();
                foreach (var b in kv.Value)
                {
                    if (!Valid(b) || list.Count >= MaxPerMap) continue;
                    b.Name = CleanName(b.Name);
                    if (b.Name.Length == 0) b.Name = $"Spot {list.Count + 1}";
                    b.Name = UniqueName(list.Select(o => o.Name), b.Name);
                    list.Add(b);
                }
                if (list.Count > 0) result[kv.Key] = list;
            }
            DebugLog.Verbose("bookmarks", () => $"bookmarks read: {result.Values.Sum(l => l.Count)} on {result.Count} map(s) from {Path.GetFileName(_path)}");
        }
        catch (Exception e)
        {
            DebugLog.Error("bookmarks", e, "BookmarkStore.Load");
            Log?.Invoke("bookmarks file unreadable, kept as .bad and starting fresh: " + e.Message);
            try { File.Copy(_path, _path + ".bad", overwrite: true); } catch (Exception ex) { DebugLog.Error("bookmarks", ex, "BookmarkStore.Load"); }
            result.Clear();
        }
        return result;
    }

    private void Persist()
    {
        if (string.IsNullOrEmpty(_path)) return;
        try
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var data = new FileData { Maps = Maps };
            string tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }));
            File.Copy(tmp, _path, overwrite: true);
            File.Delete(tmp);
            DebugLog.Verbose("bookmarks", () => $"bookmarks saved: {Maps.Values.Sum(l => l.Count)} on {Maps.Count} map(s)");
        }
        catch (Exception e) { DebugLog.Error("bookmarks", e, "BookmarkStore.Persist"); Log?.Invoke("could not save the bookmarks: " + e.Message); }
    }
}
