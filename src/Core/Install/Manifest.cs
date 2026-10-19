using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;

namespace OverTheHillPlus.Core.Install;

/// <summary>A mods.json that can't be used: wrong schema, a missing field, a download from anywhere but the public
/// repo, or a malformed hash. Nothing from such a manifest is ever downloaded.</summary>
internal sealed class ManifestException : Exception
{
    public ManifestException(string message) : base(message) { }
}

/// <summary>One downloadable mod of the release.</summary>
internal sealed class ManifestEntry
{
    public string Id;
    public string Name;
    public string Version;
    /// <summary>Plain file name of the DLL in the plugin folder, e.g. "OTH.Chat.dll".</summary>
    public string File;
    public string Url;
    /// <summary>Lower-case hex SHA-256 of the file.</summary>
    public string Sha256;
    public long Size;
    /// <summary>Ids of the mods it needs (usually "core"); never null.</summary>
    public string[] Requires = Array.Empty<string>();
    public string Description = "";
    /// <summary>"Stable", "Beta" or "Rough" (optional; empty: the Mod Manager's own table decides, see ModMaturity).</summary>
    public string Maturity = "";
}

/// <summary>
/// The release's mods.json (schema 1): which public mods exist, their versions, where to download them and their
/// SHA-256 and size. Pure logic (no BepInEx or Unity types) so it can be unit tested.
/// </summary>
internal sealed class Manifest
{
    /// <summary>Where the manifest comes from: fixed in core, never user-entered.</summary>
    public const string Url = "https://github.com/legomaster188/OverTheHillPlus-QoL/releases/latest/download/mods.json";
    /// <summary>Every download URL of the manifest must start with this.</summary>
    public const string RepoPrefix = "https://github.com/legomaster188/OverTheHillPlus-QoL/";

    public int Schema;
    public string Game = "";
    public List<ManifestEntry> Mods = new();

