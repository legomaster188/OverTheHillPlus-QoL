using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using Cinemachine;
using GPUInstancerPro;
using GPUInstancerPro.TerrainModule;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering.Universal;
using Object = UnityEngine.Object;

namespace OverTheHillPlus.Features.Public.GraphicsDetail;

/// <summary>
/// Graphics tab: draw distance and detail beyond (or below) what the game's options menu allows.
///
/// Every knob is an opt-in override grouped under its own switch, and the game's own value is remembered as a
/// baseline: switching an override off puts the game's value back, and if the game changes a value while we
/// override it (options menu, quality preset, scene load), that becomes the new baseline and our value is
/// reapplied on top. So with everything off, the game's settings are untouched.
///
/// What the game itself does (from GraphicsSettingsState and the dev-command controller in the ISIL dump):
/// - Shadow distance: URP asset shadowDistance, then GPUICoreAPI.UpdateParameterBufferData().
/// - Vegetation distance: GPUIDetailManager.detailObjectDistance + its defaultProfile.minMaxDistance.y (grass),
///   GPUITreeManager.defaultProfile.minMaxDistance.y (trees), then UpdateParameterBufferData(). The dev command
///   "tree distance limit off" sets that tree profile to (0, 6000).
/// - Vegetation density: GPUIDetailPrototypeData.densityAdjustment per prototype + SetParameterBufferData().
/// - Texture quality: QualitySettings.globalTextureMipmapLimit. Terrain quality: Terrain.heightmapPixelError.
/// - Camera far clip comes from the Cinemachine lens, pushed to the camera every frame by CinemachineBrain.
/// </summary>
internal sealed class GraphicsDetailFeature : IFeature
{
    private const string Sec = "Graphics.Detail";

    internal static ConfigEntry<bool> Enabled, FarOn, LodOn, ShadowOn, VegOn, TerrainOn, TexOn;
    internal static ConfigEntry<float> FarClip, LodBias, ShadowDist, TreeDist, GrassDist, GrassDensity, PixelError, BasemapDist;
    internal static ConfigEntry<int> MaxLod, Cascades, ShadowRes, TexQuality, Aniso;

    private static readonly string[] TexNames = { "Full", "Half", "Quarter", "Eighth" };
    private static readonly string[] AnisoNames = { "Game", "Off", "Per texture", "Forced" };

