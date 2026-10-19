using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering.Universal;
using Object = UnityEngine.Object;

namespace OverTheHillPlus.RealNights;

/// <summary>
/// Makes the lamps on cars light the ground. The game's roof light bars, bull-bar lamps and the custom light bars only
/// glow on the car (a texture channel); here each gets a few real URP lights from a small pool. At most MaxLights are
/// on at once: your own car first, then the nearest cars (<see cref="LightBudget"/>), none beyond the cull distance,
/// none by day, and a car's lamps only while its headlights are on. Brightness and range follow the car's own
/// headlight config, so the game's units carry over. The pool is made once and moved (nothing is made or destroyed
/// per frame); a light follows its car's transform every frame and fades in and out.
/// </summary>
internal static class LampLights
{
    private const float PickEvery = 0.5f;
    private const float FadePerSecond = 3f;

    private sealed class PoolLight
    {
        public GameObject Go;
        public Transform T;
        public Light Light;
        public SeenCar Car;
        public Transform Root;
        public long Key;
        public LampKind Kind;
        public LampTint Tint;
        public int Count;
        public Vector3 LocalPos, LocalDir;
        public float RefIntensity, Fade, Target, Phase;
        public bool Wanted;
    }

    private struct Plan
    {
        public SeenCar Car;
        public LampSpec Spec;
        public int Count;
        public float Offset;
        public long Key;
    }

    private static readonly List<PoolLight> Pool = new();
    private static readonly List<Plan> Plans = new();
    private static readonly List<LampCandidate> Candidates = new();
    private static readonly Dictionary<long, PoolLight> ByKey = new();
    private static readonly HashSet<int> LitCars = new();
    private static float _nextPick;
    private static int _lastLogged = -1;
    private static float _nextLog;
    private static bool _loggedPipeline;
    private static Transform _cam;
    private static int _errors;

    /// <summary>Lights on right now / cars they are on, for the MODS page.</summary>
    internal static int On, CarsLit;
    internal static bool Off { get; private set; }

    internal static void Tick(float dt)
    {
        if (Off) return;
        try
        {
            if (!RealNightsConfig.Lamps.Value || NightSky.Look.Lamp < 0.02f)
            {
                if (On > 0 || Pool.Count > 0) FadeAll(dt);
                return;
            }
            CarScan.Tick();
            float now = Time.unscaledTime;
            if (now >= _nextPick)
            {
                _nextPick = now + PickEvery;
                Pick();
            }
            Follow(dt);
            LogChange(now);
        }
        catch (Exception e)
        {
            NightSky.Failed(e, "LampLights.Tick");
            if (++_errors >= 25)
            {
                Off = true;
                RealNightsPlugin.Logger.LogError("Real Nights: too many errors in the lamp lights; they are off for this session.");
                DestroyAll();
            }
        }
    }

    private static void Pick()
    {
        LogPipelineOnce();
        Plans.Clear();
        Candidates.Clear();
        int perCar = Mathf.Clamp(RealNightsConfig.LightsPerCar.Value, 1, 6);
        var camPos = CameraPosition();

        foreach (var car in CarScan.Cars)
        {
            if (!CarScan.Alive(car.Root) || !CarScan.Alive(car.Lights)) continue;
            bool on;
            try { on = car.Lights.AreHeadlightsOn; }
            catch (Exception e) { NightSky.Failed(e, "LampLights.HeadlightsOn"); continue; }
            car.HeadlightsOn = on;
            if (!on) continue;
            float dist = (car.Root.position - camPos).magnitude;
            for (int s = 0; s < car.Lamps.Count; s++)
            {
                var spec = car.Lamps[s];
                int count = LampRules.CountFor(spec.Kind, spec.HalfWidth, perCar);
                var xs = LampRules.Offsets(count, spec.HalfWidth);
                for (int k = 0; k < count; k++)
                {
                    long key = ((long)car.Id << 16) | (uint)(s << 4 | k);
                    Plans.Add(new Plan { Car = car, Spec = spec, Count = count, Offset = xs[k], Key = key });
                    Candidates.Add(new LampCandidate { Car = car.Id, Distance = dist, IsLocal = car.Local, Lit = ByKey.ContainsKey(key) });
                }
            }
        }

        int max = Mathf.Clamp(RealNightsConfig.MaxLights.Value, 0, 16);
        var chosen = LightBudget.Pick(Candidates, max, perCar, Mathf.Max(10f, RealNightsConfig.CullDistance.Value));

        foreach (var p in Pool) p.Wanted = false;
        bool shadowGiven = false;
        foreach (int idx in chosen)
        {
            var plan = Plans[idx];
            if (!ByKey.TryGetValue(plan.Key, out var pl))
            {
                pl = FreeLight();
                if (pl == null) break;
                ByKey[plan.Key] = pl;
                pl.Key = plan.Key;
            }
            Assign(pl, plan, ref shadowGiven);
        }
        foreach (var p in Pool)
            if (!p.Wanted) p.Target = 0f;
    }

