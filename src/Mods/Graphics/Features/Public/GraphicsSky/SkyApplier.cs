using System;
using System.Collections.Generic;
using System.Reflection;
using DistantLands.Cozy;
using DistantLands.Cozy.Data;
using HarmonyLib;
using Il2CppInterop.Runtime;
using UnityEngine;
using Object = UnityEngine.Object;
using OverTheHillPlus.Graphics;

namespace OverTheHillPlus.Features.Public.GraphicsSky;

/// <summary>
/// Applies the sky overrides to COZY.
///
/// How COZY works here (from the ISIL): every frame the modules write the current sky values (colours from the
/// atmosphere profile at this time of day, cloud cover from the weather) into fields on <see cref="CozyWeather"/>,
/// then <c>CozyAtmosphereModule.LateUpdate</c> calls <see cref="CozyWeather.UpdateShaderVariables"/> which copies
/// those fields into global shader properties (CZY_ZenithColor, CZY_CumulusCoverageMultiplier, CZY_SunSize, ...).
///
/// So we patch UpdateShaderVariables: the prefix snapshots the fields we touch and writes our adjusted values, the
/// finalizer (runs even if the game throws) writes the snapshot back. The shaders see our sky, while the game's own
/// state is never changed, so the weather keeps evolving and turning the feature off is instant and lossless.
/// The moon is a real object (satellite), so it is scaled directly and the original scale restored.
/// </summary>
internal static class SkyApplier
{
    private static bool _patched, _patchFailed;
    private static bool _active; // overrides applied in the shader update (set from the main thread in Tick)
    private static float _lastPatchFire = -100f;
    private static bool _loggedApplyError;

    // Last COZY instance seen (from the patch or a throttled search).
    internal static CozyWeather Weather;
    private static float _nextFind;
    internal static string LastError;

    // Snapshot of the fields we change during one UpdateShaderVariables call.
    private struct Snap
    {
        public float Cumulus, Nimbus, Border, Cirrus, Cirro, Alto, Chem, Thickness, Gradient, SunSize, Galaxy;
        public Color Zenith, Horizon, Cloud, CloudHighlight, HighCloud, StarColor, LightColumns;
    }
    private static Snap _snap;
    private static bool _hasSnap;

    // Cached per-frame values computed on the main thread so the prefix stays cheap.
    private static float _amount, _boost, _high, _thick, _bright, _soft, _sun, _stars, _milky, _aurora, _auroraBoost;
    private static float _skyT, _horT, _cloudT;
    private static Color _skyC, _horC, _cloudC;

    // Cloud style bookkeeping (restored when the style goes back to Game or the feature is turned off).
    private static IntPtr _styleWeather;
    private static int _origStyle = -1;
    private static int _appliedStyle = -1;

    // Moon scaling: original local scale per moon transform.
    private sealed class MoonEntry { public Transform T; public Vector3 Orig; public Vector3 LastSet; }
    private static readonly Dictionary<IntPtr, MoonEntry> Moons = new();
    private static float _nextMoonScan;
    private static float _appliedMoon = 1f;

    private static readonly Color AuroraGreen = new(0.25f, 1f, 0.62f, 1f);

    // ---------------------------------------------------------------- per frame

    public static void Tick()
    {
        bool want = SkySettings.Enabled.Value;
        if (want && !_patched && !_patchFailed) Patch();

        bool dirty = SkySettings.Dirty;
        if (dirty) { SkySettings.Dirty = false; CacheSettings(); }

        if (!want)
        {
            if (_active || _appliedStyle >= 0 || Moons.Count > 0)
            {
                _active = false;
                RestoreStyle();
                RestoreMoons();
                PushNow(); // push the game's own values once so the sky snaps back even if COZY is idle
            }
            return;
        }

        _active = true;
        float now = Time.unscaledTime;
        if (!Alive(Weather) && now >= _nextFind)
        {
            _nextFind = now + 2f;
            Weather = FindWeather();
        }
        if (!Alive(Weather)) return;

        ApplyStyle(Weather);
        ApplyMoons(now);

        // COZY normally pushes every frame; if it did not recently (paused / idle sky) or a slider moved, push now.
        if (dirty || now - _lastPatchFire > 0.5f) PushNow();
    }