    public void Register()
    {
        var f = ModConfig.File;
        Enabled = f.Bind(Sec, "Enabled", false, "Master switch for the draw distance / detail overrides below. Off = the game's own graphics settings.");

        FarOn = f.Bind(Sec, "FarClipOverride", false, "Override the camera far clip (how far anything is drawn).");
        FarClip = f.Bind(Sec, "FarClip", 6000f, new ConfigDescription("Camera far clip in metres.", new AcceptableValueRange<float>(300f, 20000f)));

        LodOn = f.Bind(Sec, "LodOverride", false, "Override the LOD bias (how far objects keep their detailed models).");
        LodBias = f.Bind(Sec, "LodBias", 2f, new ConfigDescription("QualitySettings.lodBias. Higher = detailed models further away.", new AcceptableValueRange<float>(0.25f, 6f)));
        MaxLod = f.Bind(Sec, "MaximumLodLevel", 0, new ConfigDescription("QualitySettings.maximumLODLevel. 0 = best models allowed; 1-2 drop the most detailed LODs.", new AcceptableValueRange<int>(0, 2)));

        ShadowOn = f.Bind(Sec, "ShadowOverride", false, "Override the sun shadow distance, cascades and resolution.");
        ShadowDist = f.Bind(Sec, "ShadowDistance", 300f, new ConfigDescription("Sun shadow distance in metres.", new AcceptableValueRange<float>(20f, 2000f)));
        Cascades = f.Bind(Sec, "ShadowCascades", 4, new ConfigDescription("Shadow cascade count (more = sharper near shadows over a long distance).", new AcceptableValueRange<int>(1, 4)));
        ShadowRes = f.Bind(Sec, "ShadowResolution", 3, new ConfigDescription("Sun shadow map size: 0=512, 1=1024, 2=2048, 3=4096, 4=8192.", new AcceptableValueRange<int>(0, 4)));

        VegOn = f.Bind(Sec, "VegetationOverride", false, "Override tree / grass draw distance and grass density (GPU Instancer Pro).");
        TreeDist = f.Bind(Sec, "TreeDistance", 4000f, new ConfigDescription("Tree draw distance in metres.", new AcceptableValueRange<float>(100f, 10000f)));
        GrassDist = f.Bind(Sec, "GrassDistance", 300f, new ConfigDescription("Grass and small detail draw distance in metres.", new AcceptableValueRange<float>(20f, 1000f)));
        GrassDensity = f.Bind(Sec, "GrassDensity", 1f, new ConfigDescription("Grass density multiplier on top of the game's density setting.", new AcceptableValueRange<float>(0.1f, 2f)));

        TerrainOn = f.Bind(Sec, "TerrainOverride", false, "Override terrain mesh detail and the full-quality terrain material distance.");
        PixelError = f.Bind(Sec, "TerrainPixelError", 3f, new ConfigDescription("Terrain.heightmapPixelError. Lower = more accurate distant terrain shape.", new AcceptableValueRange<float>(1f, 50f)));
        BasemapDist = f.Bind(Sec, "TerrainBasemapDistance", 0f, new ConfigDescription("Distance where terrain switches to its low-res basemap. 0 = keep the game's value.", new AcceptableValueRange<float>(0f, 20000f)));

        TexOn = f.Bind(Sec, "TextureOverride", false, "Override texture resolution and anisotropic filtering.");
        TexQuality = f.Bind(Sec, "TextureQuality", 0, new ConfigDescription("Texture resolution: 0=Full, 1=Half, 2=Quarter, 3=Eighth (QualitySettings.globalTextureMipmapLimit).", new AcceptableValueRange<int>(0, 3)));
        Aniso = f.Bind(Sec, "AnisotropicFiltering", 3, new ConfigDescription("0=Game, 1=Off, 2=Per texture, 3=Forced on every texture (sharper ground at grazing angles).", new AcceptableValueRange<int>(0, 3)));

        f.SettingChanged += OnSettingChanged;

        Registry.Section(Registry.PlusMode, "Graphics", DrawMenu, 0);
        Registry.OnTick(Tick);
        PatchBrain();
    }

    private static void OnSettingChanged(object sender, SettingChangedEventArgs a)
    {
        if (a != null && a.ChangedSetting != null && a.ChangedSetting.Definition.Section == Sec) _dirty = true;
    }

    // ---------------------------------------------------------------- baseline-tracked overrides

    private sealed class State
    {
        public float Baseline, Written, LastTarget;
        public int Seen;
        public Action<float> Set;
        public Func<float> Get;
    }

    private static readonly Dictionary<string, State> States = new();
    private static readonly HashSet<string> Logged = new();
    private static int _pass;
    private static bool _dirty = true;
    private static float _next;

    private static bool Near(float a, float b) => Math.Abs(a - b) <= 0.001f + Math.Abs(b) * 0.0001f;

    /// <summary>
    /// Keeps one game value overridden while <paramref name="want"/> is true and restores the game's value when it
    /// turns false. <paramref name="target"/> receives the game's (baseline) value, so multipliers work too.
    /// Returns true when it wrote something.
    /// </summary>
    private static bool Slot(string key, bool want, Func<float> get, Action<float> set, Func<float, float> target)
    {
        States.TryGetValue(key, out var s);
        if (!want)
        {
            if (s == null) return false;
            States.Remove(key);
            float live = get();
            // Only put the game's value back if nothing else changed it since our write.
            if (Near(live, s.Written) && !Near(live, s.Baseline)) { set(s.Baseline); return true; }
            return false;
        }

        float cur = get();
        bool fresh = s == null;
        if (fresh)
        {
            s = new State { Baseline = cur, Written = cur, LastTarget = float.NaN };
            States[key] = s;
        }
        else if (!Near(cur, s.Written)) s.Baseline = cur; // the game changed it since our last write
        s.Seen = _pass;
        s.Get = get;
        s.Set = set;

        float t = target(s.Baseline);
        bool changed = false;
        // Re-write only when something actually moved, so a setter that clamps our value doesn't make us fight it.
        if (fresh || !Near(cur, s.Written) || !Near(t, s.LastTarget))
        {
            if (!Near(cur, t)) { set(t); changed = true; cur = get(); }
            s.LastTarget = t;
        }
        s.Written = cur;
        return changed;
    }

