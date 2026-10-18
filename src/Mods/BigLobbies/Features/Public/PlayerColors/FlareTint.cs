using System;
using UnityEngine;

namespace OverTheHillPlus.Features.Public.PlayerColors;

/// <summary>
/// Recolours one instantiated flare (a copy of Flare_Red) to another hue: particle start colours and colour over
/// lifetime, trails/lines, lights, and the colour properties of its materials (through property blocks, so the shared
/// prefab materials are never changed and nothing leaks when the flare is destroyed).
/// Only coloured parts are shifted (white cores and grey smoke stay as they are); brightness, HDR intensity and alpha
/// are kept, so the flare glows the same, just in the player's colour.
/// </summary>
internal static class FlareTint
{
    private static readonly string[] ColorProps = { "_BaseColor", "_Color", "_TintColor", "_MainColor", "_EmissionColor", "_EmissiveColor" };
    private static int[] _propIds;
    private static MaterialPropertyBlock _mpb;

    public static void Apply(GameObject root, Color target)
    {
        if (root == null) return;
        Color.RGBToHSV(target, out float th, out float ts, out _);

        foreach (var ps in root.GetComponentsInChildren<ParticleSystem>(true))
        {
            try { TintParticles(ps, th, ts); }
            catch (Exception e) { DebugLog.Error("biglobbies", e, "FlareTint.Apply"); PlayerPalette.LogOnce("tint particles", e); }
        }

        foreach (var light in root.GetComponentsInChildren<Light>(true))
        {
            try { light.color = Shift(light.color, th, ts); }
            catch (Exception e) { DebugLog.Error("biglobbies", e, "FlareTint.Apply"); PlayerPalette.LogOnce("tint light", e); }
        }

        foreach (var trail in root.GetComponentsInChildren<TrailRenderer>(true))
        {
            try
            {
                trail.startColor = Shift(trail.startColor, th, ts);
                trail.endColor = Shift(trail.endColor, th, ts);
            }
            catch (Exception e) { DebugLog.Error("biglobbies", e, "FlareTint.Apply"); PlayerPalette.LogOnce("tint trail", e); }
        }

        foreach (var line in root.GetComponentsInChildren<LineRenderer>(true))
        {
            try
            {
                line.startColor = Shift(line.startColor, th, ts);
                line.endColor = Shift(line.endColor, th, ts);
            }
            catch (Exception e) { DebugLog.Error("biglobbies", e, "FlareTint.Apply"); PlayerPalette.LogOnce("tint line", e); }
        }

        foreach (var r in root.GetComponentsInChildren<Renderer>(true))
        {
            try { TintRenderer(r, th, ts); }
            catch (Exception e) { DebugLog.Error("biglobbies", e, "FlareTint.Apply"); PlayerPalette.LogOnce("tint renderer", e); }
        }
    }

    private static void TintParticles(ParticleSystem ps, float th, float ts)
    {
        if (ps == null) return;
        // (colour over lifetime is stripped from this game's build, only the start colour can be changed)
        var main = ps.main;
        main.startColor = Shift(main.startColor, th, ts);
    }

    private static void TintRenderer(Renderer r, float th, float ts)
    {
        if (r == null) return;
        if (_propIds == null)
        {
            _propIds = new int[ColorProps.Length];
            for (int i = 0; i < ColorProps.Length; i++) _propIds[i] = Shader.PropertyToID(ColorProps[i]);
        }
        _mpb ??= new MaterialPropertyBlock();

        var mats = r.sharedMaterials;
        if (mats == null) return;
        for (int m = 0; m < mats.Length; m++)
        {
            var mat = mats[m];
            if (mat == null) continue;
            bool any = false;
            r.GetPropertyBlock(_mpb, m);
            foreach (int id in _propIds)
            {
                if (!mat.HasProperty(id)) continue;
                var before = mat.GetColor(id);
                var after = Shift(before, th, ts);
                if (after == before) continue;
                _mpb.SetColor(id, after);
                any = true;
            }
            if (any) r.SetPropertyBlock(_mpb, m);
            _mpb.Clear();
        }
    }

    private static ParticleSystem.MinMaxGradient Shift(ParticleSystem.MinMaxGradient g, float th, float ts)
    {
        if (g == null) return null;
        // Interop only exposes the backing fields. Unity: Color uses m_ColorMax, TwoColors both colours,
        // Gradient/RandomColor m_GradientMax, TwoGradients both gradients.
        switch (g.m_Mode)
        {
            case ParticleSystemGradientMode.Color:
                g.m_ColorMax = Shift(g.m_ColorMax, th, ts);
                break;
            case ParticleSystemGradientMode.TwoColors:
                g.m_ColorMin = Shift(g.m_ColorMin, th, ts);
                g.m_ColorMax = Shift(g.m_ColorMax, th, ts);
                break;
            case ParticleSystemGradientMode.Gradient:
            case ParticleSystemGradientMode.RandomColor:
                g.m_GradientMax = Shift(g.m_GradientMax, th, ts);
                break;
            case ParticleSystemGradientMode.TwoGradients:
                g.m_GradientMin = Shift(g.m_GradientMin, th, ts);
                g.m_GradientMax = Shift(g.m_GradientMax, th, ts);
                break;
        }
        return g;
    }

    private static Gradient Shift(Gradient g, float th, float ts)
    {
        if (g == null) return null;
        var keys = g.colorKeys;
        if (keys == null) return g;
        for (int i = 0; i < keys.Length; i++)
        {
            var k = keys[i];
            keys[i] = new GradientColorKey(Shift(k.color, th, ts), k.time);
        }
        var copy = new Gradient();
        copy.SetKeys(keys, g.alphaKeys);
        copy.mode = g.mode;
        return copy;
    }

    /// <summary>Moves a coloured value to the target hue, keeping brightness (incl. HDR) and alpha. Greys are left alone.</summary>
    internal static Color Shift(Color c, float th, float ts)
    {
        Color.RGBToHSV(c, out _, out float s, out float v);
        if (s < 0.12f || v <= 0.0001f) return c;
        var o = Color.HSVToRGB(th, Mathf.Clamp01(Mathf.Lerp(s, ts, 0.5f)), v, true);
        o.a = c.a;
        return o;
    }
}
