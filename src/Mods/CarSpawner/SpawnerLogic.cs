using System;
using System.Collections.Generic;

namespace OverTheHillPlus.CarSpawner;

/// <summary>Which cars the list shows.</summary>
internal enum CarGroup { All, Stock, Modded }

/// <summary>One car the menu can spawn. Pure: no game types.</summary>
internal sealed class CarChoice
{
    public uint ItemId;
    /// <summary>Shown in the menu ("Bull", "Vera Crossover").</summary>
    public string Display;
    /// <summary>The name after "Prefabs/Cars/Car_" (what CheatsManager.SpawnCar takes).</summary>
    public string Prefab;
    /// <summary>A car a mod adds (core's car framework), not one of the game's.</summary>
    public bool Modded;
}

/// <summary>The car list (merge, filter, order) and where a spawned car goes. Pure (unit-tested).</summary>
internal static class SpawnerLogic
{
    /// <summary>Metres from the player's car centre to the new car's centre when nothing says otherwise.</summary>
    public const float DefaultAhead = 9f;
    /// <summary>The car is dropped this far above the ground point (it settles on its suspension).</summary>
    public const float DropHeight = 1.1f;

    /// <summary>Modded cars first, then stock ones, each by name; one entry per item id and per prefab name (the first
    /// one given wins, so list the modded cars first).</summary>
    public static List<CarChoice> Merge(IEnumerable<CarChoice> modded, IEnumerable<CarChoice> stock)
    {
        var all = new List<CarChoice>();
        var ids = new HashSet<uint>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(CarChoice c)
        {
            if (c == null || string.IsNullOrEmpty(c.Prefab) || string.IsNullOrEmpty(c.Display)) return;
            if (c.ItemId != 0 && !ids.Add(c.ItemId)) return;
            if (!names.Add(c.Prefab)) return;
            all.Add(c);
        }
        if (modded != null) foreach (var c in modded) Add(c);
        if (stock != null) foreach (var c in stock) Add(c);
        all.Sort((a, b) => a.Modded != b.Modded
            ? (a.Modded ? -1 : 1)
            : string.Compare(a.Display, b.Display, StringComparison.OrdinalIgnoreCase));
        return all;
    }

    /// <summary>The cars of a group whose name contains every word of <paramref name="query"/> (any case); an empty
    /// query keeps them all. Order is kept.</summary>
    public static List<CarChoice> Filter(IReadOnlyList<CarChoice> cars, string query, CarGroup group)
    {
        var words = (query ?? "").Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        var result = new List<CarChoice>();
        if (cars == null) return result;
        foreach (var c in cars)
        {
            if (group == CarGroup.Stock && c.Modded) continue;
            if (group == CarGroup.Modded && !c.Modded) continue;
            bool all = true;
            foreach (var w in words)
                if (c.Display.IndexOf(w, StringComparison.OrdinalIgnoreCase) < 0 && c.Prefab.IndexOf(w, StringComparison.OrdinalIgnoreCase) < 0) { all = false; break; }
            if (all) result.Add(c);
        }
        return result;
    }

    /// <summary>The index of the car with this prefab name in the list, or 0 (the list's first) when it isn't there.</summary>
    public static int IndexOf(IReadOnlyList<CarChoice> cars, string prefab)
    {
        if (cars == null || prefab == null) return 0;
        for (int i = 0; i < cars.Count; i++)
            if (string.Equals(cars[i].Prefab, prefab, StringComparison.OrdinalIgnoreCase)) return i;
        return 0;
    }

    public const float MaxPlaceSlope = 35f;

    /// <summary>Is ground with this surface normal (its y part) steeper than <paramref name="maxDegrees"/>?</summary>
    public static bool TooSteep(float normalY, float maxDegrees = MaxPlaceSlope) =>
        normalY < (float)Math.Cos(maxDegrees * Math.PI / 180.0);

    /// <summary>A yaw turned by <paramref name="delta"/> degrees, kept in [0, 360).</summary>
    public static float TurnYaw(float yaw, float delta)
    {
        float y = (yaw + delta) % 360f;
        return y < 0f ? y + 360f : y;
    }

    /// <summary>The spot (x, z) <paramref name="distance"/> metres ahead of (x, z) for a car facing
    /// <paramref name="yawDegrees"/> (Unity: 0 = +Z, 90 = +X).</summary>
    public static (float x, float z) Ahead(float x, float z, float yawDegrees, float distance)
    {
        double r = yawDegrees * Math.PI / 180.0;
        return (x + (float)(Math.Sin(r) * distance), z + (float)(Math.Cos(r) * distance));
    }
}
