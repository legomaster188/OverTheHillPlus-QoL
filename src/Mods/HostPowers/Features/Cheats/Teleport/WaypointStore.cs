using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using BepInEx;
using OverTheHillPlus.HostPowers;
using UnityEngine;

namespace OverTheHillPlus.Features.Cheats.Teleport;

/// <summary>Named teleport spots per map, saved to BepInEx/config/OverTheHillPlus.waypoints.json.</summary>
internal static class WaypointStore
{
    public sealed class Waypoint
    {
        public string Name { get; set; }
        public float X { get; set; }
        public float Y { get; set; }
        public float Z { get; set; }
        public float Yaw { get; set; }
        public string Saved { get; set; }

        public Vector3 Position => new(X, Y, Z);
    }

    public const int MaxPerArea = 200;
    private static string FilePath => Path.Combine(Paths.ConfigPath, "OverTheHillPlus.waypoints.json");
    private static Dictionary<string, List<Waypoint>> _all;

    private static Dictionary<string, List<Waypoint>> All => _all ??= Load();

    private static Dictionary<string, List<Waypoint>> Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new(StringComparer.Ordinal);
            var data = JsonSerializer.Deserialize<Dictionary<string, List<Waypoint>>>(File.ReadAllText(FilePath));
            var result = new Dictionary<string, List<Waypoint>>(StringComparer.Ordinal);
            if (data != null)
                foreach (var kv in data)
                    if (kv.Key != null && kv.Value != null)
                        result[kv.Key] = kv.Value.Where(w => w != null && IsFinite(w)).ToList();
            return result;
        }
        catch (Exception e)
        {
            DebugLog.Error("hostpowers", e, "WaypointStore.Load");
            HostPowersPlugin.Logger.LogWarning("[Teleport] waypoints file unreadable, starting fresh: " + e.Message);
            try { File.Copy(FilePath, FilePath + ".bad", overwrite: true); } catch (Exception ex) { DebugLog.Error("hostpowers", ex, "WaypointStore.Load"); }
            return new(StringComparer.Ordinal);
        }
    }

    private static bool IsFinite(Waypoint w) =>
        float.IsFinite(w.X) && float.IsFinite(w.Y) && float.IsFinite(w.Z) && float.IsFinite(w.Yaw);

    private static void Persist()
    {
        try
        {
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_all, new JsonSerializerOptions { WriteIndented = true }));
            File.Copy(tmp, FilePath, overwrite: true);
            File.Delete(tmp);
        }
        catch (Exception e) { DebugLog.Error("hostpowers", e, "WaypointStore.Persist"); HostPowersPlugin.Logger.LogWarning("[Teleport] could not save waypoints: " + e.Message); }
    }

    /// <summary>Waypoints of an area (a live list; don't modify it directly).</summary>
    public static IReadOnlyList<Waypoint> For(string area) =>
        All.TryGetValue(area ?? "", out var list) ? list : (IReadOnlyList<Waypoint>)Array.Empty<Waypoint>();

    public static int CountOtherAreas(string area) => All.Where(kv => kv.Key != area).Sum(kv => kv.Value.Count);

    public static int AreaCount => All.Count(kv => kv.Value.Count > 0);

    public static string Add(string area, string name, Vector3 pos, float yaw)
    {
        if (!All.TryGetValue(area, out var list)) All[area] = list = new List<Waypoint>();
        if (list.Count >= MaxPerArea) return $"Failed: {MaxPerArea} waypoints is the limit per map.";
        name = Clean(name);
        if (string.IsNullOrEmpty(name)) name = $"Spot {list.Count + 1}";
        // Unique names keep the list readable.
        string unique = name;
        for (int n = 2; list.Any(w => string.Equals(w.Name, unique, StringComparison.OrdinalIgnoreCase)); n++) unique = $"{name} ({n})";
        list.Add(new Waypoint { Name = unique, X = pos.x, Y = pos.y, Z = pos.z, Yaw = yaw, Saved = DateTime.Now.ToString("yyyy-MM-dd HH:mm") });
        Persist();
        return $"Saved \"{unique}\".";
    }

    public static void Update(string area, Waypoint w, Vector3 pos, float yaw)
    {
        if (!All.TryGetValue(area, out var list) || !list.Contains(w)) return;
        w.X = pos.x; w.Y = pos.y; w.Z = pos.z; w.Yaw = yaw;
        w.Saved = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
        Persist();
    }

    public static void Rename(string area, Waypoint w, string name)
    {
        name = Clean(name);
        if (string.IsNullOrEmpty(name) || !All.TryGetValue(area, out var list) || !list.Contains(w)) return;
        w.Name = name;
        Persist();
    }

    public static void Remove(string area, Waypoint w)
    {
        if (!All.TryGetValue(area, out var list) || !list.Remove(w)) return;
        if (list.Count == 0) All.Remove(area);
        Persist();
    }

    public static void Move(string area, Waypoint w, int delta)
    {
        if (!All.TryGetValue(area, out var list)) return;
        int i = list.IndexOf(w), j = i + delta;
        if (i < 0 || j < 0 || j >= list.Count) return;
        (list[i], list[j]) = (list[j], list[i]);
        Persist();
    }

    private static string Clean(string name)
    {
        if (name == null) return "";
        name = new string(name.Where(c => !char.IsControl(c) && c != '<' && c != '>').ToArray()).Trim();
        return name.Length > 40 ? name.Substring(0, 40) : name;
    }
}
