using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using BepInEx;
using Fusion;
using UnityEngine;

namespace OverTheHillPlus.ServerBrowser;

/// <summary>
/// Remembers when each server (by session name) was first seen, persisted across game restarts.
/// "Exact" means we watched it appear; otherwise it already existed when we started watching,
/// so the age is only a lower bound.
/// </summary>
internal static class FirstSeenTracker
{
    private sealed class Entry
    {
        public long First { get; set; }
        public long Last { get; set; }
        public bool Exact { get; set; }
    }

    private const long PruneAfterSeconds = 3 * 24 * 3600;
    private const float ContinuityWindowSeconds = 60f;

    private static Dictionary<string, Entry> _entries = new();
    private static float _lastUpdateRealtime = -1f;
    private static IntPtr _lastRunner;
    private static bool _dirty;
    private static float _nextSave;

    private static string FilePath => Path.Combine(Paths.ConfigPath, "OverTheHillPlus.firstseen.json");

    public static int Count => _entries.Count;

    public static void Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return;
            _entries = JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(FilePath)) ?? new();
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            int before = _entries.Count;
            foreach (var key in new List<string>(_entries.Keys))
                if (now - _entries[key].Last > PruneAfterSeconds) _entries.Remove(key);
            Plugin.Logger.LogInfo($"First-seen data: {_entries.Count} servers ({before - _entries.Count} pruned)");
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning("Could not read first-seen data, starting fresh: " + e.Message);
            _entries = new();
        }
    }

    public static void Observe(NetworkRunner runner, Il2CppSystem.Collections.Generic.List<SessionInfo> sessions)
    {
        if (sessions == null) return;
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        IntPtr runnerPtr = runner == null ? IntPtr.Zero : runner.Pointer;
        bool continuous = _lastUpdateRealtime >= 0f
                          && Time.realtimeSinceStartup - _lastUpdateRealtime < ContinuityWindowSeconds
                          && runnerPtr == _lastRunner;

        for (int i = 0; i < sessions.Count; i++)
        {
            var name = sessions[i]?.Name;
            if (string.IsNullOrEmpty(name)) continue;
            if (_entries.TryGetValue(name, out var e)) e.Last = now;
            else _entries[name] = new Entry { First = now, Last = now, Exact = continuous };
        }

        _dirty = true;
        _lastUpdateRealtime = Time.realtimeSinceStartup;
        _lastRunner = runnerPtr;
    }

    public static void SaveIfDue(bool force = false)
    {
        if (!_dirty || (!force && Time.unscaledTime < _nextSave)) return;
        _nextSave = Time.unscaledTime + 30f;
        _dirty = false;
        try { File.WriteAllText(FilePath, JsonSerializer.Serialize(_entries)); }
        catch (Exception e) { Plugin.Logger.LogWarning("Could not save first-seen data: " + e.Message); }
    }

    /// <summary>"2h 15m", "≥3d 4h", or null if unknown.</summary>
    public static string AgeText(string sessionName)
    {
        if (sessionName == null || !_entries.TryGetValue(sessionName, out var e)) return null;
        long age = Math.Max(0, DateTimeOffset.UtcNow.ToUnixTimeSeconds() - e.First);
        if (e.Exact && age < 60) return "new";
        return (e.Exact ? "" : "≥") + Format(Math.Max(age, 60));
    }

    private static string Format(long seconds)
    {
        long m = seconds / 60, h = m / 60, d = h / 24;
        if (d > 0) return $"{d}d {h % 24}h";
        if (h > 0) return $"{h}h {m % 60}m";
        return $"{m}m";
    }
}
