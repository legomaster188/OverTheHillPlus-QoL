using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using DistantLands.Cozy;
using HarmonyLib;
using Il2CppInterop.Runtime;
using OccaSoftware.Buto.Runtime;
using OverTheHillPlus.UI;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace OverTheHillPlus.Features.Public.Fog;

/// <summary>
/// Graphics > Fog and atmosphere. Local-only overrides for the two fog layers the game draws:
///
/// - Buto volumetric fog (OccaSoftware.Buto, a URP VolumeComponent). COZY's CozyButoModule rewrites its density and
///   lit/shadowed colours every Update from the weather, so we re-apply in a postfix of that Update, and once per
///   frame from our own tick for scenes without the module.
/// - COZY stylized fog / height fog / sky haze. CozyAtmosphereModule.LateUpdate copies the atmosphere profile into
///   CozyWeather and calls CozyWeather.UpdateShaderVariables, which pushes CZY_* shader globals. We adjust those
///   globals in a postfix of that method, so the CozyWeather fields (which COZY's transitions read back) are never
///   touched.
/// - Unity RenderSettings fog, in case a scene uses it.
///
/// Every value we change goes through a "slot" that remembers the game's own value (the base) and what we wrote. When
/// the game writes a new value we adopt it as the new base, so nothing compounds, and turning a control back to
/// neutral (or the whole feature off) writes the game's value back and returns override flags to how they were.
/// Nothing here is networked: other players never see any of it.
/// </summary>
internal sealed class FogAtmosphere : IFeature
{
    private const string Sec = "Graphics.Fog";

    private static ConfigEntry<bool> _enabled, _noFog, _affectButo, _affectCozy;
    private static ConfigEntry<float> _density, _visibility, _heightFog, _heightOffset, _skyHaze, _godRays, _range;
    private static ConfigEntry<float> _tintStrength, _tintR, _tintG, _tintB;

    public void Register()
    {
        var f = ModConfig.File;
        _enabled = f.Bind(Sec, "Enabled", false, "Master switch for the fog and atmosphere overrides (local visuals only).");
        _noFog = f.Bind(Sec, "NoFog", false, "Turn off Buto volumetric fog, COZY fog and height fog entirely.");
        _affectButo = f.Bind(Sec, "AffectVolumetric", true, "Apply the overrides to Buto volumetric fog.");
        _affectCozy = f.Bind(Sec, "AffectCozy", true, "Apply the overrides to COZY fog, height fog and sky haze.");
        _density = f.Bind(Sec, "Density", 1f, "Fog density multiplier (1 = game default).");
        _visibility = f.Bind(Sec, "Visibility", 1f, "Visibility distance multiplier: above 1 pushes fog further away.");
        _heightFog = f.Bind(Sec, "HeightFog", 1f, "COZY height (ground) fog intensity multiplier.");
        _heightOffset = f.Bind(Sec, "HeightOffset", 0f, "Moves the fog layer up or down, in metres.");
        _skyHaze = f.Bind(Sec, "SkyHaze", 1f, "How much fog colours the sky and clouds (multiplier).");
        _godRays = f.Bind(Sec, "GodRays", 1f, "Sun scattering in the volumetric fog (god rays) multiplier.");
        _range = f.Bind(Sec, "VolumetricRange", 1f, "Render distance of the volumetric fog (multiplier; higher costs detail up close).");
        _tintStrength = f.Bind(Sec, "TintStrength", 0f, "How strongly fog colours are tinted toward the tint colour (0 = off).");
        _tintR = f.Bind(Sec, "TintR", 1f, "Tint colour red (0..1).");
        _tintG = f.Bind(Sec, "TintG", 0.85f, "Tint colour green (0..1).");
        _tintB = f.Bind(Sec, "TintB", 0.65f, "Tint colour blue (0..1).");

        f.SettingChanged += OnSettingChanged;
        InstallPatches();
        Registry.OnTick(Tick);
        Registry.Section(Registry.PlusMode, "Graphics", DrawMenu, 20);
    }

    // ---------------------------------------------------------------- state

    private static bool Active => _enabled != null && _enabled.Value;
    private static bool ButoOn => Active && _affectButo.Value;
    private static bool CozyOn => Active && _affectCozy.Value;

