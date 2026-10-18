using System;

namespace OverTheHillPlus.WorldTools;

/// <summary>
/// World Tools+'s pure rules: the safe ranges, the presets and their labels, and when the day-speed override may be
/// written. Nothing here touches the game (the tests compile this file on its own).
/// </summary>
internal static class WorldMath
{
    // ---------------------------------------------------------------- gravity

    /// <summary>Gravity as a multiple of the game's own (1 = normal). 0 would leave loose cars drifting forever.</summary>
    public const float MinGravity = 0.1f, MaxGravity = 3f;
    public const float Moon = 0.165f, Mars = 0.38f, Earth = 1f;

    public static readonly float[] GravitySteps = { 0.1f, Moon, 0.25f, Mars, 0.5f, 0.75f, Earth, 1.25f, 1.5f, 2f, 3f };

    public static float ClampGravity(float g) => Clamp(g, MinGravity, MaxGravity, Earth);

    public static string GravityLabel(float g)
    {
        g = ClampGravity(g);
        if (Near(g, Earth)) return "Normal";
        if (Near(g, Moon)) return "Moon (0.17 g)";
        if (Near(g, Mars)) return "Mars (0.38 g)";
        return g.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + " g";
    }

    // ---------------------------------------------------------------- time scale

    /// <summary>
    /// Game speed for everyone (1 = normal). Applied with the game's own TimeManager.RPC_SetTimeScale, which scales
    /// the physics step length and Unity's time on every player; Fusion's tick rate is untouched. Above 1.5 the
    /// longer physics steps start to shake the car physics, so that is the top.
    /// </summary>
    public const float MinTimeScale = 0.25f, MaxTimeScale = 1.5f;

    public static readonly float[] TimeScaleSteps = { 0.25f, 0.5f, 0.75f, 1f, 1.25f, 1.5f };

    public static float ClampTimeScale(float s) => Clamp(s, MinTimeScale, MaxTimeScale, 1f);

    public static string TimeScaleLabel(float s)
    {
        s = ClampTimeScale(s);
        if (Near(s, 1f)) return "Normal";
        return s.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + "×" + (s < 1f ? " (slow motion)" : " (fast)");
    }

    // ---------------------------------------------------------------- day length

    /// <summary>How fast the clock runs compared with the game's own speed (1 = normal, 2 = days half as long).</summary>
    public const float MinDaySpeed = 0.1f, MaxDaySpeed = 20f;

    public static readonly float[] DaySpeedSteps = { 0.1f, 0.25f, 0.5f, 1f, 2f, 4f, 8f, 16f };

    public static float ClampDaySpeed(float m) => Clamp(m, MinDaySpeed, MaxDaySpeed, 1f);

    public static string DaySpeedLabel(float m)
    {
        m = ClampDaySpeed(m);
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        if (Near(m, 1f)) return "Normal";
        return m < 1f ? (1f / m).ToString("0.#", inv) + "× longer days" : m.ToString("0.#", inv) + "× shorter days";
    }

    /// <summary>The time speed setting the game is in (TimeManager.TimeSpeedSetting).</summary>
    public enum ClockMode { Regular, Paused, CampingFastForward }

    /// <summary>
    /// The clock speed to write into the time profile, or null to leave it alone. Only in the game's regular mode:
    /// paused time and the camping fast-forward are the game's own (it writes its stock speed back when camping ends,
    /// and the override is put on again on the next check). <paramref name="stock"/> is the game's own speed
    /// (TimeManager.originalTimeSpeed); without a usable one nothing is written.
    /// </summary>
    public static float? DaySpeedTarget(float stock, float multiplier, ClockMode mode)
    {
        if (mode != ClockMode.Regular) return null;
        if (!float.IsFinite(stock) || stock <= 0f) return null;
        return stock * ClampDaySpeed(multiplier);
    }

    // ---------------------------------------------------------------- shared

    public static bool Near(float a, float b) => Math.Abs(a - b) < 0.005f;

    private static float Clamp(float v, float min, float max, float fallback)
    {
        if (!float.IsFinite(v)) return fallback;
        return v < min ? min : v > max ? max : v;
    }
}
