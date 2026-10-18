using System;
using System.Globalization;

namespace OverTheHillPlus.HudPlus;

/// <summary>Speedometer skins.</summary>
internal enum SpeedoSkin
{
    Analog,
    Digital,
    Minimal,
}

/// <summary>How a tyre is doing, for the status card.</summary>
internal enum TyreGrip
{
    Grip,
    Slipping,
    Sliding,
    InAir,
}

/// <summary>HUD+'s pure maths and wording (no game types, unit tested).</summary>
internal static class HudMath
{
    // Always a dot for decimals, whatever the PC's language.
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public const float KmhPerMph = 1.609344f;

    /// <summary>Speed in the chosen unit, rounded for display (never negative).</summary>
    public static int ShownSpeed(float kmh, bool mph)
    {
        if (float.IsNaN(kmh) || kmh < 0f) kmh = 0f;
        return (int)Math.Round(mph ? kmh / KmhPerMph : kmh);
    }

    /// <summary>
    /// Needle angle in degrees for an analog dial that starts at <paramref name="startDeg"/> (speed 0) and sweeps
    /// <paramref name="sweepDeg"/> up to <paramref name="max"/>; past the end it stays at the end.
    /// </summary>
    public static float DialAngle(float speed, float max, float startDeg, float sweepDeg)
    {
        if (!(max > 0f)) return startDeg;
        float t = float.IsNaN(speed) ? 0f : Math.Max(0f, Math.Min(1f, speed / max));
        return startDeg + sweepDeg * t;
    }

    /// <summary>A round dial maximum above <paramref name="topSpeed"/>: 100, 120, 160, 200, 240, 300 or more in steps of 100.</summary>
    public static int DialMaxFor(float topSpeed)
    {
        int[] steps = { 100, 120, 160, 200, 240, 300 };
        foreach (int s in steps)
            if (topSpeed <= s) return s;
        return ((int)Math.Ceiling(topSpeed / 100f)) * 100;
    }

    /// <summary>Lit segments of an RPM bar of <paramref name="count"/> segments.</summary>
    public static int LitSegments(float rpm, float peak, int count)
    {
        if (count <= 0 || !(peak > 0f) || float.IsNaN(rpm) || rpm <= 0f) return 0;
        return Math.Min(count, (int)Math.Ceiling(rpm / peak * count - 0.0001f));
    }

    /// <summary>Car health 0-100 from the damage system's damage and maximum (no maximum = undamaged).</summary>
    public static int HealthPercent(float damage, float maxDamage)
    {
        if (!(maxDamage > 0f) || float.IsNaN(damage)) return 100;
        float h = 1f - Math.Max(0f, Math.Min(1f, damage / maxDamage));
        return (int)Math.Round(h * 100f);
    }

    public static string GearText(int gear) => gear < 0 ? "R" : gear == 0 ? "N" : gear.ToString(Inv);

    public static TyreGrip Grip(bool grounded, float normalizedSlip)
    {
        if (!grounded) return TyreGrip.InAir;
        float s = Math.Abs(float.IsNaN(normalizedSlip) ? 0f : normalizedSlip);
        return s >= 1.2f ? TyreGrip.Sliding : s >= 0.8f ? TyreGrip.Slipping : TyreGrip.Grip;
    }

    /// <summary>
    /// The winch line of the status card, from VehicleWinchSystem's WinchState (0 Disabled, 1 attached to another
    /// player's car, 2 to a static object, 3 to a movable object), whether the rope is tense and the tension 0-1.
    /// </summary>
    public static string WinchText(int state, bool taut, float normalizedTension, float ropeLength)
    {
        string what = state switch
        {
            1 => "on a car",
            2 => "anchored",
            3 => "on an object",
            _ => null,
        };
        if (what == null) return "stowed";
        string rope = ropeLength > 0f && !float.IsNaN(ropeLength) ? " · " + ropeLength.ToString("0", Inv) + " m" : "";
        if (!taut) return what + rope + " · slack";
        int pct = (int)Math.Round(Math.Max(0f, Math.Min(1f, float.IsNaN(normalizedTension) ? 0f : normalizedTension)) * 100f);
        return what + rope + " · taut " + pct.ToString(Inv) + "%";
    }

    public static string Odometer(float meters, bool miles)
    {
        if (float.IsNaN(meters) || meters < 0f) meters = 0f;
        return miles ? (meters / 1609.344f).ToString("0.0", Inv) + " mi" : (meters / 1000f).ToString("0.0", Inv) + " km";
    }

    /// <summary>"Wet" wording for the tyres' water level (0-1).</summary>
    public static string Wetness(float level)
    {
        if (float.IsNaN(level) || level < 0.05f) return "dry";
        return level < 0.5f ? "damp" : "wet";
    }
}
