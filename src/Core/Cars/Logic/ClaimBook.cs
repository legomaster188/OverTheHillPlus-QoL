using System.Collections.Generic;

namespace OverTheHillPlus.Cars;

/// <summary>
/// Who else is tuning a car's engine and weight values right now. A <see cref="CarTuneSlot"/> takes any value it did not write
/// itself as the game's new value, so two tuners that multiply the same value would feed each other's results back in and grow
/// (or shrink) it without limit. A mod that sets such values by hand (a menu of free sliders) holds its cars here every second; the
/// mods that tune by multiplier (modded cars, Realistic Handling) stand back from a held car: they stop writing but keep their
/// slots, so when the other tuner puts the value back it still reads as theirs. A hold lapses by itself after
/// <see cref="HoldSeconds"/>. Pure code (unit-tested).
/// </summary>
public sealed class ClaimBook
{
    /// <summary>A hold is good for this long, seconds (the holders renew it every second or so).</summary>
    public const float HoldSeconds = 3f;

    private readonly Dictionary<long, float> _until = new();

    public int Count => _until.Count;

    /// <summary>The car is held until <paramref name="now"/> plus <see cref="HoldSeconds"/>.</summary>
    public void Hold(long car, float now)
    {
        if (car == 0 || !float.IsFinite(now)) return;
        _until[car] = now + HoldSeconds;
    }

    /// <summary>Whether the car is held at <paramref name="now"/> (a lapsed hold is forgotten).</summary>
    public bool IsHeld(long car, float now)
    {
        if (!_until.TryGetValue(car, out float until)) return false;
        // a clock that went back (a new session) lapses every hold it can no longer reach
        if (now > until || now < until - HoldSeconds - 1f) { _until.Remove(car); return false; }
        return true;
    }

    public void Release(long car) => _until.Remove(car);

    public void Clear() => _until.Clear();
}
