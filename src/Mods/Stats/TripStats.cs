using System;
using System.Globalization;
using System.Text.Json;

namespace OverTheHillPlus.Stats;

/// <summary>One set of trip numbers (this session, or all time). Plain properties so it saves as JSON as-is.</summary>
internal sealed class TripTotals
{
    public double DistanceM { get; set; }
    public float TopSpeedKmh { get; set; }
    /// <summary>Time in the air, over the jumps that counted.</summary>
    public double AirTimeS { get; set; }
    public int Jumps { get; set; }
    /// <summary>Longest jump: horizontal distance from take-off to landing.</summary>
    public float LongestJumpM { get; set; }
    public float LongestAirS { get; set; }
    public int Flips { get; set; }
    public int MostFlips { get; set; }
    /// <summary>Time spent moving (not parked).</summary>
    public double DrivenS { get; set; }

    public TripTotals Clone() => (TripTotals)MemberwiseClone();

    /// <summary>Drops values a hand-edited or damaged file could hold (negative, NaN, absurd).</summary>
    public void Sanitize()
    {
        DistanceM = Clean(DistanceM, 1e10);
        TopSpeedKmh = (float)Clean(TopSpeedKmh, TripRecorder.MaxPlausibleKmh);
        AirTimeS = Clean(AirTimeS, 1e9);
        LongestJumpM = (float)Clean(LongestJumpM, 1e5);
        LongestAirS = (float)Clean(LongestAirS, 1e5);
        DrivenS = Clean(DrivenS, 1e10);
        if (Jumps < 0) Jumps = 0;
        if (Flips < 0) Flips = 0;
        if (MostFlips < 0) MostFlips = 0;
    }

    private static double Clean(double v, double max) => double.IsNaN(v) || double.IsInfinity(v) || v < 0 ? 0 : Math.Min(v, max);
}

/// <summary>
/// Adds driving and jumps to this session's and the all-time numbers. Pure logic (no game types), unit tested; the
/// plugin feeds it once per frame (<see cref="Motion"/>) and once per landed jump (<see cref="Jump"/>).
/// </summary>
internal sealed class TripRecorder
{
    /// <summary>Faster than this is a glitch (or a respawn), never a real top speed.</summary>
    public const float MaxPlausibleKmh = 600f;
    /// <summary>A step longer than this speed allows (plus a little) is a respawn or teleport: not driven distance.</summary>
    public const float MaxStepSpeedMs = 120f;
    /// <summary>Moving at least this fast counts as driving time.</summary>
    public const float MovingKmh = 2f;

    public TripTotals Session { get; private set; } = new();
    public TripTotals Lifetime { get; private set; } = new();

    /// <summary>True when something changed since the last <see cref="MarkSaved"/>.</summary>
    public bool Dirty { get; private set; }

    public TripRecorder() { }

    public TripRecorder(TripTotals lifetime)
    {
        Lifetime = lifetime?.Clone() ?? new TripTotals();
        Lifetime.Sanitize();
    }

    /// <summary>
    /// One frame of driving: <paramref name="stepMeters"/> moved since the last frame at <paramref name="speedKmh"/>.
    /// A step too long for the frame (respawn, teleport) is not counted.
    /// </summary>
    public void Motion(float dt, float stepMeters, float speedKmh)
    {
        if (!(dt > 0f) || float.IsInfinity(dt)) return;
        if (stepMeters > 0f && !float.IsInfinity(stepMeters) && stepMeters <= MaxStepSpeedMs * dt + 1f)
        {
            Session.DistanceM += stepMeters;
            Lifetime.DistanceM += stepMeters;
            Dirty = true;
        }
        if (float.IsNaN(speedKmh) || speedKmh < 0f || speedKmh > MaxPlausibleKmh) return;
        if (speedKmh > Session.TopSpeedKmh) { Session.TopSpeedKmh = speedKmh; Dirty = true; }
        if (speedKmh > Lifetime.TopSpeedKmh) { Lifetime.TopSpeedKmh = speedKmh; Dirty = true; }
        if (speedKmh >= MovingKmh)
        {
            Session.DrivenS += dt;
            Lifetime.DrivenS += dt;
            Dirty = true;
        }
    }