    private static readonly Dictionary<IntPtr, ButoVolumetricFog> Fogs = new();
    private static CozyWeather _cozy;
    private static float _nextScan;
    private static bool _patchedButo, _patchedCozy;
    private static int _cozyCalls, _cozyFrame = -100;
    private static readonly HashSet<string> LoggedOnce = new();

    private static void LogOnce(string key, string msg)
    {
        if (LoggedOnce.Add(key)) Plugin.Logger.LogWarning($"[Fog] {msg}");
    }

    // ---------------------------------------------------------------- slots (base tracking)

    /// <summary>
    /// One overridden value. Its getter/setter are made once per value (never per frame), so the per-frame work is a
    /// read, maybe a write, and no allocations.
    /// </summary>
    private sealed class Slot
    {
        public readonly Func<Vector4> Get;
        public readonly Action<Vector4> Set;
        public bool Has;
        public Vector4 Base, Last;
        public int Version = -1;
        public float Arg;

        public Slot(Func<Vector4> get, Action<Vector4> set) { Get = get; Set = set; }
    }

    /// <summary>Bumped whenever any setting changes, so a slot knows its last write is still current.</summary>
    private static int _settingsVersion;

    private static void OnSettingChanged(object sender, EventArgs args) => _settingsVersion++;

    private static bool Near(Vector4 a, Vector4 b)
    {
        float d = (a - b).sqrMagnitude;
        return d <= 1e-8f * Mathf.Max(1f, b.sqrMagnitude);
    }

    /// <summary>
    /// Applies <paramref name="fn"/>(base, <paramref name="arg"/>) to the game's base value of one setting, or (when not
    /// wanted) hands the base back. The base is re-captured whenever the current value is not what we last wrote, i.e.
    /// the game changed it. When the game left our value alone and no setting changed, nothing is written.
    /// </summary>
    private static void Track(Slot s, bool want, Func<Vector4, float, Vector4> fn, float arg)
    {
        if (s == null) return;
        if (!want)
        {
            if (!s.Has) return;
            s.Has = false;
            s.Version = -1;
            if (Near(s.Get(), s.Last)) s.Set(s.Base);
            return;
        }
        var cur = s.Get();
        if (!s.Has || !Near(cur, s.Last)) { s.Base = cur; s.Has = true; }
        else if (s.Version == _settingsVersion && s.Arg == arg) return; // still exactly what we wrote
        s.Set(fn(s.Base, arg));
        s.Last = s.Get(); // read back: setters may clamp, colours may be converted
        s.Version = _settingsVersion;
        s.Arg = arg;
    }

    private static Vector4 V(float x) => new(x, 0f, 0f, 0f);

    // Value functions (static: no per-frame closures). Each gets the game's base value and one argument.
    private static readonly Func<Vector4, float, Vector4> FMulPos = (b, a) => V(Mathf.Max(0f, b.x * a));
    private static readonly Func<Vector4, float, Vector4> FMul = (b, a) => V(b.x * a);
    private static readonly Func<Vector4, float, Vector4> FAdd = (b, a) => V(b.x + a);
    private static readonly Func<Vector4, float, Vector4> FConst = (b, a) => V(a);
    private static readonly Func<Vector4, float, Vector4> FRange = (b, a) => V(Mathf.Max(1f, b.x * a));
    private static readonly Func<Vector4, float, Vector4> FAniso = (b, g) => V(Mathf.Clamp(b.x + (g - 1f) * 0.2f, -0.95f, 0.95f));
    private static readonly Func<Vector4, float, Vector4> FHeightFog = (b, h) => V(Mathf.Min(Mathf.Max(0f, b.x * h), Mathf.Max(1f, b.x)));
    private static readonly Func<Vector4, float, Vector4> FTintGamma = (c, _) => Tint(c, _tintGamma);
    private static readonly Func<Vector4, float, Vector4> FTintLinear = (c, _) => Tint(c, _tintLinear);
    private static Color _tintGamma, _tintLinear;

    // ---------------------------------------------------------------- shared maths

