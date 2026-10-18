using System;
using System.Collections.Generic;
using System.Linq;
using DistantLands.Cozy;
using UnityEngine;

namespace OverTheHillPlus.Features.Public.Graphics.Water;

/// <summary>
/// Rain, snow and ambient weather particle density, drop size and impact splashes, plus the car's water splash.
///
/// COZY drives weather particles through <c>CozyParticles.Play(weight)</c>, which (from its decompiled body) sets
/// each particle type's emission <c>rateOverTime = emissionAmount * clamp(weight)</c> and plays the system. A postfix
/// multiplies that freshly written rate, so it never compounds. Particle caps, sizes and splash systems are
/// adjusted from captured originals every few seconds and restored when switched off.
/// </summary>
internal static class WeatherParticles
{
    private enum Kind { Rain, Snow, Ambient }

    private sealed class Fx
    {
        public CozyParticles Cp;
        public Kind Kind;
        public readonly List<(ParticleSystem ps, bool splash)> Types = new();
        public float BuiltAt;
        public float LastWeight = -1f; // -1 = Play() without weight
        public bool Played;
    }

    private sealed class Ps
    {
        public ParticleSystem System;
        public ParticleSystemRenderer Renderer;
        public Kind Kind;
        public bool Splash, CarSplash;
        public int Max;
        public float Size, Rate;
        public ParticleSystem.MinMaxCurve RateCurve;
        public bool Rendered;
    }

    private static readonly Dictionary<IntPtr, Fx> Fxs = new();
    private static readonly Dictionary<IntPtr, Ps> Systems = new();
    private static float _nextScan;
    private static int _applied = -1;
    private static bool _active;

    public static int EffectCount => Fxs.Count;
    public static int ActiveEffects { get; private set; }
    public static int CarSplashCount { get; private set; }

    public static void InstallPatches() => Gfx.EnsurePatched("weather", h =>
    {
        Gfx.Patch(h, typeof(CozyParticles), nameof(CozyParticles.Play), new[] { typeof(float) }, typeof(WeatherParticles), postfix: nameof(PlayWeightPostfix));
        Gfx.Patch(h, typeof(CozyParticles), nameof(CozyParticles.Play), Type.EmptyTypes, typeof(WeatherParticles), postfix: nameof(PlayPostfix));
        Gfx.Patch(h, typeof(CozyParticles), nameof(CozyParticles.Stop), Type.EmptyTypes, typeof(WeatherParticles), postfix: nameof(StopPostfix));
    });

    public static void Tick()
    {
        if (!WaterCfg.WeatherOn.Value)
        {
            if (_active) Restore();
            return;
        }
        InstallPatches();
        _active = true;

        float now = Time.unscaledTime;
        if (now >= _nextScan)
        {
            _nextScan = now + 2.5f;
            Scan();
            ApplySystems();
        }
        // re-apply right away (debounced) when a slider moves
        if (_applied != WaterCfg.Version && now - WaterCfg.LastChange > 0.25f)
        {
            _applied = WaterCfg.Version;
            ApplySystems();
            Replay();
        }
    }

    private static float Factor(Kind k) => k switch
    {
        Kind.Rain => WaterCfg.RainDensity.Value,
        Kind.Snow => WaterCfg.SnowDensity.Value,
        _ => WaterCfg.AmbientDensity.Value,
    };

    private static Kind Classify(string name)
    {
        string n = (name ?? "").ToLowerInvariant();
        if (n.Contains("snow") || n.Contains("hail") || n.Contains("sleet") || n.Contains("blizzard")) return Kind.Snow;
        if (n.Contains("rain") || n.Contains("thunderstorm") || n.Contains("drizzle") || n.Contains("shower") && !n.Contains("meteor")) return Kind.Rain;
        return Kind.Ambient;
    }

    private static bool IsSplashName(string name)
    {
        string n = (name ?? "").ToLowerInvariant();
        return n.Contains("splash") || n.Contains("ripple") || n.Contains("impact") || n.Contains("puddle");
    }

    // ---- COZY Play hooks ----

    private static void PlayWeightPostfix(CozyParticles __instance, float weight)
    {
        if (!WaterCfg.WeatherOn.Value || __instance == null) return;
        try
        {
            var fx = Get(__instance);
            fx.LastWeight = weight;
            fx.Played = true;
            ScaleRates(fx);
        }
        catch (Exception e) { DebugLog.Error("graphics", e, "WeatherParticles.PlayWeightPostfix"); Gfx.Once("play", "weather particle hook failed: " + e.Message); }
    }

