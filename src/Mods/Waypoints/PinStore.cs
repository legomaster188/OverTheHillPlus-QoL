using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using BepInEx;
using OverTheHillPlus.Shared;
using UnityEngine;

namespace OverTheHillPlus.Waypoints;

/// <summary>A pin you saved (per map).</summary>
internal sealed class SavedPin
{
    public string Name { get; set; } = "";
    public float X { get; set; }
    public float Y { get; set; }
    public float Z { get; set; }
    public int Color { get; set; }
    public string Saved { get; set; } = "";

    [JsonIgnore] public Vector3 Position => new(X, Y, Z);
}

/// <summary>
/// Your saved pins, per map, in BepInEx/config/oth.waypoints/pins.json (written through a temporary file). Spots from
/// the older all-in-one mod's waypoints file (OverTheHillPlus.waypoints.json, same layout) can be imported once.
/// </summary>
internal static class PinStore
{
    public const int MaxPerArea = 200;

    private static string Folder => Path.Combine(Paths.ConfigPath, "oth.waypoints");
    private static string FilePath => Path.Combine(Folder, "pins.json");
    private static string OldFilePath => Path.Combine(Paths.ConfigPath, "OverTheHillPlus.waypoints.json");

    private static Dictionary<string, List<SavedPin>> _all;
    private static Dictionary<string, List<SavedPin>> All => _all ??= Load(FilePath, true);

    /// <summary>Bumped on every change (the map and the other mods refresh from it).</summary>
    public static int Version { get; private set; }

    private static Dictionary<string, List<SavedPin>> Load(string path, bool keepBad)
    {
        var result = new Dictionary<string, List<SavedPin>>(StringComparer.Ordinal);
        try
        {
            if (!File.Exists(path)) return result;
            var data = JsonSerializer.Deserialize<Dictionary<string, List<SavedPin>>>(File.ReadAllText(path));
            if (data == null) return result;
            foreach (var kv in data)
            {
                if (kv.Key == null || kv.Value == null) continue;
                var list = new List<SavedPin>();
                foreach (var p in kv.Value)
                {
                    if (p == null || !PinBoard.ValidCoordinate(p.X) || !PinBoard.ValidCoordinate(p.Y) || !PinBoard.ValidCoordinate(p.Z)) continue;
                    p.Name = PinBoard.CleanText(p.Name, PinBoard.MaxName);
                    if (p.Name.Length == 0) p.Name = "Pin";
                    p.Color = ((p.Color % PinBoard.ColorCount) + PinBoard.ColorCount) % PinBoard.ColorCount;
                    if (list.Count < MaxPerArea) list.Add(p);
                }
                if (list.Count > 0) result[kv.Key] = list;
            }
            DebugLog.Verbose("waypoints", () => $"pins read from {Path.GetFileName(path)}: {result.Values.Sum(l => l.Count)} pin(s) on {result.Count} map(s)");
        }
        catch (Exception e)
        {
            DebugLog.Error("waypoints", e, "PinStore.Load");
            WaypointsPlugin.Logger.LogWarning($"[Waypoints] {Path.GetFileName(path)} unreadable: {e.Message}");
            if (keepBad) { try { File.Copy(path, path + ".bad", overwrite: true); } catch (Exception ex) { DebugLog.Error("waypoints", ex, "PinStore.Load"); } }
        }
        return result;
    }

