using System;
using System.Collections.Generic;

namespace OverTheHillPlus.RealNights;

/// <summary>One lamp that could get a light this pass.</summary>
internal struct LampCandidate
{
    /// <summary>Which car it is on (any id, equal for the same car).</summary>
    public int Car;
    /// <summary>Metres from the camera to the car.</summary>
    public float Distance;
    /// <summary>On the player's own car: always first.</summary>
    public bool IsLocal;
    /// <summary>It had a light on the last pass: it keeps it against a car just as near (no flicker as cars pass each other).</summary>
    public bool Lit;
}

/// <summary>Which lamps get one of the few lights: pure selection, no game types.</summary>
internal static class LightBudget
{
    /// <summary>A car that already has lights counts as this much nearer.</summary>
    public const float StickyFactor = 0.85f;

    /// <summary>
    /// The indices (into <paramref name="all"/>) of the lamps that get a light: your own car first, then the nearest
    /// cars, each car with at most <paramref name="perCar"/> lamps, nothing beyond <paramref name="cullDistance"/>
    /// (your own car is never culled), at most <paramref name="max"/> in all. A car's lamps keep their order.
    /// </summary>
    public static List<int> Pick(IReadOnlyList<LampCandidate> all, int max, int perCar, float cullDistance)
    {
        var picked = new List<int>();
        if (all == null || max <= 0 || perCar <= 0) return picked;

        // One entry per car: its rank (smaller = first) and its lamps in the order they came.
        var cars = new Dictionary<int, (float rank, List<int> lamps)>();
        for (int i = 0; i < all.Count; i++)
        {
            var c = all[i];
            if (!c.IsLocal && c.Distance > cullDistance) continue;
            float rank = c.IsLocal ? float.NegativeInfinity : c.Distance * (c.Lit ? StickyFactor : 1f);
            if (!cars.TryGetValue(c.Car, out var entry)) cars[c.Car] = entry = (rank, new List<int>());
            else if (rank < entry.rank) cars[c.Car] = entry = (rank, entry.lamps);
            entry.lamps.Add(i);
        }

        var order = new List<KeyValuePair<int, (float rank, List<int> lamps)>>(cars);
        order.Sort((a, b) =>
        {
            int r = a.Value.rank.CompareTo(b.Value.rank);
            return r != 0 ? r : a.Key.CompareTo(b.Key); // equal distance: the same order every pass
        });

        foreach (var car in order)
        {
            var lamps = car.Value.lamps;
            for (int k = 0; k < lamps.Count && k < perCar && picked.Count < max; k++) picked.Add(lamps[k]);
            if (picked.Count >= max) break;
        }
        return picked;
    }
}
