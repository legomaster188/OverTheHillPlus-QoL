using System;
using System.Collections.Generic;
using System.Text;
using Il2CppInterop.Runtime;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using BeautifyFx = Beautify.Universal.Beautify;
using Object = UnityEngine.Object;
using OverTheHillPlus.Graphics;

namespace OverTheHillPlus.Features.Public.Graphics;

/// <summary>
/// Drives the post-processing overhaul.
///
/// How the game does post-processing (from the decompiled code): SceneReferences.urpVolume is a global URP Volume
/// whose profile holds Beautify (bloom, contrast/saturation, tonemapping, film grain, lens dirt), MotionBlur and HBAO;
/// GraphicsSettingsState writes the Beautify values for the player's graphics options. Photo mode loads its own profile
/// ("Global Volume Profile Photo Mode") with Beautify + URP ColorAdjustments, WhiteBalance and Vignette.
///
/// Instead of editing the game's profiles (which the game rewrites whenever a setting changes), the mod adds its own
/// global Volume with a higher priority and its own runtime profile. Only parameters whose group is overridden have
/// overrideState set, so everything else keeps blending from the game's volume. Turning the feature off just disables
/// that volume, which restores the original look exactly: the game's objects are never written to.
/// </summary>
internal static class PostFxEngine
{
    internal sealed class VolumeInfo
    {
        public string Name, Profile, Components;
        public bool Global, Enabled;
        public float Priority, Weight;
        public int Layer;
    }

    private static GameObject _go;
    private static Volume _volume;
    private static VolumeProfile _profile;
    private static BeautifyFx _beautify;
    private static ColorAdjustments _color;
    private static WhiteBalance _wb;
    private static LiftGammaGain _lgg;
    private static Vignette _vignette;
    private static ChromaticAberration _chroma;

    private static bool _dirty = true;
    private static float _nextScan, _nextApply;
    private static bool _photoModeActive;
    private static int _gameLayer;
    private static float _gamePriority;
    private static bool _createFailed;
    private static readonly HashSet<string> LoggedErrors = new();

    internal static readonly List<VolumeInfo> GameVolumes = new();
    internal static bool? CameraPostProcessing;
    internal static string CameraUpdateMode = "?";
    internal static bool GameHasBeautify;
    internal static string LastError;

    internal static bool Active => _volume != null && _volume.enabled;
    internal static bool PhotoModeActive => _photoModeActive;

    private static float _menuSeenAt = -100f;

    public static void MarkDirty() => _dirty = true;

    /// <summary>The Graphics page is on screen: keep the diagnostics fresh even while the overhaul is off.</summary>
    public static void NoteMenuVisible()
    {
        if (Features.Public.MenuUx.Indexing) return; // the menu search index draws every tab; the menu is not showing it
        _menuSeenAt = Time.unscaledTime;
    }

    public static void Tick()
    {
        float now = Time.unscaledTime;
        bool want = PostFxSettings.Enabled.Value;

        // Scanning is read-only but not free: only while the overhaul is on or its menu page is being looked at.
        if (now >= _nextScan && (want || now - _menuSeenAt < 1f))
        {
            _nextScan = now + 2f;
            Scan();
        }

        if (!want)
        {
            if (_volume != null && _volume.enabled)
            {
                Safe("disable", () => _volume.enabled = false);
                RefreshCameraStack();
            }
            return;
        }

        if (_go == null || _volume == null || _profile == null)
        {
            if (_createFailed) return;
            Create();
            _dirty = true;
            if (_volume == null) return;
        }

        if (!_volume.enabled) { Safe("enable", () => _volume.enabled = true); _dirty = true; }

        // Values only change when the settings do; re-apply now and then as well, in case a scene change
        // or something else reset the runtime profile.
        if (_dirty || now >= _nextApply)
        {
            _dirty = false;
            _nextApply = now + 3f;
            Safe("apply", Apply);
            RefreshCameraStack();
        }
    }

    /// <summary>Called on unload/disable paths; leaves the game exactly as it was.</summary>
    public static void Shutdown()
    {
        Safe("shutdown", () =>
        {
            if (_go != null) Object.Destroy(_go);
            _go = null; _volume = null; _profile = null;
        });
    }

