using System;
using HorizonBasedAmbientOcclusion.Universal;
using TND.Upscaling.Framework;
using UnityEngine;
using UnityEngine.Rendering.Universal;
using C = OverTheHillPlus.Features.Public.Gfx.GraphicsLighting;
using D = OverTheHillPlus.Features.Public.Gfx.LightingDriver;
using S = OverTheHillPlus.Features.Public.Gfx.LightingScene;

namespace OverTheHillPlus.Features.Public.Gfx;

/// <summary>
/// Applies the Graphics.Lighting settings once a second (and ~10x a second right after a setting changes).
/// All of it is local rendering only: nothing here touches the network session, so it works everywhere.
/// </summary>
internal static class LightingApply
{
    public static bool Dirty;
    private static float _nextApply, _lastApply;

    // Read-outs for the menu, rebuilt each pass so OnGUI never touches game objects.
    public static string AaNow = "Load into a world", ResNow = "-", AoNow = "-", GodRaysNow = "-", SharpenNow = "-";
    public static float AoIntensityMax = 4f, AoRadiusMin = 0.25f, AoRadiusMax = 5f, SharpenMax = 5f;
    public static int SamplesMax = 64;
    public static bool GodRaysZero;

    public static void Tick()
    {
        float now = Time.unscaledTime;
        S.Tick(false);
        if (now < _nextApply && !(Dirty && now - _lastApply >= 0.1f)) return;
        Dirty = false;
        _lastApply = now;
        _nextApply = now + 1f;
        ApplyAll();
        BuildReadouts();
    }

    public static void ApplyAll()
    {
        Safe("anti-aliasing", ApplyAntiAliasing);
        Safe("upscaler sharpening", ApplyUpscalerSharpening);
        Safe("render pipeline", ApplyPipelineAsset);
        Safe("ambient occlusion", ApplyAmbientOcclusion);
        Safe("god rays", ApplyGodRays);
        Safe("sharpening", ApplySharpening);
        D.EndPass();
    }

    private static void Safe(string what, Action a)
    {
        try { a(); }
        catch (Exception e) { DebugLog.Error("graphics", e, "LightingApply.Safe"); D.LogOnce("apply:" + what, $"[Graphics] applying {what} failed: {e.Message}"); }
    }

    // ---------------- anti-aliasing / upscalers ----------------

    internal static readonly string[] StaticModes = { "Off", "FXAA", "SMAA", "TAA" };

    internal static bool IsStatic(string mode) => Array.IndexOf(StaticModes, mode) >= 0;

    internal static bool TryUpscaler(string mode, out UpscalerName name) =>
        Enum.TryParse(mode, out name) && name != UpscalerName.None;

    internal static bool UpscalerUsable(string mode) =>
        TryUpscaler(mode, out var n) && S.Supported.Contains(n) && S.Alive(S.Tnd) && S.Alive(S.Upscalers);

    private readonly struct AaState
    {
        public readonly bool Tnd;
        public readonly int Up, Q, Cam, CamQ;
        public AaState(bool tnd, int up, int q, int cam, int camQ) { Tnd = tnd; Up = up; Q = q; Cam = cam; CamQ = camQ; }
        public override string ToString() => Tnd ? $"{(UpscalerName)Up} {(UpscalerQuality)Q}" : $"{(AntialiasingMode)Cam} q{CamQ}";
    }

    private static bool AaEq(AaState a, AaState b) =>
        a.Tnd == b.Tnd && a.Cam == b.Cam
        && (!a.Tnd || (a.Up == b.Up && a.Q == b.Q))
        && (a.Tnd || a.Cam < 2 || a.CamQ == b.CamQ);

    private static bool AaWanted()
    {
        string m = C.AntiAliasing.Value;
        if (m == "Game" || !S.Alive(S.CameraData)) return false;
        if (IsStatic(m)) return true;
        return UpscalerUsable(m);
    }

    private static AaState ReadAa()
    {
        var cd = S.CameraData;
        bool on = S.TndActive;
        int up = on ? (int)S.Tnd.GetPrimaryUpscalerName() : -1;
        int q = on ? (int)S.Tnd.GetQuality() : -1;
        return new AaState(on, up, q, (int)cd.antialiasing, (int)cd.antialiasingQuality);
    }

