using System;
using System.Collections.Generic;

namespace OverTheHillPlus.Shared;

/// <summary>
/// One grip writer per wheel. A wheel's grip is the game's per-wheel friction multiplier
/// (<c>VehicleBase.SetWheelTireFrictionMultiplier</c>), and a value written there replaces the last one, so two mods writing
/// it every physics step would fight. This is where they meet (mods never reference each other):
/// - Terrain+ works out what the ground does to each wheel (fresh deep snow, packed ruts, mud, sand, ice) and publishes it
///   here as a factor (<see cref="Set"/>), every physics step.
/// - The mod that writes the wheel's grip multiplies that factor in. Tyre Pressure does, while it tunes the car, and says so
///   (<see cref="Claim"/>) each step it writes a wheel. A vehicle without tyres (the Ermine snowmobile, core's
///   VehicleTraits) is never Tyre Pressure's: its own mod is the writer of its hidden wheels, the same way (it claims them,
///   multiplies its ski-and-track grip for the ground by Terrain+'s factor and publishes its <see cref="SetFlotation"/>).
/// - Terrain+ writes a wheel itself only while nobody claimed it lately (<see cref="ClaimedByOther"/>), so for each wheel
///   there is exactly one writer at a time. Taking a wheel back waits until the claim is stale (about five physics steps);
///   in between Tyre Pressure's last write (with the factor in) stands, and the first write after it puts the current one in.
/// - Tyre Wear+ publishes how much grip each wheel's tread has left (<see cref="SetWear"/>); <see cref="Factor"/> multiplies it in
///   with the ground's, so every writer that already uses the factor gets the wear too. It writes a wheel only while nobody else
///   claimed it (as Terrain+ does), claiming it under its own name. That claim is a soft one (<see cref="ClaimSoft"/>): Terrain+
///   still sees the wheel as written, but the owner-aware check ignores it, so a car's own tyre writer (the Meerkat) can take over.
/// A factor or claim that was not refreshed for <see cref="StaleAfter"/> seconds of physics time counts as gone (the mod was
/// switched off, the car despawned), so nothing sticks. Times are physics time (Time.fixedTime in the step). Main thread
/// only; no game types (unit-tested).
/// </summary>
internal static class GroundGrip
{
    /// <summary>A factor or claim older than this (seconds of physics time) is ignored: about five physics steps.</summary>
    public const float StaleAfter = 0.1f;

    /// <summary>The factor never leaves this range, whatever a caller passes.</summary>
    public const float MinFactor = 0.3f, MaxFactor = 1.5f;

    /// <summary>A tyre's wear never takes more than this off the grip, whatever a caller passes.</summary>
    public const float MinWear = 0.5f;

    /// <summary>Wheels per car this keeps (more are left alone: factor 1, never claimed).</summary>
    public const int MaxWheels = 8;

    private sealed class Car
    {
        public readonly float[] Factor = new float[MaxWheels];
        public readonly float[] FactorAt = new float[MaxWheels];
        public readonly float[] Wear = new float[MaxWheels];
        public readonly float[] WearAt = new float[MaxWheels];
        public readonly float[] ClaimAt = new float[MaxWheels];
        public readonly float[] SoftAt = new float[MaxWheels];
        public readonly string[] ClaimBy = new string[MaxWheels];
        public float Touched;
        public float Flotation, FlotationAt = float.NegativeInfinity;

        public Car()
        {
            for (int i = 0; i < MaxWheels; i++)
            {
                Factor[i] = 1f;
                Wear[i] = 1f;
                FactorAt[i] = WearAt[i] = ClaimAt[i] = SoftAt[i] = float.NegativeInfinity;
            }
        }
    }

    private static readonly Dictionary<IntPtr, Car> Cars = new();
    private static readonly List<IntPtr> Old = new();
    private static float _nextPrune;

    /// <summary>How many cars have a factor or a claim (for the log).</summary>
    public static int Count => Cars.Count;

    private static bool Fresh(float at, float now) => now - at <= StaleAfter && now >= at - 1f;

    private static Car Get(IntPtr car, float now, bool create)
    {
        if (car == IntPtr.Zero) return null;
        if (!Cars.TryGetValue(car, out var c))
        {
            if (!create) return null;
            Prune(now);
            Cars[car] = c = new Car();
        }
        c.Touched = now;
        return c;
    }

    /// <summary>The ground's grip factor for one wheel at <paramref name="now"/> (clamped to the range; NaN reads as 1).</summary>
    public static void Set(IntPtr car, int wheel, float factor, float now)
    {
        if (wheel < 0 || wheel >= MaxWheels || !float.IsFinite(now)) return;
        var c = Get(car, now, true);
        if (c == null) return;
        c.Factor[wheel] = float.IsFinite(factor) ? Math.Max(MinFactor, Math.Min(MaxFactor, factor)) : 1f;
        c.FactorAt[wheel] = now;
    }

