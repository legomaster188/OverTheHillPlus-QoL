using System;
using System.Collections.Generic;
using HarmonyLib;
using Il2CppInterop.Runtime;
using UnityEngine;
using Constraints = FreeCameraMovement.MovementConstraints;
using OverTheHillPlus.Photo;

namespace OverTheHillPlus.Features.Public.Photo;

/// <summary>
/// Wider limits for the game's photo mode: how far the free camera may roam from the car, the zoom (FOV) range,
/// free pitch, and wider effect sliders (exposure, contrast, saturation, white balance, vignette, bloom, lens dirt,
/// film grain, focus distance, aperture).
///
/// What the decompiled code shows:
/// - FreeCameraManager.ActivatePhotoModeCamera is inlined into StatePhotoModeMenu.Open and copies
///   cameraConfigs.photoModeCameraConfig (fovLimit, movementConstraints, pitchRotationLimit, maxCameraDistance)
///   straight into FreeCameraMovement. So we widen that config object (picked up on every photo mode start) and,
///   after StatePhotoModeMenu.Open, push it into the live FreeCameraMovement with SetCameraConfig.
/// - FreeCameraMovement.ProcessMovement clamps the camera to maxDistance from the car unless the
///   DisallowMaxDistance flag is set; ProcessZoom clamps FOV to fovLimit.
/// - StatePhotoModeEffectsMenu.Open initialises every effect slider from PhotoModeCameraDefaultSettings'
///   *ValueRange fields each time it opens, so widening those ranges (before it opens) widens the sliders.
/// Only the photo mode config is touched (never the polaroid camera used for animal photos).
/// Everything is restored to the game's values when the option is switched off.
/// </summary>
internal static class PhotoModeLimits
{
    public static bool PhotoModeOpen { get; private set; }
    public static bool PatchesOk { get; private set; }
    public static string LastApplied { get; private set; } = "";

    private sealed class CameraOrig
    {
        public float FovMin, FovMax, MaxDistance, PitchMin, PitchMax;
        public Constraints Flags;
    }

    private static readonly Dictionary<IntPtr, CameraOrig> CameraOrigs = new();
    private static readonly Dictionary<IntPtr, string> CameraApplied = new();
    private static readonly Dictionary<IntPtr, float[]> RangeOrigs = new();
    private static float _nextTick;
    private static bool _loggedError;

    public const float NoLimit = 20f;
    /// <summary>In multiplayer the camera may roam at most this many times the game's limit and never without a
    /// limit, so photo mode cannot be used to scout the map. "No limit" works in single-player only.</summary>
    public const float MultiplayerMaxRoam = 4f;

    /// <summary>True only in a single-player session (the only place an unlimited free camera is allowed).</summary>
    public static bool SinglePlayer
    {
        get
        {
            try { var r = GameState.Runner; return r != null && r.IsRunning && r.IsSinglePlayer; }
            catch (Exception ex) { DebugLog.Error("photo", ex, "PhotoModeLimits.SinglePlayer"); return false; }
        }
    }

    /// <summary>The roam multiplier that applies right now (the setting, capped in multiplayer).</summary>
    public static float EffectiveRoam =>
        SinglePlayer ? PhotoConfig.PhotoDistance.Value : Mathf.Min(PhotoConfig.PhotoDistance.Value, MultiplayerMaxRoam);

    // ---- patches ----

    public static void Install()
    {
        var h = new Harmony(PhotoPlugin.Guid + ".photo");
        int ok = 0;
        ok += TryPatch(h, typeof(StatePhotoModeMenu), nameof(StatePhotoModeMenu.Open), nameof(PhotoOpenPostfix), prefix: false);
        ok += TryPatch(h, typeof(StatePhotoModeMenu), nameof(StatePhotoModeMenu.Close), nameof(PhotoClosePostfix), prefix: false);
        ok += TryPatch(h, typeof(StatePhotoModeEffectsMenu), nameof(StatePhotoModeEffectsMenu.Open), nameof(EffectsOpenPrefix), prefix: true);
        PatchesOk = ok == 3;
    }

    private static int TryPatch(Harmony h, Type type, string method, string handler, bool prefix)
    {
        try
        {
            var target = AccessTools.Method(type, method, Type.EmptyTypes);
            if (target == null) { PhotoPlugin.Logger.LogWarning($"Photo: {type.Name}.{method} not found"); return 0; }
            var hm = new HarmonyMethod(typeof(PhotoModeLimits), handler);
            if (prefix) h.Patch(target, prefix: hm);
            else h.Patch(target, postfix: hm);
            return 1;
        }
        catch (Exception e)
        {
            DebugLog.Error("photo", e, "PhotoModeLimits.TryPatch");
            PhotoPlugin.Logger.LogWarning($"Photo: could not patch {type.Name}.{method}: {e.Message}");
            return 0;
        }
    }