    private static void CacheSettings()
    {
        _amount = Mathf.Max(0f, SkySettings.CloudAmount.Value);
        _boost = Mathf.Max(0f, SkySettings.CloudBoost.Value);
        _high = Mathf.Max(0f, SkySettings.HighClouds.Value);
        _thick = Mathf.Max(0f, SkySettings.CloudThickness.Value);
        _bright = Mathf.Max(0f, SkySettings.SkyBrightness.Value);
        _soft = Mathf.Max(0.01f, SkySettings.HorizonSoftness.Value);
        _sun = Mathf.Max(0f, SkySettings.SunSize.Value);
        _stars = Mathf.Max(0f, SkySettings.Stars.Value);
        _milky = Mathf.Max(0f, SkySettings.MilkyWay.Value);
        _aurora = Mathf.Max(0f, SkySettings.Aurora.Value);
        _auroraBoost = Mathf.Max(0f, SkySettings.AuroraBoost.Value);
        _skyT = Mathf.Clamp01(SkySettings.SkyTint.Value);
        _horT = Mathf.Clamp01(SkySettings.HorizonTint.Value);
        _cloudT = Mathf.Clamp01(SkySettings.CloudTint.Value);
        _skyC = SkySettings.ParseHex(SkySettings.SkyColor.Value, Color.white);
        _horC = SkySettings.ParseHex(SkySettings.HorizonColor.Value, Color.white);
        _cloudC = SkySettings.ParseHex(SkySettings.CloudColor.Value, Color.white);
    }

    // ---------------------------------------------------------------- patch

    private static void Patch()
    {
        try
        {
            var target = AccessTools.Method(typeof(CozyWeather), nameof(CozyWeather.UpdateShaderVariables));
            if (target == null) throw new MissingMethodException("CozyWeather.UpdateShaderVariables");
            var flags = BindingFlags.Static | BindingFlags.NonPublic;
            var harmony = GraphicsPlugin.Harmony ?? new Harmony(GraphicsPlugin.Guid + ".sky");
            harmony.Patch(target,
                prefix: new HarmonyMethod(typeof(SkyApplier).GetMethod(nameof(Prefix), flags)),
                finalizer: new HarmonyMethod(typeof(SkyApplier).GetMethod(nameof(Finalizer), flags)));
            _patched = true;
            CacheSettings();
            GraphicsPlugin.Logger.LogInfo("Graphics.Sky: hooked CozyWeather.UpdateShaderVariables");
        }
        catch (Exception e)
        {
            _patchFailed = true;
            LastError = "Failed to hook COZY: " + e.Message;
            GraphicsPlugin.Logger.LogError($"Graphics.Sky: patch failed: {e}");
        }
    }