    private static Func<float, float> Abs(float v) => _ => v;

    private static void LogOnce(string what, Exception e)
    {
        if (Logged.Add(what)) Plugin.Logger.LogWarning($"[Graphics.Detail] {what} failed (logged once): {e.Message}");
    }

    // ---------------------------------------------------------------- per-frame

    private static bool _farActive;
    private static float _farTarget;

    private static void Tick()
    {
        float now = Time.unscaledTime;
        if (!_dirty && now < _next) return;
        _next = now + 0.5f;
        _dirty = false;
        _pass++;

        bool on = Enabled.Value;
        // Nothing is overridden and nothing to restore: only refresh the read-outs (cheap).
        bool idle = !on && States.Count == 0;
        if (!idle)
        {
            ApplyQuality(on);
            ApplyShadows(on);
            ApplyVegetation(on);
            ApplyTerrain(on);
            ApplyCamera(on);
            Prune();
        }
        else _farActive = false;
        Snapshot();
    }

    private static void Prune()
    {
        List<string> dead = null;
        foreach (var kv in States)
            if (_pass - kv.Value.Seen > 10) (dead ??= new()).Add(kv.Key); // its object is gone (scene change)
        if (dead != null) foreach (var k in dead) States.Remove(k);
    }

    private static void ApplyQuality(bool on)
    {
        try
        {
            bool lod = on && LodOn.Value;
            Slot("q.lodBias", lod, () => QualitySettings.lodBias, v => QualitySettings.lodBias = v, Abs(LodBias.Value));
            Slot("q.maxLod", lod, () => QualitySettings.maximumLODLevel, v => QualitySettings.maximumLODLevel = Mathf.RoundToInt(v), Abs(MaxLod.Value));

            bool tex = on && TexOn.Value;
            Slot("q.mipLimit", tex, () => QualitySettings.globalTextureMipmapLimit,
                v => QualitySettings.globalTextureMipmapLimit = Mathf.RoundToInt(v), Abs(Mathf.Clamp(TexQuality.Value, 0, 3)));
            // Aniso: 0 = leave the game's value; 1..3 = Disable / Enable / ForceEnable.
            Slot("q.aniso", tex && Aniso.Value > 0, () => (int)QualitySettings.anisotropicFiltering,
                v => QualitySettings.anisotropicFiltering = (AnisotropicFiltering)Mathf.RoundToInt(v), Abs(Mathf.Clamp(Aniso.Value - 1, 0, 2)));
        }
        catch (Exception e) { LogOnce("quality settings", e); }
    }

    private static UniversalRenderPipelineAsset Urp()
    {
        var rp = UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline;
        return rp == null ? null : rp.TryCast<UniversalRenderPipelineAsset>();
    }

    private static void ApplyShadows(bool on)
    {
        try
        {
            var urp = Urp();
            if (urp == null) return;
            string k = "urp" + urp.Pointer.ToInt64() + ".";
            bool want = on && ShadowOn.Value;
            bool distChanged = Slot(k + "shadowDistance", want, () => urp.shadowDistance, v => urp.shadowDistance = v, Abs(ShadowDist.Value));
            Slot(k + "cascades", want, () => urp.shadowCascadeCount, v => urp.shadowCascadeCount = Mathf.RoundToInt(v), Abs(Mathf.Clamp(Cascades.Value, 1, 4)));
            Slot(k + "shadowRes", want, () => urp.mainLightShadowmapResolution,
                v => urp.mainLightShadowmapResolution = Mathf.RoundToInt(v), Abs(512 << Mathf.Clamp(ShadowRes.Value, 0, 4)));
            // GPUI culls instanced trees' shadows by the shadow distance; the game refreshes it the same way.
            if (distChanged) UpdateGpuiParameters();
        }
        catch (Exception e) { LogOnce("shadows", e); }
    }

