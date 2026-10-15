using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using sc.modeling.river.runtime;
using StylizedWater3;
using UnityEngine;
using UnityEngine.Rendering;

namespace OverTheHillPlus.Features.Public.Graphics.Water;

/// <summary>
/// Re-tints and re-tunes the Stylized Water 3 materials used by rivers and lakes. The game never touches these
/// materials at runtime, so we capture each material's original values once, write our values on top, and put
/// the originals back when the option is switched off. Physics (WaterBody / buoyancy) is not affected.
///
/// Shader property names come from Stylized Water 3 (several confirmed in its runtime code: _WaveHeight,
/// _WaveSpeed, _WaveFrequency, _Speed, _NormalStrength, _FoamStrength); every write is guarded by HasProperty,
/// so a name that does not exist in this game's shader version is simply skipped.
/// </summary>
internal static class WaterSurface
{
    private sealed class Mat
    {
        public Material M;
        public Shader S;
        public readonly Dictionary<string, float> Floats = new();
        public readonly Dictionary<string, Color> Colors = new();
        public readonly Dictionary<string, Vector2> Ranges = new();
    }

    private static readonly string[] ColorProps = { "_BaseColor", "_ShallowColor", "_HorizonColor" };

    // knob -> candidate float properties it multiplies
    private static readonly (string knob, string[] props)[] FloatKnobs =
    {
        ("clarity", new[] { "_DepthVertical", "_DepthHorizontal" }),
        ("reflections", new[] { "_ReflectionStrength" }),
        ("sun", new[] { "_SunReflectionStrength", "_SparkleIntensity" }),
        ("waves", new[] { "_WaveHeight" }),
        ("speed", new[] { "_WaveSpeed", "_Speed" }),
        ("ripples", new[] { "_NormalStrength", "_WaveNormalStr" }),
        ("foam", new[] { "_FoamStrength" }),
    };

    private static readonly Dictionary<IntPtr, Mat> Mats = new();
    private static float _nextScan;
    private static int _applied = -1;
    private static bool _active;
    private static bool? _ssrOriginal;
    private static string _ssrApplied = "Game";

    public static int MaterialCount => Mats.Count;
    public static string Supported { get; private set; } = "";
    public static string SsrState { get; private set; } = "not checked";

    public static void ForceRescan() => _nextScan = 0f;

    public static void Tick()
    {
        if (!WaterCfg.WaterOn.Value)
        {
            if (_active) Restore();
            return;
        }

        float now = Time.unscaledTime;
        if (now >= _nextScan)
        {
            _nextScan = now + 4f;
            if (Scan()) _applied = -1;
        }
        if (_applied != WaterCfg.Version)
        {
            _applied = WaterCfg.Version;
            Apply();
        }
    }

    // ---- discovery ----

    /// <summary>Finds water materials; returns true when a new one appeared (so values are re-applied).</summary>
    private static bool Scan()
    {
        bool added = false;
        // drop destroyed materials (scene changes)
        foreach (var key in Mats.Where(kv => kv.Value.M == null).Select(kv => kv.Key).ToList()) Mats.Remove(key);

        try
        {
            foreach (var wo in Gfx.Find<WaterObject>())
            {
                try
                {
                    added |= AddMaterial(wo.material);
                    added |= AddRenderer(wo.meshRenderer);
                }
                catch (Exception e) { Gfx.Once("wo", "WaterObject read failed: " + e.Message); }
            }
            foreach (var rm in Gfx.Find<RiverModeler>())
            {
                try
                {
                    var mf = rm.meshFilter;
                    if (mf != null) added |= AddRenderer(mf.GetComponent<MeshRenderer>());
                }
                catch (Exception e) { Gfx.Once("rm", "RiverModeler read failed: " + e.Message); }
            }
            foreach (var wb in Gfx.Find<WaterBody>())
            {
                try
                {
                    var rs = wb.GetComponentsInChildren<MeshRenderer>(true);
                    if (rs.Length > 32) added |= AddRenderer(wb.GetComponent<MeshRenderer>()); // a big parent: just itself
                    else foreach (var r in rs) added |= AddRenderer(r);
                }
                catch (Exception e) { Gfx.Once("wb", "WaterBody read failed: " + e.Message); }
            }
        }
        catch (Exception e) { Gfx.Once("scan", "water scan failed: " + e.Message); }

        if (added) UpdateSupported();
        return added;
    }