    private static void Prefix(CozyWeather __instance)
    {
        _hasSnap = false;
        if (__instance == null) return;
        _lastPatchFire = Time.unscaledTime;
        if (!_active) return;
        try
        {
            Weather = __instance;
            var w = __instance;
            _snap = new Snap
            {
                Cumulus = w.cumulus, Nimbus = w.nimbus, Border = w.borderHeight, Cirrus = w.cirrus, Cirro = w.cirrostratus,
                Alto = w.altocumulus, Chem = w.chemtrails, Thickness = w.cloudThickness, Gradient = w.gradientExponent,
                SunSize = w.sunSize, Galaxy = w.galaxyIntensity,
                Zenith = w.skyZenithColor, Horizon = w.skyHorizonColor, Cloud = w.cloudColor, CloudHighlight = w.cloudHighlightColor,
                HighCloud = w.highAltitudeCloudColor, StarColor = w.starColor, LightColumns = w.lightScatteringColor,
            };
            _hasSnap = true;

            var s = _snap;
            w.cumulus = Mathf.Max(0f, s.Cumulus * _amount + _boost);
            w.nimbus = Mathf.Max(0f, s.Nimbus * _amount);
            w.cirrus = s.Cirrus * _high;
            w.cirrostratus = s.Cirro * _high;
            w.altocumulus = s.Alto * _high;
            w.chemtrails = s.Chem * _high;
            w.cloudThickness = s.Thickness * _thick;
            w.gradientExponent = s.Gradient * _soft;
            w.sunSize = s.SunSize * _sun;
            w.galaxyIntensity = s.Galaxy * _milky;

            w.skyZenithColor = Scale(Tint(s.Zenith, _skyC, _skyT), _bright);
            w.skyHorizonColor = Scale(Tint(s.Horizon, _horC, _horT), _bright);
            if (_cloudT > 0f)
            {
                w.cloudColor = Tint(s.Cloud, _cloudC, _cloudT);
                w.highAltitudeCloudColor = Tint(s.HighCloud, _cloudC, _cloudT);
                w.cloudHighlightColor = Tint(s.CloudHighlight, _cloudC, _cloudT * 0.5f);
            }
            w.starColor = Scale(s.StarColor, _stars);
            var lc = Scale(s.LightColumns, _aurora);
            if (_auroraBoost > 0f)
            {
                lc.r += AuroraGreen.r * _auroraBoost;
                lc.g += AuroraGreen.g * _auroraBoost;
                lc.b += AuroraGreen.b * _auroraBoost;
                lc.a = Mathf.Max(lc.a, Mathf.Clamp01(_auroraBoost));
            }
            w.lightScatteringColor = lc;
        }
        catch (Exception e)
        {
            // Put back whatever we managed to snapshot; the error is logged once and shown in the menu.
            Restore(__instance);
            if (!_loggedApplyError) { _loggedApplyError = true; GraphicsPlugin.Logger.LogError($"Graphics.Sky: apply failed: {e}"); }
            LastError = "Failed to apply: " + e.Message;
        }
    }

    private static void Finalizer(CozyWeather __instance) => Restore(__instance);

    private static void Restore(CozyWeather w)
    {
        if (!_hasSnap || w == null) return;
        _hasSnap = false;
        try
        {
            var s = _snap;
            w.cumulus = s.Cumulus; w.nimbus = s.Nimbus; w.borderHeight = s.Border; w.cirrus = s.Cirrus; w.cirrostratus = s.Cirro;
            w.altocumulus = s.Alto; w.chemtrails = s.Chem; w.cloudThickness = s.Thickness; w.gradientExponent = s.Gradient;
            w.sunSize = s.SunSize; w.galaxyIntensity = s.Galaxy;
            w.skyZenithColor = s.Zenith; w.skyHorizonColor = s.Horizon; w.cloudColor = s.Cloud; w.cloudHighlightColor = s.CloudHighlight;
            w.highAltitudeCloudColor = s.HighCloud; w.starColor = s.StarColor; w.lightScatteringColor = s.LightColumns;
        }
        catch (Exception e)
        {
            if (!_loggedApplyError) { _loggedApplyError = true; GraphicsPlugin.Logger.LogError($"Graphics.Sky: restore failed: {e}"); }
        }
    }

    /// <summary>Calls COZY's own shader push (which runs our prefix when active).</summary>
    private static void PushNow()
    {
        var w = Weather;
        if (!_patched || !Alive(w)) return;
        try
        {
            if (!w.isActiveAndEnabled) return;
            _lastPatchFire = Time.unscaledTime; // even if the hook somehow did not fire, do not spam
            w.UpdateShaderVariables();
        }
        catch (Exception e)
        {
            DebugLog.Error("graphics", e, "SkyApplier.PushNow");
            if (!_loggedApplyError) { _loggedApplyError = true; GraphicsPlugin.Logger.LogWarning($"Graphics.Sky: push failed: {e.Message}"); }
        }
    }

