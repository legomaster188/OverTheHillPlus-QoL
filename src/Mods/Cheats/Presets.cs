using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using BepInEx;
using OverTheHillPlus.CheatsPlus;

namespace OverTheHillPlus;

/// <summary>Named presets: your car plus every assist/cheat/movement setting, saved to a JSON file.</summary>
internal static class Presets
{
    public sealed class Preset
    {
        public string Name { get; set; }
        public uint CarId { get; set; }
        public string CarName { get; set; }
        public Dictionary<string, string> Settings { get; set; } = new();
    }

    private static readonly string[] Sections = { "Assists", "Cheats", "Movement", "Vehicles" };
    private static string FilePath => Path.Combine(Paths.ConfigPath, "OverTheHillPlus.presets.json");
    private static List<Preset> _all;
    private static readonly Random Rng = new();

    public static IReadOnlyList<Preset> All => _all ??= Load();

    private static List<Preset> Load()
    {
        try { return File.Exists(FilePath) ? JsonSerializer.Deserialize<List<Preset>>(File.ReadAllText(FilePath)) ?? new() : new(); }
        catch (Exception e) { DebugLog.Error("cheats", e, "Presets.Load"); CheatsPlugin.Logger.LogWarning("Presets unreadable, starting fresh: " + e.Message); return new(); }
    }

    private static void Persist()
    {
        try { File.WriteAllText(FilePath, JsonSerializer.Serialize(_all, new JsonSerializerOptions { WriteIndented = true })); }
        catch (Exception e) { DebugLog.Error("cheats", e, "Presets.Persist"); CheatsPlugin.Logger.LogWarning("Could not save presets: " + e.Message); }
    }

    /// <summary>The car you're driving now, if the game can tell us.</summary>
    public static ItemCatalog.Item CurrentCar()
    {
        try
        {
            var runner = OverlandNetworkRunner.Instance;
            var me = GameState.LocalPlayer;
            if (runner == null || me == null) return null;
            var info = runner.GetCarInfo(me.Value);
            if (info == null) return null;
            return ItemCatalog.Cars().FirstOrDefault(c => c.Id == info.ItemId);
        }
        catch (Exception ex) { DebugLog.Error("cheats", ex, "Presets.CurrentCar"); return null; }
    }

    public static string Save(string name)
    {
        name = (name ?? "").Trim();
        if (name.Length == 0) return "Failed: give the preset a name.";
        var car = CurrentCar();
        var preset = new Preset { Name = name, CarId = car?.Id ?? 0, CarName = car?.Name };
        foreach (var kv in CheatsConfig.File)
            if (Sections.Contains(kv.Key.Section))
                preset.Settings[$"{kv.Key.Section}/{kv.Key.Key}"] = kv.Value.GetSerializedValue();
        _all ??= Load();
        _all.RemoveAll(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        _all.Add(preset);
        _all.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        Persist();
        return $"Saved preset '{name}'" + (car != null ? $" ({car.Name})" : " (settings only: car unknown)");
    }

    public static string Apply(Preset preset)
    {
        int applied = 0;
        foreach (var kv in CheatsConfig.File)
        {
            if (!preset.Settings.TryGetValue($"{kv.Key.Section}/{kv.Key.Key}", out var value)) continue;
            try { kv.Value.SetSerializedValue(value); applied++; } catch (Exception ex) { DebugLog.Error("cheats", ex, "Presets.Apply"); }
        }
        string carResult = "";
        if (preset.CarId != 0)
        {
            var car = ItemCatalog.Cars().FirstOrDefault(c => c.Id == preset.CarId);
            carResult = car != null ? " · " + Vehicles.SwitchCar(car) : " · car not available";
        }
        return $"Loaded '{preset.Name}': {applied} settings{carResult}";
    }

    public static string Delete(Preset preset)
    {
        _all?.Remove(preset);
        Persist();
        return $"Deleted '{preset.Name}'";
    }

    public static string RandomCar()
    {
        var cars = ItemCatalog.Cars().ToList();
        if (cars.Count == 0) return "Failed: load into a world first.";
        var current = CurrentCar();
        var pool = cars.Where(c => current == null || c.Id != current.Id).ToList();
        var pick = pool[Rng.Next(pool.Count)];
        return Vehicles.SwitchCar(pick);
    }
}