    // ---- volume creation ----

    private static void Create()
    {
        try
        {
            if (_go != null) Object.Destroy(_go);
            _go = new GameObject("OverTheHillPlus.PostFx");
            Object.DontDestroyOnLoad(_go);
            _go.hideFlags = HideFlags.DontSave;
            _go.layer = _gameLayer; // set before the Volume registers itself, so the camera's volume mask sees it

            _profile = ScriptableObject.CreateInstance(Il2CppType.Of<VolumeProfile>()).TryCast<VolumeProfile>();
            _profile.name = "OverTheHillPlus PostFx";
            _profile.hideFlags = HideFlags.DontSave;

            _beautify = Add<BeautifyFx>();
            _color = Add<ColorAdjustments>();
            _wb = Add<WhiteBalance>();
            _lgg = Add<LiftGammaGain>();
            _vignette = Add<Vignette>();
            _chroma = Add<ChromaticAberration>();

            _volume = _go.AddComponent<Volume>();
            _volume.isGlobal = true;
            _volume.priority = _gamePriority + 50f;
            _volume.weight = PostFxSettings.Strength.Value;
            _volume.sharedProfile = _profile;
            GraphicsPlugin.Logger.LogInfo($"PostFx: created override volume on layer {_gameLayer}, priority {_volume.priority}.");
        }
        catch (Exception e)
        {
            _createFailed = true;
            LastError = "Could not create the post-processing volume: " + e.Message;
            GraphicsPlugin.Logger.LogError("PostFx: volume creation failed: " + e);
            try { if (_go != null) Object.Destroy(_go); } catch (Exception ex) { DebugLog.Error("graphics", ex, "PostFxEngine.Create"); }
            _go = null; _volume = null; _profile = null;
        }
    }

    private static T Add<T>() where T : VolumeComponent
    {
        try
        {
            var c = _profile.Add(Il2CppType.Of<T>(), false);
            var t = c?.TryCast<T>();
            if (t != null) t.active = true;
            return t;
        }
        catch (Exception e)
        {
            DebugLog.Error("graphics", e, "PostFxEngine.Add");
            LogOnce("add " + typeof(T).Name, e);
            return null;
        }
    }

    /// <summary>Lets the user retry after a failure (menu button).</summary>
    internal static void Recreate()
    {
        _createFailed = false;
        LastError = null;
        Shutdown();
        _dirty = true;
    }

    // ---- applying settings ----