    public ManifestEntry Find(string id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        foreach (var m in Mods)
            if (string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase)) return m;
        return null;
    }

    /// <summary>Parses and validates a manifest; throws <see cref="ManifestException"/> on anything unexpected.</summary>
    public static Manifest Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) throw new ManifestException("The mod list is empty.");
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json.TrimStart('\uFEFF')); }
        catch (JsonException e) { throw new ManifestException("The mod list isn't valid JSON: " + e.Message); }
        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new ManifestException("The mod list isn't a JSON object.");
            int schema = root.TryGetProperty("schema", out var s) && s.ValueKind == JsonValueKind.Number && s.TryGetInt32(out var n) ? n : -1;
            if (schema != 1) throw new ManifestException(schema < 0 ? "The mod list has no schema." : $"The mod list has schema {schema}; this Mod Manager reads schema 1. Update the Mod Manager.");
            var m = new Manifest { Schema = schema, Game = OptionalString(root, "game") };
            if (!root.TryGetProperty("mods", out var mods) || mods.ValueKind != JsonValueKind.Array)
                throw new ManifestException("The mod list has no \"mods\" array.");
            int i = 0;
            foreach (var e in mods.EnumerateArray())
            {
                var entry = ParseEntry(e, i++);
                if (m.Find(entry.Id) != null) throw new ManifestException($"Mod \"{entry.Id}\" is listed twice.");
                m.Mods.Add(entry);
            }
            return m;
        }
    }

    private static ManifestEntry ParseEntry(JsonElement e, int index)
    {
        if (e.ValueKind != JsonValueKind.Object) throw new ManifestException($"Mod #{index + 1} isn't a JSON object.");
        string where = $"mod #{index + 1}";
        var entry = new ManifestEntry { Id = RequiredString(e, "id", where) };
        where = $"mod \"{entry.Id}\"";
        entry.Name = RequiredString(e, "name", where);
        entry.Version = RequiredString(e, "version", where);
        entry.File = RequiredString(e, "file", where);
        entry.Url = RequiredString(e, "url", where);
        entry.Sha256 = RequiredString(e, "sha256", where).ToLowerInvariant();
        entry.Description = OptionalString(e, "description");
        entry.Maturity = OptionalString(e, "maturity");
        if (!e.TryGetProperty("size", out var size) || size.ValueKind != JsonValueKind.Number || !size.TryGetInt64(out entry.Size) || entry.Size <= 0)
            throw new ManifestException($"{where} has no valid \"size\".");
        if (e.TryGetProperty("requires", out var req) && req.ValueKind != JsonValueKind.Null)
        {
            if (req.ValueKind != JsonValueKind.Array) throw new ManifestException($"{where}: \"requires\" isn't a list.");
            var list = new List<string>();
            foreach (var r in req.EnumerateArray())
            {
                if (r.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(r.GetString()))
                    throw new ManifestException($"{where}: \"requires\" has an entry that isn't a mod id.");
                list.Add(r.GetString());
            }
            entry.Requires = list.ToArray();
        }

        if (!IsModFileName(entry.File)) throw new ManifestException($"{where}: \"{entry.File}\" isn't a mod file name (OTH.<name>.dll).");
        if (!IsHex64(entry.Sha256)) throw new ManifestException($"{where}: \"sha256\" isn't 64 hex digits.");
        if (!IsRepoUrl(entry.Url)) throw new ManifestException($"{where}: downloads only come from {RepoPrefix}, not {entry.Url}.");
        return entry;
    }

    private static string RequiredString(JsonElement e, string name, string where)
    {
        if (!e.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(v.GetString()))
            throw new ManifestException($"{where} has no \"{name}\".");
        return v.GetString().Trim();
    }

    private static string OptionalString(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    /// <summary>HTTPS on github.com under the public repo, with nothing that could walk out of it ("..", "%2e").</summary>
    internal static bool IsRepoUrl(string url)
    {
        if (string.IsNullOrEmpty(url) || !url.StartsWith(RepoPrefix, StringComparison.Ordinal)) return false;
        if (url.Contains("..") || url.Contains("%") || url.Contains("\\") || url.Contains("@")) return false;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        var repo = new Uri(RepoPrefix);
        return uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort &&
               string.Equals(uri.Host, repo.Host, StringComparison.OrdinalIgnoreCase) &&
               uri.AbsolutePath.StartsWith(repo.AbsolutePath, StringComparison.Ordinal);
    }

    /// <summary>A bare "Name.dll": no folders, no "..", nothing Windows would refuse.</summary>
    internal static bool IsSafeFileName(string file)
    {
        if (string.IsNullOrEmpty(file) || file.Length > 100) return false;
        if (!file.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || file.StartsWith(".")) return false;
        if (file.IndexOfAny(new[] { '/', '\\', ':' }) >= 0 || file.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return false;
        return file == Path.GetFileName(file) && !file.Contains("..");
    }

    /// <summary>A mod of this manager: a plain <see cref="IsSafeFileName"/> of the form OTH.&lt;name&gt;.dll (any case), the
    /// name made of letters, digits, '.', '-' and '_'. Nothing else is ever downloaded or removed.</summary>
    internal static bool IsModFileName(string file)
    {
        if (!IsSafeFileName(file)) return false;
        const string prefix = "OTH.", suffix = ".dll";
        if (!file.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || file.Length <= prefix.Length + suffix.Length) return false;
        string name = file.Substring(prefix.Length, file.Length - prefix.Length - suffix.Length);
        if (name[0] == '.' || name[name.Length - 1] == '.') return false;
        foreach (char c in name)
            if (!(c >= 'a' && c <= 'z' || c >= 'A' && c <= 'Z' || c >= '0' && c <= '9' || c == '.' || c == '-' || c == '_')) return false;
        return true;
    }

    internal static bool IsHex64(string s)
    {
        if (s == null || s.Length != 64) return false;
        foreach (char c in s)
            if (!(c >= '0' && c <= '9' || c >= 'a' && c <= 'f' || c >= 'A' && c <= 'F')) return false;
        return true;
    }
}

/// <summary>Version comparison and size wording for the AVAILABLE tab.</summary>
internal static class ModVersion
{
    /// <summary>True when <paramref name="listed"/> is a higher version than <paramref name="installed"/> ("1.2.0",
    /// "v1.2"; a "-beta" / "+build" suffix is ignored). Anything unreadable counts as not newer.</summary>
    public static bool IsNewer(string listed, string installed)
    {
        var a = TryParse(listed);
        var b = TryParse(installed);
        return a != null && b != null && a > b;
    }

    private static Version TryParse(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Trim();
        if (s.StartsWith("v", StringComparison.OrdinalIgnoreCase)) s = s.Substring(1);
        int cut = s.IndexOfAny(new[] { '-', '+', ' ' });
        if (cut >= 0) s = s.Substring(0, cut);
        if (!s.Contains(".")) s += ".0";
        if (!Version.TryParse(s, out var v)) return null;
        // 1.2 and 1.2.0 are the same version.
        return new Version(v.Major, v.Minor, Math.Max(0, v.Build), Math.Max(0, v.Revision));
    }

    /// <summary>"120 KB", "2.5 MB".</summary>
    public static string FormatSize(long bytes)
    {
        if (bytes < 1024L * 1024L) return Math.Max(1L, (bytes + 512L) / 1024L).ToString(CultureInfo.InvariantCulture) + " KB";
        return (bytes / (1024.0 * 1024.0)).ToString("0.#", CultureInfo.InvariantCulture) + " MB";
    }
}
