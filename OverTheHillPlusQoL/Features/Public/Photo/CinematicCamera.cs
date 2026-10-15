using System;
using Cinemachine;
using Il2CppInterop.Runtime;
using UnityEngine;
using Object = UnityEngine.Object;

namespace OverTheHillPlus.Features.Public.Photo;

/// <summary>
/// Cinematic chase camera: FOV, distance, height, followed-point offsets, dutch angle and a longer zoom-out range,
/// applied to the game's own camera rig. Purely local (your view only), so it works in every session.
///
/// How the game's rig works (from the decompiled code):
/// - CameraSettingsInitializer.cinemachineVirtualCamera is the main CinemachineFreeLook (FOV lives in m_Lens).
/// - ExtraCameraControls.UpdateRadii sets every rig orbit each frame to originalRigHeights/Radii[i] * zoomMultiplier,
///   with the zoom target clamped to [minZoom, maxZoom]. Scaling the "original" arrays therefore moves the camera
///   without fighting the game, and the mouse-wheel zoom keeps working on top.
/// - CameraFollowUpdater.UpdateCameraTarget places the followed point at car + up * yOffset + forward * zOffset.
/// Every value we touch is remembered per instance and put back exactly when the feature is switched off.
/// </summary>
internal static class CinematicCamera
{
    internal readonly struct Preset
    {
        public readonly string Name;
        public readonly float Fov, Distance, Height, TargetHeight, LookAhead, Dutch;

        public Preset(string name, float fov, float distance, float height, float targetHeight, float lookAhead, float dutch)
        {
            Name = name; Fov = fov; Distance = distance; Height = height;
            TargetHeight = targetHeight; LookAhead = lookAhead; Dutch = dutch;
        }
    }

    public static readonly Preset[] Presets =
    {
        new("Close chase", 72f, 0.72f, 0.85f, 0f, 0.6f, 0f),
        new("Far chase", 58f, 1.6f, 1.25f, 0f, 0f, 0f),
        new("Low & wide", 88f, 0.85f, 0.4f, -0.25f, 0f, 0f),
        new("Aerial", 55f, 2.3f, 2.8f, 0f, 0f, 0f),
        new("Telephoto", 26f, 2.9f, 1f, 0.3f, 0f, 0f),
        new("Dutch drama", 48f, 1.1f, 0.65f, 0.1f, 0f, 12f),
    };

    private static CameraSettingsInitializer _init;
    private static ExtraCameraControls _zoom;
    private static float _nextFind;

    // Baselines (the game's values before we changed anything), per instance.
    private static IntPtr _zoomPtr;
    private static float[] _baseHeights, _baseRadii;
    private static float _baseMaxZoom;
    private static IntPtr _followPtr;
    private static float _baseY, _baseZ;
    private static IntPtr _lensPtr;
    private static float _baseDutch;
    private static bool _fovTouched;

    private static bool _applied;
    private static bool _loggedError;

    public static string Status { get; private set; } = "off";

    public static void ApplyPreset(Preset p)
    {
        PhotoConfig.CamFov.Value = p.Fov;
        PhotoConfig.CamDistance.Value = p.Distance;
        PhotoConfig.CamHeight.Value = p.Height;
        PhotoConfig.CamTargetHeight.Value = p.TargetHeight;
        PhotoConfig.CamLookAhead.Value = p.LookAhead;
        PhotoConfig.CamDutch.Value = p.Dutch;
        PhotoConfig.CinematicPreset.Value = p.Name;
        PhotoConfig.Cinematic.Value = true;
    }

    public static void ResetValues()
    {
        PhotoConfig.CamFov.Value = 0f;
        PhotoConfig.CamDistance.Value = 1f;
        PhotoConfig.CamHeight.Value = 1f;
        PhotoConfig.CamTargetHeight.Value = 0f;
        PhotoConfig.CamLookAhead.Value = 0f;
        PhotoConfig.CamDutch.Value = 0f;
        PhotoConfig.CamZoomOutRange.Value = 1f;
        PhotoConfig.CinematicPreset.Value = "Custom";
    }

    public static void Tick()
    {
        bool on = PhotoConfig.Cinematic.Value;
        if (!on && !_applied) return;
        try
        {
            Find();
            if (on) Apply();
            else Restore();
        }
        catch (Exception e)
        {
            if (!_loggedError) { _loggedError = true; Plugin.Logger.LogWarning("Cinematic camera: " + e); }
            Status = "error: " + e.Message;
        }
    }

    private static void Find()
    {
        // Unity's == null also catches destroyed objects (scene changes), so we only search when something is missing.
        if (_init != null && _zoom != null) return;
        if (Time.unscaledTime < _nextFind) return;
        _nextFind = Time.unscaledTime + 1f;

        if (_init == null) _init = Object.FindObjectOfType<CameraSettingsInitializer>();
        if (_zoom == null)
        {
            IntPtr main = IntPtr.Zero;
            try { main = _init?.cinemachineVirtualCamera?.Pointer ?? IntPtr.Zero; } catch { }
            ExtraCameraControls first = null;
            foreach (var o in Object.FindObjectsOfType(Il2CppType.Of<ExtraCameraControls>()))
            {
                var z = o.TryCast<ExtraCameraControls>();
                if (z == null) continue;
                first ??= z;
                try
                {
                    // Prefer the one driving the main chase camera (not a garage/menu rig).
                    if (main != IntPtr.Zero && z.freeLookCamera != null && z.freeLookCamera.Pointer == main) { first = z; break; }
                }
                catch { }
            }
            _zoom = first;
        }
    }