    private static float DensityFactor => _noFog.Value ? 0f : Mathf.Max(0f, _density.Value) / Mathf.Max(0.05f, _visibility.Value);

    private static Color TintColor => new(Mathf.Clamp01(_tintR.Value), Mathf.Clamp01(_tintG.Value), Mathf.Clamp01(_tintB.Value), 1f);

    private static float Luma(Vector4 c) => 0.2126f * c.x + 0.7152f * c.y + 0.0722f * c.z;

    /// <summary>Moves a colour toward the tint while keeping its brightness (and alpha).</summary>
    private static Vector4 Tint(Vector4 c, Color tint)
    {
        float s = Mathf.Clamp01(_tintStrength.Value);
        var t = new Vector4(tint.r, tint.g, tint.b, 0f);
        float lt = Mathf.Max(0.05f, Luma(t));
        var target = t * (Luma(c) / lt);
        var o = Vector4.Lerp(c, target, s);
        o.w = c.w;
        return o;
    }

    private static bool WantDensity => _noFog.Value || !Mathf.Approximately(_density.Value, 1f) || !Mathf.Approximately(_visibility.Value, 1f);
    private static bool WantTint => _tintStrength.Value > 0.001f;

    // ---------------------------------------------------------------- Buto volumetric fog

    /// <summary>The slots of one Buto fog component, made once when the component is first seen.</summary>
    private sealed class ButoSlots
    {
        public Slot Mode, ModeO, Density, DensityO, Light, LightO, Aniso, AnisoO, Range, RangeO, Height, HeightO;
        public Slot Lit, LitO, Shadow, ShadowO;
    }

    private static readonly Dictionary<IntPtr, ButoSlots> ButoState = new();

    private static Slot FloatSlot(VolumeParameter<float> p) => p == null ? null : new Slot(() => V(p.value), v => p.value = v.x);
    private static Slot ColorSlot(VolumeParameter<Color> p) => p == null ? null : new Slot(() => (Vector4)p.value, v => p.value = (Color)v);
    private static Slot OverrideSlot(VolumeParameter p) => p == null ? null : new Slot(() => V(p.overrideState ? 1f : 0f), v => p.overrideState = v.x > 0.5f);

    private static ButoSlots SlotsFor(ButoVolumetricFog f)
    {
        if (ButoState.TryGetValue(f.Pointer, out var s)) return s;
        s = new ButoSlots();
        var mode = f.mode;
        if (mode != null)
        {
            s.Mode = new Slot(() => V((int)mode.value), v => mode.value = (VolumetricFogMode)Mathf.RoundToInt(v.x));
            s.ModeO = OverrideSlot(mode);
        }
        VolumeParameter<float> p = f.fogDensity; s.Density = FloatSlot(p); s.DensityO = OverrideSlot(p);
        p = f.lightIntensity; s.Light = FloatSlot(p); s.LightO = OverrideSlot(p);
        p = f.anisotropy; s.Aniso = FloatSlot(p); s.AnisoO = OverrideSlot(p);
        p = f.maxDistanceVolumetric; s.Range = FloatSlot(p); s.RangeO = OverrideSlot(p);
        p = f.baseHeight; s.Height = FloatSlot(p); s.HeightO = OverrideSlot(p);
        VolumeParameter<Color> c = f.litColor; s.Lit = ColorSlot(c); s.LitO = OverrideSlot(c);
        c = f.shadowedColor; s.Shadow = ColorSlot(c); s.ShadowO = OverrideSlot(c);
        ButoState[f.Pointer] = s;
        return s;
    }