    private static void Apply()
    {
        float weight = Mathf.Clamp01(PostFxSettings.Strength.Value);
        if (PostFxSettings.PauseInPhotoMode.Value && _photoModeActive) weight = 0f;
        _volume.weight = weight;
        _volume.priority = _gamePriority + 50f;
        if (_go.layer != _gameLayer) _go.layer = _gameLayer;

        if (_beautify != null)
        {
            Safe("bloom", () =>
            {
                bool on = PostFxSettings.BloomOn.Value;
                F(_beautify.bloomIntensity, on, Mathf.Max(0f, PostFxSettings.BloomIntensity.Value));
                F(_beautify.bloomThreshold, on, Mathf.Max(0f, PostFxSettings.BloomThreshold.Value));
            });
            Safe("tonemap", () =>
            {
                bool on = PostFxSettings.TonemapOn.Value;
                var p = _beautify.tonemap;
                if (p != null)
                {
                    p.overrideState = on;
                    if (on) p.value = (BeautifyFx.TonemapOperator)Mathf.Clamp(PostFxSettings.TonemapMode.Value, 0, 3);
                }
                F(_beautify.tonemapExposurePre, on, Mathf.Max(0f, PostFxSettings.TonemapExposure.Value));
                F(_beautify.tonemapBrightnessPost, on, Mathf.Max(0f, PostFxSettings.TonemapBrightness.Value));
            });
            Safe("grain", () =>
            {
                bool on = PostFxSettings.GrainOn.Value;
                float v = Mathf.Clamp01(PostFxSettings.GrainIntensity.Value);
                B(_beautify.filmGrainEnabled, on, v > 0.001f);
                C(_beautify.filmGrainIntensity, on, v);
            });
            Safe("sharpen", () => C(_beautify.sharpenIntensity, PostFxSettings.SharpenOn.Value, Mathf.Max(0f, PostFxSettings.SharpenIntensity.Value)));
        }

        if (_color != null)
            Safe("color", () =>
            {
                bool on = PostFxSettings.ColorOn.Value;
                F(_color.postExposure, on, PostFxSettings.Exposure.Value);
                C(_color.contrast, on, PostFxSettings.Contrast.Value);
                C(_color.saturation, on, PostFxSettings.Saturation.Value);
                C(_color.hueShift, on, PostFxSettings.HueShift.Value);
            });

        if (_wb != null)
            Safe("white balance", () =>
            {
                bool on = PostFxSettings.WhiteBalanceOn.Value;
                C(_wb.temperature, on, PostFxSettings.Temperature.Value);
                C(_wb.tint, on, PostFxSettings.Tint.Value);
            });

        if (_lgg != null)
            Safe("lift gamma gain", () =>
            {
                bool on = PostFxSettings.LggOn.Value;
                float k = Mathf.Clamp(PostFxSettings.SplitTone.Value, -1f, 1f);
                // xyz is a colour (white = neutral), w an offset; URP removes the luminance from xyz, so only hue matters.
                var lift = new Vector4(1f - 0.15f * k, 1f + 0.03f * k, 1f + 0.15f * k, PostFxSettings.Lift.Value);
                var gamma = new Vector4(1f, 1f, 1f, PostFxSettings.Gamma.Value);
                var gain = new Vector4(1f + 0.12f * k, 1f + 0.02f * k, 1f - 0.12f * k, PostFxSettings.Gain.Value);
                V(_lgg.lift, on, lift);
                V(_lgg.gamma, on, gamma);
                V(_lgg.gain, on, gain);
            });

        if (_vignette != null)
            Safe("vignette", () =>
            {
                bool on = PostFxSettings.VignetteOn.Value;
                C(_vignette.intensity, on, Mathf.Clamp01(PostFxSettings.VignetteIntensity.Value));
                C(_vignette.smoothness, on, Mathf.Clamp(PostFxSettings.VignetteSmoothness.Value, 0.01f, 1f));
                if (_vignette.color != null) _vignette.color.overrideState = false;
            });

        if (_chroma != null)
            Safe("chromatic aberration", () =>
                C(_chroma.intensity, PostFxSettings.ChromaOn.Value, Mathf.Clamp01(PostFxSettings.ChromaIntensity.Value)));

        try { _profile.isDirty = true; } catch (Exception ex) { DebugLog.Error("graphics", ex, "PostFxEngine.Apply"); /* older/newer URP: not needed */ }
    }

    private static void F(FloatParameter p, bool on, float v)
    {
        if (p == null) return;
        p.overrideState = on;
        if (on) p.value = v;
    }

    private static void C(ClampedFloatParameter p, bool on, float v)
    {
        if (p == null) return;
        p.overrideState = on;
        if (on) p.value = Mathf.Clamp(v, p.min, p.max);
    }

    private static void B(BoolParameter p, bool on, bool v)
    {
        if (p == null) return;
        p.overrideState = on;
        if (on) p.value = v;
    }

    private static void V(Vector4Parameter p, bool on, Vector4 v)
    {
        if (p == null) return;
        p.overrideState = on;
        if (on) p.value = v;
    }

    /// <summary>
    /// Cameras set to update their volume stack "via scripting" would not notice our changes; nudge the main
    /// camera's stack after every apply (harmless when it already updates every frame).
    /// </summary>
    private static void RefreshCameraStack()
    {
        try
        {
            var cam = Camera.main;
            if (cam == null) return;
            var data = cam.GetComponent<UniversalAdditionalCameraData>();
            if (data == null) return;
            if (!data.requiresVolumeFrameworkUpdate) CameraExtensions.UpdateVolumeStack(cam, data);
        }
        catch (Exception e) { DebugLog.Error("graphics", e, "PostFxEngine.RefreshCameraStack"); LogOnce("camera stack", e); }
    }

