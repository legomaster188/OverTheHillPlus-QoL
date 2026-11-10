using System;

namespace OverTheHillPlus.Ermine;

/// <summary>
/// How a ski sits on the ground under it, and what the front suspension does to follow it. The ski hangs from its spindle
/// on a bolt (pitch only) and the spindle rides up and down on two A-arms (the travel); every peer works its own skis out
/// from where the sled is and the ground under it (two rays per ski, at its tip and its tail), so they look right on other
/// players' sleds too and nothing is sent. Pure (unit-tested).
/// </summary>
public static class SkiMath
{
    /// <summary>How far the spindle may rise above its rest point (compression) or drop below it (droop), metres.</summary>
    public const float Up = 0.13f, Down = 0.09f;
    /// <summary>The ski's pitch on its bolt, degrees either way (the bolt's rubber stops).</summary>
    public const float MaxPitch = 22f;

    /// <summary>
    /// The ski's rise above its rest height (+ up) and its pitch (degrees, + nose up) from the ground heights under its tip
    /// and tail (in the sled's own up axis, relative to the ski's running surface at rest: 0 = where the ski's bottom would
    /// be at rest), <paramref name="span"/> metres apart. No ground under one end (a ray found nothing) leaves that end
    /// hanging at its full droop. The rise is the mean of the two ends, clamped to the travel; the pitch follows the
    /// slope between them, clamped to the stops.
    /// </summary>
    public static (float rise, float pitch) Pose(float? tipGround, float? tailGround, float span)
    {
        float tip = tipGround ?? -Down, tail = tailGround ?? -Down;
        if (!float.IsFinite(tip)) tip = -Down;
        if (!float.IsFinite(tail)) tail = -Down;
        float rise = Math.Max(-Down, Math.Min(Up, (tip + tail) * 0.5f));
        float pitch = span > 0.05f ? MathF.Atan2(tip - tail, span) * 180f / MathF.PI : 0f;
        pitch = Math.Max(-MaxPitch, Math.Min(MaxPitch, pitch));
        return (rise, pitch);
    }

    /// <summary>An A-arm's swing (degrees, + = its outer end up) for the spindle's rise, for an arm <paramref name="length"/>
    /// metres from its inner pivots to its ball joint.</summary>
    public static float ArmAngle(float rise, float length)
    {
        if (!(length > 0.05f) || !float.IsFinite(rise)) return 0f;
        float s = Math.Max(-0.95f, Math.Min(0.95f, rise / length));
        return MathF.Asin(s) * 180f / MathF.PI;
    }

    /// <summary>Exponential smoothing toward <paramref name="target"/> with time constant <paramref name="tau"/> seconds.</summary>
    public static float Ease(float current, float target, float dt, float tau)
    {
        if (!float.IsFinite(target)) return current;
        if (!float.IsFinite(current)) return target;
        if (!(dt > 0f)) return current;
        if (!(tau > 1e-4f)) return target;
        return current + (target - current) * (1f - MathF.Exp(-dt / tau));
    }

    /// <summary>
    /// The steering angle (degrees) of a sled seen from outside: from its yaw rate (degrees per second) and speed (m/s)
    /// on a wheelbase (metres), as a bicycle model gives it; 0 below walking pace, clamped to the skis' lock.
    /// </summary>
    public static float SteerFromYaw(float yawRateDeg, float speed, float wheelbase, float maxLock)
    {
        if (!float.IsFinite(yawRateDeg) || !float.IsFinite(speed) || Math.Abs(speed) < 1.0f || !(wheelbase > 0.1f)) return 0f;
        float r = yawRateDeg * MathF.PI / 180f;
        float a = MathF.Atan(wheelbase * r / Math.Abs(speed)) * 180f / MathF.PI * Math.Sign(speed);
        return Math.Max(-maxLock, Math.Min(maxLock, a));
    }

    /// <summary>
    /// The lean a rider puts in (an angular acceleration about the sled's length, rad/s²): nothing within
    /// <see cref="LeanFree"/> degrees of upright, then a spring back toward it and a damper on the roll rate. A sled's
    /// narrow track tips it in a hard turn; the rider's weight keeps it up, and so does this.
    /// </summary>
    public static float LeanTorque(float rollDeg, float rollRateDegS)
    {
        if (!float.IsFinite(rollDeg) || !float.IsFinite(rollRateDegS)) return 0f;
        float over = Math.Abs(rollDeg) - LeanFree;
        float spring = over > 0f ? -Math.Sign(rollDeg) * Math.Min(over, 30f) * LeanSpring : 0f;
        float damp = Math.Abs(rollDeg) > LeanFree * 0.5f ? -rollRateDegS * LeanDamp : 0f;
        float t = spring + damp;
        return Math.Max(-LeanMax, Math.Min(LeanMax, t));
    }

    public const float LeanFree = 6f, LeanSpring = 0.18f, LeanDamp = 0.02f, LeanMax = 4.5f;
}