    private static void ScanButo()
    {
        try
        {
            foreach (var o in Object.FindObjectsOfType(Il2CppType.Of<Volume>()))
            {
                var vol = o.TryCast<Volume>();
                VolumeProfile prof = null;
                try { prof = vol?.profileRef; } catch { prof = vol?.sharedProfile; }
                var comps = prof?.components;
                if (comps == null) continue;
                for (int i = 0; i < comps.Count; i++)
                {
                    var bf = comps[i]?.TryCast<ButoVolumetricFog>();
                    if (bf != null) Fogs[bf.Pointer] = bf;
                }
            }
            foreach (var o in Object.FindObjectsOfType(Il2CppType.Of<CozyButoModule>()))
            {
                var bf = o.TryCast<CozyButoModule>()?.fog;
                if (bf != null) Fogs[bf.Pointer] = bf;
            }
        }
        catch (Exception e) { LogOnce("scanButo", "Buto scan failed: " + e.Message); }

        try
        {
            if (_cozy == null)
                foreach (var o in Object.FindObjectsOfType(Il2CppType.Of<CozyWeather>()))
                {
                    _cozy = o.TryCast<CozyWeather>();
                    if (_cozy != null) break;
                }
        }
        catch (Exception e) { LogOnce("scanCozy", "COZY scan failed: " + e.Message); }

        // Forget components that were destroyed (scene unloads). Live ones are kept even when no volume uses them
        // right now, so a profile coming back still has its slots and is never double-applied.
        List<IntPtr> dead = null;
        foreach (var kv in Fogs)
            if (kv.Value == null || kv.Value.WasCollected) (dead ??= new()).Add(kv.Key);
        if (dead != null)
            foreach (var p in dead)
            {
                Fogs.Remove(p);
                ButoState.Remove(p);
            }
    }

    /// <summary>A value and its override flag (forced on while we override the value).</summary>
    private static void Param(Slot value, Slot overrideFlag, bool want, Func<Vector4, float, Vector4> fn, float arg)
    {
        Track(value, want, fn, arg);
        Track(overrideFlag, want, FConst, 1f);
    }

    private static void ApplyButo(ButoVolumetricFog f, bool restore)
    {
        var s = SlotsFor(f);
        bool on = !restore && ButoOn;

        // Mode (On/Off) for "no fog".
        Param(s.Mode, s.ModeO, on && _noFog.Value, FConst, (int)VolumetricFogMode.Off);

        Param(s.Density, s.DensityO, on && WantDensity, FMulPos, DensityFactor);

        float g = Mathf.Max(0f, _godRays.Value);
        bool wantRays = on && !Mathf.Approximately(g, 1f);
        Param(s.Light, s.LightO, wantRays, FMulPos, g);
        Param(s.Aniso, s.AnisoO, wantRays, FAniso, g);

        float range = Mathf.Max(0.1f, _range.Value);
        Param(s.Range, s.RangeO, on && !Mathf.Approximately(range, 1f), FRange, range);

        float off = _heightOffset.Value;
        Param(s.Height, s.HeightO, on && Mathf.Abs(off) > 0.01f, FAdd, off);

        bool wantTint = on && WantTint;
        _tintGamma = TintColor;
        Param(s.Lit, s.LitO, wantTint, FTintGamma, 0f);
        Param(s.Shadow, s.ShadowO, wantTint, FTintGamma, 0f);
    }

    private static void ApplyAllButo(bool restore)
    {
        if (Fogs.Count == 0) return;
        foreach (var kv in Fogs)
        {
            var f = kv.Value;
            if (f == null || f.WasCollected) continue;
            try { ApplyButo(f, restore); }
            catch (Exception e) { LogOnce("buto", "Buto fog override failed: " + e.Message); }
        }
    }

    // ---------------------------------------------------------------- COZY shader globals

    private static bool _ids;
    private static Slot _gDepth, _gHeightInt, _gHeightBase, _gHeightDist, _gSkyFog, _gCloudFog, _gCloudFogLight;
    private static Slot[] _gStarts, _gColors;

    private static Slot GlobalFloat(int id) => new(() => V(Shader.GetGlobalFloat(id)), v => Shader.SetGlobalFloat(id, v.x));

    // Colours go through the vector API so no sRGB/linear conversion happens on the round trip.
    private static Slot GlobalColor(int id) => new(() => Shader.GetGlobalVector(id), v => Shader.SetGlobalVector(id, v));