    // ---------------------------------------------------------------- colour math

    private static float Lum(Color c) => 0.2126f * c.r + 0.7152f * c.g + 0.0722f * c.b;

    /// <summary>Re-hues <paramref name="orig"/> towards <paramref name="tint"/> while keeping its brightness, so night stays dark.</summary>
    private static Color Tint(Color orig, Color tint, float amount)
    {
        if (amount <= 0f) return orig;
        float lo = Lum(orig);
        float lt = Mathf.Max(0.05f, Lum(tint));
        float k = lo / lt;
        var t = new Color(tint.r * k, tint.g * k, tint.b * k, orig.a);
        return Color.Lerp(orig, t, amount);
    }

    private static Color Scale(Color c, float m) => m == 1f ? c : new Color(c.r * m, c.g * m, c.b * m, c.a);

    // ---------------------------------------------------------------- cloud style

    private static readonly CozyWeather.CloudStyle[] StyleValues =
    {
        CozyWeather.CloudStyle.cozyDesktop, CozyWeather.CloudStyle.cozyDesktop, CozyWeather.CloudStyle.soft,
        CozyWeather.CloudStyle.paintedSkies, CozyWeather.CloudStyle.luxury, CozyWeather.CloudStyle.ghibliDesktop,
        CozyWeather.CloudStyle.singleTexture,
    };

    private static int WantedStyle()
    {
        int i = Array.IndexOf(SkySettings.CloudStyles, SkySettings.CloudStyle.Value);
        return i <= 0 ? -1 : (int)StyleValues[i];
    }

    internal static string CurrentStyleName()
    {
        try { return Alive(Weather) ? Weather.cloudStyle.ToString() : "?"; } catch (Exception ex) { DebugLog.Error("graphics", ex, "SkyApplier.CurrentStyleName"); return "?"; }
    }

    private static void ApplyStyle(CozyWeather w)
    {
        int want = WantedStyle();
        if (want == _appliedStyle && w.Pointer == _styleWeather) return;
        try
        {
            if (w.Pointer != _styleWeather)
            {
                // New COZY instance (scene change): remember its own style before touching it.
                _styleWeather = w.Pointer;
                _origStyle = (int)w.cloudStyle;
                _appliedStyle = -1;
            }
            if (want < 0)
            {
                RestoreStyle();
                return;
            }
            if ((int)w.cloudStyle != want)
            {
                w.cloudStyle = (CozyWeather.CloudStyle)want;
                w.ResetQuality();
                GraphicsPlugin.Logger.LogInfo($"Graphics.Sky: cloud style -> {(CozyWeather.CloudStyle)want}");
            }
            _appliedStyle = want;
        }
        catch (Exception e)
        {
            DebugLog.Error("graphics", e, "SkyApplier.ApplyStyle");
            _appliedStyle = want; // do not retry every frame
            LastError = "Failed to switch cloud style: " + e.Message;
            GraphicsPlugin.Logger.LogWarning($"Graphics.Sky: cloud style switch failed: {e.Message}");
        }
    }

    private static void RestoreStyle()
    {
        if (_appliedStyle < 0) return;
        _appliedStyle = -1;
        try
        {
            var w = Weather;
            if (!Alive(w) || w.Pointer != _styleWeather || _origStyle < 0) return;
            if ((int)w.cloudStyle != _origStyle)
            {
                w.cloudStyle = (CozyWeather.CloudStyle)_origStyle;
                w.ResetQuality();
            }
        }
        catch (Exception e) { DebugLog.Error("graphics", e, "SkyApplier.RestoreStyle"); GraphicsPlugin.Logger.LogWarning($"Graphics.Sky: cloud style restore failed: {e.Message}"); }
    }

    // ---------------------------------------------------------------- moon