    private static PoolLight FreeLight()
    {
        foreach (var p in Pool)
            if (!p.Light.enabled && p.Fade <= 0.001f && !ByKey.ContainsValue(p)) return p;
        // Every light is busy: one this pass no longer wants (fading out) is taken over.
        foreach (var p in Pool)
            if (!p.Wanted) { ByKey.Remove(p.Key); return p; }
        if (Pool.Count >= 16) return null;
        var go = new GameObject("RealNights_Lamp" + Pool.Count);
        Object.DontDestroyOnLoad(go);
        var light = go.AddComponent<Light>();
        light.shadows = LightShadows.None;
        light.enabled = false;
        var pool = new PoolLight { Go = go, T = go.transform, Light = light, Phase = Pool.Count * 1.7f };
        Pool.Add(pool);
        return pool;
    }

    private static void Assign(PoolLight pl, Plan plan, ref bool shadowGiven)
    {
        var spec = plan.Spec;
        var car = plan.Car;
        bool fresh = pl.Car != car || pl.Kind != spec.Kind || pl.Tint != spec.Tint || !pl.Light.enabled;
        pl.Car = car;
        pl.Root = car.Root;
        pl.Kind = spec.Kind;
        pl.Tint = spec.Tint;
        pl.Count = plan.Count;
        pl.Wanted = true;
        pl.Target = 1f;
        CarScan.Reference(car, out float refI, out float refR);
        pl.RefIntensity = refI;

        var light = pl.Light;
        bool beacon = spec.Kind == LampKind.Beacon;
        if (fresh)
        {
            light.type = beacon ? LightType.Point : LightType.Spot;
            var rgb = LampRules.Rgb(spec.Tint);
            light.color = new Color(rgb.r, rgb.g, rgb.b);
            light.intensity = 0f;
            pl.Fade = 0f;
        }
        light.range = LampRules.Range(refR, RealNightsConfig.LampRange.Value, spec.Kind);
        if (!beacon)
        {
            float angle = LampRules.SpotAngle(spec.Kind, plan.Count);
            light.spotAngle = angle;
            light.innerSpotAngle = angle * 0.45f;
        }

        // The front lamp of your own car throws shadows (when asked for): the ground and rocks stand out in its beam.
        bool shadow = RealNightsConfig.LampShadows.Value && car.Local && !beacon && !shadowGiven;
        if (shadow)
        {
            shadowGiven = true;
            if (light.shadows == LightShadows.None)
            {
                light.shadows = LightShadows.Hard;
                light.shadowStrength = 0.85f;
                light.shadowNearPlane = 0.4f;
            }
        }
        else if (light.shadows != LightShadows.None) light.shadows = LightShadows.None;

        // In the car's space: on the bar's face, a little ahead of it, so the bar does not shade its own light. A beacon
        // glows above its dome.
        pl.LocalPos = beacon
            ? new Vector3(spec.Centre.x, spec.Top + 0.15f, spec.Centre.z)
            : new Vector3(spec.Centre.x + plan.Offset, spec.Centre.y, spec.Front + 0.12f);
        pl.LocalDir = new Vector3(0f, -0.05f, 1f).normalized;
        light.enabled = true;
    }