    private static void EnsureIds()
    {
        if (_ids) return;
        _gDepth = GlobalFloat(CozyShaderIDs.CZY_FogDepthMultiplierID);
        _gHeightInt = GlobalFloat(CozyShaderIDs.CZY_HeightFogIntensityID);
        _gHeightBase = GlobalFloat(CozyShaderIDs.CZY_HeightFogBaseID);
        _gHeightDist = GlobalFloat(CozyShaderIDs.CZY_HeightFogDistanceID);
        _gSkyFog = GlobalFloat(CozyShaderIDs.CZY_SkyFogAmountID);
        _gCloudFog = GlobalFloat(CozyShaderIDs.CZY_CloudsFogAmountID);
        _gCloudFogLight = GlobalFloat(CozyShaderIDs.CZY_CloudsFogLightAmountID);
        _gStarts = new[]
        {
            GlobalFloat(CozyShaderIDs.CZY_FogColorStart1ID), GlobalFloat(CozyShaderIDs.CZY_FogColorStart2ID),
            GlobalFloat(CozyShaderIDs.CZY_FogColorStart3ID), GlobalFloat(CozyShaderIDs.CZY_FogColorStart4ID),
        };
        _gColors = new[]
        {
            GlobalColor(CozyShaderIDs.CZY_FogColor1ID), GlobalColor(CozyShaderIDs.CZY_FogColor2ID), GlobalColor(CozyShaderIDs.CZY_FogColor3ID),
            GlobalColor(CozyShaderIDs.CZY_FogColor4ID), GlobalColor(CozyShaderIDs.CZY_FogColor5ID), GlobalColor(CozyShaderIDs.CZY_HeightFogColorID),
            GlobalColor(CozyShaderIDs.CZY_FogLitColorID), GlobalColor(CozyShaderIDs.CZY_FogShadowColorID),
        };
        _ids = true;
    }

    private static void ApplyCozy(bool restore)
    {
        if (!_ids) { if (restore) return; EnsureIds(); }
        bool on = !restore && CozyOn;
        bool none = _noFog.Value;

        Track(_gDepth, on && WantDensity, FMulPos, DensityFactor);

        float h = none ? 0f : Mathf.Max(0f, _heightFog.Value) * Mathf.Max(0f, _density.Value);
        Track(_gHeightInt, on && (none || !Mathf.Approximately(h, 1f)), FHeightFog, h);

        float off = _heightOffset.Value;
        Track(_gHeightBase, on && Mathf.Abs(off) > 0.01f, FAdd, off);

        float vis = Mathf.Max(0.05f, _visibility.Value);
        bool wantVis = on && !Mathf.Approximately(vis, 1f);
        Track(_gHeightDist, wantVis, FMul, vis);
        foreach (var s in _gStarts) Track(s, wantVis, FMul, vis);

        float haze = Mathf.Max(0f, _skyHaze.Value);
        bool wantHaze = on && !Mathf.Approximately(haze, 1f);
        Track(_gSkyFog, wantHaze, FMulPos, haze);
        Track(_gCloudFog, wantHaze, FMulPos, haze);
        Track(_gCloudFogLight, wantHaze, FMulPos, haze);

        bool wantTint = on && WantTint;
        _tintLinear = TintColor.linear;
        foreach (var s in _gColors) Track(s, wantTint, FTintLinear, 0f);
    }

    private static IntPtr _meshPtr;
    private static Slot _meshSlot;

    /// <summary>Hides the COZY fog dome for "no fog" (restored when turned back on).</summary>
    private static void ApplyCozyMesh(CozyWeather w, bool restore)
    {
        var mesh = w?.fogMesh;
        if (mesh == null) return;
        if (_meshSlot == null || _meshPtr != mesh.Pointer)
        {
            // A different fog dome (new scene): the old one is gone, so its slot is simply dropped.
            var m = mesh;
            _meshPtr = mesh.Pointer;
            _meshSlot = new Slot(() => V(m.enabled ? 1f : 0f), v => m.enabled = v.x > 0.5f);
        }
        bool want = !restore && CozyOn && _noFog.Value;
        Track(_meshSlot, want, FConst, 0f);
    }

    // ---------------------------------------------------------------- Unity RenderSettings fog

    private static readonly Slot RsFog = new(() => V(RenderSettings.fog ? 1f : 0f), v => RenderSettings.fog = v.x > 0.5f);
    private static readonly Slot RsDensity = new(() => V(RenderSettings.fogDensity), v => RenderSettings.fogDensity = v.x);
    private static readonly Slot RsStart = new(() => V(RenderSettings.fogStartDistance), v => RenderSettings.fogStartDistance = v.x);
    private static readonly Slot RsEnd = new(() => V(RenderSettings.fogEndDistance), v => RenderSettings.fogEndDistance = v.x);
    private static readonly Slot RsColor = new(() => (Vector4)RenderSettings.fogColor, v => RenderSettings.fogColor = (Color)v);

