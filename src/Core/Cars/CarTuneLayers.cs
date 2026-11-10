using System;
using UnityEngine;

namespace OverTheHillPlus.Cars;

/// <summary>
/// Upgrade layers on cars in the scene (see <see cref="TuneLayerBook"/>): a mod puts an effect on a car about once a
/// second (the career's bought upgrades), and core's tuners multiply it into what they write: <see cref="CarTuning"/> for
/// modded cars, Realistic Handling for the stock cars it tunes, and <see cref="CarLayerTuner"/> for every other stock car.
/// So upgrades work on stock and modded cars alike and no two mods write the same value. Whoever sets a layer decides
/// where it runs: the host for every car it simulates, a guest only for its own car (its prediction), with the numbers the
/// host accepted. Main thread only.
/// </summary>
internal static class CarTuneLayers
{
    private static readonly TuneLayerBook Book = new();

    /// <summary>Puts <paramref name="owner"/>'s layer on a car (renew it about once a second; it lapses after 5 s).
    /// An effect that changes nothing takes it off.</summary>
    internal static bool Set(string owner, IntPtr car, CarUpgradeEffect effect)
    {
        if (car == IntPtr.Zero) return false;
        if (effect != null && !effect.IsNone) CarLayerTuner.EnsureTicking();
        return Book.Set(owner, car.ToInt64(), effect, Time.unscaledTime);
    }

    internal static void Remove(string owner, IntPtr car) => Book.Remove(owner, car.ToInt64());

    internal static void RemoveOwner(string owner) => Book.RemoveOwner(owner);

    /// <summary>Every fresh layer of the car together (an effect that changes nothing when it has none).</summary>
    internal static CarUpgradeEffect For(IntPtr car) =>
        car == IntPtr.Zero ? CarUpgradeEffect.None : Book.For(car.ToInt64(), Time.unscaledTime);

    internal static bool Has(IntPtr car) => car != IntPtr.Zero && Book.Has(car.ToInt64(), Time.unscaledTime);

    /// <summary>The cars with a fresh layer.</summary>
    internal static System.Collections.Generic.List<long> Cars() => Book.Cars(Time.unscaledTime);

    /// <summary>Cars another multiplier tuner (Realistic Handling) tunes this second, with the layers in: core's stock tuner
    /// leaves them alone. Renewed by that tuner about once a second.</summary>
    private static readonly ClaimBook Tuned = new();

    /// <summary>Realistic Handling (or any mod that tunes stock cars by multiplier): it tunes this car now and multiplies the
    /// layers in itself (<see cref="For"/>). Renew about once a second.</summary>
    internal static void TunedElsewhere(IntPtr car) => Tuned.Hold(car.ToInt64(), Time.unscaledTime);

    internal static bool IsTunedElsewhere(IntPtr car) => Tuned.IsHeld(car.ToInt64(), Time.unscaledTime);
}
