using System;
using DistantLands.Cozy;
using HarmonyLib;
using UnityEngine;

namespace OverTheHillPlus.RealNights;

/// <summary>
/// Darkens the night and puts the stars and Milky Way in the sky by changing what COZY pushes to the shaders and the
/// scene, once per <c>CozyWeather.UpdateShaderVariables</c> (the same call Graphics+ hooks; see <see cref="SkyPatch"/>).
///
/// Before the call the sky, cloud and fog colours are scaled and the star colour and galaxy intensity set (COZY's modules
/// rewrite those fields every frame, and they are put back afterwards). After the call COZY has set the main light's
/// colour and the scene's ambient colours from its fields; those two are scaled in place (COZY writes them fresh on the
/// next call). So the game's own state is never changed, turning the mod off is instant, and the weather and time keep
/// running as they do.
/// </summary>
internal static class NightSky
{
    private const int Slots = 16;
    private const int SkyFirst = 0, SkyLast = 5, FogFirst = 6, FogLast = 14, StarSlot = 15;

    /// <summary>The look worked out at the latest COZY update; the lamps and headlights follow it.</summary>
    internal static NightLook Look = new() { Ambient = 1f, Main = 1f, Fog = 1f, Sky = 1f };
    internal static float NightNow, StormNow, ClearNow, HourNow;
    internal static bool Hooked;
    internal static string LastError;

    private static readonly Color[] Orig = new Color[Slots], Wrote = new Color[Slots];
    private static readonly bool[] Touched = new bool[Slots];
    private static bool _galaxyTouched;
    private static float _galaxyOrig, _galaxyWrote;
    private static Color _lastMain, _lastSky, _lastEquator, _lastGround;
    private static bool _haveLast;
    private static int _clockFrame = -1;
    private static float _hour = 12f;
    private static bool _loggedError;

    /// <summary>The settings as sliders hold them now.</summary>
    internal static NightSettings Settings() => new()
    {
        DarkNights = RealNightsConfig.DarkNights.Value,
        Darkness = RealNightsConfig.Darkness.Value,
        StormDarkness = RealNightsConfig.StormDarkness.Value,
        TwilightKeep = RealNightsConfig.TwilightKeep.Value,
    };

    internal static void Before(CozyWeather w)
    {
        if (w == null) return;
        Array.Clear(Touched, 0, Slots);
        _galaxyTouched = false;

        NightNow = NightMath.Night(Hour());
        StormNow = NightMath.Storm(w.nimbus);
        ClearNow = NightMath.Clear(w.cloudCoverage);
        HourNow = _hour;
        Look = NightMath.Compute(Settings(), NightNow, StormNow, ClearNow);
        if (!RealNightsConfig.Stars.Value) Look.Stars = 0f;

        if (RealNightsConfig.DarkNights.Value)
        {
            Scale(w, SkyFirst, SkyLast, Look.Sky);
            Scale(w, FogFirst, FogLast, Look.Fog);
        }
        if (RealNightsConfig.Stars.Value && Look.Stars > 0.001f)
        {
            var star = Get(w, StarSlot);
            float b = Mathf.Max(0f, RealNightsConfig.StarBrightness.Value);
            Put(w, StarSlot, Color.Lerp(star, new Color(b, b, b * 1.05f, star.a), Look.Stars));
            _galaxyOrig = w.galaxyIntensity;
            _galaxyWrote = Mathf.Lerp(_galaxyOrig, Mathf.Max(0f, RealNightsConfig.MilkyWay.Value), Look.Stars);
            w.galaxyIntensity = _galaxyWrote;
            _galaxyTouched = true;
        }
    }

    internal static void After(CozyWeather w)
    {
        if (w == null) return;
        for (int i = 0; i < Slots; i++)
        {
            if (!Touched[i]) continue;
            if (Same(Get(w, i), Wrote[i])) Set(w, i, Orig[i]);
        }
        if (_galaxyTouched && Mathf.Approximately(w.galaxyIntensity, _galaxyWrote)) w.galaxyIntensity = _galaxyOrig;

        if (!RealNightsConfig.DarkNights.Value) { _haveLast = false; return; }
        var sun = w.sunLight;
        if (sun != null && Mathf.Abs(Look.Main - 1f) > 0.001f)
        {
            var c = sun.color;
            // COZY sets the colour on every call; one it did not set this time would be scaled again and again.
            if (!_haveLast || !Same(c, _lastMain))
            {
                c = new Color(c.r * Look.Main, c.g * Look.Main, c.b * Look.Main, c.a);
                sun.color = c;
                _lastMain = c;
            }
        }
        if (w.handleSceneLighting && Mathf.Abs(Look.Ambient - 1f) > 0.001f)
        {
            var sky = RenderSettings.ambientSkyColor;
            if (!_haveLast || !Same(sky, _lastSky))
            {
                float a = Look.Ambient;
                RenderSettings.ambientSkyColor = _lastSky = Mul(sky, a);
                RenderSettings.ambientEquatorColor = _lastEquator = Mul(RenderSettings.ambientEquatorColor, a);
                RenderSettings.ambientGroundColor = _lastGround = Mul(RenderSettings.ambientGroundColor, a);
            }
        }
        _haveLast = true;
    }

