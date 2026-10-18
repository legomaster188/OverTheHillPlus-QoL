using System;

namespace OverTheHillPlus.Cars;

/// <summary>
/// How a modded car drives compared with its donor's physics skeleton: multipliers on the donor's / the game's own values
/// (1 = as the donor). Pure code (unit-tested); the same numbers run on the host and on every modded guest, so
/// prediction agrees with the host.
/// </summary>
public sealed class CarTuningProfile
{
    public const float MinMultiplier = 0.25f;
    public const float MaxMultiplier = 4f;

    public float Mass = 1f;
    /// <summary>Metres the centre of mass moves down (negative: up).</summary>
    public float CentreOfMassDrop;
    public float Torque = 1f;
    /// <summary>Peak / max / limiter rpm (idle stays).</summary>
    public float RevRange = 1f;
    /// <summary>Every forward gear ratio (lower = taller gearing).</summary>
    public float GearRatios = 1f;
    public float FinalDrive = 1f;
    public float SuspensionTravel = 1f;
    public float SpringRate = 1f;
    public float DamperRate = 1f;
    public float TyreGrip = 1f;
    public float SteeringLock = 1f;

    /// <summary>A copy with every multiplier finite and inside [<see cref="MinMultiplier"/>, <see cref="MaxMultiplier"/>]
    /// (a non-finite one becomes 1) and the centre-of-mass drop inside ±0.5 m.</summary>
    public CarTuningProfile Clamped() => new()
    {
        Mass = C(Mass), Torque = C(Torque), RevRange = C(RevRange), GearRatios = C(GearRatios), FinalDrive = C(FinalDrive),
        SuspensionTravel = C(SuspensionTravel), SpringRate = C(SpringRate), DamperRate = C(DamperRate), TyreGrip = C(TyreGrip),
        SteeringLock = C(SteeringLock),
        CentreOfMassDrop = float.IsNaN(CentreOfMassDrop) || float.IsInfinity(CentreOfMassDrop) ? 0f : Math.Clamp(CentreOfMassDrop, -0.5f, 0.5f),
    };

    /// <summary>A copy of every value as it is.</summary>
    public CarTuningProfile Copy() => (CarTuningProfile)MemberwiseClone();

    private static float C(float m) => float.IsNaN(m) || float.IsInfinity(m) ? 1f : Math.Clamp(m, MinMultiplier, MaxMultiplier);

    /// <summary>max(base × mul, min); the base itself when the multiplier isn't a finite number.</summary>
    public static float Scale(float baseValue, float mul, float min = float.MinValue)
    {
        if (float.IsNaN(mul) || float.IsInfinity(mul)) return baseValue;
        return Math.Max(baseValue * mul, min);
    }

    /// <summary>A short line for the log (every value, invariant culture).</summary>
    public string Describe() => string.Format(System.Globalization.CultureInfo.InvariantCulture,
        "m{0:0.###} com{1:0.###} t{2:0.###} rev{3:0.###} g{4:0.###} fd{5:0.###} st{6:0.###} k{7:0.###} c{8:0.###} grip{9:0.###} steer{10:0.###}",
        Mass, CentreOfMassDrop, Torque, RevRange, GearRatios, FinalDrive, SuspensionTravel, SpringRate, DamperRate, TyreGrip, SteeringLock);
}