    private static bool AddRenderer(Renderer r)
    {
        if (r == null) return false;
        bool added = false;
        var mats = r.sharedMaterials;
        if (mats == null) return false;
        foreach (var m in mats) added |= AddMaterial(m);
        return added;
    }

    private static bool AddMaterial(Material m)
    {
        if (m == null || Mats.ContainsKey(m.Pointer)) return false;
        var s = m.shader;
        if (s == null) return false;
        string sn = (s.name ?? "").ToLowerInvariant();
        bool looksLikeWater = sn.Contains("water") || m.HasProperty("_WaveHeight") || m.HasProperty("_ShallowColor");
        if (!looksLikeWater || sn.Contains("underwater")) return false;

        var e = new Mat { M = m, S = s };
        foreach (var p in ColorProps)
            if (m.HasProperty(p)) e.Colors[p] = m.GetColor(p);
        foreach (var (_, props) in FloatKnobs)
            foreach (var p in props)
            {
                if (!m.HasProperty(p) || e.Floats.ContainsKey(p)) continue;
                e.Floats[p] = m.GetFloat(p);
                try
                {
                    int i = s.FindPropertyIndex(p);
                    if (i >= 0 && s.GetPropertyType(i) == ShaderPropertyType.Range) e.Ranges[p] = s.GetPropertyRangeLimits(i);
                }
                catch { }
            }
        Mats[m.Pointer] = e;
        Plugin.Logger.LogInfo($"[Graphics.Water] water material '{m.name}' (shader '{s.name}'): {e.Colors.Count} colours, {e.Floats.Count} tunable values");
        return true;
    }

    private static void UpdateSupported()
    {
        var names = new List<string>();
        bool any(params string[] props) => Mats.Values.Any(m => props.Any(p => m.Floats.ContainsKey(p) || m.Colors.ContainsKey(p)));
        if (any("_BaseColor", "_ShallowColor")) names.Add("colour");
        foreach (var (knob, props) in FloatKnobs)
            if (any(props)) names.Add(knob);
        Supported = names.Count == 0 ? "none" : string.Join(", ", names);
    }

    // ---- applying ----

    private static void Apply()
    {
        foreach (var e in Mats.Values)
        {
            try { ApplyTo(e); }
            catch (Exception ex) { Gfx.Once("apply", "applying water values failed: " + ex.Message); }
        }
        ApplySsr(WaterCfg.Ssr.Value);
        _active = true;
    }

    private static void ApplyTo(Mat e)
    {
        var m = e.M;
        if (m == null) return;

        bool recolour = WaterCfg.Preset.Value != "Game";
        PresetColors(out var deep, out var shallow);
        float tint = Mathf.Clamp01(WaterCfg.Tint.Value);
        float bright = WaterCfg.Brightness.Value;
        float clarity = WaterCfg.Clarity.Value;

        foreach (var kv in e.Colors)
        {
            var o = kv.Value;
            Color target = kv.Key == "_BaseColor" ? deep : shallow;
            float t = recolour ? (kv.Key == "_HorizonColor" ? tint * 0.5f : tint) : 0f;
            var c = Color.Lerp(o, target, t);
            c.r *= bright; c.g *= bright; c.b *= bright;
            // alpha is the colour's opacity in Stylized Water: raising it to a power > 1 makes the water clearer
            c.a = kv.Key == "_HorizonColor" || Gfx.Near(clarity, 1f) ? o.a : Mathf.Clamp01(Mathf.Pow(Mathf.Clamp01(o.a), clarity));
            m.SetColor(kv.Key, c);
        }

        foreach (var (knob, props) in FloatKnobs)
        {
            float mul = knob switch
            {
                "clarity" => clarity,
                "reflections" => WaterCfg.Reflections.Value,
                "sun" => WaterCfg.SunGlint.Value,
                "waves" => WaterCfg.WaveHeight.Value,
                "speed" => WaterCfg.WaveSpeed.Value,
                "ripples" => WaterCfg.Ripples.Value,
                "foam" => WaterCfg.Foam.Value,
                _ => 1f,
            };
            foreach (var p in props)
            {
                if (!e.Floats.TryGetValue(p, out var orig)) continue;
                float v = orig * mul;
                if (e.Ranges.TryGetValue(p, out var lim)) v = Mathf.Clamp(v, Mathf.Min(lim.x, lim.y), Mathf.Max(lim.x, lim.y));
                m.SetFloat(p, v);
            }
        }
    }

