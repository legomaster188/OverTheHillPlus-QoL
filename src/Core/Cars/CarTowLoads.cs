using System;
using UnityEngine;

namespace OverTheHillPlus.Cars;

/// <summary>
/// The trailer weight each car pulls right now. The Trailers mod reports it (about twice a second, on every peer that has
/// the trailer); a car with a tow rating asks for it (<see cref="TowRules"/>): the modded cars through their tuning, stock
/// cars through the Realistic Handling mod. Keyed by the car's native pointer, so nothing is kept for a car that is gone.
/// </summary>
internal static class CarTowLoads
{
    private static readonly TowLoadBook Book = new();

    /// <summary>The car pulls <paramref name="kg"/> of trailer.</summary>
    internal static void Report(IntPtr car, float kg) => Book.Report(car.ToInt64(), kg, Time.unscaledTime);

    /// <summary>The trailer weight the car pulls now, kg (0: none).</summary>
    internal static float Of(IntPtr car) => Book.Load(car.ToInt64(), Time.unscaledTime);
}
