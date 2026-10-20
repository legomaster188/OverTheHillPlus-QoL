using System;

namespace OverTheHillPlus.Flares;

/// <summary>The flare's flight and the name tags' distance curves. Pure maths, no game types.</summary>
internal static class FlareMath
{
    // ---------------------------------------------------------------- the flare

    /// <summary>Metres above the launch point: rises quickly and slows to the apex over <paramref name="riseTime"/> s, then sinks slowly (<paramref name="drift"/> m/s).</summary>
    public static float Height(float t, float apex, float riseTime, float drift)
    {
        if (t <= 0f || apex <= 0f) return 0f;
        if (riseTime <= 0.01f) riseTime = 0.01f;
        if (t < riseTime)
        {
            float u = 1f - t / riseTime;
            return apex * (1f - u * u);
        }
        return Math.Max(0f, apex - Math.Max(0f, drift) * (t - riseTime));
    }

    /// <summary>1 for most of the flare's life, then a fade to 0 over the last <paramref name="fadeOut"/> seconds.</summary>
    public static float LifeAlpha(float age, float life, float fadeOut)
    {
        if (age <= 0f) return 1f;
        if (age >= life) return 0f;
        if (fadeOut <= 0f || age < life - fadeOut) return 1f;
        return (life - age) / fadeOut;
    }

    /// <summary>
    /// How much bigger a far flare is drawn so it stays visible: 1 up to <paramref name="startDist"/> metres, growing in
    /// step with the distance, at most <paramref name="maxScale"/>.
    /// </summary>
    public static float FlareScale(float dist, float startDist, float maxScale)
    {
        if (!float.IsFinite(dist) || startDist <= 0f || dist <= startDist) return 1f;
        return Math.Min(Math.Max(1f, maxScale), dist / startDist);
    }

    // ---------------------------------------------------------------- name tags

    private static float Smooth(float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    /// <summary>Opacity of a name tag: 1 up to <paramref name="fadeStart"/> (a fraction of <paramref name="far"/>), then a smooth fade to 0 at <paramref name="far"/> metres.</summary>
    public static float NameAlpha(float dist, float far, float fadeStart)
    {
        if (!float.IsFinite(dist) || far <= 0f) return 0f;
        float start = far * Math.Clamp(fadeStart, 0f, 0.99f);
        if (dist <= start) return 1f;
        if (dist >= far) return 0f;
        return 1f - Smooth((dist - start) / (far - start));
    }

    /// <summary>Size of a name tag: 1 up to <paramref name="fullUntil"/> metres, shrinking to <paramref name="minScale"/> at <paramref name="far"/>, never below it.</summary>
    public static float NameScale(float dist, float fullUntil, float far, float minScale)
    {
        minScale = Math.Clamp(minScale, 0.05f, 1f);
        if (!float.IsFinite(dist) || dist <= fullUntil) return 1f;
        if (dist >= far || far <= fullUntil) return minScale;
        return minScale + (1f - minScale) * (1f - (dist - fullUntil) / (far - fullUntil));
    }

    public static string Metres(float dist) => float.IsFinite(dist) ? Math.Max(0, (int)Math.Round(dist)) + " m" : "";
}

/// <summary>
/// Notices a car lying on its side or roof, nearly still, for a number of seconds (the "stuck" of auto flares). A car
/// that is merely stopped on its wheels is never stuck. After it fires it fires again every <see cref="RepeatEvery"/>
/// seconds while the car stays that way, and starts over once the car is back on its wheels.
/// </summary>
internal sealed class StuckDetector
{
    /// <summary>Car-up Y below this = on its side or roof (0.35 is about 70 degrees of tilt); above the second = back on its wheels.</summary>
    public const float TippedUp = 0.35f, RecoveredUp = 0.7f;
    /// <summary>Metres per second below which the car counts as not moving.</summary>
    public const float StillSpeed = 3f;

    public float Seconds = 15f;
    public float RepeatEvery = 60f;

    private float _stuckFor, _sinceFire = -1f;

    /// <summary>One sample; true on the sample that fires.</summary>
    public bool Step(float dt, float upY, float speed)
    {
        if (!float.IsFinite(dt) || dt <= 0f || dt > 1f) return false; // a hitch or pause: don't count it
        if (upY >= RecoveredUp)
        {
            _stuckFor = 0f;
            _sinceFire = -1f;
            return false;
        }
        if (upY >= TippedUp || speed > StillSpeed)
        {
            _stuckFor = 0f; // moving, or only leaning: start counting again
            return false;
        }
        _stuckFor += dt;
        if (_sinceFire >= 0f) _sinceFire += dt;
        bool first = _sinceFire < 0f && _stuckFor >= Seconds;
        bool again = _sinceFire >= 0f && _sinceFire >= RepeatEvery;
        if (!first && !again) return false;
        _sinceFire = 0f;
        return true;
    }

    public void Reset()
    {
        _stuckFor = 0f;
        _sinceFire = -1f;
    }
}