    private static void ApplyRenderSettings(bool restore)
    {
        bool on = !restore && CozyOn;
        bool none = _noFog.Value;
        Track(RsFog, on && none, FConst, 0f);

        float dens = DensityFactor;
        bool wantD = on && WantDensity && !none;
        Track(RsDensity, wantD, FMulPos, dens);
        float lin = dens > 0.0001f ? 1f / dens : 1f;
        Track(RsStart, wantD, FMul, lin);
        Track(RsEnd, wantD, FMul, lin);

        _tintGamma = TintColor;
        Track(RsColor, on && WantTint, FTintGamma, 0f);
    }

    // ---------------------------------------------------------------- frame driver

    private static bool _hadWork;

    private static void Tick()
    {
        if (!Active)
        {
            if (_hadWork) RestoreAll();
            return;
        }
        _hadWork = true;

        if (Time.unscaledTime >= _nextScan)
        {
            _nextScan = Time.unscaledTime + 2f;
            ScanButo();
        }

        ApplyAllButo(!ButoOn);
        // The COZY postfix already applies RenderSettings each frame while it runs; only do it here otherwise.
        if (!CozyOn || Time.frameCount - _cozyFrame > 2)
        {
            try { ApplyRenderSettings(false); }
            catch (Exception e) { LogOnce("rs", "RenderSettings fog override failed: " + e.Message); }
        }
        if (!CozyOn) RestoreCozy();
    }

    private static void RestoreCozy()
    {
        try
        {
            ApplyCozy(true);
            if (_cozy != null && !_cozy.WasCollected) ApplyCozyMesh(_cozy, true);
            ApplyRenderSettings(true);
        }
        catch (Exception e) { LogOnce("restoreCozy", "COZY restore failed: " + e.Message); }
    }

    private static void RestoreAll()
    {
        _hadWork = false;
        ApplyAllButo(true);
        RestoreCozy();
    }

    // ---------------------------------------------------------------- Harmony (installed by hand so a missing method can't break plugin load)

    private static void InstallPatches()
    {
        Harmony h;
        try { h = new Harmony(Plugin.Guid + ".gfx-fog"); }
        catch (Exception e) { Plugin.Logger.LogError($"[Fog] Harmony unavailable: {e.Message}"); return; }

        try
        {
            var m = AccessTools.Method(typeof(CozyButoModule), nameof(CozyButoModule.Update));
            if (m != null)
            {
                h.Patch(m, postfix: new HarmonyMethod(typeof(FogAtmosphere), nameof(ButoModulePostfix)));
                _patchedButo = true;
            }
        }
        catch (Exception e) { Plugin.Logger.LogWarning($"[Fog] Could not patch CozyButoModule.Update: {e.Message}"); }

        try
        {
            var m = AccessTools.Method(typeof(CozyWeather), nameof(CozyWeather.UpdateShaderVariables));
            if (m != null)
            {
                h.Patch(m, postfix: new HarmonyMethod(typeof(FogAtmosphere), nameof(CozyShaderPostfix)));
                _patchedCozy = true;
            }
        }
        catch (Exception e) { Plugin.Logger.LogWarning($"[Fog] Could not patch CozyWeather.UpdateShaderVariables: {e.Message}"); }
    }

    // COZY rewrites Buto density/colours every Update; apply on top of that write.
    private static void ButoModulePostfix(CozyButoModule __instance)
    {
        if (!ButoOn) return;
        try
        {
            var f = __instance?.fog;
            if (f == null) return;
            if (!Fogs.ContainsKey(f.Pointer)) Fogs[f.Pointer] = f;
            ApplyButo(f, false);
        }
        catch (Exception e) { LogOnce("butoPost", "Buto postfix failed: " + e.Message); }
    }

