using System;
using System.Collections.Generic;

namespace OverTheHillPlus.Cars;

/// <summary>
/// Upgrade layers on cars: each owner (a mod: the career's upgrades, a quest's reward) puts a <see cref="CarUpgradeEffect"/> on
/// a car and renews it about once a second; core's tuners multiply every fresh layer of a car into what they write
/// (<see cref="TunedCar"/>), so two mods never write the same game value. A layer not renewed for <see cref="StaleSeconds"/>
/// lapses by itself (the mod went, the car despawned). Cars are keys (the car's pointer); times are seconds on one clock
/// (Time.unscaledTime in the game). Pure code (unit-tested).
/// </summary>
public sealed class TuneLayerBook
{
    /// <summary>A layer is good for this long, seconds (owners renew it every second or so).</summary>
    public const float StaleSeconds = 5f;

    /// <summary>Layers per car (more owners are refused).</summary>
    public const int MaxLayersPerCar = 8;

    private sealed class Layer
    {
        public CarUpgradeEffect Effect;
        public float At;
    }

    private readonly Dictionary<long, Dictionary<string, Layer>> _cars = new();
    private readonly List<long> _old = new();

    /// <summary>Cars with at least one layer (fresh or not).</summary>
    public int Count => _cars.Count;

    private static bool Fresh(float at, float now) => now - at <= StaleSeconds && now >= at - 1f;

    /// <summary>
    /// Puts <paramref name="owner"/>'s layer on <paramref name="car"/> at <paramref name="now"/> (replacing its last one). An
    /// effect that changes nothing removes the layer. False for a bad key, owner or time, or a car that already has
    /// <see cref="MaxLayersPerCar"/> other owners.
    /// </summary>
    public bool Set(string owner, long car, CarUpgradeEffect effect, float now)
    {
        if (car == 0 || string.IsNullOrEmpty(owner) || !float.IsFinite(now)) return false;
        if (effect == null || effect.IsNone)
        {
            Remove(owner, car);
            return true;
        }
        if (!_cars.TryGetValue(car, out var layers)) _cars[car] = layers = new Dictionary<string, Layer>(StringComparer.Ordinal);
        if (!layers.ContainsKey(owner) && layers.Count >= MaxLayersPerCar) return false;
        layers[owner] = new Layer { Effect = effect.Clamped(), At = now };
        return true;
    }

    /// <summary>Takes <paramref name="owner"/>'s layer off <paramref name="car"/>.</summary>
    public void Remove(string owner, long car)
    {
        if (!_cars.TryGetValue(car, out var layers)) return;
        layers.Remove(owner ?? "");
        if (layers.Count == 0) _cars.Remove(car);
    }

    /// <summary>Takes every layer of <paramref name="owner"/> off every car.</summary>
    public void RemoveOwner(string owner)
    {
        _old.Clear();
        foreach (var kv in _cars)
        {
            kv.Value.Remove(owner ?? "");
            if (kv.Value.Count == 0) _old.Add(kv.Key);
        }
        foreach (var k in _old) _cars.Remove(k);
    }

    /// <summary>Every fresh layer of <paramref name="car"/> at <paramref name="now"/> multiplied together (none: an effect that
    /// changes nothing). Lapsed layers are forgotten.</summary>
    public CarUpgradeEffect For(long car, float now)
    {
        if (!_cars.TryGetValue(car, out var layers)) return CarUpgradeEffect.None;
        CarUpgradeEffect total = null;
        List<string> lapsed = null;
        // owners in a fixed order, so the product (and its rounding) is the same on every peer
        var owners = new List<string>(layers.Keys);
        owners.Sort(StringComparer.Ordinal);
        foreach (var o in owners)
        {
            var l = layers[o];
            if (!Fresh(l.At, now)) { (lapsed ??= new List<string>()).Add(o); continue; }
            total = total == null ? l.Effect.Copy() : total.Times(l.Effect);
        }
        if (lapsed != null)
        {
            foreach (var o in lapsed) layers.Remove(o);
            if (layers.Count == 0) _cars.Remove(car);
        }
        return total?.Clamped() ?? CarUpgradeEffect.None;
    }

    /// <summary>True when <paramref name="car"/> has a fresh layer at <paramref name="now"/>.</summary>
    public bool Has(long car, float now)
    {
        if (!_cars.TryGetValue(car, out var layers)) return false;
        foreach (var l in layers.Values)
            if (Fresh(l.At, now)) return true;
        return false;
    }

    /// <summary>The cars that have a fresh layer at <paramref name="now"/> (lapsed cars are forgotten).</summary>
    public List<long> Cars(float now)
    {
        var list = new List<long>();
        _old.Clear();
        foreach (var kv in _cars)
        {
            bool any = false;
            foreach (var l in kv.Value.Values)
                if (Fresh(l.At, now)) { any = true; break; }
            if (any) list.Add(kv.Key);
            else _old.Add(kv.Key);
        }
        foreach (var k in _old) _cars.Remove(k);
        list.Sort();
        return list;
    }

    public void Clear() => _cars.Clear();
}
