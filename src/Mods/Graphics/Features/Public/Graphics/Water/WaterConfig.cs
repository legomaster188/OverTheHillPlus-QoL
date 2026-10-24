using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;
using Il2CppInterop.Runtime;
using UnityEngine;
using Object = UnityEngine.Object;
using OverTheHillPlus.Graphics;

namespace OverTheHillPlus.Features.Public.Graphics.Water;

/// <summary>Settings for the water and weather visuals (config section "Graphics.Water"). Everything here is local and visual only.</summary>
internal static class WaterCfg
{
    public const string Section = "Graphics.Water";

    // Water surface (Stylized Water 3 materials)
    public static ConfigEntry<bool> WaterOn;
    public static ConfigEntry<string> Preset;
    public static ConfigEntry<float> Tint, Hue, Brightness, Clarity, Reflections, SunGlint, WaveHeight, WaveSpeed, Ripples, Foam;
    public static ConfigEntry<string> Ssr;

    // Rain and snow (COZY particle effects)
    public static ConfigEntry<bool> WeatherOn;
    public static ConfigEntry<float> RainDensity, SnowDensity, AmbientDensity, DropSize, Splashes, CarSplash;

    // Ground wetness (environment wetness + MicroSplat puddles)
    public static ConfigEntry<bool> WetOn, RainRipples;
    public static ConfigEntry<float> WetScale, WetMin, Puddles, PuddleMin;

    // Car dirt (stain shader amounts)
    public static ConfigEntry<bool> DirtOn, OnlyMine;
    public static ConfigEntry<float> DirtScale, CarSnow, CarWet;

    public static readonly string[] Presets = { "Game", "Tropical", "Glacier", "Deep blue", "Stream", "Murky", "Custom" };
    public static readonly string[] SsrModes = { "Game", "On", "Off" };

    /// <summary>Bumped on every change in this config section, so each part re-applies only when needed.</summary>
    public static int Version { get; private set; }
    public static float LastChange { get; private set; }

    public static void Bind(ConfigFile f)
    {
        ConfigEntry<float> F(string key, float def, float min, float max, string desc) =>
            f.Bind(Section, key, def, new ConfigDescription(desc, new AcceptableValueRange<float>(min, max)));

        WaterOn = f.Bind(Section, "WaterEnabled", false, "Adjust the look of rivers and lakes (Stylized Water 3 materials). Visual only.");
        Preset = f.Bind(Section, "WaterColourPreset", "Game", new ConfigDescription("Water colour preset.", new AcceptableValueList<string>(Presets)));
        Tint = F("WaterTintStrength", 0.7f, 0f, 1f, "How strongly the colour preset replaces the game's water colour.");
        Hue = F("WaterCustomHue", 185f, 0f, 360f, "Hue used by the Custom colour preset (degrees).");
        Brightness = F("WaterBrightness", 1f, 0.3f, 2f, "Water colour brightness multiplier.");
        Clarity = F("WaterClarity", 1f, 0.25f, 4f, "Water clarity: above 1 you see deeper into the water, below 1 it gets murkier.");
        Reflections = F("WaterReflections", 1f, 0f, 2f, "Reflection strength multiplier.");
        SunGlint = F("WaterSunGlint", 1f, 0f, 3f, "Sun glint / sparkle strength multiplier.");
        WaveHeight = F("WaterWaveHeight", 1f, 0f, 4f, "Visual wave height multiplier (physics and buoyancy are unchanged).");
        WaveSpeed = F("WaterWaveSpeed", 1f, 0f, 3f, "Wave and surface flow animation speed multiplier.");
        Ripples = F("WaterRippleDetail", 1f, 0f, 3f, "Surface normal (ripple detail) strength multiplier.");
        Foam = F("WaterFoam", 1f, 0f, 3f, "Foam strength multiplier.");
        Ssr = f.Bind(Section, "WaterScreenSpaceReflections", "Game", new ConfigDescription("Stylized Water screen-space reflections: Game, On or Off (experimental).", new AcceptableValueList<string>(SsrModes)));

        WeatherOn = f.Bind(Section, "WeatherEnabled", false, "Adjust rain, snow and ambient weather particles.");
        RainDensity = F("RainDensity", 1f, 0f, 3f, "Rain particle density multiplier.");
        SnowDensity = F("SnowDensity", 1f, 0f, 3f, "Snow / hail particle density multiplier.");
        AmbientDensity = F("AmbientParticleDensity", 1f, 0f, 3f, "Leaves, pollen, dust motes, bugs and other ambient weather particles.");
        DropSize = F("PrecipitationSize", 1f, 0.3f, 2.5f, "Rain drop / snow flake size multiplier.");
        Splashes = F("RainSplashes", 1f, 0f, 1f, "Rain and snow impact splashes (0 hides them).");
        CarSplash = F("CarWaterSplash", 1f, 0f, 2.5f, "Water splash amount when driving through rivers and lakes.");

        WetOn = f.Bind(Section, "WetnessEnabled", false, "Adjust how wet the ground and vegetation look in rain.");
        WetScale = F("WetnessScale", 1f, 0f, 3f, "Ground / vegetation wetness multiplier.");
        WetMin = F("WetnessMinimum", 0f, 0f, 1f, "Minimum wetness (a wet look even in dry weather).");
        Puddles = F("PuddleScale", 1f, 0f, 3f, "Puddle amount multiplier (MicroSplat terrain puddles, when the map uses them).");
        PuddleMin = F("PuddleMinimum", 0f, 0f, 1f, "Minimum puddle amount.");
        RainRipples = f.Bind(Section, "PuddleRainRipples", true, "Show rain ripples in puddles.");

        DirtOn = f.Bind(Section, "CarDirtEnabled", false, "Scale how dirty, snowy and wet cars look (local visual only).");
        DirtScale = F("CarDirtScale", 1f, 0f, 3f, "Dust, mud and sand on cars.");
        CarSnow = F("CarSnowScale", 1f, 0f, 3f, "Snow build-up on cars.");
        CarWet = F("CarWetScale", 1f, 0f, 3f, "Wet / rain-drop look on cars.");
        OnlyMine = f.Bind(Section, "CarDirtOnlyMyCar", false, "Only change the look of your own car.");

        f.SettingChanged += OnSettingChanged;
    }