    private static void WriteAa(AaState s)
    {
        var cd = S.CameraData;
        if (s.Tnd)
        {
            // Same order as the game's GraphicsSettingsState.ApplyAntiAliasing: camera AA off, upscaler on, quality.
            cd.antialiasing = (AntialiasingMode)s.Cam;
            var um = S.Upscalers;
            var tnd = S.Tnd;
            var name = (UpscalerName)s.Up;
            if (!tnd.enabled || tnd.GetPrimaryUpscalerName() != name) um.SetUpscaler(name);
            var q = (UpscalerQuality)s.Q;
            if (tnd.GetQuality() != q) um.SetQuality(q);
        }
        else
        {
            if (S.TndActive) S.Tnd.enabled = false;
            cd.antialiasing = (AntialiasingMode)s.Cam;
            cd.antialiasingQuality = (AntialiasingQuality)Mathf.Clamp(s.CamQ, 0, 2);
        }
    }

    private static AaState AaTarget(AaState b)
    {
        string m = C.AntiAliasing.Value;
        int idx = Array.IndexOf(StaticModes, m);
        if (idx >= 0) return new AaState(false, -1, -1, idx, Mathf.Clamp(C.AaQuality.Value, 0, 2));
        TryUpscaler(m, out var name);
        return new AaState(true, (int)name, (int)ParseQuality(C.UpscaleQuality.Value), (int)AntialiasingMode.None, b.CamQ);
    }

    internal static UpscalerQuality ParseQuality(string s) =>
        Enum.TryParse(s, out UpscalerQuality q) && q != UpscalerQuality.Off && q != UpscalerQuality.Custom ? q : UpscalerQuality.Quality;

    private static void ApplyAntiAliasing()
    {
        if (!S.Alive(S.CameraData)) return;
        D.Drive<AaState>("aa", S.CameraData, AaWanted, ReadAa, WriteAa, AaTarget, AaEq);
    }

    private static void ApplyUpscalerSharpening()
    {
        var tnd = S.Tnd;
        if (!S.Alive(tnd)) return;
        D.Drive<(bool on, float v)>("tnd-sharpen", tnd,
            () => C.UpscalerSharpen.Value && S.TndActive,
            () => (tnd.GetSharpening(), tnd.GetSharpness()),
            x => { tnd.SetSharpening(x.on); tnd.SetSharpness(x.v); },
            b => C.UpscalerSharpness.Value > 0.001f ? (true, Mathf.Clamp01(C.UpscalerSharpness.Value)) : (false, b.v),
            (a, b) => a.on == b.on && Mathf.Abs(a.v - b.v) < 0.0005f);
    }

    // ---------------- URP asset: render scale, upscale filter, MSAA ----------------

    private static void ApplyPipelineAsset()
    {
        var asset = S.UrpAsset;
        if (asset == null) return;

        // While a TND upscaler runs it sets renderScale itself at the start of every camera render.
        D.Drive<float>("render-scale", asset,
            () => C.RenderScaleOn.Value && !S.TndActive,
            () => asset.renderScale,
            v => asset.renderScale = v,
            _ => Mathf.Clamp(C.RenderScale.Value, 0.3f, 2f),
            (a, b) => Mathf.Abs(a - b) < 0.001f);

        D.Drive<int>("upscale-filter", asset,
            () => C.UpscaleFilter.Value != "Game" && Enum.TryParse(C.UpscaleFilter.Value, out UpscalingFilterSelection _),
            () => (int)asset.upscalingFilter,
            v => asset.upscalingFilter = (UpscalingFilterSelection)v,
            b => Enum.TryParse(C.UpscaleFilter.Value, out UpscalingFilterSelection f) ? (int)f : b,
            (a, b) => a == b);

        D.Drive<(bool on, float v)>("fsr-sharpness", asset,
            () => C.UpscaleFilter.Value == "FSR",
            () => (asset.fsrOverrideSharpness, asset.fsrSharpness),
            x => { asset.fsrOverrideSharpness = x.on; asset.fsrSharpness = x.v; },
            _ => (true, Mathf.Clamp01(C.FsrSharpness.Value)),
            (a, b) => a.on == b.on && Mathf.Abs(a.v - b.v) < 0.0005f);

        // MSAA is left alone while a temporal upscaler runs (they expect a non-MSAA colour target).
        D.Drive<int>("msaa", asset,
            () => C.Msaa.Value > 0 && !S.TndActive,
            () => asset.msaaSampleCount,
            v => asset.msaaSampleCount = v,
            _ => C.Msaa.Value switch { >= 8 => 8, >= 4 => 4, >= 2 => 2, _ => 1 },
            (a, b) => a == b);
    }

    // ---------------- HBAO ----------------

