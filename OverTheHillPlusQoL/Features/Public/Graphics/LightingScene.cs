using System;
using System.Collections.Generic;
using HorizonBasedAmbientOcclusion.Universal;
using Il2CppInterop.Runtime;
using OccaSoftware.LSPP.Runtime;
using TND.Upscaling.Framework;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using BeautifyFx = global::Beautify.Universal.Beautify;
using Object = UnityEngine.Object;

namespace OverTheHillPlus.Features.Public.Gfx;

/// <summary>
/// Finds the rendering objects the lighting/quality settings act on, refreshed every few seconds (never per frame):
/// the camera's UpscalerManager (TND: DLSS/FSR/SGSR) and URP camera data, and the HBAO / light-scattering /
/// Beautify volume components in every active Volume profile.
///
/// This mirrors what the game's GraphicsSettingsState does: it takes HBAO and Beautify from
/// SceneReferences.UrpVolume.profile and the upscaler + camera data from the main camera.
/// </summary>
internal static class LightingScene
{
    public static UpscalerManager Upscalers;
    public static TNDUpscaler Tnd;
    public static UniversalAdditionalCameraData CameraData;
    public static readonly List<HBAO> Hbaos = new();
    public static readonly List<LightScatteringPostProcess> GodRays = new();
    public static readonly List<BeautifyFx> Beautifies = new();
    public static readonly List<UpscalerName> Supported = new();

    /// <summary>The components on the game's main URP volume, for showing current values.</summary>
    public static HBAO MainHbao;
    public static LightScatteringPostProcess MainGodRays;
    public static BeautifyFx MainBeautify;

    private static float _nextScan;
    private static bool _scanFailedLogged;

    public static bool Alive(Object o)
    {
        try { return o != null && o.Pointer != IntPtr.Zero && o; }
        catch { return false; }
    }

    /// <summary>The TND upscaler is on: it then owns render scale and the camera's AA.</summary>
    public static bool TndActive
    {
        get
        {
            try { return Alive(Tnd) && Tnd.enabled; }
            catch { return false; }
        }
    }

    public static UniversalRenderPipelineAsset UrpAsset
    {
        get
        {
            try { return UniversalRenderPipeline.asset; }
            catch { return null; }
        }
    }

    public static void Tick(bool force)
    {
        float now = Time.unscaledTime;
        bool lost = (Upscalers != null && !Alive(Upscalers)) || (MainHbao != null && !Alive(MainHbao));
        if (!force && !lost && now < _nextScan) return;
        _nextScan = now + 3f;
        try { Scan(); }
        catch (Exception e)
        {
            if (!_scanFailedLogged) Plugin.Logger.LogWarning($"[Graphics] scene scan failed: {e.Message}");
            _scanFailedLogged = true;
        }
    }

    private static void Scan()
    {
        // ---- upscaler + camera data ----
        if (!Alive(Upscalers))
        {
            Upscalers = null;
            Tnd = null;
            Supported.Clear();
            var found = Object.FindObjectOfType(Il2CppType.Of<UpscalerManager>());
            Upscalers = found == null ? null : found.TryCast<UpscalerManager>();
            if (Upscalers != null)
            {
                try { Tnd = Upscalers.tndUpscaler; } catch { Tnd = null; }
                try
                {
                    var list = Upscalers.supportedUpscalers;
                    if (list != null)
                        for (int i = 0; i < list.Count; i++)
                            if (!Supported.Contains(list[i])) Supported.Add(list[i]);
                }
                catch { }
            }
        }
        if (!Alive(CameraData))
        {
            CameraData = null;
            Component c = null;
            if (Upscalers != null) c = Upscalers.GetComponent(Il2CppType.Of<UniversalAdditionalCameraData>());
            if (c == null)
            {
                var cam = Camera.main;
                if (cam != null) c = cam.GetComponent(Il2CppType.Of<UniversalAdditionalCameraData>());
            }
            CameraData = c == null ? null : c.TryCast<UniversalAdditionalCameraData>();
        }

        // ---- volume components ----
        Hbaos.Clear();
        GodRays.Clear();
        Beautifies.Clear();
        MainHbao = null;
        MainGodRays = null;
        MainBeautify = null;

        VolumeProfile mainProfile = null;
        try
        {
            var refs = SceneReferences.Instance;
            if (refs != null && refs.UrpVolume != null) mainProfile = refs.UrpVolume.profileRef;
        }
        catch { }

        var volumes = Object.FindObjectsOfType(Il2CppType.Of<Volume>());
        if (volumes == null) return;
        var seen = new HashSet<IntPtr>();
        foreach (var o in volumes)
        {
            var v = o == null ? null : o.TryCast<Volume>();
            if (v == null) continue;
            VolumeProfile profile;
            try { profile = v.profileRef; } catch { continue; }
            if (profile == null || !seen.Add(profile.Pointer)) continue;
            bool isMain = mainProfile != null && profile.Pointer == mainProfile.Pointer;
            var comps = profile.components;
            if (comps == null) continue;
            for (int i = 0; i < comps.Count; i++)
            {
                var comp = comps[i];
                if (comp == null) continue;
                var h = comp.TryCast<HBAO>();
                if (h != null)
                {
                    Hbaos.Add(h);
                    if (isMain || MainHbao == null) MainHbao = h;
                    continue;
                }
                var g = comp.TryCast<LightScatteringPostProcess>();
                if (g != null)
                {
                    GodRays.Add(g);
                    if (isMain || MainGodRays == null) MainGodRays = g;
                    continue;
                }
                var b = comp.TryCast<BeautifyFx>();
                if (b != null)
                {
                    Beautifies.Add(b);
                    if (isMain || (mainProfile == null && MainBeautify == null)) MainBeautify = b;
                }
            }
        }
    }
}
