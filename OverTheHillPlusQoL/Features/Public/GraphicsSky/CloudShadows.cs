using System;
using Il2CppInterop.Runtime;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace OverTheHillPlus.Features.Public.GraphicsSky;

/// <summary>
/// Experimental cloud shadows: a procedural, tileable cloud texture set as the light cookie of COZY's sun, scrolled
/// over time. COZY itself has no ground cloud shadows. Everything is restored (cookie, cookie size/offset) when the
/// feature is turned off. Only shaders that sample URP light cookies show the shadows; terrain shaders may not.
/// </summary>
internal static class CloudShadows
{
    private const int Size = 256;

    private static Texture2D _tex;
    private static float[] _noise;
    private static float _builtStrength = -1f, _builtCoverage = -1f;

    private static Light _light;
    private static UniversalAdditionalLightData _data;
    private static Texture _origCookie;
    private static float _origCookieSize;
    private static Vector2 _origDataSize, _origDataOffset;
    private static Vector2 _offset;
    private static bool _applied, _loggedError;

    internal static string Status = "off";

    public static void Tick()
    {
        bool want = SkySettings.Enabled.Value && SkySettings.CloudShadows.Value;
        if (!want)
        {
            if (_applied) Restore();
            Status = "off";
            return;
        }

        var w = SkyApplier.Weather;
        Light sun = null;
        try { if (SkyApplier.Alive(w)) sun = w.sunLight; } catch { sun = null; }
        if (!SkyApplier.Alive(sun))
        {
            if (_applied) Restore();
            Status = "waiting for the sun light";
            return;
        }

        try
        {
            if (_applied && sun.Pointer != _light?.Pointer) Restore();
            if (!_applied) Capture(sun);

            EnsureTexture();
            if (_light.cookie == null || _light.cookie.Pointer != _tex.Pointer) _light.cookie = _tex;

            float scale = Mathf.Max(10f, SkySettings.ShadowScale.Value);
            float rad = SkySettings.ShadowDirection.Value * Mathf.Deg2Rad;
            _offset += new Vector2(Mathf.Cos(rad), Mathf.Sin(rad)) * (SkySettings.ShadowSpeed.Value * Time.deltaTime);
            // Keep the offset small (the pattern tiles every `scale` metres) so float precision never degrades.
            _offset.x = Mathf.Repeat(_offset.x, scale);
            _offset.y = Mathf.Repeat(_offset.y, scale);

            if (_data != null)
            {
                _data.lightCookieSize = new Vector2(scale, scale);
                _data.lightCookieOffset = _offset;
            }
            try { _light.cookieSize = scale; } catch { /* obsolete on some versions */ }
            Status = _data != null ? "on" : "on (no URP light data: size/drift may not apply)";
        }
        catch (Exception e)
        {
            if (!_loggedError) { _loggedError = true; Plugin.Logger.LogError($"Graphics.Sky: cloud shadows failed: {e}"); }
            Status = "Failed: " + e.Message;
            if (_applied) Restore();
        }
    }

    private static void Capture(Light sun)
    {
        _light = sun;
        _origCookie = sun.cookie;
        try { _origCookieSize = sun.cookieSize; } catch { _origCookieSize = 10f; }
        _data = null;
        try { _data = sun.GetComponent(Il2CppType.Of<UniversalAdditionalLightData>())?.TryCast<UniversalAdditionalLightData>(); }
        catch { _data = null; }
        if (_data != null)
        {
            _origDataSize = _data.lightCookieSize;
            _origDataOffset = _data.lightCookieOffset;
        }
        _applied = true;
    }

    public static void Restore()
    {
        try
        {
            if (SkyApplier.Alive(_light))
            {
                _light.cookie = _origCookie;
                try { _light.cookieSize = _origCookieSize; } catch { }
                if (_data != null && SkyApplier.Alive(_data))
                {
                    _data.lightCookieSize = _origDataSize;
                    _data.lightCookieOffset = _origDataOffset;
                }
            }
        }
        catch (Exception e) { Plugin.Logger.LogWarning($"Graphics.Sky: cloud shadow restore failed: {e.Message}"); }
        _applied = false;
        _light = null;
        _data = null;
        _origCookie = null;
    }