    private static void Follow(float dt)
    {
        float t = Time.time;
        float need = NightSky.Look.Lamp;
        float userMul = RealNightsConfig.LampIntensity.Value;
        foreach (var p in Pool)
        {
            if (!p.Light.enabled) continue;
            if (!CarScan.Alive(p.Root))
            {
                Kill(p);
                continue;
            }
            p.Fade = Mathf.MoveTowards(p.Fade, p.Target, dt * FadePerSecond);
            if (p.Fade <= 0.001f && p.Target <= 0f)
            {
                Kill(p);
                continue;
            }
            var root = p.Root;
            p.T.SetPositionAndRotation(root.TransformPoint(p.LocalPos), Quaternion.LookRotation(root.TransformDirection(p.LocalDir), root.up));

            float flicker = 1f;
            if (p.Kind == LampKind.Beacon)
            {
                float wave = Mathf.Sin(t * 9f + p.Phase);
                flicker = 0.5f + 0.5f * Mathf.Abs(wave);
                if (p.Tint == LampTint.RedBlue)
                {
                    var a = LampRules.Rgb(LampTint.Red);
                    var b = LampRules.Rgb(LampTint.Blue);
                    float k = wave > 0f ? 1f : 0f;
                    p.Light.color = new Color(Mathf.Lerp(a.r, b.r, k), Mathf.Lerp(a.g, b.g, k), Mathf.Lerp(a.b, b.b, k));
                }
            }
            p.Light.intensity = LampRules.Intensity(p.RefIntensity, userMul, need, p.Kind, p.Count) * p.Fade * flicker;
        }
        int on = 0;
        LitCars.Clear();
        foreach (var p in Pool)
            if (p.Light.enabled && p.Target > 0f) { on++; if (p.Car != null) LitCars.Add(p.Car.Id); }
        On = on;
        CarsLit = LitCars.Count;
    }

    private static void FadeAll(float dt)
    {
        foreach (var p in Pool) p.Target = 0f;
        Follow(dt);
        if (On == 0) _lastLogged = -1;
    }

    private static void Kill(PoolLight p)
    {
        p.Light.enabled = false;
        p.Light.shadows = LightShadows.None;
        p.Fade = 0f;
        p.Target = 0f;
        p.Car = null;
        p.Root = null;
        ByKey.Remove(p.Key);
    }

    private static void DestroyAll()
    {
        foreach (var p in Pool)
            if (p.Go != null) Object.Destroy(p.Go);
        Pool.Clear();
        ByKey.Clear();
        On = CarsLit = 0;
    }

    private static Vector3 CameraPosition()
    {
        if (!CarScan.Alive(_cam))
        {
            var cam = Camera.main;
            _cam = cam != null ? cam.transform : null;
        }
        return _cam != null ? _cam.position : Vector3.zero;
    }

    private static void LogChange(float now)
    {
        if (On == _lastLogged || now < _nextLog) return;
        _lastLogged = On;
        _nextLog = now + 1f;
        DebugLog.Info(RealNightsPlugin.ModId, $"lamp lights: {On} on, on {CarsLit} car(s), darkness need {NightSky.Look.Lamp:0.00}");
    }

    /// <summary>The render pipeline's limits for extra lights, logged once (the page says why a lamp might not show).</summary>
    private static void LogPipelineOnce()
    {
        if (_loggedPipeline) return;
        _loggedPipeline = true;
        try
        {
            var asset = UniversalRenderPipeline.asset;
            if (asset == null) return;
            DebugLog.Info(RealNightsPlugin.ModId,
                $"URP: extra lights {asset.additionalLightsRenderingMode}, at most {asset.maxAdditionalLightsCount} per camera, extra-light shadows {(asset.supportsAdditionalLightShadows ? "on" : "off")}");
        }
        catch (Exception e) { NightSky.Failed(e, "LampLights.LogPipelineOnce"); }
    }

    internal static string Status =>
        Off ? "off after errors" : !RealNightsConfig.Lamps.Value ? "switched off" : $"{On} light(s) on {CarsLit} car(s), {CarScan.Cars.Count} car(s) seen";
}