    // ---- scanning the game's volumes (throttled, read-only) ----

    private static void Scan()
    {
        try
        {
            GameVolumes.Clear();
            GameHasBeautify = false;
            bool photo = false;
            float maxPriority = 0f;
            int layer = -1;

            // The game's main volume (SceneReferences.urpVolume) decides the layer the camera listens to.
            try
            {
                var refs = SceneReferences.Instance;
                var main = refs != null ? refs.UrpVolume : null;
                if (main != null) layer = main.gameObject.layer;
            }
            catch (Exception e) { DebugLog.Error("graphics", e, "PostFxEngine.Scan"); LogOnce("scene refs", e); }

            foreach (var o in Object.FindObjectsOfType(Il2CppType.Of<Volume>()))
            {
                var v = o.TryCast<Volume>();
                if (v == null || v == _volume) continue;
                VolumeProfile prof = null;
                try { prof = v.HasInstantiatedProfile() ? v.profile : v.sharedProfile; } catch (Exception ex) { DebugLog.Error("graphics", ex, "PostFxEngine.Scan"); }
                string profName = prof != null ? prof.name : "(none)";
                var info = new VolumeInfo
                {
                    Name = v.gameObject.name,
                    Profile = profName,
                    Global = v.isGlobal,
                    Enabled = v.enabled && v.gameObject.activeInHierarchy,
                    Priority = v.priority,
                    Weight = v.weight,
                    Layer = v.gameObject.layer,
                    Components = Describe(prof),
                };
                GameVolumes.Add(info);

                bool isPhoto = profName.IndexOf("Photo", StringComparison.OrdinalIgnoreCase) >= 0;
                if (isPhoto && info.Enabled && info.Weight > 0f) photo = true;
                if (!info.Enabled) continue;
                if (!isPhoto && info.Priority > maxPriority) maxPriority = info.Priority;
                if (prof != null && !GameHasBeautify)
                {
                    try { GameHasBeautify = prof.Has(Il2CppType.Of<BeautifyFx>()); } catch (Exception ex) { DebugLog.Error("graphics", ex, "PostFxEngine.Scan"); }
                }
                if (layer < 0 && info.Global) layer = info.Layer;
            }

            if (layer >= 0) _gameLayer = layer;
            _gamePriority = maxPriority;
            if (photo != _photoModeActive) { _photoModeActive = photo; _dirty = true; }

            try
            {
                var cam = Camera.main;
                var data = cam != null ? cam.GetComponent<UniversalAdditionalCameraData>() : null;
                if (data != null)
                {
                    CameraPostProcessing = data.renderPostProcessing;
                    CameraUpdateMode = data.requiresVolumeFrameworkUpdate ? "every frame" : "via scripting";
                }
                else CameraPostProcessing = null;
            }
            catch (Exception e) { DebugLog.Error("graphics", e, "PostFxEngine.Scan"); LogOnce("camera info", e); }
        }
        catch (Exception e) { DebugLog.Error("graphics", e, "PostFxEngine.Scan"); LogOnce("scan", e); }
    }

    private static string Describe(VolumeProfile prof)
    {
        if (prof == null) return "";
        try
        {
            var sb = new StringBuilder();
            var list = prof.components;
            for (int i = 0; list != null && i < list.Count; i++)
            {
                var c = list[i];
                if (c == null) continue;
                if (sb.Length > 0) sb.Append(", ");
                string n = c.GetIl2CppType().Name;
                sb.Append(c.active ? n : n + " (off)");
            }
            return sb.ToString();
        }
        catch (Exception e)
        {
            DebugLog.Error("graphics", e, "PostFxEngine.Describe");
            LogOnce("describe", e);
            return "?";
        }
    }

    // ---- helpers ----

    private static void Safe(string what, Action a)
    {
        try { a(); }
        catch (Exception e) { DebugLog.Error("graphics", e, "PostFxEngine.Safe"); LogOnce(what, e); }
    }

    private static void LogOnce(string what, Exception e)
    {
        LastError = $"{what}: {e.Message}";
        if (LoggedErrors.Add(what)) GraphicsPlugin.Logger.LogWarning($"PostFx {what} failed: {e}");
    }
}