    private static void PhotoOpenPostfix()
    {
        PhotoModeOpen = true;
        try { ApplyCameraConfig(pushLive: true); }
        catch (Exception e) { DebugLog.Error("photo", e, "PhotoModeLimits.PhotoOpenPostfix"); LogOnce(e); }
    }

    private static void PhotoClosePostfix() => PhotoModeOpen = false;

    private static void EffectsOpenPrefix(StatePhotoModeEffectsMenu __instance)
    {
        try { ApplyEffectRanges(__instance); }
        catch (Exception e) { DebugLog.Error("photo", e, "PhotoModeLimits.EffectsOpenPrefix"); LogOnce(e); }
    }

    private static void LogOnce(Exception e)
    {
        if (_loggedError) return;
        _loggedError = true;
        PhotoPlugin.Logger.LogWarning("Photo mode limits: " + e);
    }

    // ---- per frame (throttled) ----

    public static void Tick()
    {
        if (Time.unscaledTime < _nextTick) return;
        _nextTick = Time.unscaledTime + 1f;
        try { ApplyCameraConfig(pushLive: PhotoModeOpen); }
        catch (Exception e) { DebugLog.Error("photo", e, "PhotoModeLimits.Tick"); LogOnce(e); }
    }

    /// <summary>Called when a setting changes in the menu, so an open photo mode updates right away.</summary>
    public static void Refresh() => _nextTick = 0f;

    // ---- camera limits ----

    private static string Signature() =>
        !PhotoConfig.ExtendPhotoMode.Value ? "off"
            : $"{EffectiveRoam:0.00}|{PhotoConfig.PhotoFovMin.Value:0.0}|{PhotoConfig.PhotoFovMax.Value:0.0}|{PhotoConfig.PhotoFreePitch.Value}";

    private static void ApplyCameraConfig(bool pushLive)
    {
        var mgr = FreeCameraManager.Instance;
        if (mgr == null) return;
        var configs = mgr.cameraConfigs;
        var cfg = configs == null ? null : configs.photoModeCameraConfig;
        if (cfg == null) return;

        IntPtr ptr = cfg.Pointer;
        if (!CameraOrigs.TryGetValue(ptr, out var orig))
        {
            var fl = cfg.fovLimit;
            var pl = cfg.pitchRotationLimit;
            orig = new CameraOrig
            {
                FovMin = fl.Min, FovMax = fl.Max,
                PitchMin = pl.Min, PitchMax = pl.Max,
                MaxDistance = cfg.maxCameraDistance,
                Flags = cfg.movementConstraints,
            };
            CameraOrigs[ptr] = orig;
            PhotoPlugin.Logger.LogInfo($"Photo: game photo-mode limits: FOV {orig.FovMin:0.#}-{orig.FovMax:0.#}, distance {orig.MaxDistance:0.#} m, pitch {orig.PitchMin:0.#}..{orig.PitchMax:0.#}, flags {orig.Flags}");
        }

        string sig = Signature();
        // Never touched yet = the game's own values ("off"), so nothing is written until the option is used.
        if (!CameraApplied.TryGetValue(ptr, out var applied)) applied = "off";
        if (applied == sig && !pushLive) return;
        if (applied != sig)
        {
            if (sig == "off") WriteCamera(cfg, orig.FovMin, orig.FovMax, orig.MaxDistance, orig.PitchMin, orig.PitchMax, orig.Flags);
            else
            {
                float fovMin = Mathf.Min(orig.FovMin, Mathf.Clamp(PhotoConfig.PhotoFovMin.Value, 1f, 60f));
                float fovMax = Mathf.Max(orig.FovMax, Mathf.Clamp(PhotoConfig.PhotoFovMax.Value, 60f, 170f));
                var flags = orig.Flags;
                float mult = EffectiveRoam;
                float dist = orig.MaxDistance;
                if (mult >= NoLimit - 0.01f)
                {
                    flags |= Constraints.DisallowMaxDistance; // "no max distance" (skips the clamp in ProcessMovement)
                    if (dist > 0f) dist *= NoLimit;
                }
                else if (dist > 0f) dist *= Mathf.Clamp(mult, 1f, NoLimit);
                float pMin = orig.PitchMin, pMax = orig.PitchMax;
                if (PhotoConfig.PhotoFreePitch.Value)
                {
                    flags &= ~Constraints.LimitPitchRotation;
                    pMin = Mathf.Min(pMin, -89f);
                    pMax = Mathf.Max(pMax, 89f);
                }
                WriteCamera(cfg, fovMin, fovMax, dist, pMin, pMax, flags);
            }
            CameraApplied[ptr] = sig;
            LastApplied = sig;
        }

        if (pushLive && PhotoModeOpen && CameraApplied.ContainsKey(ptr) && mgr.IsActive)
        {
            var move = mgr.freeCameraMovement;
            if (move != null) move.SetCameraConfig(cfg);
        }
    }

