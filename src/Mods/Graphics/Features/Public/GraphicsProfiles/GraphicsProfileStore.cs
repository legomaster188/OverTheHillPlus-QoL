using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using BepInEx;
using OverTheHillPlus.Graphics;

namespace OverTheHillPlus.Features.Public.GraphicsProfiles;

/// <summary>One saved look: graphics mod settings (section → key → value) and optionally the game's own options.</summary>
internal sealed class GraphicsProfile
{
    public const string FormatId = "OverTheHillPlus.graphics";

    public string Format { get; set; } = FormatId;
    public int Version { get; set; } = 1;
    public string Name { get; set; }
    public string Created { get; set; }
    public string ModVersion { get; set; }
    public Dictionary<string, Dictionary<string, string>> Settings { get; set; } = new();
    /// <summary>The game's own graphics options (quality preset + advanced), or null when not included.</summary>
    public Dictionary<string, int> Game { get; set; }

    public GraphicsCatalog.Values ToValues()
    {
        var v = new GraphicsCatalog.Values();
        if (Settings == null) return v;
        foreach (var s in Settings)
        {
            if (s.Key == null || s.Value == null) continue;
            foreach (var kv in s.Value)
                if (kv.Key != null) v.Set(s.Key, kv.Key, kv.Value ?? "");
        }
        return v;
    }

    public static GraphicsProfile FromCurrent(string name, bool includeGame)
    {
        var p = new GraphicsProfile
        {
            Name = name,
            Created = DateTime.Now.ToString("yyyy-MM-dd HH:mm"),
            ModVersion = GraphicsPlugin.Version,
        };
        foreach (var s in GraphicsCatalog.Snapshot()) p.Settings[s.Key] = new Dictionary<string, string>(s.Value);
        if (includeGame) p.Game = GameGraphicsBridge.Read();
        return p;
    }

    public int SettingCount => Settings?.Sum(s => s.Value?.Count ?? 0) ?? 0;
}

/// <summary>
/// Profiles live as one JSON file each in BepInEx/config/OverTheHillPlus.graphics/ (easy to back up, edit or post).
/// Share codes are the same JSON, deflated and base64'd behind a prefix so they survive chat apps.
/// </summary>
internal static class GraphicsProfileStore
{
    public const string SharePrefix = "OTHP-GFX1:";
    private const int MaxImportChars = 512 * 1024;