    private static void UpdateGpuiParameters()
    {
        try { GPUICoreAPI.UpdateParameterBufferData(); }
        catch (Exception e) { LogOnce("GPUI parameter update", e); }
    }

    private static GPUIDetailManager _detail;
    private static GPUITreeManager _trees;
    private static float _nextFind;

    private static void FindManagers()
    {
        // Throttled either way: in menus there are no managers and FindObjectOfType is not free.
        if (Time.unscaledTime < _nextFind) return;
        _nextFind = Time.unscaledTime + 2f;
        _detail = null;
        _trees = null;
        try
        {
            var refs = SceneReferences.Instance;
            if (refs != null)
            {
                _detail = refs.GPUIDetailManager;
                _trees = refs.GPUITreeManager;
            }
        }
        catch (Exception e) { LogOnce("SceneReferences", e); }
        try
        {
            if (_detail == null)
            {
                var o = Object.FindObjectOfType(Il2CppInterop.Runtime.Il2CppType.Of<GPUIDetailManager>());
                if (o != null) _detail = o.TryCast<GPUIDetailManager>();
            }
            if (_trees == null)
            {
                var o = Object.FindObjectOfType(Il2CppInterop.Runtime.Il2CppType.Of<GPUITreeManager>());
                if (o != null) _trees = o.TryCast<GPUITreeManager>();
            }
        }
        catch (Exception e) { LogOnce("find GPUI managers", e); }
    }

    private static Func<float> ProfileMax(GPUIProfile p) => () => p.minMaxDistance.y;

    private static Action<float> SetProfileMax(GPUIProfile p) => v =>
    {
        var mm = p.minMaxDistance;
        mm.y = v;
        p.minMaxDistance = mm;
    };

    private static void ApplyVegetation(bool on)
    {
        bool want = on && VegOn.Value;
        if (!want && !HasStates("gpui")) return;
        FindManagers();
        bool changed = false;

        try
        {
            var tree = _trees;
            var p = tree != null ? tree.defaultProfile : null;
            if (p != null)
                changed |= Slot("gpui.treeProfile" + p.Pointer.ToInt64(), want, ProfileMax(p), SetProfileMax(p), Abs(TreeDist.Value));
        }
        catch (Exception e) { LogOnce("tree distance", e); }

        try
        {
            var d = _detail;
            if (d != null)
            {
                string k = "gpui.detail" + d.Pointer.ToInt64();
                changed |= Slot(k + ".distance", want, () => d.detailObjectDistance, v => d.SetDetailObjectDistance(v), Abs(GrassDist.Value));
                var p = d.defaultProfile;
                var tp = _trees != null ? _trees.defaultProfile : null;
                if (p != null && (tp == null || tp.Pointer != p.Pointer)) // never fight the tree slot over a shared profile
                    changed |= Slot("gpui.detailProfile" + p.Pointer.ToInt64(), want, ProfileMax(p), SetProfileMax(p), Abs(GrassDist.Value));

                bool density = false;
                float mult = GrassDensity.Value;
                int n = d.GetPrototypeCount();
                for (int i = 0; i < n; i++)
                {
                    var data = d.GetPrototypeData(i);
                    if (data == null) continue;
                    density |= Slot(k + ".density" + data.Pointer.ToInt64(), want && !Near(mult, 1f),
                        () => data.densityAdjustment,
                        v => { data.densityAdjustment = v; data.SetParameterBufferData(); },
                        b => b * mult);
                }
                if (density) d.RequireUpdate();
                changed |= density;
            }
        }
        catch (Exception e) { LogOnce("grass distance / density", e); }

        if (changed) UpdateGpuiParameters();
    }

