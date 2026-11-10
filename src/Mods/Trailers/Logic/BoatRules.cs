using System;

namespace OverTheHillPlus.Trailers;

/// <summary>
/// The boat trailer at a ramp, decided by the host (solo counts) from what it measures, and drawn by every peer from the
/// state it sends:
/// <list type="bullet">
/// <item><b>Floating off.</b> Back the loaded trailer down a ramp until its bunks are deep enough for the boat to float
///   (its draft and a hand more over the keel) and stand still a moment: the boat slides off astern and floats there,
///   moored, while you pull the empty trailer out and park it. Take the boat from the garage or its Drive button.</item>
/// <item><b>Winching on.</b> Back the empty trailer in again beside a moored boat (its bunks within a few metres of the
///   boat and deep enough) and stand still: the boat is winched up onto the bunks; drive out with it.</item>
/// </list>
/// Only a boat a player can drive (a boat mod's) comes off; the trailer's own runabout is only for show. Pure (unit-tested).
/// </summary>
public static class BoatRules
{
    /// <summary>The boat floats off once the water over its keel on the bunks is its floating draft plus this, m.</summary>
    public const float FloatMargin = 0.10f;
    /// <summary>A moored boat is winched on once the water at the bunks is its draft plus this, m (a boat floats onto the
    /// bunks a little shallower than it floats off them: the winch pulls its bow up the rollers).</summary>
    public const float WinchMargin = 0.04f;
    /// <summary>Slower than this (m/s) for <see cref="StillFor"/> seconds counts as standing still.</summary>
    public const float StillSpeed = 0.6f, StillFor = 1.0f;
    /// <summary>How near the boat's root must be to its place on the bunks to be winched on, m.</summary>
    public const float WinchReach = 5.5f;
    /// <summary>How far astern of its place on the bunks a boat that floats off comes to rest, m: a little more than half its
    /// length beyond the trailer's tail.</summary>
    public const float Drift = 3.6f;
    /// <summary>How long the boat takes to slide off or be winched on (every peer draws it), s.</summary>
    public const float SlideSeconds = 4f;
    /// <summary>The owner changed car while their trailer still carries a boat they can drive: the boat mod has this long to
    /// say the boat came off (the player took it out from the trailer) before the trailer goes as any other would, s.</summary>
    public const float CarChangeGrace = 6f;

    /// <summary>A boat floats off only at a boat ramp (a map's "boat_ramp" place this near the bunks, m): not in a ford
    /// the rig stops in. Anywhere else a boat goes in from its trailer when its driver takes it (the boat mod's rule).</summary>
    public const float RampReach = 40f;

    /// <summary>Does the loaded boat float off now? <paramref name="atRamp"/>: a boat ramp within <see cref="RampReach"/>;
    /// <paramref name="depth"/>: the water's height over the boat's keel on the bunks (m; NaN: no water there);
    /// <paramref name="still"/>: how long the trailer has stood still, s.</summary>
    public static bool FloatsOff(TrailerState s, bool atRamp, float depth, float draft, float speed, float still) =>
        atRamp && s != null && s.Kind == TrailerKind.Boat && s.CargoCarId != 0 && s.Boat == BoatPlace.OnTrailer && s.Hitched &&
        float.IsFinite(depth) && depth >= Math.Max(0.05f, draft) + FloatMargin && speed < StillSpeed && still >= StillFor;

    /// <summary>Is the moored boat winched on now? <paramref name="distance"/>: from its root to its place on the bunks, m
    /// (on the ground plane); <paramref name="depth"/>: the water at the bunks over where the keel would sit, m.</summary>
    public static bool WinchesOn(TrailerState s, float distance, float depth, float draft, float speed, float still) =>
        s != null && s.Kind == TrailerKind.Boat && s.Boat == BoatPlace.Afloat && s.Hitched &&
        float.IsFinite(distance) && distance <= WinchReach && float.IsFinite(depth) && depth >= Math.Max(0.05f, draft) + WinchMargin &&
        speed < StillSpeed && still >= StillFor;

    /// <summary>Where a boat that floats off comes to rest: <see cref="Drift"/> astern of its place on the bunks (the trailer
    /// points its hitch up the ramp, at <paramref name="yaw"/> degrees; 0 = +z).</summary>
    public static (float X, float Z) DriftSpot(float seatX, float seatZ, float yaw)
    {
        float a = yaw * MathF.PI / 180f;
        return (seatX - MathF.Sin(a) * Drift, seatZ - MathF.Cos(a) * Drift);
    }

    /// <summary>The trailer's mass: the spec's, a car hauler's load on top, a boat trailer's boat off it while it is out.</summary>
    public static float Mass(TrailerSpec spec, TrailerState s)
    {
        if (spec == null) return 0f;
        float m = spec.Mass;
        if (s == null) return m;
        if (spec.Kind == TrailerKind.Hauler && s.CargoCarId != 0) m += spec.CargoMass;
        if (spec.Kind == TrailerKind.Boat && s.Boat != BoatPlace.OnTrailer) m -= spec.LoadMass;
        return Math.Max(m, 50f);
    }

    /// <summary>The trailer stays when its owner's car changes: its boat is out or moored (they went out in it, or back for
    /// the car), or it still carries a boat they can drive and the change is recent (<see cref="CarChangeGrace"/>).</summary>
    public static bool KeepThroughCarChange(TrailerState s, float changedFor)
    {
        if (s == null || s.Kind != TrailerKind.Boat || s.CargoCarId == 0) return false;
        if (s.Boat != BoatPlace.OnTrailer) return true;
        return float.IsFinite(changedFor) && changedFor < CarChangeGrace;
    }

    /// <summary>Smooth start and stop over 0..1.</summary>
    public static float Ease(float u)
    {
        if (!(u > 0f)) return 0f;
        if (u >= 1f) return 1f;
        return u * u * (3f - 2f * u);
    }

    /// <summary>A moored boat's slow movement on calm water at time <paramref name="t"/> (s): a centimetre or two of heave
    /// and under a degree of roll and pitch, not in step with any other boat (<paramref name="seed"/>).</summary>
    public static (float Heave, float Roll, float Pitch) Bob(float t, uint seed)
    {
        float p = (seed % 997) * 0.37f;
        return (0.014f * MathF.Sin(1.25f * t + p) + 0.006f * MathF.Sin(2.9f * t + 2f * p),
                0.8f * MathF.Sin(0.83f * t + p + 1f),
                0.45f * MathF.Sin(1.07f * t + 0.5f * p + 2f));
    }
}