    private static void PresetColors(out Color deep, out Color shallow)
    {
        switch (WaterCfg.Preset.Value)
        {
            case "Tropical": deep = new Color(0.00f, 0.30f, 0.42f); shallow = new Color(0.15f, 0.80f, 0.75f); break;
            case "Glacier": deep = new Color(0.04f, 0.28f, 0.38f); shallow = new Color(0.50f, 0.85f, 0.88f); break;
            case "Deep blue": deep = new Color(0.01f, 0.06f, 0.18f); shallow = new Color(0.08f, 0.30f, 0.48f); break;
            case "Stream": deep = new Color(0.04f, 0.16f, 0.14f); shallow = new Color(0.30f, 0.55f, 0.45f); break;
            case "Murky": deep = new Color(0.10f, 0.12f, 0.06f); shallow = new Color(0.33f, 0.32f, 0.18f); break;
            case "Custom":
                float h = Mathf.Repeat(WaterCfg.Hue.Value, 360f) / 360f;
                deep = Color.HSVToRGB(h, 0.9f, 0.32f);
                shallow = Color.HSVToRGB(h, 0.55f, 0.85f);
                break;
            default: deep = shallow = Color.white; break;
        }
    }

    private static void ApplySsr(string mode)
    {
        if (mode == _ssrApplied && mode == "Game") return;
        try
        {
            var feature = StylizedWaterRenderFeature.GetDefault();
            var s = feature == null ? null : feature.screenSpaceReflectionSettings;
            if (s == null) { SsrState = "render feature not found"; return; }
            _ssrOriginal ??= s.allow;
            bool want = mode == "On" || (mode != "Off" && _ssrOriginal.Value);
            s.allow = want;
            _ssrApplied = mode;
            SsrState = (want ? "on" : "off") + (mode == "Game" ? " (game)" : "");
        }
        catch (Exception e)
        {
            SsrState = "unavailable";
            Gfx.Once("ssr", "screen-space reflection toggle failed: " + e.Message);
        }
    }

    private static void Restore()
    {
        foreach (var e in Mats.Values)
        {
            try
            {
                if (e.M == null) continue;
                foreach (var kv in e.Colors) e.M.SetColor(kv.Key, kv.Value);
                foreach (var kv in e.Floats) e.M.SetFloat(kv.Key, kv.Value);
            }
            catch (Exception ex) { Gfx.Once("restore", "restoring water values failed: " + ex.Message); }
        }
        if (_ssrApplied != "Game") ApplySsr("Game");
        _active = false;
        _applied = -1;
        _nextScan = 0f;
    }

    /// <summary>Writes every property of the first water shader to the log, to help tune the property lists.</summary>
    public static string LogShaderProperties()
    {
        var e = Mats.Values.FirstOrDefault(m => m.M != null && m.S != null);
        if (e == null) return "Failed: no water material found yet (drive near a river or lake)";
        try
        {
            var s = e.S;
            var sb = new StringBuilder($"[Graphics.Water] shader '{s.name}' properties:");
            int n = s.GetPropertyCount();
            for (int i = 0; i < n; i++)
            {
                var type = s.GetPropertyType(i);
                string name = s.GetPropertyName(i);
                string val = type switch
                {
                    ShaderPropertyType.Color => e.M.GetColor(name).ToString(),
                    ShaderPropertyType.Float or ShaderPropertyType.Range => e.M.GetFloat(name).ToString("0.###"),
                    ShaderPropertyType.Vector => e.M.GetVector(name).ToString(),
                    _ => "",
                };
                sb.Append($"\n  {name} ({type}) {val}");
            }
            Plugin.Logger.LogInfo(sb.ToString());
            return $"Logged {n} water shader properties to the BepInEx log";
        }
        catch (Exception ex) { return "Failed: " + ex.Message; }
    }
}