    private static bool HasState(string key) => States.ContainsKey(key);

    private static bool HasStates(string prefix)
    {
        foreach (var k in States.Keys) if (k.StartsWith(prefix, StringComparison.Ordinal)) return true;
        return false;
    }

    private static void ApplyTerrain(bool on)
    {
        bool want = on && TerrainOn.Value;
        bool veg = on && VegOn.Value;
        if (!want && !veg && !HasStates("terrain")) return;
        try
        {
            var terrains = Terrain.activeTerrains;
            if (terrains == null) return;
            foreach (var t in terrains)
            {
                if (t == null) continue;
                var tt = t;
                string k = "terrain" + tt.Pointer.ToInt64() + ".";
                Slot(k + "pixelError", want, () => tt.heightmapPixelError, v => tt.heightmapPixelError = v, Abs(PixelError.Value));
                Slot(k + "basemap", want && BasemapDist.Value > 0f, () => tt.basemapDistance, v => tt.basemapDistance = v, Abs(BasemapDist.Value));
                // Only matters for terrains that draw their own trees/grass (GPUI normally takes those over).
                bool own = tt.drawTreesAndFoliage;
                Slot(k + "treeDistance", veg && own, () => tt.treeDistance, v => tt.treeDistance = v, Abs(TreeDist.Value));
                Slot(k + "detailDistance", veg && own, () => tt.detailObjectDistance, v => tt.detailObjectDistance = v, Abs(GrassDist.Value));
            }
        }
        catch (Exception e) { LogOnce("terrain", e); }
    }

    private static void ApplyCamera(bool on)
    {
        bool want = on && FarOn.Value;
        float far = FarClip.Value;
        try
        {
            // The Cinemachine lenses of the two player cameras (the brain copies the lens to the camera every frame).
            var refs = SceneReferences.Instance;
            if (refs != null)
            {
                LensSlot("lens.main", refs.MainVirtualCamera, want, far);
                LensSlot("lens.free", refs.FreeVirtualCamera, want, far);
            }
        }
        catch (Exception e) { LogOnce("camera lens", e); }

        try
        {
            // The camera itself: covers cameras without a brain, and remembers the game's value for restoring.
            var cam = Camera.main;
            if (cam != null)
                Slot("cam" + cam.Pointer.ToInt64(), want, () => cam.farClipPlane, v => cam.farClipPlane = v, Abs(far));
        }
        catch (Exception e) { LogOnce("camera far clip", e); }

        // Enabled after the camera slot read the game's value, so the baseline is the game's.
        _farTarget = far;
        _farActive = want;
    }

    private static void LensSlot(string key, CinemachineFreeLook vcam, bool want, float far)
    {
        if (vcam == null) return;
        var v = vcam;
        Slot(key + v.Pointer.ToInt64(), want, () => v.m_Lens.FarClipPlane, x =>
        {
            var lens = v.m_Lens;
            lens.FarClipPlane = x;
            v.m_Lens = lens;
        }, Abs(far));
    }

    // Brain postfix: every camera Cinemachine drives (cut-scenes, merchant and lobby cameras too) gets the far clip.
    private static void PatchBrain()
    {
        try
        {
            var target = AccessTools.Method(typeof(CinemachineBrain), "PushStateToUnityCamera");
            if (target == null) { Plugin.Logger.LogWarning("[Graphics.Detail] CinemachineBrain.PushStateToUnityCamera not found; far clip uses the lens only."); return; }
            new Harmony(Plugin.Guid + ".graphics.detail").Patch(target, postfix: new HarmonyMethod(typeof(GraphicsDetailFeature), nameof(BrainPostfix)));
        }
        catch (Exception e) { Plugin.Logger.LogWarning($"[Graphics.Detail] far clip patch failed, lens fallback only: {e.Message}"); }
    }

    private static void BrainPostfix(CinemachineBrain __instance)
    {
        if (!_farActive) return;
        try
        {
            var cam = __instance.OutputCamera;
            if (cam != null && !Near(cam.farClipPlane, _farTarget)) cam.farClipPlane = _farTarget;
        }
        catch { }
    }