    // COZY just pushed its CZY_* globals; adjust them before anything renders.
    private static void CozyShaderPostfix(CozyWeather __instance)
    {
        if (!CozyOn) return;
        try
        {
            _cozyCalls++;
            _cozyFrame = Time.frameCount;
            if (__instance != null) _cozy = __instance;
            ApplyCozy(false);
            ApplyCozyMesh(__instance, false);
            ApplyRenderSettings(false);
        }
        catch (Exception e) { LogOnce("cozyPost", "COZY postfix failed: " + e.Message); }
    }

    // ---------------------------------------------------------------- menu

    private static void Set(ConfigEntry<float> e, float v) => e.Value = v;

    private static void ResetAll()
    {
        _noFog.Value = false;
        foreach (var e in new[] { _density, _visibility, _heightFog, _heightOffset, _skyHaze, _godRays, _range, _tintStrength, _tintR, _tintG, _tintB })
            e.Value = (float)e.DefaultValue;
    }

    private static void Look(float density, float vis, float height, float haze, float rays)
    {
        _enabled.Value = true;
        _noFog.Value = false;
        Set(_density, density); Set(_visibility, vis); Set(_heightFog, height); Set(_skyHaze, haze); Set(_godRays, rays);
    }

    private static void TintTo(float r, float g, float b, float strength)
    {
        _enabled.Value = true;
        Set(_tintR, r); Set(_tintG, g); Set(_tintB, b); Set(_tintStrength, strength);
    }

    // ---- Simple view (UI/SimplePages: Graphics > Fog) ----

    /// <summary>Fog looks offered in the Simple view: the game's fog, three looks of the card below, no fog.</summary>
    internal static readonly string[] SimpleLooks = { "Game", "Clear", "Light haze", "Thick fog", "No fog" };

    // density, visibility, ground fog, sky haze, god rays: the same values as the card's buttons
    private static readonly float[][] SimpleLookValues =
    {
        new[] { 0.35f, 2.5f, 0.4f, 0.6f, 1f },
        new[] { 0.7f, 1.5f, 0.8f, 0.9f, 1.2f },
        new[] { 2.2f, 0.6f, 1.8f, 1.3f, 1f },
    };

    /// <summary>Index into <see cref="SimpleLooks"/> of the current fog, or -1 for a custom one.</summary>
    internal static int CurrentSimpleLook()
    {
        if (_enabled == null) return -1;
        if (!_enabled.Value) return 0;
        if (_noFog.Value) return 4;
        for (int i = 0; i < SimpleLookValues.Length; i++)
        {
            var v = SimpleLookValues[i];
            if (Near(_density, v[0]) && Near(_visibility, v[1]) && Near(_heightFog, v[2]) && Near(_skyHaze, v[3]) && Near(_godRays, v[4])
                && Near(_heightOffset, 0f) && _tintStrength.Value < 0.001f)
                return i + 1;
        }
        return -1;
    }

    private static bool Near(ConfigEntry<float> e, float v) => Mathf.Abs(e.Value - v) < 0.005f;

    internal static void ApplySimpleLook(int index)
    {
        if (_enabled == null) return;
        if (index <= 0) { _enabled.Value = false; return; }
        if (index == 4)
        {
            _enabled.Value = true;
            _noFog.Value = true;
            return;
        }
        var v = SimpleLookValues[Mathf.Clamp(index - 1, 0, SimpleLookValues.Length - 1)];
        Look(v[0], v[1], v[2], v[3], v[4]);
        Set(_heightOffset, 0f);
        Set(_tintStrength, 0f);
    }

