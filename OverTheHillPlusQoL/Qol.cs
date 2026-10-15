using System;
using System.Collections.Generic;
using HarmonyLib;
using Il2CppInterop.Runtime;
using UnityEngine;
using Object = UnityEngine.Object;

namespace OverTheHillPlus;

/// <summary>Non-cheat quality-of-life tweaks. None of these give an advantage; all work in any session.</summary>
internal static class Qol
{
    private static float _nextSlowTick;
    private static CameraSettingsInitializer _camera;
    private static readonly Dictionary<IntPtr, float> CompassOriginal = new();
    private static bool _headlightsByUs;

    private static bool _wasFocused = true;
    private static bool _backgroundCapActive;
    private static int _savedTargetFrameRate, _savedVSync;

    public static void Tick()
    {
        ApplyFov();
        if (Application.isFocused != _wasFocused)
        {
            _wasFocused = Application.isFocused;
            ApplyFrameCap();
        }
        if (Time.unscaledTime < _nextSlowTick) return;
        _nextSlowTick = Time.unscaledTime + 1f;
        // Unity pauses unfocused windows by default: an alt-tabbed host would freeze the session for everyone.
        if (Application.runInBackground != ModConfig.RunInBackground.Value) Application.runInBackground = ModConfig.RunInBackground.Value;
        ApplyFrameCap();
        ApplyCompass();
        AutoHeadlights();
    }

    public static void ApplyFrameCap()
    {
        int bgCap = ModConfig.BackgroundFpsCap.Value;
        if (!Application.isFocused && bgCap > 0)
        {
            // Rendering slower in the background frees the PC for the focused window; the network
            // simulation runs on fixed ticks, so the session itself isn't slowed down.
            if (!_backgroundCapActive)
            {
                _savedTargetFrameRate = Application.targetFrameRate;
                _savedVSync = QualitySettings.vSyncCount;
                _backgroundCapActive = true;
            }
            if (QualitySettings.vSyncCount != 0) QualitySettings.vSyncCount = 0;
            if (Application.targetFrameRate != bgCap) Application.targetFrameRate = bgCap;
            return;
        }
        if (_backgroundCapActive)
        {
            _backgroundCapActive = false;
            Application.targetFrameRate = _savedTargetFrameRate;
            QualitySettings.vSyncCount = _savedVSync;
        }

        int cap = ModConfig.FpsCap.Value;
        if (cap <= 0) return;
        if (QualitySettings.vSyncCount != 0) QualitySettings.vSyncCount = 0;
        if (Application.targetFrameRate != cap) Application.targetFrameRate = cap;
    }

    private static void ApplyFov()
    {
        float fov = ModConfig.FovOverride.Value;
        if (fov <= 0f) return;
        try
        {
            if (_camera == null) _camera = Object.FindObjectOfType<CameraSettingsInitializer>();
            var cam = _camera?.cinemachineVirtualCamera;
            if (cam == null) return;
            var lens = cam.m_Lens;
            if (Math.Abs(lens.FieldOfView - fov) < 0.01f) return;
            lens.FieldOfView = fov;
            cam.m_Lens = lens;
        }
        catch { _camera = null; }
    }

    private static void ApplyCompass()
    {
        float mult = ModConfig.CompassRange.Value;
        try
        {
            foreach (var o in Object.FindObjectsOfType(Il2CppType.Of<CompassController>()))
            {
                var c = o.TryCast<CompassController>();
                if (c == null) continue;
                if (!CompassOriginal.TryGetValue(c.Pointer, out var orig)) CompassOriginal[c.Pointer] = orig = c.maxVisibleRadius;
                float want = orig * mult;
                if (Math.Abs(c.maxVisibleRadius - want) > 0.01f) c.maxVisibleRadius = want;
            }
        }
        catch { }
    }

    private static void AutoHeadlights()
    {
        if (!ModConfig.AutoHeadlights.Value) return;
        try
        {
            var tm = TimeManager.Instance;
            var input = GameState.LocalCar?.GetComponent<VehicleInput>();
            if (tm == null || input == null) return;
            int h = tm.Hours;
            bool night = h >= 19 || h < 7;
            if (night && !input.HeadlightsOn) { input.ToggleHeadlights(); _headlightsByUs = true; }
            else if (!night && input.HeadlightsOn && _headlightsByUs) { input.ToggleHeadlights(); _headlightsByUs = false; }
        }
        catch { }
    }
}

[HarmonyPatch(typeof(GraphicsSettingsState), nameof(GraphicsSettingsState.ApplyVSyncAndFrameRateCap))]
internal static class FrameCapPatch
{
    private static void Postfix() => Qol.ApplyFrameCap();
}

/// <summary>Skips the studio/NVIDIA/FMOD logo screens at startup.</summary>
[HarmonyPatch(typeof(StateDeveloperSplashScreen), nameof(StateDeveloperSplashScreen.Update))]
internal static class SkipSplashPatch
{
    private static readonly HashSet<IntPtr> Skipped = new();

    private static void Postfix(StateDeveloperSplashScreen __instance)
    {
        if (!ModConfig.SkipSplash.Value || !Skipped.Add(__instance.Pointer)) return;
        try { __instance.MoveToNextScreen(); }
        catch (Exception e) { Plugin.Logger.LogDebug("Skip splash: " + e.Message); }
    }
}

/// <summary>Shortens menu/screen fades (scaled when the fade opens; the constructor can't be patched for native callers).</summary>
[HarmonyPatch(typeof(StateFadeTransition), nameof(StateFadeTransition.Open))]
internal static class FasterFadesPatch
{
    private static readonly HashSet<IntPtr> Scaled = new();

    private static void Prefix(StateFadeTransition __instance)
    {
        if (Scaled.Count > 256) Scaled.Clear();
        if (!Scaled.Add(__instance.Pointer)) return;
        __instance.duration *= Mathf.Clamp(ModConfig.FadeSpeed.Value, 0.05f, 1f);
    }
}

/// <summary>Optional: no contextual tutorial hints.</summary>
[HarmonyPatch(typeof(TutorialManager), nameof(TutorialManager.StartTutorialPhase))]
internal static class NoTutorialHintsPatch
{
    private static bool Prefix() => !ModConfig.HideTutorialHints.Value;
}