    /// <summary>A landed jump: <paramref name="airTime"/> seconds, <paramref name="distance"/> metres, <paramref name="flips"/> flips.</summary>
    public void Jump(float airTime, float distance, int flips)
    {
        if (!(airTime > 0f) || float.IsInfinity(airTime)) return;
        if (float.IsNaN(distance) || distance < 0f || float.IsInfinity(distance)) distance = 0f;
        if (flips < 0) flips = 0;
        AddJump(Session, airTime, distance, flips);
        AddJump(Lifetime, airTime, distance, flips);
        Dirty = true;
    }

    private static void AddJump(TripTotals t, float airTime, float distance, int flips)
    {
        t.Jumps++;
        t.AirTimeS += airTime;
        t.Flips += flips;
        if (distance > t.LongestJumpM) t.LongestJumpM = distance;
        if (airTime > t.LongestAirS) t.LongestAirS = airTime;
        if (flips > t.MostFlips) t.MostFlips = flips;
    }

    /// <summary>Starts this session's numbers again (all-time numbers stay).</summary>
    public void ResetSession() => Session = new TripTotals();

    /// <summary>Clears the all-time numbers too.</summary>
    public void ResetLifetime()
    {
        Lifetime = new TripTotals();
        Dirty = true;
    }

    public void MarkSaved() => Dirty = false;
}

/// <summary>The all-time numbers as JSON (BepInEx/config/oth.stats.json).</summary>
internal static class TripStore
{
    private sealed class FileShape
    {
        public int Version { get; set; } = 1;
        public TripTotals Lifetime { get; set; }
    }

    private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true };
    private static readonly JsonSerializerOptions Lenient = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        MaxDepth = 8,
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    public static string Serialize(TripTotals lifetime) =>
        JsonSerializer.Serialize(new FileShape { Lifetime = lifetime ?? new TripTotals() }, Pretty);

    /// <summary>The saved all-time numbers; empty ones when the text is missing or unreadable.</summary>
    public static TripTotals Parse(string json) => TryParse(json, out var t) ? t : new TripTotals();

    /// <summary>
    /// Reads saved all-time numbers. False (and empty numbers) when the text is empty, too long, not JSON, or JSON
    /// without a "Lifetime" object: the caller then keeps the file aside instead of overwriting it.
    /// </summary>
    public static bool TryParse(string json, out TripTotals totals)
    {
        totals = new TripTotals();
        if (string.IsNullOrWhiteSpace(json) || json.Length > 64 * 1024) return false;
        try
        {
            var shape = JsonSerializer.Deserialize<FileShape>(json, Lenient);
            if (shape?.Lifetime == null) return false;
            totals = shape.Lifetime;
            totals.Sanitize();
            return true;
        }
        catch (JsonException ex) { DebugLog.Error("stats", ex, "TripStore.TryParse"); return false; }
        catch (NotSupportedException ex) { DebugLog.Error("stats", ex, "TripStore.TryParse"); return false; }
        catch (InvalidOperationException ex) { DebugLog.Error("stats", ex, "TripStore.TryParse"); return false; }
    }
}

/// <summary>How the HUD card and the page show the numbers.</summary>
internal static class TripFormat
{
    // Always a dot for decimals, whatever the PC's language: the card's columns are laid out for it.
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private static string N(double v, string format) => v.ToString(format, Inv);

    public static string Distance(double meters, bool miles)
    {
        if (miles)
        {
            double mi = meters / 1609.344;
            return N(mi, mi < 10 ? "0.00" : mi < 1000 ? "0.0" : "0") + " mi";
        }
        if (meters < 1000) return N(meters, "0") + " m";
        double km = meters / 1000.0;
        return N(km, km < 100 ? "0.00" : km < 1000 ? "0.0" : "0") + " km";
    }

    /// <summary>A short length (jumps): metres or feet.</summary>
    public static string Length(float meters, bool feet) => feet ? N(meters * 3.28084f, "0") + " ft" : N(meters, "0.0") + " m";

    public static string Speed(float kmh, bool mph) => mph ? N(kmh / 1.609344f, "0") + " mph" : N(kmh, "0") + " km/h";

    public static string Duration(double seconds)
    {
        if (double.IsNaN(seconds) || seconds < 0) seconds = 0;
        long s = (long)seconds;
        if (s < 60) return N(seconds, "0.0") + " s";
        if (s < 3600) return (s / 60).ToString(Inv) + " m " + (s % 60).ToString("00", Inv) + " s";
        return (s / 3600).ToString(Inv) + " h " + (s / 60 % 60).ToString("00", Inv) + " m";
    }
}
