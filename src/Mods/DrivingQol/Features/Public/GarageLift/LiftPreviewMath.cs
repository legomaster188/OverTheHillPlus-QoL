using System;
using System.Collections.Generic;
using System.Globalization;

namespace OverTheHillPlus.Features.Public.GarageLift;

/// <summary>
/// The numbers behind the garage's suspension lift preview. Pure code (unit-tested); no game types.
///
/// How the game lifts a car: <c>SuspensionLiftUpgradeEffect.ApplySuspensionTravel</c> sets every wheel's travel to the
/// CarInfo's own base travel plus the stage's <c>suspensionModifier</c> (metres; the stages are +0.08 and +0.16 in the
/// game's data). The wheel stays on the ground and the spring's sag under the car's weight does not change, so the
/// body rides higher by exactly the change in travel. A modded car's CarInfo holds its own base travel
/// (<c>CarUpgradeFit.LiftBaseTravel</c>) and the stage adds its modifier to that, so the height a stage adds is the
/// stage's modifier on every car, stock or modded. The garage model shows the difference between the highlighted
/// stage and the fitted one by raising or lowering its body (not its wheels) by that much.
/// </summary>
public static class LiftPreviewMath
{
    /// <summary>The largest lift taken as real, in metres. A bigger number is a misread value, not a stage.</summary>
    public const float MaxLift = 0.6f;

    /// <summary>A height in metres that is safe to move a model by: 0 for NaN or infinity, otherwise clamped to
    /// <see cref="MaxLift"/> either way.</summary>
    public static float Sane(float metres)
    {
        if (float.IsNaN(metres) || float.IsInfinity(metres)) return 0f;
        return Math.Clamp(metres, -MaxLift, MaxLift);
    }

    /// <summary>The travel a wheel gets with a stage fitted: the car's base travel plus the stage's modifier (what the
    /// game's effect sets). NaN when the base is not a positive number.</summary>
    public static float StageTravel(float baseTravel, float modifier)
    {
        if (float.IsNaN(baseTravel) || float.IsInfinity(baseTravel) || baseTravel <= 0f) return float.NaN;
        return baseTravel + Sane(modifier);
    }

    /// <summary>The lift of what is fitted: the largest modifier among the suspension stages on the car (a car carries
    /// one stage; none fitted is 0).</summary>
    public static float Fitted(IEnumerable<float> fittedModifiers)
    {
        float best = 0f;
        if (fittedModifiers == null) return best;
        foreach (var m in fittedModifiers)
        {
            float s = Sane(m);
            if (s > best) best = s;
        }
        return best;
    }

    /// <summary>A stage's total lift over stock: the sum of the lift effects on the part (a stage has one).</summary>
    public static float StageLift(IEnumerable<float> effectModifiers)
    {
        float sum = 0f;
        if (effectModifiers == null) return sum;
        foreach (var m in effectModifiers) sum += Sane(m);
        return Sane(sum);
    }

    /// <summary>How much higher the body rides on the highlighted stage than on the fitted one, in metres (negative:
    /// lower). The wheel travel difference between the two.</summary>
    public static float RideDelta(float previewLift, float fittedLift) => Sane(Sane(previewLift) - Sane(fittedLift));

    /// <summary>Whole centimetres of a height in metres, rounded to the nearest (halves away from zero).</summary>
    public static int Centimetres(float metres) => (int)Math.Round(Sane(metres) * 100f, MidpointRounding.AwayFromZero);

    /// <summary>"+8 cm", "-8 cm" or "no change" for a height difference.</summary>
    public static string Label(float deltaMetres)
    {
        int cm = Centimetres(deltaMetres);
        if (cm == 0) return "no change";
        return (cm > 0 ? "+" : "-") + Math.Abs(cm).ToString(CultureInfo.InvariantCulture) + " cm";
    }

    /// <summary>The line under a suspension stage's description. <paramref name="previewLift"/> and
    /// <paramref name="fittedLift"/> are the lifts over stock of the highlighted and the fitted stage.</summary>
    public static string Line(float previewLift, float fittedLift)
    {
        float delta = RideDelta(previewLift, fittedLift);
        int cm = Centimetres(delta);
        int over = Centimetres(previewLift);
        if (cm == 0)
            return over == 0 ? "Ride height: stock" : "Ride height: " + Label(previewLift) + " over stock (fitted now)";
        string line = "Ride height: " + Label(delta);
        // Fitted stage already lifts the car: say where this one ends up compared with stock as well.
        if (Centimetres(fittedLift) != 0) line += " (" + (over == 0 ? "stock" : Label(previewLift) + " over stock") + ")";
        return line;
    }
}

/// <summary>
/// A critically damped ease for the garage car's ride height: a short, smooth rise or drop that keeps its speed when the
/// target changes midway (scrolling quickly through the stages), never overshooting from rest. Pure code.
/// </summary>
public sealed class LiftEase
{
    /// <summary>Settling time of the default ease, seconds (95 % of a step).</summary>
    public const float DefaultSeconds = 0.35f;
    /// <summary>Kept in range so a bad setting still moves the car and never snaps or crawls.</summary>
    public const float MinSeconds = 0.05f, MaxSeconds = 2f;

    private float _velocity;

    /// <summary>The height now, metres.</summary>
    public float Value { get; private set; }

    /// <summary>The ease starts at rest at <paramref name="value"/>.</summary>
    public LiftEase(float value = 0f) { Value = LiftPreviewMath.Sane(value); }

    /// <summary>Puts the height at <paramref name="value"/> at once, at rest.</summary>
    public void Jump(float value)
    {
        Value = LiftPreviewMath.Sane(value);
        _velocity = 0f;
    }

    /// <summary>The smoothing time that settles 95 % of a step in <paramref name="settleSeconds"/>.</summary>
    public static float SmoothTime(float settleSeconds)
    {
        if (float.IsNaN(settleSeconds)) settleSeconds = DefaultSeconds;
        return Math.Clamp(settleSeconds, MinSeconds, MaxSeconds) / 2.37f;
    }

    /// <summary>True while the car is still moving or off its target.</summary>
    public bool Moving(float target) => Math.Abs(Value - LiftPreviewMath.Sane(target)) > Rest || Math.Abs(_velocity) > RestSpeed;

    private const float Rest = 0.0004f;       // 0.4 mm
    private const float RestSpeed = 0.004f;   // 4 mm/s

    /// <summary>Moves toward <paramref name="target"/> by <paramref name="dt"/> seconds (the same critically damped
    /// spring Unity's SmoothDamp uses). Returns the new height. A zero or bad step changes nothing.</summary>
    public float Step(float target, float dt, float settleSeconds = DefaultSeconds)
    {
        target = LiftPreviewMath.Sane(target);
        if (float.IsNaN(dt) || float.IsInfinity(dt) || dt <= 0f) return Value;
        dt = Math.Min(dt, 0.1f); // a hitch must not throw the car
        float smooth = SmoothTime(settleSeconds);
        float omega = 2f / smooth;
        float x = omega * dt;
        float exp = 1f / (1f + x + 0.48f * x * x + 0.235f * x * x * x);
        float change = Value - target;
        float temp = (_velocity + omega * change) * dt;
        _velocity = (_velocity - omega * temp) * exp;
        Value = target + (change + temp) * exp;
        if (!Moving(target))
        {
            // Close enough and slow enough: sit exactly on it.
            Value = target;
            _velocity = 0f;
        }
        return Value;
    }
}
