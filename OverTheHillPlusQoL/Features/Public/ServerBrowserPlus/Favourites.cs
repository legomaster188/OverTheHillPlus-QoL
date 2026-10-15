using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using BepInEx;
using UnityEngine;

namespace OverTheHillPlus.Features.Public.ServerBrowserPlus;

/// <summary>
/// Favourite servers, remembered by their host/room name (not the session id, which changes every time the host
/// opens a new room). Names are compared without rich-text tags and case, so a host restyling their name keeps
/// the star. Saved to BepInEx/config/OverTheHillPlus.favourites.json.
/// </summary>
internal static class Favourites
{
    internal sealed class Entry
    {
        public string Name { get; set; }
        public long Added { get; set; }
        public long LastSeen { get; set; }
        public int LastPlayers { get; set; }
        public int LastMax { get; set; }
    }

    private const int MaxFavourites = 200;
    private static readonly Regex Tags = new("<[^>]*>", RegexOptions.Compiled);

    private static Dictionary<string, Entry> _entries = new();
    private static bool _loaded, _dirty;
    private static float _nextSave;

    /// <summary>Bumped on every add/remove so cached views (rows, sort) know to refresh.</summary>
    public static int Version { get; private set; }

    private static string FilePath => Path.Combine(Paths.ConfigPath, "OverTheHillPlus.favourites.json");

    public static int Count { get { EnsureLoaded(); return _entries.Count; } }

    /// <summary>Plain display name: rich-text tags removed, whitespace trimmed.</summary>
    public static string PlainName(string displayName)
    {
        if (string.IsNullOrEmpty(displayName)) return "";
        string s = displayName.IndexOf('<') >= 0 ? Tags.Replace(displayName, "") : displayName;
        return s.Trim();
    }

    /// <summary>Comparison key: plain name, lower case, inner whitespace collapsed.</summary>
    public static string Key(string displayName)
    {
        string s = PlainName(displayName).ToLowerInvariant();
        return s.Length == 0 ? "" : Regex.Replace(s, @"\s+", " ");
    }

    /// <summary>The name to remember a session by: the room's display name, else the part of the id after '_'.</summary>
    public static string DisplayNameOf(OverlandSessionInfo info)
    {
        if (info == null) return "";
        string name = null;
        try { name = info.RoomDisplayName; } catch { }
        if (string.IsNullOrWhiteSpace(PlainName(name)))
        {
            try
            {
                string raw = info.FusionSessionInfo?.Name ?? "";
                int cut = raw.IndexOf('_');
                name = cut >= 0 ? raw.Substring(cut + 1) : raw;
            }
            catch { }
        }
        return name ?? "";
    }

    public static bool IsFavourite(OverlandSessionInfo info) => IsFavouriteName(DisplayNameOf(info));

    public static bool IsFavouriteName(string displayName)
    {
        EnsureLoaded();
        if (_entries.Count == 0) return false;
        string key = Key(displayName);
        return key.Length > 0 && _entries.ContainsKey(key);
    }

    public static IEnumerable<Entry> All()
    {
        EnsureLoaded();
        return _entries.Values.OrderByDescending(e => e.LastSeen).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Adds or removes; returns the new state (true = now a favourite).</summary>
    public static bool Toggle(string displayName)
    {
        EnsureLoaded();
        string key = Key(displayName);
        if (key.Length == 0) return false;
        if (_entries.Remove(key)) { Changed(); return false; }
        if (_entries.Count >= MaxFavourites) return false;
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        _entries[key] = new Entry { Name = PlainName(displayName), Added = now, LastSeen = now };
        Changed();
        return true;
    }

    public static void Remove(string plainName)
    {
        EnsureLoaded();
        if (_entries.Remove(Key(plainName))) Changed();
    }

    /// <summary>Notes that a favourite is online right now (for the "last seen" column in the menu).</summary>
    public static void Seen(string displayName, int players, int max)
    {
        if (_entries.Count == 0) return;
        if (!_entries.TryGetValue(Key(displayName), out var e)) return;
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (now - e.LastSeen < 30 && e.LastPlayers == players && e.LastMax == max) return;
        e.LastSeen = now;
        e.LastPlayers = players;
        e.LastMax = max;
        _dirty = true;
    }

    public static string LastSeenText(Entry e)
    {
        long age = Math.Max(0, DateTimeOffset.UtcNow.ToUnixTimeSeconds() - e.LastSeen);
        if (age < 90) return e.LastMax > 0 ? $"online · {e.LastPlayers}/{e.LastMax}" : "online";
        long m = age / 60, h = m / 60, d = h / 24;
        string ago = d > 0 ? $"{d}d" : h > 0 ? $"{h}h" : $"{m}m";
        return $"seen {ago} ago";
    }

    private static void Changed()
    {
        Version++;
        _dirty = true;
        SaveIfDue(force: true);
    }

    public static void SaveIfDue(bool force = false)
    {
        if (!_dirty || (!force && Time.unscaledTime < _nextSave)) return;
        _nextSave = Time.unscaledTime + 30f;
        _dirty = false;
        try { File.WriteAllText(FilePath, JsonSerializer.Serialize(_entries)); }
        catch (Exception e) { Plugin.Logger.LogWarning("Could not save favourite servers: " + e.Message); }
    }

    private static void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;
        try
        {
            if (!File.Exists(FilePath)) return;
            var raw = JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(FilePath)) ?? new();
            _entries = new Dictionary<string, Entry>();
            foreach (var kv in raw)
            {
                if (kv.Value == null) continue;
                string key = Key(kv.Value.Name ?? kv.Key);
                if (key.Length > 0) _entries[key] = kv.Value;
            }
            Plugin.Logger.LogInfo($"Favourite servers: {_entries.Count}");
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning("Could not read favourite servers, starting fresh: " + e.Message);
            _entries = new();
        }
    }
}
