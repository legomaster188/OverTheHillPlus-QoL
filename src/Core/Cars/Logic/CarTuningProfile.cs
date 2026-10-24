using System;

namespace OverTheHillPlus.Cars;

/// <summary>
/// How a modded car drives compared with its donor's physics skeleton: multipliers on the donor's / the game's own values
/// (1 = as the donor). Pure code (unit-tested); the same numbers run on the host and on every modded guest, so
/// prediction agrees with the host.
/// </summary>
public sealed class CarTuningProfile
{
    /// <summary>Lowest / highest multiplier. A kei van's engine is a fifth of a 2.8 t donor's, so the floor is low.</summary>
    public const float MinMultiplier = 0.1f;
    public const float MaxMultiplier = 4f;
    /// <summary>Highest towing capacity a profile may name, kg (a loaded semi: 40 t).</summary>
    public const float MaxTowKg = 60000f;

    public float Mass = 1f;
    /// <summary>Metres the centre of mass moves down (negative: up).</summary>
    public float CentreOfMassDrop;
    public float Torque = 1f;
    /// <summary>The engine's power cap (the game's <c>powerCapLimit</c>, kW). Torque above the cap only helps below it, so a
    /// strong engine on a weak donor needs this too; a weak one on a strong donor is held down by it.</summary>
    public float Power = 1f;
    /// <summary>The speed limiter's limit (the game's <c>speedControl.speedLimit</c>, m/s): every donor has its own, far
    /// below what its real counterpart does.</summary>
    public float TopSpeed = 1f;
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
    /// <summary>What the real car may tow, kg (0: not rated, tows anything). A trailer heavier than this takes power and top speed
    /// off the car while it is hitched (<see cref="TowRules"/>).</summary>
    public float TowKg;

    /// <summary>A copy with every multiplier finite and inside [<see cref="MinMultiplier"/>, <see cref="MaxMultiplier"/>]
    /// (a non-finite one becomes 1) and the centre-of-mass drop inside ±0.5 m.</summary>
    public CarTuningProfile Clamped() => new()
    {
        Mass = C(Mass), Torque = C(Torque), RevRange = C(RevRange), GearRatios = C(GearRatios), FinalDrive = C(FinalDrive),
        SuspensionTravel = C(SuspensionTravel), SpringRate = C(SpringRate), DamperRate = C(DamperRate), TyreGrip = C(TyreGrip),
        SteeringLock = C(SteeringLock), Power = C(Power), TopSpeed = C(TopSpeed),
        TowKg = float.IsNaN(TowKg) || float.IsInfinity(TowKg) ? 0f : Math.Clamp(TowKg, 0f, MaxTowKg),
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
        "m{0:0.###} com{1:0.###} t{2:0.###} rev{3:0.###} g{4:0.###} fd{5:0.###} st{6:0.###} k{7:0.###} c{8:0.###} grip{9:0.###} steer{10:0.###} pw{11:0.###} top{12:0.###} tow{13:0.###}",
        Mass, CentreOfMassDrop, Torque, RevRange, GearRatios, FinalDrive, SuspensionTravel, SpringRate, DamperRate, TyreGrip, SteeringLock,
        Power, TopSpeed, TowKg);
}