    private static void Persist()
    {
        Version++;
        try
        {
            Directory.CreateDirectory(Folder);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_all, new JsonSerializerOptions { WriteIndented = true }));
            File.Copy(tmp, FilePath, overwrite: true);
            File.Delete(tmp);
            DebugLog.Verbose("waypoints", () => $"pins saved (version {Version}): {_all.Values.Sum(l => l.Count)} pin(s) on {_all.Count} map(s)");
        }
        catch (Exception e) { DebugLog.Error("waypoints", e, "PinStore.Persist"); WaypointsPlugin.Logger.LogWarning("[Waypoints] could not save pins: " + e.Message); }
    }

    public static IReadOnlyList<SavedPin> For(string area) =>
        All.TryGetValue(area ?? "", out var list) ? list : (IReadOnlyList<SavedPin>)Array.Empty<SavedPin>();

    public static int CountOtherAreas(string area) => All.Where(kv => kv.Key != area).Sum(kv => kv.Value.Count);

    public static bool Contains(string area, SavedPin p) => p != null && All.TryGetValue(area ?? "", out var list) && list.Contains(p);

    public static string Add(string area, string name, Vector3 pos, int color)
    {
        if (!PinBoard.ValidCoordinate(pos.x) || !PinBoard.ValidCoordinate(pos.y) || !PinBoard.ValidCoordinate(pos.z)) return "Failed: that spot is off the map.";
        if (!All.TryGetValue(area, out var list)) All[area] = list = new List<SavedPin>();
        if (list.Count >= MaxPerArea) return $"Failed: {MaxPerArea} pins is the limit per map.";
        name = PinBoard.CleanText(name, PinBoard.MaxName);
        if (name.Length == 0) name = $"Pin {list.Count + 1}";
        string unique = name;
        for (int n = 2; list.Any(w => string.Equals(w.Name, unique, StringComparison.OrdinalIgnoreCase)); n++)
            unique = PinBoard.CleanText(name, PinBoard.MaxName - 5) + $" ({n})";
        list.Add(new SavedPin
        {
            Name = unique, X = pos.x, Y = pos.y, Z = pos.z,
            Color = ((color % PinBoard.ColorCount) + PinBoard.ColorCount) % PinBoard.ColorCount,
            Saved = DateTime.Now.ToString("yyyy-MM-dd HH:mm"),
        });
        Persist();
        DebugLog.Info("waypoints", $"pin saved on '{area}': '{unique}' at {pos.x:0},{pos.z:0} (colour {color}; {list.Count} pin(s) on this map)");
        return $"Saved \"{unique}\".";
    }

    public static void Remove(string area, SavedPin p)
    {
        if (!All.TryGetValue(area ?? "", out var list) || !list.Remove(p)) return;
        if (list.Count == 0) All.Remove(area);
        DebugLog.Info("waypoints", $"pin removed on '{area}': '{p.Name}'");
        Persist();
    }

    // ---------------------------------------------------------------- the older mod's waypoints file

    /// <summary>Spots in the old waypoints file not saved here yet (0 when there is no such file).</summary>
    public static int ImportableCount()
    {
        try { if (!File.Exists(OldFilePath)) return 0; }
        catch (Exception ex) { DebugLog.Error("waypoints", ex, "PinStore.ImportableCount"); return 0; }
        int n = 0;
        foreach (var kv in Load(OldFilePath, false))
            foreach (var p in kv.Value)
                if (!Has(kv.Key, p)) n++;
        return n;
    }

    public static string ImportOld()
    {
        int added = 0;
        foreach (var kv in Load(OldFilePath, false))
        {
            if (!All.TryGetValue(kv.Key, out var list)) All[kv.Key] = list = new List<SavedPin>();
            foreach (var p in kv.Value)
            {
                if (list.Count >= MaxPerArea || Has(kv.Key, p)) continue;
                p.Saved = string.IsNullOrEmpty(p.Saved) ? DateTime.Now.ToString("yyyy-MM-dd HH:mm") : p.Saved;
                list.Add(p);
                added++;
            }
            if (list.Count == 0) All.Remove(kv.Key);
        }
        DebugLog.Info("waypoints", $"import of the old waypoints file: {added} spot(s) added");
        if (added > 0) Persist();
        return added > 0 ? $"Imported {added} spot(s) from the old waypoints file." : "Nothing new to import.";
    }

    private static bool Has(string area, SavedPin p) =>
        All.TryGetValue(area, out var list) && list.Any(w => Mathf.Abs(w.X - p.X) < 1f && Mathf.Abs(w.Z - p.Z) < 1f &&
                                                            string.Equals(w.Name, p.Name, StringComparison.OrdinalIgnoreCase));
}