    private static void WriteCamera(CameraConfigs.CameraConfig cfg, float fovMin, float fovMax, float dist, float pMin, float pMax, Constraints flags)
    {
        cfg.fovLimit = new MinMax<float>(fovMin, fovMax);
        cfg.pitchRotationLimit = new MinMax<float>(pMin, pMax);
        cfg.maxCameraDistance = dist;
        cfg.movementConstraints = flags;
    }

    // ---- effect slider ranges ----

    private enum Kind { Signed, Positive, Aperture }

    private static readonly (Func<PhotoModeCameraDefaultSettings, MinMax<float>> Get, Action<PhotoModeCameraDefaultSettings, MinMax<float>> Set, Kind Kind)[] Ranges =
    {
        (s => s.FocusDistanceValueRange, (s, v) => s.FocusDistanceValueRange = v, Kind.Positive),
        (s => s.ApertureValueRange, (s, v) => s.ApertureValueRange = v, Kind.Aperture),
        (s => s.ExposureValueRange, (s, v) => s.ExposureValueRange = v, Kind.Signed),
        (s => s.ContrastValueRange, (s, v) => s.ContrastValueRange = v, Kind.Signed),
        (s => s.SaturationValueRange, (s, v) => s.SaturationValueRange = v, Kind.Signed),
        (s => s.TemperatureValueRange, (s, v) => s.TemperatureValueRange = v, Kind.Signed),
        (s => s.TintValueRange, (s, v) => s.TintValueRange = v, Kind.Signed),
        (s => s.VignetteValueRange, (s, v) => s.VignetteValueRange = v, Kind.Positive),
        (s => s.BloomValueRange, (s, v) => s.BloomValueRange = v, Kind.Positive),
        (s => s.LensDirtValueRange, (s, v) => s.LensDirtValueRange = v, Kind.Positive),
        (s => s.FilmGrainValueRange, (s, v) => s.FilmGrainValueRange = v, Kind.Positive),
    };

    private static void ApplyEffectRanges(StatePhotoModeEffectsMenu menu)
    {
        var seen = new HashSet<IntPtr>();
        try
        {
            var fromState = menu?.statePhotoModeMenu?.DefaultCameraSettings;
            if (fromState != null && seen.Add(fromState.Pointer)) ApplyRanges(fromState);
        }
        catch (Exception e) { DebugLog.Error("photo", e, "PhotoModeLimits.ApplyEffectRanges"); LogOnce(e); }

        foreach (var o in Resources.FindObjectsOfTypeAll(Il2CppType.Of<PhotoModeCameraDefaultSettings>()))
        {
            var s = o.TryCast<PhotoModeCameraDefaultSettings>();
            if (s != null && seen.Add(s.Pointer)) ApplyRanges(s);
        }
    }

    private static void ApplyRanges(PhotoModeCameraDefaultSettings s)
    {
        IntPtr ptr = s.Pointer;
        bool on = PhotoConfig.ExtendEffectRanges.Value;
        if (!RangeOrigs.TryGetValue(ptr, out var orig))
        {
            if (!on) return; // never touched: nothing to restore
            orig = new float[Ranges.Length * 2];
            for (int i = 0; i < Ranges.Length; i++)
            {
                var r = Ranges[i].Get(s);
                orig[i * 2] = r.Min;
                orig[i * 2 + 1] = r.Max;
            }
            RangeOrigs[ptr] = orig;
            var sb = new System.Text.StringBuilder("Photo: game effect ranges (focus, aperture, exposure, contrast, saturation, temperature, tint, vignette, bloom, lens dirt, grain):");
            for (int i = 0; i < Ranges.Length; i++) sb.Append($" [{orig[i * 2]:0.###}, {orig[i * 2 + 1]:0.###}]");
            PhotoPlugin.Logger.LogInfo(sb.ToString());
        }

        float f = Mathf.Clamp(PhotoConfig.EffectRangeFactor.Value, 1f, 5f);
        for (int i = 0; i < Ranges.Length; i++)
        {
            float min = orig[i * 2], max = orig[i * 2 + 1];
            if (on && max > min)
            {
                float span = max - min;
                switch (Ranges[i].Kind)
                {
                    case Kind.Signed:
                        float c = (min + max) / 2f;
                        min = c - span / 2f * f;
                        max = c + span / 2f * f;
                        break;
                    case Kind.Positive:
                        max = min + span * f;
                        break;
                    case Kind.Aperture:
                        min = Mathf.Max(0.1f, min / f);
                        max *= f;
                        break;
                }
            }
            Ranges[i].Set(s, new MinMax<float>(min, max));
        }
    }
}
