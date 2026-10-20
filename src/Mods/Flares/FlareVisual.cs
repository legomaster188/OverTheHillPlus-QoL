using System;
using OverTheHillPlus.Shared;
using UnityEngine;
using Object = UnityEngine.Object;

namespace OverTheHillPlus.Flares;

/// <summary>
/// One flare in the sky: a copy of the game's own flare (the pinpoint flare, Art/Environments/Shared/Prefabs/Flares/...,
/// recoloured to the player's colour with core's FlareTint) that climbs from the launch point, slows, hangs and sinks
/// before it goes out, leaving a smoke trail (a TrailRenderer using the flare's own particle material). Far flares are
/// drawn bigger so they stay visible from a long way off. Purely local; the network only carries the launch.
/// </summary>
internal sealed class FlareVisual
{
    private const float RiseTime = 4f, Drift = 2.5f, FadeOut = 3f, GrowFrom = 80f, MaxGrow = 12f;

    public int Owner;
    public Vector3 Origin;
    public Color Color;
    public string Label;
    public float Born, Life, Apex;
    public GameObject Root;
    /// <summary>The compass icon that follows the flare (the game removes it when the flare is gone).</summary>
    public CompassIcon Icon;

    private Transform _tr;
    private TrailRenderer _trail;

    private static GameObject _prefab;
    private static float _nextPrefabTry;
    private static Material _fallbackTrail;

    /// <summary>The game's flare prefab (any colour: it is recoloured), looked up the way the game does for a pinpoint.</summary>
    private static GameObject Prefab()
    {
        if (_prefab != null) return _prefab;
        if (Time.unscaledTime < _nextPrefabTry) return null;
        _nextPrefabTry = Time.unscaledTime + 10f;
        try
        {
            var who = GameState.LocalPlayer ?? default;
            _prefab = WorldSpacePinpoint.GetPlayerFlare(who);
        }
        catch (Exception e)
        {
            DebugLog.Error(FlaresPlugin.ModId, e, "FlareVisual.Prefab");
            FlaresCore.LogOnce("prefab", "the game's flare could not be loaded, using a plain light: " + e.Message);
        }
        return _prefab;
    }

    public static FlareVisual Spawn(Vector3 origin, Color color, string label, int owner, float now, float life, float apex)
    {
        var v = new FlareVisual { Origin = origin, Color = color, Label = label, Owner = owner, Born = now, Life = life, Apex = apex };
        try { v.Build(); }
        catch (Exception e)
        {
            DebugLog.Error(FlaresPlugin.ModId, e, "FlareVisual.Spawn");
            FlaresCore.LogOnce("build", "building a flare: " + e.Message);
            v.Destroy();
            return null;
        }
        return v;
    }

    private void Build()
    {
        var prefab = Prefab();
        var go = prefab != null ? Object.Instantiate(prefab) : new GameObject();
        go.name = "OTHPlus_Flare";
        Root = go;
        _tr = go.transform;
        _tr.position = Origin;
        FlareTint.Apply(go, Color);

        if (go.GetComponentInChildren<Light>(true) == null)
        {
            var light = go.AddComponent<Light>();
            light.type = LightType.Point;
            light.color = Color;
            light.range = 90f;
            light.intensity = 6f;
            light.shadows = LightShadows.None;
        }
        AddTrail(go);
        go.SetActive(true);
    }

    private void AddTrail(GameObject go)
    {
        try
        {
            Material mat = null;
            foreach (var r in go.GetComponentsInChildren<ParticleSystemRenderer>(true))
            {
                if (r != null && r.sharedMaterial != null) { mat = r.sharedMaterial; break; }
            }
            if (mat == null)
            {
                if (_fallbackTrail == null)
                {
                    var shader = Shader.Find("Sprites/Default");
                    if (shader != null) _fallbackTrail = new Material(shader);
                }
                mat = _fallbackTrail;
            }
            if (mat == null) return;

            var trail = go.AddComponent<TrailRenderer>();
            trail.sharedMaterial = mat;
            trail.time = Math.Min(10f, Life);
            trail.minVertexDistance = 1f;
            trail.startWidth = 1.2f;
            trail.endWidth = 5f;
            trail.startColor = new Color(Color.r, Color.g, Color.b, 0.6f);
            trail.endColor = new Color(Color.r * 0.5f + 0.35f, Color.g * 0.5f + 0.35f, Color.b * 0.5f + 0.35f, 0f); // smoke greys out
            trail.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            trail.receiveShadows = false;
            _trail = trail;
        }
        catch (Exception e)
        {
            DebugLog.Error(FlaresPlugin.ModId, e, "FlareVisual.AddTrail");
            FlaresCore.LogOnce("trail", "the smoke trail failed, the flare has none: " + e.Message);
        }
    }

    /// <summary>Moves the flare for this frame; false when it is over (or its object is gone).</summary>
    public bool Update(float now, Vector3 camPos)
    {
        if (Root == null || _tr == null) return false;
        float age = now - Born;
        if (age >= Life) return false;
        _tr.position = Origin + Vector3.up * FlareMath.Height(age, Apex, RiseTime, Drift);
        float grow = FlareMath.FlareScale(Vector3.Distance(camPos, _tr.position), GrowFrom, MaxGrow);
        _tr.localScale = Vector3.one * (grow * Mathf.Max(0.02f, FlareMath.LifeAlpha(age, Life, FadeOut)));
        if (_trail != null) _trail.widthMultiplier = grow;
        return true;
    }

    public void Destroy()
    {
        try { if (Root != null) Object.Destroy(Root); }
        catch (Exception e) { DebugLog.Error(FlaresPlugin.ModId, e, "FlareVisual.Destroy"); }
        Root = null;
    }
}