    /// <summary>Whether the active URP asset was set up with light cookies (if not, shader variants may be stripped).</summary>
    internal static string RendererSupport()
    {
        try
        {
            var rp = UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline;
            var urp = rp?.TryCast<UniversalRenderPipelineAsset>();
            if (urp == null) return "unknown";
            return urp.supportsLightCookies ? "yes" : "no (shadows may not show)";
        }
        catch { return "unknown"; }
    }

    // ---------------------------------------------------------------- texture

    private static void EnsureTexture()
    {
        float strength = Mathf.Clamp01(SkySettings.ShadowStrength.Value);
        float coverage = Mathf.Clamp01(SkySettings.ShadowCoverage.Value);
        bool texAlive = _tex != null && !_tex.WasCollected && _tex;
        if (texAlive && Mathf.Abs(strength - _builtStrength) < 0.01f && Mathf.Abs(coverage - _builtCoverage) < 0.01f) return;

        if (!texAlive)
        {
            _tex = new Texture2D(Size, Size, TextureFormat.RGBA32, true)
            {
                name = "OTHPlus_CloudShadows",
                wrapMode = TextureWrapMode.Repeat,
                filterMode = FilterMode.Trilinear,
                anisoLevel = 2,
                hideFlags = HideFlags.HideAndDontSave,
            };
        }
        _noise ??= BuildNoise();

        // Map noise to a soft cloud mask: more coverage = lower threshold.
        float threshold = Mathf.Lerp(0.78f, 0.28f, coverage);
        const float soft = 0.12f;
        var px = new Color32[Size * Size];
        for (int i = 0; i < px.Length; i++)
        {
            float m = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(threshold - soft, threshold + soft, _noise[i]));
            byte v = (byte)Mathf.Clamp(Mathf.RoundToInt((1f - strength * m) * 255f), 0, 255);
            px[i] = new Color32(v, v, v, 255);
        }
        _tex.SetPixels32(px);
        _tex.Apply(true, false);
        _builtStrength = strength;
        _builtCoverage = coverage;
    }

    /// <summary>Tileable fractal value noise in [0,1], 4 octaves.</summary>
    private static float[] BuildNoise()
    {
        var n = new float[Size * Size];
        int[] periods = { 4, 8, 16, 32 };
        float[] amps = { 0.5f, 0.25f, 0.15f, 0.1f };
        for (int y = 0; y < Size; y++)
        for (int x = 0; x < Size; x++)
        {
            float sum = 0f;
            for (int o = 0; o < periods.Length; o++)
            {
                int p = periods[o];
                float fx = x * p / (float)Size, fy = y * p / (float)Size;
                int x0 = (int)fx, y0 = (int)fy;
                float tx = Fade(fx - x0), ty = Fade(fy - y0);
                int x1 = (x0 + 1) % p, y1 = (y0 + 1) % p;
                float a = Hash(x0, y0, o), b = Hash(x1, y0, o), c = Hash(x0, y1, o), d = Hash(x1, y1, o);
                sum += amps[o] * Mathf.Lerp(Mathf.Lerp(a, b, tx), Mathf.Lerp(c, d, tx), ty);
            }
            n[y * Size + x] = sum;
        }
        return n;
    }

    private static float Fade(float t) => t * t * (3f - 2f * t);

    private static float Hash(int x, int y, int seed)
    {
        unchecked
        {
            uint h = (uint)(x * 374761393 + y * 668265263 + seed * 1442695041);
            h = (h ^ (h >> 13)) * 1274126177u;
            h ^= h >> 16;
            return (h & 0xFFFFFF) / 16777215f;
        }
    }
}