    private static void PlayPostfix(CozyParticles __instance)
    {
        if (!WaterCfg.WeatherOn.Value || __instance == null) return;
        try
        {
            var fx = Get(__instance);
            fx.LastWeight = -1f;
            fx.Played = true;
            ScaleRates(fx);
        }
        catch (Exception e) { DebugLog.Error("graphics", e, "WeatherParticles.PlayPostfix"); Gfx.Once("play0", "weather particle hook failed: " + e.Message); }
    }

    private static void StopPostfix(CozyParticles __instance)
    {
        try
        {
            if (__instance != null && Fxs.TryGetValue(__instance.Pointer, out var fx)) fx.Played = false;
        }
        catch (Exception ex) { DebugLog.Error("graphics", ex, "WeatherParticles.StopPostfix"); }
    }

    private static void ScaleRates(Fx fx)
    {
        float density = Factor(fx.Kind);
        float splash = WaterCfg.Splashes.Value;
        foreach (var (ps, isSplash) in fx.Types)
        {
            float f = isSplash ? splash : density;
            if (Gfx.Near(f, 1f) || ps == null) continue;
            var em = ps.emission;
            // the interop setter of rateOverTimeMultiplier is stripped; COZY just wrote a constant rate, so rewrite it
            em.rateOverTime = new ParticleSystem.MinMaxCurve(em.rateOverTimeMultiplier * f);
        }
    }

    private static Fx Get(CozyParticles cp)
    {
        if (!Fxs.TryGetValue(cp.Pointer, out var fx) || fx.Cp == null)
        {
            fx = new Fx { Cp = cp };
            Fxs[cp.Pointer] = fx;
        }
        if (fx.BuiltAt <= 0f || Time.unscaledTime - fx.BuiltAt > 10f) Build(fx);
        return fx;
    }

    private static void Build(Fx fx)
    {
        fx.BuiltAt = Time.unscaledTime;
        fx.Types.Clear();
        var cp = fx.Cp;
        string name = cp.gameObject != null ? cp.gameObject.name : "";
        fx.Kind = Classify(name);
        var types = cp.m_ParticleTypes;
        if (types == null) return;
        for (int i = 0; i < types.Count; i++)
        {
            var t = types[i];
            var ps = t == null ? null : t.particleSystem;
            if (ps == null) continue;
            fx.Types.Add((ps, IsSplashName(ps.gameObject.name)));
        }
        // a parent named neutrally ("Particles") but with "rain" systems inside
        if (fx.Kind == Kind.Ambient)
            foreach (var (ps, _) in fx.Types)
            {
                var k = Classify(ps.gameObject.name);
                if (k != Kind.Ambient) { fx.Kind = k; break; }
            }
    }

    /// <summary>Calls Play again on each effect so its rates are rewritten (and re-scaled, or restored when off).</summary>
    private static void Replay()
    {
        foreach (var fx in Fxs.Values.ToList())
        {
            if (!fx.Played || fx.Cp == null) continue;
            try
            {
                if (!fx.Cp.isActiveAndEnabled) continue;
                if (fx.LastWeight >= 0f) fx.Cp.Play(fx.LastWeight);
                else fx.Cp.Play();
            }
            catch (Exception e) { DebugLog.Error("graphics", e, "WeatherParticles.Replay"); Gfx.Once("replay", "re-applying weather particles failed: " + e.Message); }
        }
    }

    // ---- caps, sizes, splashes ----