    private static void OnSettingChanged(object sender, SettingChangedEventArgs e)
    {
        try
        {
            if (e == null || e.ChangedSetting == null || e.ChangedSetting.Definition.Section != Section) return;
            Version++;
            LastChange = Time.unscaledTime;
        }
        catch (Exception ex) { DebugLog.Error("graphics", ex, "WaterCfg.OnSettingChanged"); }
    }

    public static void ResetToDefault(params ConfigEntryBase[] entries)
    {
        foreach (var e in entries)
            if (e != null) e.BoxedValue = e.DefaultValue;
    }
}

/// <summary>Small helpers shared by the water/weather parts: lazy Harmony patches, cached object searches, log-once.</summary>
internal static class Gfx
{
    private static Harmony _harmony;
    private static readonly HashSet<string> Logged = new();
    private static readonly HashSet<string> PatchedGroups = new();

    private static Harmony H => _harmony ??= new Harmony(GraphicsPlugin.Guid + ".gfx-water");

    public static void Once(string key, string message)
    {
        if (Logged.Add(key)) GraphicsPlugin.Logger.LogWarning("[Graphics.Water] " + message);
    }

    /// <summary>Installs a group of patches the first time it is needed. Each patch is independent: one failing does not stop the others.</summary>
    public static void EnsurePatched(string group, Action<Harmony> install)
    {
        if (!PatchedGroups.Add(group)) return;
        try { install(H); }
        catch (Exception e) { DebugLog.Error("graphics", e, "Gfx.EnsurePatched"); Once("patch-" + group, $"patch group {group} failed: {e.Message}"); }
    }

    public static bool Patch(Harmony h, Type type, string method, Type[] args, Type host, string prefix = null, string postfix = null)
    {
        try
        {
            var m = args == null ? AccessTools.Method(type, method) : AccessTools.Method(type, method, args);
            if (m == null) { Once("missing-" + type.Name + method, $"{type.Name}.{method} not found, that option will do nothing"); return false; }
            h.Patch(m,
                prefix == null ? null : new HarmonyMethod(host, prefix),
                postfix == null ? null : new HarmonyMethod(host, postfix));
            return true;
        }
        catch (Exception e)
        {
            DebugLog.Error("graphics", e, "Gfx.Patch");
            Once("patchfail-" + type.Name + method, $"could not patch {type.Name}.{method}: {e.Message}");
            return false;
        }
    }

    /// <summary>All loaded objects of a type. Not for per-frame use: call it every few seconds at most.</summary>
    public static List<T> Find<T>() where T : Object
    {
        var list = new List<T>();
        try
        {
            foreach (var o in Object.FindObjectsOfType(Il2CppType.Of<T>()))
            {
                var t = o == null ? null : o.TryCast<T>();
                if (t != null) list.Add(t);
            }
        }
        catch (Exception e) { DebugLog.Error("graphics", e, "Gfx.Find"); Once("find-" + typeof(T).Name, $"search for {typeof(T).Name} failed: {e.Message}"); }
        return list;
    }

    public static bool Near(float a, float b) => Mathf.Abs(a - b) < 0.001f;
}