    private static void ApplyMoons(float now)
    {
        float mult = Mathf.Max(0.05f, SkySettings.MoonSize.Value);
        bool neutral = Mathf.Abs(mult - 1f) < 0.001f;
        if (neutral && Moons.Count == 0) return;

        if (now >= _nextMoonScan)
        {
            _nextMoonScan = now + 2f;
            if (!neutral) ScanMoons();
        }
        if (neutral) { RestoreMoons(); return; }

        List<IntPtr> dead = null;
        foreach (var kv in Moons)
        {
            var m = kv.Value;
            try
            {
                if (!Alive(m.T)) { (dead ??= new()).Add(kv.Key); continue; }
                var cur = m.T.localScale;
                // COZY rebuilt the satellite (or changed its size): adopt the new scale as the original.
                if ((cur - m.LastSet).sqrMagnitude > 1e-6f) m.Orig = cur;
                if (Mathf.Abs(mult - _appliedMoon) > 0.0001f || (cur - m.LastSet).sqrMagnitude > 1e-6f)
                {
                    m.LastSet = m.Orig * mult;
                    m.T.localScale = m.LastSet;
                }
            }
            catch (Exception ex) { DebugLog.Error("graphics", ex, "SkyApplier.ApplyMoons"); (dead ??= new()).Add(kv.Key); }
        }
        _appliedMoon = mult;
        if (dead != null) foreach (var k in dead) Moons.Remove(k);
    }

    private static void ScanMoons()
    {
        try
        {
            foreach (var o in Object.FindObjectsOfType(Il2CppType.Of<CozySatelliteModule>()))
            {
                var mod = o?.TryCast<CozySatelliteModule>();
                var sats = mod?.satellites;
                if (sats == null) continue;
                for (int i = 0; i < sats.Length; i++)
                {
                    SatelliteProfile sat = sats[i];
                    var t = sat?.moonRef;
                    if (!Alive(t) || Moons.ContainsKey(t.Pointer)) continue;
                    var sc = t.localScale;
                    float mult = Mathf.Max(0.05f, SkySettings.MoonSize.Value);
                    Moons[t.Pointer] = new MoonEntry { T = t, Orig = sc, LastSet = sc * mult };
                    t.localScale = sc * mult;
                }
            }
        }
        catch (Exception e)
        {
            DebugLog.Error("graphics", e, "SkyApplier.ScanMoons");
            if (!_loggedApplyError) { _loggedApplyError = true; GraphicsPlugin.Logger.LogWarning($"Graphics.Sky: moon scan failed: {e.Message}"); }
        }
    }

    private static void RestoreMoons()
    {
        foreach (var m in Moons.Values)
        {
            try { if (Alive(m.T) && (m.T.localScale - m.LastSet).sqrMagnitude < 1e-6f) m.T.localScale = m.Orig; }
            catch (Exception ex) { DebugLog.Error("graphics", ex, "SkyApplier.RestoreMoons"); /* destroyed */ }
        }
        Moons.Clear();
        _appliedMoon = 1f;
    }

    internal static int MoonCount => Moons.Count;

    // ---------------------------------------------------------------- helpers

    private static CozyWeather FindWeather()
    {
        try
        {
            CozyWeather best = null;
            foreach (var o in Object.FindObjectsOfType(Il2CppType.Of<CozyWeather>()))
            {
                var w = o?.TryCast<CozyWeather>();
                if (w == null) continue;
                if (best == null || (w.isActiveAndEnabled && !best.isActiveAndEnabled)) best = w;
            }
            return best;
        }
        catch (Exception ex) { DebugLog.Error("graphics", ex, "SkyApplier.FindWeather"); return null; }
    }

    internal static bool Alive(Object o)
    {
        try { return o != null && !o.WasCollected && o; }
        catch (Exception ex) { DebugLog.Error("graphics", ex, "SkyApplier.Alive"); return false; }
    }

    internal static bool Hooked => _patched;
    internal static bool SeenRecently => Time.unscaledTime - _lastPatchFire < 2f;
}