    internal static void Failed(Exception e, string where)
    {
        LastError = where + ": " + e.Message;
        DebugLog.Error(RealNightsPlugin.ModId, e, where);
        if (_loggedError) return;
        _loggedError = true;
        RealNightsPlugin.Logger.LogWarning($"Nights+: {where} failed: {e.Message}");
    }

    /// <summary>The game's clock as hours, read once per frame (it is a networked value).</summary>
    private static float Hour()
    {
        int frame = Time.frameCount;
        if (frame == _clockFrame) return _hour;
        _clockFrame = frame;
        try
        {
            var tm = TimeManager.Instance;
            if (tm != null) _hour = tm.Hours + tm.Minutes / 60f;
        }
        catch (Exception e) { Failed(e, "NightSky.Hour"); }
        return _hour;
    }

    private static void Scale(CozyWeather w, int from, int to, float mul)
    {
        if (Mathf.Abs(mul - 1f) < 0.001f) return;
        for (int i = from; i <= to; i++) Put(w, i, Mul(Get(w, i), mul));
    }

    private static void Put(CozyWeather w, int i, Color c)
    {
        Orig[i] = Get(w, i);
        Wrote[i] = c;
        Touched[i] = true;
        Set(w, i, c);
    }

    private static Color Mul(Color c, float m) => new(c.r * m, c.g * m, c.b * m, c.a);

    private static bool Same(Color a, Color b) => a.r == b.r && a.g == b.g && a.b == b.b && a.a == b.a;

    private static Color Get(CozyWeather w, int i) => i switch
    {
        0 => w.skyZenithColor, 1 => w.skyHorizonColor, 2 => w.cloudColor, 3 => w.cloudHighlightColor,
        4 => w.highAltitudeCloudColor, 5 => w.cloudMoonColor,
        6 => w.fogColor1, 7 => w.fogColor2, 8 => w.fogColor3, 9 => w.fogColor4, 10 => w.fogColor5,
        11 => w.heightFogColor, 12 => w.fogLitColor, 13 => w.fogMoonFlareColor, 14 => w.fogFlareColor,
        _ => w.starColor,
    };

    private static void Set(CozyWeather w, int i, Color c)
    {
        switch (i)
        {
            case 0: w.skyZenithColor = c; break;
            case 1: w.skyHorizonColor = c; break;
            case 2: w.cloudColor = c; break;
            case 3: w.cloudHighlightColor = c; break;
            case 4: w.highAltitudeCloudColor = c; break;
            case 5: w.cloudMoonColor = c; break;
            case 6: w.fogColor1 = c; break;
            case 7: w.fogColor2 = c; break;
            case 8: w.fogColor3 = c; break;
            case 9: w.fogColor4 = c; break;
            case 10: w.fogColor5 = c; break;
            case 11: w.heightFogColor = c; break;
            case 12: w.fogLitColor = c; break;
            case 13: w.fogMoonFlareColor = c; break;
            case 14: w.fogFlareColor = c; break;
            default: w.starColor = c; break;
        }
    }
}

/// <summary>
/// The hook into COZY's shader push, at low priority so it runs after Graphics+'s own (when both are on, Graphics+ has
/// done its sky first, and Nights+ then sets the night's stars and darkness on top: only night and storms are ours).
/// Both halves go through a guard site: timed, on the crash trail, and an exception never reaches the game.
/// </summary>
[HarmonyPatch(typeof(CozyWeather), nameof(CozyWeather.UpdateShaderVariables))]
internal static class SkyPatch
{
    private static readonly GuardSite BeforeSite = Guard.Site(RealNightsPlugin.ModId, "sky prefix");
    private static readonly GuardSite AfterSite = Guard.Site(RealNightsPlugin.ModId, "sky finalizer");
    private static readonly Action BeforeBody = () => NightSky.Before(_w);
    private static readonly Action AfterBody = () => NightSky.After(_w);
    private static CozyWeather _w;

    [HarmonyPriority(Priority.Low)]
    private static void Prefix(CozyWeather __instance)
    {
        NightSky.Hooked = true;
        _w = __instance;
        BeforeSite.Run(BeforeBody);
    }

    [HarmonyPriority(Priority.Low)]
    private static void Finalizer(CozyWeather __instance)
    {
        _w = __instance;
        AfterSite.Run(AfterBody);
    }
}