    // ---------------------------------------------------------------- read-outs

    private static readonly List<(string, string)> Live = new();

    private static void Snapshot()
    {
        Live.Clear();
        FindManagers();
        void Add(string k, Func<string> v)
        {
            try { Live.Add((k, v())); } catch { Live.Add((k, "-")); }
        }

        Add("Camera far clip", () => Camera.main != null ? Camera.main.farClipPlane.ToString("0") + " m" : "-");
        Add("LOD bias / max LOD", () => QualitySettings.lodBias.ToString("0.00") + " / " + QualitySettings.maximumLODLevel);
        Add("Shadows", () =>
        {
            var u = Urp();
            return u == null ? "-" : $"{u.shadowDistance:0} m, {u.shadowCascadeCount} cascades, {u.mainLightShadowmapResolution}px";
        });
        Add("Trees / grass", () =>
        {
            string tr = _trees != null && _trees.defaultProfile != null ? _trees.defaultProfile.minMaxDistance.y.ToString("0") + " m" : "-";
            string gr = _detail != null ? _detail.detailObjectDistance.ToString("0") + " m" : "-";
            return tr + " / " + gr;
        });
        Add("Grass density", () =>
        {
            if (_detail == null || _detail.GetPrototypeCount() == 0) return "-";
            var d = _detail.GetPrototypeData(0);
            return d == null ? "-" : d.densityAdjustment.ToString("0.00") + "x (first type)";
        });
        Add("Terrain pixel error", () =>
        {
            var ts = Terrain.activeTerrains;
            if (ts == null || ts.Length == 0) return "-";
            return ts[0].heightmapPixelError.ToString("0.0") + $" ({ts.Length} terrain{(ts.Length == 1 ? "" : "s")}), basemap {ts[0].basemapDistance:0} m";
        });
        Add("Textures", () => TexNames[Mathf.Clamp(QualitySettings.globalTextureMipmapLimit, 0, 3)] + ", aniso " + QualitySettings.anisotropicFiltering);
    }

    // ---------------------------------------------------------------- presets

    private static void Ultra()
    {
        FarOn.Value = true; FarClip.Value = 8000f;
        LodOn.Value = true; LodBias.Value = 3f; MaxLod.Value = 0;
        ShadowOn.Value = true; ShadowDist.Value = 400f; Cascades.Value = 4; ShadowRes.Value = 3;
        VegOn.Value = true; TreeDist.Value = 6000f; GrassDist.Value = 350f; GrassDensity.Value = 1.25f;
        TerrainOn.Value = true; PixelError.Value = 2f; BasemapDist.Value = 6000f;
        TexOn.Value = true; TexQuality.Value = 0; Aniso.Value = 3;
        Enabled.Value = true;
        Menu.SetStatus("Graphics: Ultra applied (heavy on the GPU; turn shadows or grass down if it stutters)");
    }

    private static void Performance()
    {
        FarOn.Value = false;
        LodOn.Value = true; LodBias.Value = 0.6f; MaxLod.Value = 0;
        ShadowOn.Value = true; ShadowDist.Value = 80f; Cascades.Value = 2; ShadowRes.Value = 1;
        VegOn.Value = true; TreeDist.Value = 900f; GrassDist.Value = 60f; GrassDensity.Value = 0.6f;
        TerrainOn.Value = true; PixelError.Value = 10f; BasemapDist.Value = 0f;
        TexOn.Value = true; TexQuality.Value = 1; Aniso.Value = 2;
        Enabled.Value = true;
        Menu.SetStatus("Graphics: Performance applied");
    }

    private static void GameDefaults()
    {
        Enabled.Value = false;
        Menu.SetStatus("Graphics: overrides off, the game's settings are back");
    }

    // ---------------------------------------------------------------- menu