    private static void Apply()
    {
        _applied = true;
        int parts = 0;

        // Rig orbits (distance / height) and zoom-out range.
        var z = _zoom;
        if (z != null)
        {
            var heights = z.originalRigHeights;
            var radii = z.originalRigRadii;
            if (heights != null && radii != null && heights.Length > 0 && radii.Length == heights.Length)
            {
                if (z.Pointer != _zoomPtr || _baseHeights == null || _baseHeights.Length != heights.Length)
                {
                    _zoomPtr = z.Pointer;
                    _baseHeights = new float[heights.Length];
                    _baseRadii = new float[radii.Length];
                    for (int i = 0; i < heights.Length; i++) { _baseHeights[i] = heights[i]; _baseRadii[i] = radii[i]; }
                    _baseMaxZoom = z.maxZoom;
                }
                float dist = Mathf.Clamp(PhotoConfig.CamDistance.Value, 0.2f, 6f);
                float height = Mathf.Clamp(PhotoConfig.CamHeight.Value, 0f, 6f);
                for (int i = 0; i < heights.Length; i++)
                {
                    float wantH = _baseHeights[i] * dist * height;
                    float wantR = _baseRadii[i] * dist;
                    if (Mathf.Abs(heights[i] - wantH) > 0.0001f) heights[i] = wantH;
                    if (Mathf.Abs(radii[i] - wantR) > 0.0001f) radii[i] = wantR;
                }
                float wantMax = _baseMaxZoom * Mathf.Clamp(PhotoConfig.CamZoomOutRange.Value, 1f, 5f);
                if (Mathf.Abs(z.maxZoom - wantMax) > 0.0001f) z.maxZoom = wantMax;
                parts++;
            }
        }

        // Followed point.
        var f = CameraFollowUpdater.ActiveCameraUpdater;
        if (f != null)
        {
            if (f.Pointer != _followPtr)
            {
                _followPtr = f.Pointer;
                _baseY = f.yOffset;
                _baseZ = f.zOffset;
            }
            float wantY = _baseY + Mathf.Clamp(PhotoConfig.CamTargetHeight.Value, -3f, 6f);
            float wantZ = _baseZ + Mathf.Clamp(PhotoConfig.CamLookAhead.Value, -6f, 10f);
            if (Mathf.Abs(f.yOffset - wantY) > 0.0001f) f.yOffset = wantY;
            if (Mathf.Abs(f.zOffset - wantZ) > 0.0001f) f.zOffset = wantZ;
            parts++;
        }

        // Lens: FOV and dutch.
        var cam = _init == null ? null : _init.cinemachineVirtualCamera;
        if (cam != null)
        {
            var lens = cam.m_Lens;
            if (cam.Pointer != _lensPtr)
            {
                _lensPtr = cam.Pointer;
                _baseDutch = lens.Dutch;
            }
            bool dirty = false;
            float fov = PhotoConfig.CamFov.Value;
            if (fov >= 5f)
            {
                fov = Mathf.Clamp(fov, 5f, 150f);
                if (Mathf.Abs(lens.FieldOfView - fov) > 0.01f) { lens.FieldOfView = fov; dirty = true; }
                _fovTouched = true;
            }
            else if (_fovTouched)
            {
                RestoreFov(ref lens);
                dirty = true;
            }
            float dutch = _baseDutch + Mathf.Clamp(PhotoConfig.CamDutch.Value, -45f, 45f);
            if (Mathf.Abs(lens.Dutch - dutch) > 0.01f) { lens.Dutch = dutch; dirty = true; }
            if (dirty) cam.m_Lens = lens;
            parts++;
        }

        Status = parts == 0 ? "waiting for the driving camera" : "active";
    }

    private static void RestoreFov(ref LensSettings lens)
    {
        _fovTouched = false;
        // The QoL FOV override (if set) re-applies itself next frame; otherwise go back to the game setting.
        if (ModConfig.FovOverride.Value > 0f) return;
        try
        {
            int game = _init != null ? _init.currentFOV : 0;
            if (game > 0) lens.FieldOfView = game;
        }
        catch { }
    }

    private static void Restore()
    {
        var z = _zoom;
        if (z != null && z.Pointer == _zoomPtr && _baseHeights != null)
        {
            var heights = z.originalRigHeights;
            var radii = z.originalRigRadii;
            if (heights != null && radii != null && heights.Length == _baseHeights.Length && radii.Length == _baseRadii.Length)
            {
                for (int i = 0; i < heights.Length; i++) { heights[i] = _baseHeights[i]; radii[i] = _baseRadii[i]; }
            }
            z.maxZoom = _baseMaxZoom;
        }
        _zoomPtr = IntPtr.Zero;
        _baseHeights = _baseRadii = null;

        var f = CameraFollowUpdater.ActiveCameraUpdater;
        if (f != null && f.Pointer == _followPtr)
        {
            f.yOffset = _baseY;
            f.zOffset = _baseZ;
        }
        _followPtr = IntPtr.Zero;

        var cam = _init == null ? null : _init.cinemachineVirtualCamera;
        if (cam != null && cam.Pointer == _lensPtr)
        {
            var lens = cam.m_Lens;
            lens.Dutch = _baseDutch;
            if (_fovTouched) RestoreFov(ref lens);
            cam.m_Lens = lens;
        }
        _lensPtr = IntPtr.Zero;
        _fovTouched = false;

        _applied = false;
        Status = "off";
    }
}