    /// <summary>What is left of the wheel's grip with its tread worn (1 = new), at <paramref name="now"/>; clamped to <see cref="MinWear"/> to 1.</summary>
    public static void SetWear(IntPtr car, int wheel, float left, float now)
    {
        if (wheel < 0 || wheel >= MaxWheels || !float.IsFinite(now)) return;
        var c = Get(car, now, true);
        if (c == null) return;
        c.Wear[wheel] = float.IsFinite(left) ? Math.Max(MinWear, Math.Min(1f, left)) : 1f;
        c.WearAt[wheel] = now;
    }

    /// <summary>The factor for the wheel to multiply into its grip: the ground's times the tread's wear, each 1 when none is fresh.</summary>
    public static float Factor(IntPtr car, int wheel, float now)
    {
        if (wheel < 0 || wheel >= MaxWheels || !Cars.TryGetValue(car, out var c)) return 1f;
        float f = Fresh(c.FactorAt[wheel], now) ? c.Factor[wheel] : 1f;
        return Fresh(c.WearAt[wheel], now) ? f * c.Wear[wheel] : f;
    }

    /// <summary>A grip writer other than the ground's own (Tyre Pressure) wrote this wheel at <paramref name="now"/>, with <see cref="Factor"/> in it.</summary>
    public static void Claim(IntPtr car, int wheel, float now) => Claim(car, wheel, now, "");

    /// <summary>Like <see cref="Claim(IntPtr, int, float)"/>, naming the writer, so a writer that steps back for others
    /// can tell its own claim from theirs (<see cref="ClaimedByOther(IntPtr, int, float, string)"/>).</summary>
    public static void Claim(IntPtr car, int wheel, float now, string owner)
    {
        if (wheel < 0 || wheel >= MaxWheels || !float.IsFinite(now)) return;
        var c = Get(car, now, true);
        if (c == null) return;
        c.ClaimAt[wheel] = now;
        c.ClaimBy[wheel] = owner ?? "";
    }

    /// <summary>
    /// A writer that steps back for any other writer wrote this wheel at <paramref name="now"/> (Tyre Wear+). Only the plain
    /// <see cref="ClaimedByOther(IntPtr, int, float)"/> (Terrain+) counts it; the owner-aware overload never does.
    /// </summary>
    public static void ClaimSoft(IntPtr car, int wheel, float now)
    {
        if (wheel < 0 || wheel >= MaxWheels || !float.IsFinite(now)) return;
        var c = Get(car, now, true);
        if (c != null) c.SoftAt[wheel] = now;
    }

    /// <summary>True while a writer other than <paramref name="me"/> keeps the wheel (its claim is fresh): a car's own
    /// tyre writer (the Meerkat's street and mud tyres) writes only while nobody else (Tyre Pressure) does.</summary>
    public static bool ClaimedByOther(IntPtr car, int wheel, float now, string me)
    {
        if (wheel < 0 || wheel >= MaxWheels || !Cars.TryGetValue(car, out var c)) return false;
        return Fresh(c.ClaimAt[wheel], now) && !string.Equals(c.ClaimBy[wheel] ?? "", me ?? "", StringComparison.Ordinal);
    }

    /// <summary>True while another writer keeps the wheel (it claimed it, softly or not, within <see cref="StaleAfter"/>).</summary>
    public static bool ClaimedByOther(IntPtr car, int wheel, float now)
    {
        if (wheel < 0 || wheel >= MaxWheels || !Cars.TryGetValue(car, out var c)) return false;
        return Fresh(c.ClaimAt[wheel], now) || Fresh(c.SoftAt[wheel], now);
    }

    /// <summary>
    /// How much the vehicle floats on soft ground, 0 (a tyre: sinks in) to 1 (rides on top), published every physics step
    /// by a vehicle mod whose ground contact isn't tyres (the Ermine's skis and wide track). Terrain+ takes that share off
    /// its sinkage and bulldozing drag. Clamped to 0..1; NaN reads as 0.
    /// </summary>
    public static void SetFlotation(IntPtr car, float flotation, float now)
    {
        if (!float.IsFinite(now)) return;
        var c = Get(car, now, true);
        if (c == null) return;
        c.Flotation = float.IsFinite(flotation) ? Math.Max(0f, Math.Min(1f, flotation)) : 0f;
        c.FlotationAt = now;
    }

    /// <summary>The vehicle's flotation (<see cref="SetFlotation"/>), or 0 (a tyre) when none is fresh.</summary>
    public static float Flotation(IntPtr car, float now)
    {
        if (!Cars.TryGetValue(car, out var c)) return 0f;
        return Fresh(c.FlotationAt, now) ? c.Flotation : 0f;
    }

    /// <summary>Drops the car's factors (Terrain+ stopped for it): the wheels read 1 at once.</summary>
    public static void Forget(IntPtr car) => Cars.Remove(car);

    public static void Clear() => Cars.Clear();

    /// <summary>Every few seconds: forgets cars nothing touched for a while (despawned, pointers can be reused).</summary>
    private static void Prune(float now)
    {
        if (now < _nextPrune && now >= _nextPrune - 10f) return;
        _nextPrune = now + 5f;
        Old.Clear();
        foreach (var kv in Cars)
            if (now - kv.Value.Touched > 2f || now < kv.Value.Touched - 1f) Old.Add(kv.Key);
        foreach (var k in Old) Cars.Remove(k);
    }
}
