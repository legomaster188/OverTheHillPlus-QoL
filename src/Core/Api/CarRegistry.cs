using System;
using System.Collections.Generic;

namespace OverTheHillPlus.Api;

/// <summary>
/// The modded cars that registered with <see cref="Cars"/>: the checks and the list, without BepInEx or Unity types
/// (unit-tested). Main thread only.
/// </summary>
internal sealed class CarRegistry
{
    public const int MaxCars = 32, MaxDisplayName = 48, MaxLiveryCar = 32;

    internal sealed class Entry
    {
        public string Id, DisplayName, LiveryCar;
        public Func<string> CurrentSkin;
        public Action<string> ApplySkin;
    }

    private readonly List<Entry> _cars = new();

    public IReadOnlyList<Entry> All => _cars;

    public Entry Find(string id)
    {
        if (id == null) return null;
        foreach (var c in _cars)
            if (string.Equals(c.Id, id, StringComparison.OrdinalIgnoreCase)) return c;
        return null;
    }

    /// <summary>Takes a car out again (its mod failed to start after registering); false when it isn't registered.</summary>
    public bool Remove(string id)
    {
        var c = Find(id);
        return c != null && _cars.Remove(c);
    }

    /// <summary>Adds a car; null on success, otherwise why it was refused (nothing is added then).</summary>
    public string Add(Entry e)
    {
        if (e == null) return "no car given";
        if (!ApiRules.IsValidModId(e.Id)) return $"\"{e.Id}\" is not a valid car id: use 2 to 40 characters from a-z, 0-9, '_', '.' and '-'";
        if (string.IsNullOrWhiteSpace(e.DisplayName) || e.DisplayName.Length > MaxDisplayName)
            return $"car \"{e.Id}\": the display name must be 1 to {MaxDisplayName} characters";
        if (!IsLiveryCarName(e.LiveryCar))
            return $"car \"{e.Id}\": the livery car name must be 1 to {MaxLiveryCar} letters, digits or '_' (as manifests name it)";
        if (IsStockCarName(e.LiveryCar))
            return $"car \"{e.Id}\": \"{e.LiveryCar}\" is a stock car's livery name (its liveries are the game's)";
        if (e.CurrentSkin == null || e.ApplySkin == null) return $"car \"{e.Id}\": both skin callbacks are needed";
        if (Find(e.Id) != null) return $"car \"{e.Id}\" is already registered";
        foreach (var c in _cars)
            if (string.Equals(c.LiveryCar, e.LiveryCar, StringComparison.OrdinalIgnoreCase))
                return $"car \"{e.Id}\": the livery car name \"{e.LiveryCar}\" is already used by \"{c.Id}\"";
        if (_cars.Count >= MaxCars) return $"at most {MaxCars} modded cars can register";
        _cars.Add(new Entry
        {
            Id = e.Id, DisplayName = e.DisplayName.Trim(), LiveryCar = e.LiveryCar, CurrentSkin = e.CurrentSkin, ApplySkin = e.ApplySkin,
        });
        return null;
    }

    /// <summary>The game's own cars as livery manifests name them (and their CarAudioManager.VehicleType names), plus
    /// the unreleased ones: a modded car can't take these.</summary>
    public static readonly string[] StockCarNames =
    {
        "Bull", "BullV1", "Protector", "TrailBurner", "Ding", "DingDing", "Burro", "Timberbox", "Trailrat", "Brick", "RustOx",
        "Woodman", "GrandWoodman", "BlastWagon", "Mammoth", "Panda", "Boar", "Marshal", "Polyana", "Lynx", "Rancho",
        "Wombat", "Tinny", "Cumbre", "Roamer", "Cordillera", "Brigand", "Grizzly", "Hammer", "Default",
    };

    public static bool IsStockCarName(string name)
    {
        foreach (var n in StockCarNames)
            if (string.Equals(n, name, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>A car name as livery manifests use it (the game's own are like "Bull", "TrailBurner", "Ding").</summary>
    public static bool IsLiveryCarName(string name)
    {
        if (string.IsNullOrEmpty(name) || name.Length > MaxLiveryCar) return false;
        foreach (char c in name)
            if (!(c >= 'a' && c <= 'z' || c >= 'A' && c <= 'Z' || c >= '0' && c <= '9' || c == '_')) return false;
        return true;
    }
}