    private static void ApplyAmbientOcclusion()
    {
        foreach (var h in S.Hbaos)
        {
            if (!S.Alive(h)) continue;
            var hb = h;
            D.Drive<bool>("ao-active", hb,
                () => C.AmbientOcclusion.Value is "On" or "Off",
                () => hb.active,
                v => hb.active = v,
                _ => C.AmbientOcclusion.Value == "On",
                (a, b) => a == b);

            var pi = hb.intensity;
            if (pi != null)
                D.DriveParam("ao-intensity", hb, () => C.AoTune.Value,
                    () => pi.value, v => pi.value = v, () => pi.overrideState, o => pi.overrideState = o,
                    _ => Mathf.Clamp(C.AoIntensity.Value, pi.min, pi.max));

            var pr = hb.radius;
            if (pr != null)
                D.DriveParam("ao-radius", hb, () => C.AoTune.Value,
                    () => pr.value, v => pr.value = v, () => pr.overrideState, o => pr.overrideState = o,
                    _ => Mathf.Clamp(C.AoRadius.Value, pr.min, pr.max));

            var pq = hb.quality;
            if (pq != null)
                D.Drive<(int q, bool ov)>("ao-quality", hb,
                    () => C.AoQuality.Value != "Game" && Enum.TryParse(C.AoQuality.Value, out HBAO.Quality _),
                    () => ((int)hb.GetQuality(), pq.overrideState),
                    x => { hb.SetQuality((HBAO.Quality)x.q); pq.overrideState = x.ov; },
                    b => (Enum.TryParse(C.AoQuality.Value, out HBAO.Quality q) ? (int)q : b.q, true),
                    (a, b) => a.q == b.q && a.ov == b.ov);
        }
    }

    // ---------------- LSPP god rays ----------------

    private static void ApplyGodRays()
    {
        foreach (var g in S.GodRays)
        {
            if (!S.Alive(g)) continue;
            var gr = g;
            var pd = gr.fogDensity;
            if (pd != null)
                D.DriveParam("godrays-density", gr, () => C.GodRays.Value is "Off" or "Tuned",
                    () => pd.value, v => pd.value = v, () => pd.overrideState, o => pd.overrideState = o,
                    b => C.GodRays.Value == "Off" ? Mathf.Max(pd.min, 0f) : Mathf.Max(pd.min, b * Mathf.Max(0f, C.GodRayStrength.Value)));

            var pl = gr.maxRayDistance;
            if (pl != null)
                D.DriveParam("godrays-length", gr, () => C.GodRays.Value == "Tuned" && Mathf.Abs(C.GodRayLength.Value - 1f) > 0.01f,
                    () => pl.value, v => pl.value = v, () => pl.overrideState, o => pl.overrideState = o,
                    b => Mathf.Clamp(b * C.GodRayLength.Value, pl.min, pl.max));

            var ps = gr.numberOfSamples;
            if (ps != null)
                D.Drive<(int v, bool ov)>("godrays-samples", gr,
                    () => C.GodRays.Value == "Tuned" && C.GodRaySamples.Value > 0,
                    () => (ps.value, ps.overrideState),
                    x => { ps.overrideState = x.ov; ps.value = x.v; },
                    _ => (Mathf.Clamp(C.GodRaySamples.Value, ps.min, ps.max), true),
                    (a, b) => a.v == b.v && a.ov == b.ov);
        }
    }

    // ---------------- Beautify sharpen ----------------

    private static void ApplySharpening()
    {
        // Only the main URP volume: photo mode has its own Beautify that it drives itself.
        var b = S.MainBeautify;
        if (!S.Alive(b)) return;
        var p = b.sharpenIntensity;
        if (p == null) return;
        D.DriveParam("sharpen", b, () => C.SharpenOn.Value,
            () => p.value, v => p.value = v, () => p.overrideState, o => p.overrideState = o,
            _ => Mathf.Clamp(C.Sharpen.Value, p.min, p.max));
    }

    // ---------------- read-outs ----------------

    internal static string UpscalerLabel(UpscalerName n) => n switch
    {
        UpscalerName.DLSS3 or UpscalerName.DLSS4 => "DLSS",
        UpscalerName.FSR1 => "FSR 1",
        UpscalerName.FSR2 => "FSR 2",
        UpscalerName.FSR3 => "FSR 3",
        UpscalerName.FSR4 => "FSR 4",
        UpscalerName.SGSR1 => "SGSR 1",
        UpscalerName.SGSR2 => "SGSR 2",
        UpscalerName.XeSS1 or UpscalerName.XeSS2 or UpscalerName.XeSS3 => "XeSS",
        _ => n.ToString(),
    };

