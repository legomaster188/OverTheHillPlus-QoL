using System;
using System.IO;
using System.Text.Json;
using BepInEx;

namespace OverTheHillPlus.Features.Public.Rejoin;

/// <summary>
/// The last server this game joined as a guest (never your own hosted room or single-player). Saved to
/// BepInEx/config/OverTheHillPlus.lastserver.json so the "Rejoin last server" button also works after a restart.
/// </summary>
internal static class LastServerStore
{
    internal sealed class Entry
    {
        /// <summary>Fusion session name (unique per room; changes when the host opens a new room).</summary>
        public string SessionName { get; set; }
        /// <summary>Plain room/host name as the browser shows it (fallback match for public rooms).</summary>
        public string DisplayName { get; set; }
        /// <summary>Photon region code the room lives in (e.g. "eu").</summary>
        public string Region { get; set; }
        /// <summary>Room code the host sent us, if any (normalised: no dash, upper case).</summary>
        public string RoomCode { get; set; }
        public bool Private { get; set; }
        public long JoinedAt { get; set; }
        public long LastSeenAt { get; set; }
    }

    private static Entry _entry;
    private static bool _loaded;

    private static string FilePath => Path.Combine(Paths.ConfigPath, "OverTheHillPlus.lastserver.json");

    public static Entry Current { get { EnsureLoaded(); return _entry; } }

    public static bool HasServer => Current != null && !string.IsNullOrEmpty(Current.SessionName);

    /// <summary>Bumped on every change so cached views (main-menu button) can notice.</summary>
    public static int Version { get; private set; }

    public static string NormaliseCode(string code)
    {
        if (string.IsNullOrWhiteSpace(code)) return null;
        string c = code.Replace("-", "").Replace(" ", "").Trim().ToUpperInvariant();
        return c.Length == 0 ? null : c;
    }

    public static void Remember(Entry e)
    {
        if (e == null || string.IsNullOrEmpty(e.SessionName)) return;
        EnsureLoaded();
        _entry = e;
        Version++;
        Save();
    }

    /// <summary>Updates fields learned after joining (room code arrives a moment later) without a new entry.</summary>
    public static void Touch(string sessionName, string roomCode)
    {
        EnsureLoaded();
        if (_entry == null || _entry.SessionName != sessionName) return;
        bool changed = false;
        string code = NormaliseCode(roomCode);
        if (code != null && code != _entry.RoomCode) { _entry.RoomCode = code; changed = true; }
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (now - _entry.LastSeenAt >= 60) { _entry.LastSeenAt = now; changed = true; }
        if (changed) Save();
    }

    public static void Forget()
    {
        EnsureLoaded();
        _entry = null;
        Version++;
        try { if (File.Exists(FilePath)) File.Delete(FilePath); }
        catch (Exception e) { Plugin.Logger.LogWarning("[Rejoin] could not delete the saved last server: " + e.Message); }
    }

    public static string AgoText(long unix)
    {
        if (unix <= 0) return "unknown";
        long age = Math.Max(0, DateTimeOffset.UtcNow.ToUnixTimeSeconds() - unix);
        if (age < 60) return "just now";
        long m = age / 60, h = m / 60, d = h / 24;
        return d > 0 ? $"{d}d ago" : h > 0 ? $"{h}h ago" : $"{m}m ago";
    }

    private static void Save()
    {
        try
        {
            if (_entry == null) return;
            File.WriteAllText(FilePath, JsonSerializer.Serialize(_entry));
        }
        catch (Exception e) { Plugin.Logger.LogWarning("[Rejoin] could not save the last server: " + e.Message); }
    }

    private static void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;
        try
        {
            if (!File.Exists(FilePath)) return;
            var e = JsonSerializer.Deserialize<Entry>(File.ReadAllText(FilePath));
            if (e != null && !string.IsNullOrEmpty(e.SessionName)) _entry = e;
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogWarning("[Rejoin] could not read the saved last server, starting fresh: " + ex.Message);
            _entry = null;
        }
    }
}