    private static void DrawMenu()
    {
        bool on = _enabled.Value;
        bool shape = on && !_noFog.Value;

        Menu.Card("Fog & atmosphere", "Only changes what you see. Turn it off and the game's own fog comes straight back.", () =>
        {
            Menu.Switch(_enabled, "Fog overrides", "Master switch for everything on this card", true);
            Menu.Switch(_noFog, "No fog", "Removes volumetric fog, COZY fog and ground fog", on);
            Menu.SliderRow(_density, "Density", 0f, 3f, "0.00", "", shape, percent: true);
            Menu.SliderRow(_visibility, "Visibility distance", 0.25f, 4f, "0.00", "x", shape);
            Menu.SliderRow(_heightFog, "Ground (height) fog", 0f, 3f, "0.00", "", shape, percent: true);
            Menu.SliderRow(_heightOffset, "Fog layer height", -100f, 100f, "0", " m", shape);
            Menu.SliderRow(_skyHaze, "Sky and cloud haze", 0f, 2f, "0.00", "", on, percent: true);
            Menu.SliderRow(_godRays, "God rays", 0f, 3f, "0.00", "", shape, percent: true);
            Menu.SliderRow(_range, "Volumetric range", 0.5f, 3f, "0.00", "x", shape);
            Menu.Chips(3,
                ("Clear", () => Look(0.35f, 2.5f, 0.4f, 0.6f, 1f), true),
                ("Light haze", () => Look(0.7f, 1.5f, 0.8f, 0.9f, 1.2f), true),
                ("Thick fog", () => Look(2.2f, 0.6f, 1.8f, 1.3f, 1f), true),
                ("God rays", () => Look(1.3f, 1f, 1f, 1f, 2.4f), true),
                ("Valley mist", () => { Look(1f, 1f, 2.5f, 1f, 1.3f); Set(_heightOffset, -10f); }, true),
                ("Reset", ResetAll, true));
            Menu.Small("Volumetric range above 1x spreads the same fog detail over a longer distance, so close-up fog gets blockier.");
        });

        Menu.Card("Fog colour", "Tints fog towards a colour while keeping its brightness.", () =>
        {
            Menu.SliderRow(_tintStrength, "Tint strength", 0f, 1f, "0.00", "", on, percent: true);
            bool t = on && _tintStrength.Value > 0.001f;
            Menu.SliderRow(_tintR, "Red", 0f, 1f, "0.00", "", t);
            Menu.SliderRow(_tintG, "Green", 0f, 1f, "0.00", "", t);
            Menu.SliderRow(_tintB, "Blue", 0f, 1f, "0.00", "", t);
            var r = Menu.NextRow(18f);
            Theme.Fill(new Rect(r.x + 4f, r.y, r.width - 8f, r.height), TintColor, 6);
            Menu.Chips(3,
                ("Warm", () => TintTo(1f, 0.82f, 0.6f, 0.45f), true),
                ("Golden hour", () => TintTo(1f, 0.7f, 0.35f, 0.6f), true),
                ("Cool blue", () => TintTo(0.6f, 0.78f, 1f, 0.45f), true),
                ("Misty white", () => TintTo(0.92f, 0.94f, 0.96f, 0.6f), true),
                ("Rose", () => TintTo(1f, 0.65f, 0.75f, 0.45f), true),
                ("No tint", () => Set(_tintStrength, 0f), true));
        });

        Menu.Card("Fog status", null, () =>
        {
            Menu.Switch(_affectButo, "Volumetric fog (Buto)", null, on);
            Menu.Switch(_affectCozy, "COZY fog, ground fog and sky haze", null, on);
            int alive = 0;
            ButoVolumetricFog first = null;
            foreach (var kv in Fogs)
                if (kv.Value != null && !kv.Value.WasCollected) { alive++; first ??= kv.Value; }
            string butoInfo = alive == 0 ? (on ? "not found in this scene" : "scanned when enabled") : alive + " profile" + (alive == 1 ? "" : "s");
            if (first != null)
            {
                try { butoInfo += $"  |  {first.mode?.value}  density {first.fogDensity?.value:0.###}"; }
                catch { /* display only */ }
            }
            Menu.KeyValue("Volumetric fog", butoInfo);
            string cozyInfo;
            if (_cozy == null || _cozy.WasCollected) cozyInfo = _patchedCozy ? "waiting for COZY weather" : "hook failed (see log)";
            else
            {
                try { cozyInfo = "found  |  style " + _cozy.fogStyle; }
                catch { cozyInfo = "found"; }
            }
            Menu.KeyValue("COZY weather", cozyInfo);
            Menu.KeyValue("Hooks", (_patchedButo ? "Buto ok" : "Buto missing") + ", " + (_patchedCozy ? "COZY ok" : "COZY missing") + (_cozyCalls > 0 ? "  (live)" : ""));
        });
    }
}