    private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true };
    private static readonly JsonSerializerOptions Compact = new() { WriteIndented = false };
    private static readonly JsonSerializerOptions Lenient = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        MaxDepth = 16,
    };

    internal sealed class Entry
    {
        public string Path;
        public GraphicsProfile Profile;
        public DateTime Modified;
    }

    public static string Folder => System.IO.Path.Combine(Paths.ConfigPath, "OverTheHillPlus.graphics");

    private static List<Entry> _cache = new();
    private static float _nextScan = -1f;

    /// <summary>Saved profiles, newest first; re-scans the folder at most every few seconds (files may be added by hand).</summary>
    public static IReadOnlyList<Entry> List(bool force = false)
    {
        float now = UnityEngine.Time.unscaledTime;
        if (!force && now < _nextScan) return _cache;
        _nextScan = now + 4f;
        var list = new List<Entry>();
        try
        {
            if (Directory.Exists(Folder))
            {
                foreach (var file in Directory.GetFiles(Folder, "*.json"))
                {
                    try
                    {
                        var info = new FileInfo(file);
                        if (info.Length > MaxImportChars * 2L) continue;
                        var p = Parse(File.ReadAllText(file));
                        if (p == null) continue;
                        if (string.IsNullOrWhiteSpace(p.Name)) p.Name = System.IO.Path.GetFileNameWithoutExtension(file);
                        list.Add(new Entry { Path = file, Profile = p, Modified = info.LastWriteTime });
                    }
                    catch (Exception e) { DebugLog.Error("graphics", e, "GraphicsProfileStore.List"); GraphicsCatalog.LogOnce("read:" + file, $"Graphics profile {file} unreadable: {e.Message}"); }
                }
            }
        }
        catch (Exception e) { DebugLog.Error("graphics", e, "GraphicsProfileStore.List"); GraphicsCatalog.LogOnce("scan", "Graphics profiles folder unreadable: " + e.Message); }
        list.Sort((a, b) => string.Compare(a.Profile.Name, b.Profile.Name, StringComparison.OrdinalIgnoreCase));
        _cache = list;
        return _cache;
    }

    public static string Save(GraphicsProfile p)
    {
        Directory.CreateDirectory(Folder);
        string path = PathFor(p.Name);
        File.WriteAllText(path, JsonSerializer.Serialize(p, Pretty));
        DebugLog.Info(GraphicsPlugin.ModId, $"profile '{p.Name}' saved to {System.IO.Path.GetFileName(path)}");
        List(force: true);
        return path;
    }

    public static void Delete(Entry e)
    {
        if (e?.Path == null) return;
        // Only ever files inside our own folder.
        string full = System.IO.Path.GetFullPath(e.Path);
        if (!full.StartsWith(System.IO.Path.GetFullPath(Folder), StringComparison.OrdinalIgnoreCase)) return;
        if (File.Exists(full)) File.Delete(full);
        DebugLog.Info(GraphicsPlugin.ModId, $"profile file {System.IO.Path.GetFileName(full)} deleted");
        List(force: true);
    }

    public static bool Exists(string name) => File.Exists(PathFor(name));

    public static string PathFor(string name) => System.IO.Path.Combine(Folder, SafeFileName(name) + ".json");

    public static string SafeFileName(string name)
    {
        var sb = new StringBuilder();
        foreach (char c in (name ?? "").Trim())
        {
            if (sb.Length >= 48) break;
            sb.Append(char.IsLetterOrDigit(c) || c is ' ' or '-' or '_' or '.' or '(' or ')' ? c : '_');
        }
        string s = sb.ToString().Trim().Trim('.');
        return s.Length == 0 ? "profile" : s;
    }

    // ---- sharing ----

    public static string ToShareCode(GraphicsProfile p)
    {
        byte[] json = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(p, Compact));
        using var ms = new MemoryStream();
        using (var z = new DeflateStream(ms, CompressionLevel.Optimal, leaveOpen: true)) z.Write(json, 0, json.Length);
        return SharePrefix + Convert.ToBase64String(ms.ToArray()).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    /// <summary>Accepts a share code or raw profile JSON (e.g. pasted from a file). Returns null and a reason on failure.</summary>
    public static GraphicsProfile FromText(string text, out string error)
    {
        error = null;
        text = (text ?? "").Trim();
        if (text.Length == 0) { error = "the clipboard is empty"; return null; }
        if (text.Length > MaxImportChars) { error = "that text is too large to be a profile"; return null; }
        try
        {
            string json;
            int at = text.IndexOf(SharePrefix, StringComparison.OrdinalIgnoreCase);
            if (at >= 0)
            {
                string b64 = new string(text.Substring(at + SharePrefix.Length).TakeWhile(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '+' or '/' or '=').ToArray());
                b64 = b64.TrimEnd('=').Replace('-', '+').Replace('_', '/');
                b64 = b64.PadRight(b64.Length + (4 - b64.Length % 4) % 4, '=');
                byte[] packed = Convert.FromBase64String(b64);
                using var input = new MemoryStream(packed);
                using var z = new DeflateStream(input, CompressionMode.Decompress);
                using var output = new MemoryStream();
                var buf = new byte[8192];
                int n;
                while ((n = z.Read(buf, 0, buf.Length)) > 0)
                {
                    output.Write(buf, 0, n);
                    if (output.Length > MaxImportChars * 4L) { error = "that code unpacks to something too large"; return null; }
                }
                json = Encoding.UTF8.GetString(output.ToArray());
            }
            else if (text.StartsWith("{")) json = text;
            else { error = $"not a graphics profile (codes start with {SharePrefix})"; return null; }

            var p = Parse(json);
            if (p == null) { error = "not a graphics profile"; return null; }
            return p;
        }
        catch (Exception e) { DebugLog.Error("graphics", e, "GraphicsProfileStore.FromText"); error = "the code is damaged (" + e.Message + ")"; return null; }
    }

    private static GraphicsProfile Parse(string json)
    {
        var p = JsonSerializer.Deserialize<GraphicsProfile>(json, Lenient);
        if (p == null) return null;
        if (!string.IsNullOrEmpty(p.Format) && p.Format != GraphicsProfile.FormatId) return null;
        p.Settings ??= new();
        // Keep only graphics sections: a profile can never carry anything else.
        foreach (var key in p.Settings.Keys.ToList())
            if (!GraphicsCatalog.IsGraphicsSection(key) || p.Settings[key] == null) p.Settings.Remove(key);
        if (p.Game != null)
        {
            var allowed = new HashSet<string>(GameGraphicsBridge.AdvancedKeys) { GameGraphicsBridge.QualityKey };
            foreach (var key in p.Game.Keys.ToList())
                if (!allowed.Contains(key)) p.Game.Remove(key);
            if (p.Game.Count == 0) p.Game = null;
        }
        if (p.Name != null)
        {
            // Shown in rich-text labels: no markup, no control characters, short.
            p.Name = new string(p.Name.Where(c => !char.IsControl(c) && c != '<' && c != '>').ToArray()).Trim();
            if (p.Name.Length > 48) p.Name = p.Name.Substring(0, 48);
        }
        p.Created = p.Created == null ? null : new string(p.Created.Where(c => !char.IsControl(c) && c != '<' && c != '>').Take(32).ToArray());
        return p;
    }
}