    private static void IntSlider(ConfigEntry<int> e, string label, int min, int max, Func<int, string> shown, bool enabled)
    {
        int v = Mathf.RoundToInt(Menu.RawSlider(label, e.Value, min, max, shown(e.Value), enabled));
        if (v != e.Value) e.Value = Mathf.Clamp(v, min, max);
    }

    private static void DrawMenu()
    {
        bool on = Enabled.Value;

        Menu.Card("Draw distance & detail", "Push view distance and detail past the game's options, or trade it for FPS. Everything here is local to your PC.", () =>
        {
            Menu.Switch(Enabled, "Override graphics detail", on ? "On: the switches below replace the game's values" : "Off: the game's own graphics settings are used", true);
            Menu.Chips(3,
                ("Ultra", Ultra, true),
                ("Performance", Performance, true),
                ("Game settings", GameDefaults, on));
            Menu.Small("Overrides follow the game: if you change a graphics option in the game menu, the override is re-applied on top, and turning one off restores the game's value.");
        });

        Menu.Card("View distance", null, () =>
        {
            Menu.Switch(FarOn, "Camera far clip", "How far anything is drawn at all", on);
            Menu.SliderRow(FarClip, "Far clip", 300f, 20000f, "0", " m", on && FarOn.Value);
            Menu.Switch(LodOn, "Model detail (LOD)", "Keep detailed models further away", on);
            Menu.SliderRow(LodBias, "LOD bias", 0.25f, 6f, "0.00", "x", on && LodOn.Value);
            IntSlider(MaxLod, "Lowest LOD skipped", 0, 2, v => v == 0 ? "None" : v.ToString(), on && LodOn.Value);
        });

        Menu.Card("Shadows", null, () =>
        {
            Menu.Switch(ShadowOn, "Sun shadows", "Distance, cascades and shadow map size", on);
            bool en = on && ShadowOn.Value;
            Menu.SliderRow(ShadowDist, "Shadow distance", 20f, 2000f, "0", " m", en);
            IntSlider(Cascades, "Cascades", 1, 4, v => v.ToString(), en);
            IntSlider(ShadowRes, "Shadow resolution", 0, 4, v => (512 << v).ToString(), en);
            if (en && ShadowRes.Value >= 4) Menu.Small("8192 shadow maps use a lot of VRAM.");
        });

        Menu.Card("Vegetation", null, () =>
        {
            Menu.Switch(VegOn, "Trees and grass", "GPU Instancer draw distance and grass density", on);
            bool en = on && VegOn.Value;
            Menu.SliderRow(TreeDist, "Tree distance", 100f, 10000f, "0", " m", en);
            Menu.SliderRow(GrassDist, "Grass distance", 20f, 1000f, "0", " m", en);
            Menu.SliderRow(GrassDensity, "Grass density", 0.1f, 2f, "0", "", en, percent: true);
            if (en && FarOn.Value == false && TreeDist.Value > 3000f) Menu.Small("Trees are also cut at the camera far clip; raise that too to see them further.");
        });

        Menu.Card("Terrain", null, () =>
        {
            Menu.Switch(TerrainOn, "Terrain detail", "Ground mesh accuracy and full-quality material distance", on);
            bool en = on && TerrainOn.Value;
            Menu.SliderRow(PixelError, "Pixel error", 1f, 50f, "0.0", "", en);
            Menu.SliderRow(BasemapDist, "Basemap distance", 0f, 20000f, "0", BasemapDist.Value <= 0f ? " (game)" : " m", en);
        });

        Menu.Card("Textures", null, () =>
        {
            Menu.Switch(TexOn, "Texture quality", "Resolution and anisotropic filtering", on);
            bool en = on && TexOn.Value;
            IntSlider(TexQuality, "Resolution", 0, 3, v => TexNames[Mathf.Clamp(v, 0, 3)], en);
            IntSlider(Aniso, "Anisotropic", 0, 3, v => AnisoNames[Mathf.Clamp(v, 0, 3)], en);
        });

        Menu.Card("Live values", "What the game is actually using right now.", () =>
        {
            foreach (var (k, v) in Live) Menu.KeyValue(k, v);
        });
    }
}
