using System;
using DistantLands.Cozy;
using UnityEngine;

namespace OverTheHillPlus.Features.Public.Graphics.Water;

/// <summary>
/// Ground, rooftop and vegetation wetness plus MicroSplat puddles.
///
/// From the decompiled game code:
/// - <c>EnvironmentNetworkedMaterialManager.UpdateRainAmount(amount)</c> scales the amount by globalWetnessScale and
///   writes the global shader values _Rain_Amount and _Global_WetnessParams and the TVE (vegetation) wetness.
///   It runs when the networked rain value changes. A prefix scales the amount it is given; nothing networked is written.
/// - <c>CozyMicrosplatModule.UpdateShaderProperties()</c> writes _Global_WetnessParams, _Global_PuddleParams,
///   _Global_RainIntensity and _Global_StreamMax from COZY's groundwater amount. A postfix rewrites those it drives.
/// </summary>
internal static class GroundWetness
{
    private static readonly int WetnessParams = Shader.PropertyToID("_Global_WetnessParams");
    private static readonly int PuddleParams = Shader.PropertyToID("_Global_PuddleParams");
    private static readonly int RainIntensity = Shader.PropertyToID("_Global_RainIntensity");
    private static readonly int StreamMax = Shader.PropertyToID("_Global_StreamMax");

    private static int _applied; // matches the initial WaterCfg.Version, so nothing runs while off
    private static bool _active;

    public static int EnvironmentCount { get; private set; } = -1;
    public static string MicroSplatState { get; private set; } = "not checked";
    public static float LastRain { get; private set; } = -1f;
    public static float LastGroundwater { get; private set; } = -1f;

    public static void InstallPatches() => Gfx.EnsurePatched("wetness", h =>
    {
        Gfx.Patch(h, typeof(EnvironmentNetworkedMaterialManager), nameof(EnvironmentNetworkedMaterialManager.UpdateRainAmount), new[] { typeof(float) }, typeof(GroundWetness), prefix: nameof(RainPrefix));
        Gfx.Patch(h, typeof(CozyMicrosplatModule), nameof(CozyMicrosplatModule.UpdateShaderProperties), Type.EmptyTypes, typeof(GroundWetness), postfix: nameof(MicroSplatPostfix));
    });

    public static void Tick()
    {
        bool on = WaterCfg.WetOn.Value;
        if (on) InstallPatches();

        if (on != _active)
        {
            _active = on;
            _applied = -1; // turning off also needs one refresh, to put the game's values back
        }
        if (!on && _applied != -1) return;
        if (_applied != WaterCfg.Version && Time.unscaledTime - WaterCfg.LastChange > 0.25f)
        {
            _applied = WaterCfg.Version;
            Refresh(); // with the option off this re-runs the game's own code, restoring its values
        }
    }

    public static float Wet(float amount) =>
        Mathf.Clamp01(Mathf.Max(amount * WaterCfg.WetScale.Value, WaterCfg.WetMin.Value));

    private static void RainPrefix(ref float amount)
    {
        try
        {
            LastRain = amount;
            if (!WaterCfg.WetOn.Value) return;
            amount = Wet(amount);
        }
        catch { }
    }

    private static void MicroSplatPostfix(CozyMicrosplatModule __instance)
    {
        if (!WaterCfg.WetOn.Value || __instance == null) return;
        try
        {
            var weather = __instance.weatherSphere;
            var climate = weather == null ? null : weather.climateModule;
            if (climate == null) return;
            float gw = climate.groundwaterAmount;
            LastGroundwater = gw;

            if (__instance.updateWetness)
            {
                float lo = __instance.minWetness, hi = __instance.maxWetness;
                float w = Wet(Mathf.Clamp(gw, Mathf.Min(lo, hi), Mathf.Max(lo, hi)));
                Shader.SetGlobalVector(WetnessParams, new Vector4(lo, w, 0f, 0f));
            }
            float puddles = Mathf.Clamp01(Mathf.Max(gw * WaterCfg.Puddles.Value, WaterCfg.PuddleMin.Value));
            if (__instance.updatePuddles) Shader.SetGlobalFloat(PuddleParams, puddles);
            if (__instance.updateStreams) Shader.SetGlobalFloat(StreamMax, puddles);
            if (__instance.updateRainRipples && !WaterCfg.RainRipples.Value) Shader.SetGlobalFloat(RainIntensity, 0f);
        }
        catch (Exception e) { Gfx.Once("microsplat", "puddle hook failed: " + e.Message); }
    }

    /// <summary>Makes the game re-send its current values through the (patched) methods.</summary>
    private static void Refresh()
    {
        var envs = Gfx.Find<EnvironmentNetworkedMaterialManager>();
        EnvironmentCount = envs.Count;
        foreach (var env in envs)
        {
            try { env.UpdateRainAmount(); }
            catch (Exception e) { Gfx.Once("env-refresh", "wetness refresh skipped (environment not ready): " + e.Message); }
        }

        var modules = Gfx.Find<CozyMicrosplatModule>();
        if (modules.Count == 0) MicroSplatState = "not in this map";
        foreach (var m in modules)
        {
            try
            {
                MicroSplatState = $"wetness {(m.updateWetness ? "on" : "off")}, puddles {(m.updatePuddles ? "on" : "off")}, ripples {(m.updateRainRipples ? "on" : "off")}";
                m.UpdateShaderProperties();
            }
            catch (Exception e) { Gfx.Once("ms-refresh", "puddle refresh failed: " + e.Message); }
        }
    }
}
