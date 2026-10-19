using System;
using UnityEngine;

namespace OverTheHillPlus.Cars;

/// <summary>
/// The cars another mod is tuning by hand right now (see <see cref="ClaimBook"/>). A mod that writes a car's engine, weight or
/// steering values calls <see cref="Hold"/> for each such car about once a second; <see cref="CarTuning"/> (the modded cars) and
/// Realistic Handling ask <see cref="IsHeld"/> and leave a held car alone.
/// </summary>
internal static class CarTuneClaims
{
    private static readonly ClaimBook Book = new();

    /// <summary>Another mod is tuning this car's values (renew about once a second).</summary>
    internal static void Hold(IntPtr car) => Book.Hold(car.ToInt64(), Time.unscaledTime);

    /// <summary>Whether another mod is tuning this car's values now.</summary>
    internal static bool IsHeld(IntPtr car) => Book.IsHeld(car.ToInt64(), Time.unscaledTime);
}