    private static void Scan()
    {
        foreach (var key in Fxs.Where(kv => kv.Value.Cp == null).Select(kv => kv.Key).ToList()) Fxs.Remove(key);
        foreach (var key in Systems.Where(kv => kv.Value.System == null).Select(kv => kv.Key).ToList()) Systems.Remove(key);

        int active = 0;
        foreach (var cp in Gfx.Find<CozyParticles>())
        {
            try
            {
                var fx = Get(cp);
                if (cp.isActiveAndEnabled) active++;
                var subs = new HashSet<IntPtr>();
                var all = cp.GetComponentsInChildren<ParticleSystem>(true);
                foreach (var ps in all)
                {
                    try
                    {
                        var se = ps.subEmitters;
                        int n = se.subEmittersCount;
                        for (int i = 0; i < n; i++)
                        {
                            var sub = se.GetSubEmitterSystem(i);
                            if (sub != null) subs.Add(sub.Pointer);
                        }
                    }
                    catch (Exception ex) { DebugLog.Error("graphics", ex, "WeatherParticles.Scan"); }
                }
                foreach (var ps in all)
                {
                    if (ps == null || Systems.ContainsKey(ps.Pointer)) continue;
                    bool splash = subs.Contains(ps.Pointer) || IsSplashName(ps.gameObject.name);
                    var k = fx.Kind == Kind.Ambient ? Classify(ps.gameObject.name) : fx.Kind;
                    Capture(ps, k, splash, false);
                }
            }
            catch (Exception e) { DebugLog.Error("graphics", e, "WeatherParticles.Scan"); Gfx.Once("scan-cp", "weather particle scan failed: " + e.Message); }
        }
        ActiveEffects = active;

        int car = 0;
        foreach (var wpm in Gfx.Find<WaterParticlesManager>())
        {
            try
            {
                var ps = wpm.waterSplashingParticleSystem;
                if (ps == null) continue;
                car++;
                if (!Systems.ContainsKey(ps.Pointer)) Capture(ps, Kind.Ambient, false, true);
            }
            catch (Exception e) { DebugLog.Error("graphics", e, "WeatherParticles.Scan"); Gfx.Once("scan-wpm", "car splash scan failed: " + e.Message); }
        }
        CarSplashCount = car;
    }

    private static void Capture(ParticleSystem ps, Kind kind, bool splash, bool carSplash)
    {
        var main = ps.main;
        var em = ps.emission;
        var r = ps.GetComponent<ParticleSystemRenderer>();
        Systems[ps.Pointer] = new Ps
        {
            System = ps,
            Renderer = r,
            Kind = kind,
            Splash = splash,
            CarSplash = carSplash,
            Max = main.maxParticles,
            Size = main.startSizeMultiplier,
            Rate = em.rateOverTimeMultiplier,
            RateCurve = em.rateOverTime,
            Rendered = r != null && r.enabled,
        };
    }

    private static void ApplySystems()
    {
        foreach (var o in Systems.Values)
        {
            if (o.System == null) continue;
            try
            {
                var main = o.System.main;
                bool precip = o.Kind != Kind.Ambient;
                if (o.CarSplash)
                {
                    float f = WaterCfg.CarSplash.Value;
                    var em = o.System.emission;
                    em.rateOverTime = new ParticleSystem.MinMaxCurve(o.Rate * f);
                    main.maxParticles = Cap(o.Max, Mathf.Max(1f, f));
                    if (o.Renderer != null) o.Renderer.enabled = o.Rendered && f > 0.01f;
                }
                else if (o.Splash)
                {
                    float f = WaterCfg.Splashes.Value;
                    main.maxParticles = f >= 0.999f ? o.Max : Math.Max(1, (int)(o.Max * f));
                    main.startSizeMultiplier = o.Size * (precip ? Mathf.Lerp(1f, WaterCfg.DropSize.Value, 0.5f) : 1f);
                    if (o.Renderer != null) o.Renderer.enabled = o.Rendered && f > 0.01f;
                }
                else
                {
                    main.maxParticles = Cap(o.Max, Mathf.Max(1f, Factor(o.Kind)));
                    if (precip) main.startSizeMultiplier = o.Size * WaterCfg.DropSize.Value;
                }
            }
            catch (Exception e) { DebugLog.Error("graphics", e, "WeatherParticles.ApplySystems"); Gfx.Once("apply-ps", "adjusting a weather particle system failed: " + e.Message); }
        }
    }

    private static int Cap(int max, float f) => (int)Mathf.Min(max * f, 200000f);

    private static void Restore()
    {
        foreach (var o in Systems.Values)
        {
            if (o.System == null) continue;
            try
            {
                var main = o.System.main;
                main.maxParticles = o.Max;
                main.startSizeMultiplier = o.Size;
                if (o.CarSplash && o.RateCurve != null) { var em = o.System.emission; em.rateOverTime = o.RateCurve; }
                if (o.Renderer != null) o.Renderer.enabled = o.Rendered;
            }
            catch (Exception ex) { DebugLog.Error("graphics", ex, "WeatherParticles.Restore"); }
        }
        Systems.Clear();
        Replay(); // WeatherOn is false now, so the postfix leaves the game's rates alone
        Fxs.Clear();
        _active = false;
        _applied = -1;
        _nextScan = 0f;
    }
}
