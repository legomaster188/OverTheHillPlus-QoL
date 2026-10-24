using System;
using System.Collections.Generic;

namespace OverTheHillPlus.Cars;

/// <summary>
/// What a trailer does to the car pulling it, when it is heavier than the car's tow rating. The physics already makes a
/// heavy trailer hard to pull; this adds what an overloaded real car feels: the engine runs out of breath and the car will
/// not go as fast. Up to the rating nothing changes. A car without a rating (0) tows anything. Pure code (unit-tested).
/// </summary>
public static class TowRules
{
    /// <summary>The most power an overloaded car loses (it keeps 40 %).</summary>
    public const float MinPower = 0.4f;
    /// <summary>The most top speed an overloaded car loses (it keeps 55 %).</summary>
    public const float MinSpeed = 0.55f;
    /// <summary>How far past the rating the loss is complete (1.5 = two and a half times the rating).</summary>
    private const float FullAt = 1.5f;

    /// <summary>How far past the rating the load is: 0 up to the rating, 1 at double the rating, capped where the loss is complete. 0 for no rating.</summary>
    public static float Over(float loadKg, float ratedKg)
    {
        if (!(ratedKg > 0f) || !float.IsFinite(loadKg) || !(loadKg > ratedKg)) return 0f;
        float over = (loadKg - ratedKg) / ratedKg;
        return over > FullAt ? FullAt : over;
    }

    /// <summary>Multiplier on the car's engine power and torque while it tows <paramref name="loadKg"/>.</summary>
    public static float PowerFactor(float loadKg, float ratedKg) =>
        1f - (1f - MinPower) * Over(loadKg, ratedKg) / FullAt;

    /// <summary>Multiplier on the car's speed limit while it tows <paramref name="loadKg"/>.</summary>
    public static float SpeedFactor(float loadKg, float ratedKg) =>
        1f - (1f - MinSpeed) * Over(loadKg, ratedKg) / FullAt;

    /// <summary>"Tows up to 1500 kg" for a car page, or null when the car has no rating.</summary>
    public static string Describe(float ratedKg) =>
        ratedKg > 0f && float.IsFinite(ratedKg)
            ? "Tows up to " + Math.Round(ratedKg / 50f) * 50 + " kg"
            : null;
}

/// <summary>
/// The weight of the trailer each car is pulling right now, as the trailers report it (every half second or so). A report
/// older than <see cref="MaxAge"/> seconds counts as unhitched, so a trailer that goes away (unhitched, despawned, the
/// mod switched off) stops weighing on the car by itself. Keyed by the car's native pointer. Pure code (unit-tested).
/// </summary>
public sealed class TowLoadBook
{
    /// <summary>A report is good for this long, seconds.</summary>
    public const float MaxAge = 2.5f;

    private readonly Dictionary<long, (float kg, float at)> _loads = new();

    /// <summary>The car <paramref name="car"/> pulls <paramref name="kg"/> of trailer as of <paramref name="now"/>.
    /// Several trailers on one car add up.</summary>
    public void Report(long car, float kg, float now)
    {
        if (car == 0 || !float.IsFinite(kg) || kg <= 0f) return;
        if (_loads.TryGetValue(car, out var cur) && now - cur.at < 0.05f) kg += cur.kg;
        _loads[car] = (kg, now);
    }

    /// <summary>The trailer weight reported for the car in the last <see cref="MaxAge"/> seconds, 0 when none.</summary>
    public float Load(long car, float now)
    {
        if (!_loads.TryGetValue(car, out var e)) return 0f;
        if (now - e.at > MaxAge || now < e.at - 1f) { _loads.Remove(car); return 0f; }
        return e.kg;
    }

    public int Count => _loads.Count;

    public void Clear() => _loads.Clear();
}
