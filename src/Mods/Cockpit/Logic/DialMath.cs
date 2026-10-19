using System;

namespace OverTheHillPlus.Cockpit;

/// <summary>
/// The cockpit's gauge and wheel maths (no game types, unit tested): needle angles, dial scales, the steering wheel's
/// turn, and the seven-segment characters of its displays. Angles are degrees clockwise from 12 o'clock as seen by the
/// driver.
/// </summary>
internal static class DialMath
{
    /// <summary>Where a dial's scale starts (lower left) and how far it sweeps (to the lower right).</summary>
    public const float StartDeg = -135f, SweepDeg = 270f;

    private static readonly int[] SpeedMaxes = { 120, 160, 200, 240, 280, 320 };

    /// <summary>A round speedometer maximum above <paramref name="topSpeed"/> (in the shown unit).</summary>
    public static int SpeedMax(float topSpeed)
    {
        foreach (int m in SpeedMaxes)
            if (!(topSpeed > m)) return m;
        return SpeedMaxes[SpeedMaxes.Length - 1];
    }

    /// <summary>A tachometer maximum above the engine's peak RPM: 4000 to 9000, in thousands.</summary>
    public static int RpmMax(float peakRpm)
    {
        if (float.IsNaN(peakRpm)) return 6000;
        int k = (int)Math.Ceiling(peakRpm / 1000.0);
        return Math.Max(4, Math.Min(9, k)) * 1000;
    }

    /// <summary>Needle angle for a value on a scale of 0..max.</summary>
    public static float NeedleAngle(float value, float max)
    {
        if (!(max > 0f) || float.IsNaN(value)) return StartDeg;
        float t = value <= 0f ? 0f : value >= max ? 1f : value / max;
        return StartDeg + SweepDeg * t;
    }

    /// <summary>Angle of tick <paramref name="i"/> of a scale cut in <paramref name="count"/> steps (0 = start, count = end).</summary>
    public static float TickAngle(int i, int count) =>
        count <= 0 ? StartDeg : StartDeg + SweepDeg * Math.Max(0, Math.Min(count, i)) / count;

    /// <summary>The steering wheel's turn for a steering input of -1..1 and the turn at full lock (degrees, clockwise).</summary>
    public static float WheelAngle(float steer, float lockDeg)
    {
        if (float.IsNaN(steer)) return 0f;
        float s = steer < -1f ? -1f : steer > 1f ? 1f : steer;
        return s * Math.Max(0f, lockDeg);
    }

    /// <summary>Exponential smoothing toward a target, whatever the frame rate.</summary>
    public static float Smooth(float current, float target, float sharpness, float dt)
    {
        if (!(dt > 0f)) return current;
        if (sharpness <= 0f) return target;
        return current + (target - current) * (1f - (float)Math.Exp(-sharpness * dt));
    }

    /// <summary>Speed in the shown unit, rounded for the display.</summary>
    public static int ShownSpeed(float kmh, bool mph)
    {
        if (float.IsNaN(kmh) || kmh < 0f) return 0;
        return (int)Math.Round(mph ? kmh * 0.621371f : kmh);
    }

    /// <summary>The gear as one display character: R, N or the gear number (9 for anything above).</summary>
    public static char GearChar(int gear) => gear < 0 ? 'R' : gear == 0 ? 'N' : gear > 9 ? '9' : (char)('0' + gear);

    /// <summary>The characters of a right-aligned number in <paramref name="width"/> digits (blank where there is none; 9s if too big).</summary>
    public static string Digits(int value, int width)
    {
        if (width <= 0) return "";
        if (value < 0) value = 0;
        string s = value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (s.Length > width) return new string('9', width);
        return s.PadLeft(width, ' ');
    }

    // Seven-segment bits: a top, b top right, c bottom right, d bottom, e bottom left, f top left, g middle.
    private const int A = 1, B = 2, C = 4, D = 8, E = 16, F = 32, G = 64;

    /// <summary>The segments (bit 0 = a ... bit 6 = g) that show <paramref name="ch"/>; 0 (dark) for anything it can't draw.</summary>
    public static int Segments(char ch) => ch switch
    {
        '0' => A | B | C | D | E | F,
        '1' => B | C,
        '2' => A | B | G | E | D,
        '3' => A | B | G | C | D,
        '4' => F | G | B | C,
        '5' => A | F | G | C | D,
        '6' => A | F | G | E | C | D,
        '7' => A | B | C,
        '8' => A | B | C | D | E | F | G,
        '9' => A | B | C | D | F | G,
        'H' => F | E | G | B | C,
        'L' => F | E | D,
        'N' => E | G | C,
        'R' => E | G,
        '-' => G,
        _ => 0,
    };
}
