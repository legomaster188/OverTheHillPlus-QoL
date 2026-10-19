using System;

namespace OverTheHillPlus.RealNights;

/// <summary>The settings the night look is worked out from (all 0..1 unless noted).</summary>
internal struct NightSettings
{
    /// <summary>Whether the night gets darker at all (off: every multiplier is 1).</summary>
    public bool DarkNights;
    /// <summary>0 = the game's night, 1 = moonlight only and almost nothing else.</summary>
    public float Darkness;
    /// <summary>How much a storm darkens the sky: at night it goes pitch black, by day it only dims.</summary>
    public float StormDarkness;
    /// <summary>How long the game's own dusk and dawn colours last before the dark arrives (0 = darkens evenly).</summary>
    public float TwilightKeep;
}

/// <summary>What to multiply the game's lighting by this frame (1 = leave as it is).</summary>
internal struct NightLook
{
    /// <summary>The scene's ambient light.</summary>
    public float Ambient;
    /// <summary>The main directional light (the sun by day, the moon at night).</summary>
    public float Main;
    /// <summary>Fog colours: fog lit by the sky must go dark with it, or the night looks like a grey haze.</summary>
    public float Fog;
    /// <summary>Sky and cloud colours.</summary>
    public float Sky;
    /// <summary>How visible stars and the Milky Way are: 0 by day, in cloud or in a storm, 1 on a clear night.</summary>
    public float Stars;
    /// <summary>How much the car lamps are needed: 0 in daylight, 1 at night or under a storm.</summary>
    public float Lamp;
}

/// <summary>Darkness over the time of day and weather: pure maths, no game types.</summary>
internal static class NightMath
{
    // The game's clock: the auto headlights of Driving QoL use 19:00-07:00. Dusk ramps from 18:30 to 20:30 and dawn
    // from 04:30 to 06:30, so the sky is at its best around 19:30 and 05:30.
    public const float DuskStart = 18.5f, DuskEnd = 20.5f, DawnStart = 4.5f, DawnEnd = 6.5f;

    private static float Clamp01(float v) => v < 0f ? 0f : v > 1f ? 1f : v;

    public static float Smooth(float t)
    {
        t = Clamp01(t);
        return t * t * (3f - 2f * t);
    }

    /// <summary>Hour (0..24, any value wraps) to how night it is: 0 by day, 1 deep in the night, smooth over dusk and dawn.</summary>
    public static float Night(float hour)
    {
        hour %= 24f;
        if (hour < 0f) hour += 24f;
        if (hour >= DuskEnd || hour <= DawnStart) return 1f;
        if (hour < DawnEnd) return 1f - Smooth((hour - DawnStart) / (DawnEnd - DawnStart));
        if (hour <= DuskStart) return 0f;
        return Smooth((hour - DuskStart) / (DuskEnd - DuskStart));
    }

    /// <summary>
    /// The night weight the darkening follows: the same at 0 and 1, but with a high <paramref name="keep"/> the middle
    /// of dusk and dawn stays close to the game's own look (the dark arrives late and leaves early).
    /// </summary>
    public static float Eased(float night, float keep) => (float)Math.Pow(Clamp01(night), 1f + 2f * Clamp01(keep));

    /// <summary>COZY's storm-cloud amount to 0..1 (nothing below 0.15, full from 0.8).</summary>
    public static float Storm(float nimbus) => Smooth((nimbus - 0.15f) / 0.65f);

    /// <summary>Sky cover to 0..1 clear (a few clouds are fine; overcast hides the stars).</summary>
    public static float Clear(float cloudCoverage) => 1f - Smooth((cloudCoverage - 0.1f) / 0.7f);

    /// <summary>The light left at full night for a darkness setting: 1 at 0, a little moon-blue glow at 1.</summary>
    public static float NightFloor(float darkness) => 0.03f + 0.97f * (float)Math.Pow(1f - Clamp01(darkness), 1.6f);

    public static NightLook Compute(NightSettings s, float night, float storm01, float clear01)
    {
        night = Clamp01(night);
        float storm = Clamp01(storm01);
        var look = new NightLook { Ambient = 1f, Main = 1f, Fog = 1f, Sky = 1f, Lamp = Clamp01(Math.Max(night, storm * 0.8f)) };
        look.Stars = Smooth((night - 0.5f) / 0.5f) * Clamp01(clear01) * (1f - storm);
        if (!s.DarkNights) return look;

        float d = Clamp01(s.Darkness);
        float n = Eased(night, s.TwilightKeep);
        float hit = storm * Clamp01(s.StormDarkness);
        // Night: the ambient falls to NightFloor, the moon to about half. A storm takes 97 % of what is left at night and
        // half by day, so a storm night is pitch black (lightning still lights it: that is a light of its own).
        look.Ambient = Lerp(1f, NightFloor(d), n) * (1f - hit * (n * 0.97f + (1f - n) * 0.5f));
        // The moon is hidden behind the storm: it gets a second cut on top of the first.
        look.Main = Lerp(1f, 1f - 0.5f * d, n) * (1f - hit * (n * 0.97f + (1f - n) * 0.45f)) * (1f - hit * n * 0.85f);
        look.Fog = Math.Max(0.02f, look.Ambient);
        look.Sky = Lerp(1f, 1f - 0.65f * d, n) * (1f - hit * n * 0.8f);
        return look;
    }

    public static float Lerp(float a, float b, float t) => a + (b - a) * Clamp01(t);
}