    internal static string QualityLabel(UpscalerQuality q) => q switch
    {
        UpscalerQuality.NativeAA => "Native AA",
        UpscalerQuality.UltraQuality => "Ultra quality",
        UpscalerQuality.UltraPerformance => "Ultra performance",
        _ => q.ToString(),
    };

    private static string AaLabel(AntialiasingMode m) => m switch
    {
        AntialiasingMode.None => "Off",
        AntialiasingMode.FastApproximateAntialiasing => "FXAA",
        AntialiasingMode.SubpixelMorphologicalAntiAliasing => "SMAA",
        AntialiasingMode.TemporalAntiAliasing => "TAA",
        _ => m.ToString(),
    };

    private static void BuildReadouts()
    {
        try
        {
            if (!S.Alive(S.CameraData)) AaNow = "Load into a world to see this";
            else if (S.TndActive)
            {
                var t = S.Tnd;
                string size = "";
                try
                {
                    var r = t.ScaledRenderSize;
                    var d = t.DisplaySize;
                    if (r.x > 0 && d.x > 0) size = $"  |  renders {r.x}x{r.y} -> {d.x}x{d.y}";
                }
                catch (Exception ex) { DebugLog.Error("graphics", ex, "LightingApply.BuildReadouts"); }
                AaNow = $"{UpscalerLabel(t.GetActiveUpscaler())}, {QualityLabel(t.GetQuality())}{size}";
            }
            else
            {
                var cd = S.CameraData;
                var m = cd.antialiasing;
                AaNow = AaLabel(m) + (m >= AntialiasingMode.SubpixelMorphologicalAntiAliasing ? $" ({cd.antialiasingQuality})" : "");
            }
        }
        catch (Exception ex) { DebugLog.Error("graphics", ex, "LightingApply.BuildReadouts"); AaNow = "-"; }

        try
        {
            var a = S.UrpAsset;
            ResNow = a == null
                ? "-"
                : $"{a.renderScale * 100f:0}% render scale  |  filter {a.upscalingFilter}  |  MSAA {a.msaaSampleCount}x"
                  + (S.TndActive ? "  (upscaler drives the resolution)" : "");
        }
        catch (Exception ex) { DebugLog.Error("graphics", ex, "LightingApply.BuildReadouts"); ResNow = "-"; }

        try
        {
            var h = S.MainHbao;
            if (!S.Alive(h)) AoNow = "No HBAO found (load into a world)";
            else
            {
                AoNow = $"{(h.active ? "On" : "Off")}, {h.GetQuality()}  |  intensity {h.intensity.value:0.00}  |  radius {h.radius.value:0.00}"
                        + (S.Hbaos.Count > 1 ? $"  ({S.Hbaos.Count} volumes)" : "");
                AoIntensityMax = Mathf.Clamp(h.intensity.max, 1f, 10f);
                AoRadiusMin = Mathf.Max(0.05f, h.radius.min);
                AoRadiusMax = Mathf.Clamp(h.radius.max, AoRadiusMin + 0.5f, 10f);
            }
        }
        catch (Exception ex) { DebugLog.Error("graphics", ex, "LightingApply.BuildReadouts"); AoNow = "-"; }

        try
        {
            var g = S.MainGodRays;
            if (!S.Alive(g)) { GodRaysNow = "No light scattering found in this scene"; GodRaysZero = false; }
            else
            {
                float dens = g.fogDensity.value;
                GodRaysZero = dens <= 0.00001f && C.GodRays.Value != "Off";
                GodRaysNow = $"density {dens:0.###}  |  samples {g.numberOfSamples.value}  |  ray length {g.maxRayDistance.value:0.#}"
                             + (S.GodRays.Count > 1 ? $"  ({S.GodRays.Count} volumes)" : "");
                SamplesMax = Mathf.Clamp(g.numberOfSamples.max, 8, 256);
            }
        }
        catch (Exception ex) { DebugLog.Error("graphics", ex, "LightingApply.BuildReadouts"); GodRaysNow = "-"; }

        try
        {
            var b = S.MainBeautify;
            if (!S.Alive(b)) SharpenNow = "No post-process sharpen found (load into a world)";
            else
            {
                SharpenNow = $"sharpen {b.sharpenIntensity.value:0.00}";
                SharpenMax = Mathf.Clamp(b.sharpenIntensity.max, 1f, 10f);
            }
        }
        catch (Exception ex) { DebugLog.Error("graphics", ex, "LightingApply.BuildReadouts"); SharpenNow = "-"; }
    }
}
