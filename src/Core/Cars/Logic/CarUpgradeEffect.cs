using System;
using System.Globalization;

namespace OverTheHillPlus.Cars;

/// <summary>
/// What upgrades do to one car on top of its own numbers (the game's, or a modded car's own tuning): multipliers, 1 = no
/// change. A career's engine stages, turbo, gearing, lift, brakes, winch and tank all end up as one of these per car
/// (<see cref="Times"/> combines them), and core's tuning puts it on the car where this game simulates it
/// (<see cref="TuneLayerBook"/>). The same numbers on the host and the guest whose car it is, so the guest's prediction
/// matches the host. Pure code (unit-tested).
/// </summary>
public sealed class CarUpgradeEffect
{
    /// <summary>No multiplier goes below or above these, whatever was combined.</summary>
    public const float Min = 0.25f, Max = 3f;

    /// <summary>All engine torque: idle, peak and the torque cap.</summary>
    public float Torque = 1f;
    /// <summary>Only the top end (peak torque and the cap, not idle): what a turbo adds once it is on boost.</summary>
    public float PeakTorque = 1f;
    /// <summary>The engine's power cap.</summary>
    public float Power = 1f;
    /// <summary>Peak, max and limiter rpm (idle stays).</summary>
    public float Revs = 1f;
    /// <summary>The speed limiter's limit.</summary>
    public float TopSpeed = 1f;
    /// <summary>The final drive: above 1 shorter (more pull, less top speed), below 1 taller.</summary>
    public float FinalDrive = 1f;
    /// <summary>Every forward gear ratio.</summary>
    public float Gears = 1f;
    /// <summary>The car's weight.</summary>
    public float Mass = 1f;
    /// <summary>Suspension travel of every wheel (a lift kit).</summary>
    public float Suspension = 1f;
    /// <summary>Brake and handbrake torque.</summary>
    public float Brakes = 1f;
    /// <summary>The winch's pull (its max and stall tension) and reel speed.</summary>
    public float Winch = 1f;
    /// <summary>The fuel tank's size (read by a fuel mod).</summary>
    public float FuelTank = 1f;

    /// <summary>A new effect that changes nothing.</summary>
    public static CarUpgradeEffect None => new();

    private const float Eps = 1e-4f;

    /// <summary>True when every multiplier is 1 (nothing to apply).</summary>
    public bool IsNone =>
        Near1(Torque) && Near1(PeakTorque) && Near1(Power) && Near1(Revs) && Near1(TopSpeed) && Near1(FinalDrive) && Near1(Gears) &&
        Near1(Mass) && Near1(Suspension) && Near1(Brakes) && Near1(Winch) && Near1(FuelTank);

    /// <summary>True when the effect touches the engine, drivetrain or weight (the values core's tuning writes).</summary>
    public bool TouchesDrive =>
        !(Near1(Torque) && Near1(PeakTorque) && Near1(Power) && Near1(Revs) && Near1(TopSpeed) && Near1(FinalDrive) && Near1(Gears) && Near1(Mass));

    private static bool Near1(float v) => Math.Abs(v - 1f) <= Eps;

    /// <summary>This effect and <paramref name="other"/> together (each multiplier multiplied), clamped.</summary>
    public CarUpgradeEffect Times(CarUpgradeEffect other)
    {
        if (other == null) return Clamped();
        return new CarUpgradeEffect
        {
            Torque = Torque * other.Torque,
            PeakTorque = PeakTorque * other.PeakTorque,
            Power = Power * other.Power,
            Revs = Revs * other.Revs,
            TopSpeed = TopSpeed * other.TopSpeed,
            FinalDrive = FinalDrive * other.FinalDrive,
            Gears = Gears * other.Gears,
            Mass = Mass * other.Mass,
            Suspension = Suspension * other.Suspension,
            Brakes = Brakes * other.Brakes,
            Winch = Winch * other.Winch,
            FuelTank = FuelTank * other.FuelTank,
        }.Clamped();
    }

    /// <summary>A copy with every multiplier finite and inside [<see cref="Min"/>, <see cref="Max"/>] (a non-finite one becomes 1).</summary>
    public CarUpgradeEffect Clamped() => new()
    {
        Torque = C(Torque), PeakTorque = C(PeakTorque), Power = C(Power), Revs = C(Revs), TopSpeed = C(TopSpeed),
        FinalDrive = C(FinalDrive), Gears = C(Gears), Mass = C(Mass), Suspension = C(Suspension), Brakes = C(Brakes),
        Winch = C(Winch), FuelTank = C(FuelTank),
    };

    /// <summary>A copy of every value as it is.</summary>
    public CarUpgradeEffect Copy() => (CarUpgradeEffect)MemberwiseClone();

    private static float C(float m) => float.IsNaN(m) || float.IsInfinity(m) ? 1f : Math.Clamp(m, Min, Max);

    /// <summary>True when both have the same multipliers (within a small tolerance).</summary>
    public bool SameAs(CarUpgradeEffect o)
    {
        if (o == null) return IsNone;
        return Same(Torque, o.Torque) && Same(PeakTorque, o.PeakTorque) && Same(Power, o.Power) && Same(Revs, o.Revs) &&
               Same(TopSpeed, o.TopSpeed) && Same(FinalDrive, o.FinalDrive) && Same(Gears, o.Gears) && Same(Mass, o.Mass) &&
               Same(Suspension, o.Suspension) && Same(Brakes, o.Brakes) && Same(Winch, o.Winch) && Same(FuelTank, o.FuelTank);
    }

    private static bool Same(float a, float b) => Math.Abs(a - b) <= Eps;

    /// <summary>A short line for the log (only the multipliers that aren't 1, invariant culture); "none" when nothing changes.</summary>
    public string Describe()
    {
        var sb = new System.Text.StringBuilder();
        void Add(string name, float v)
        {
            if (Near1(v)) return;
            if (sb.Length > 0) sb.Append(' ');
            sb.Append(name).Append('x').Append(v.ToString("0.###", CultureInfo.InvariantCulture));
        }
        Add("torque", Torque); Add("peak", PeakTorque); Add("power", Power); Add("revs", Revs); Add("top", TopSpeed);
        Add("final", FinalDrive); Add("gears", Gears); Add("mass", Mass); Add("lift", Suspension); Add("brakes", Brakes);
        Add("winch", Winch); Add("tank", FuelTank);
        return sb.Length == 0 ? "none" : sb.ToString();
    }

    /// <summary>The multipliers in a fixed order (for the wire and the tests).</summary>
    public float[] ToArray() => new[] { Torque, PeakTorque, Power, Revs, TopSpeed, FinalDrive, Gears, Mass, Suspension, Brakes, Winch, FuelTank };

    /// <summary>The number of values <see cref="ToArray"/> gives.</summary>
    public const int Count = 12;

    /// <summary>An effect from <see cref="ToArray"/>'s order (missing values are 1, extra ones ignored), clamped.</summary>
    public static CarUpgradeEffect FromArray(float[] v)
    {
        float At(int i) => v != null && i < v.Length ? v[i] : 1f;
        return new CarUpgradeEffect
        {
            Torque = At(0), PeakTorque = At(1), Power = At(2), Revs = At(3), TopSpeed = At(4), FinalDrive = At(5), Gears = At(6),
            Mass = At(7), Suspension = At(8), Brakes = At(9), Winch = At(10), FuelTank = At(11),
        }.Clamped();
    }
}
