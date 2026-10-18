using System;
using UnityEngine;
using OverTheHillPlus.Graphics;

namespace OverTheHillPlus.Features.Public.Graphics.Water;

/// <summary>
/// "Graphics" tab (PLUS mode): water surface look, rain/snow particles, ground wetness and car dirt.
/// Everything is local and visual only; nothing networked or physical is changed, so it is allowed everywhere.
/// All four parts are off by default and put the game's own values back when switched off.
/// </summary>
internal sealed class WaterWeatherFeature : IFeature
{
    public void Register()
    {
        WaterCfg.Bind(GraphicsConfig.File);
        Registry.OnTick(Tick);
        Registry.Section(Registry.PlusMode, "Graphics", DrawWater, 40);
        Registry.Section(Registry.PlusMode, "Graphics", DrawWeather, 41);
        Registry.Section(Registry.PlusMode, "Graphics", DrawWetness, 42);
        Registry.Section(Registry.PlusMode, "Graphics", DrawDirt, 43);
    }

    private static void Tick()
    {
        // each part catches its own errors so one failing part does not disable the others
        Safe("water", WaterSurface.Tick);
        Safe("weather", WeatherParticles.Tick);
        Safe("wetness", GroundWetness.Tick);
        Safe("dirt", CarDirt.Tick);
    }

    private static void Safe(string key, Action a)
    {
        try { a(); }
        catch (Exception e) { DebugLog.Error("graphics", e, "WaterWeatherFeature.Safe"); Gfx.Once("tick-" + key, $"{key} update failed: {e.Message}"); }
    }

    // ---- menu ----

    private static void DrawWater()
    {
        Menu.Card("Water surface", "Rivers and lakes (Stylized Water 3). Local, visual only: buoyancy and physics stay the same.", () =>
        {
            Menu.Switch(WaterCfg.WaterOn, "Adjust water look", "Off restores the game's water exactly", true);
            bool on = WaterCfg.WaterOn.Value;

            var presets = WaterCfg.Presets;
            var chips = new (string, Action, bool)[presets.Length];
            for (int i = 0; i < presets.Length; i++)
            {
                string p = presets[i];
                chips[i] = (WaterCfg.Preset.Value == p ? "> " + p + " <" : p, () => WaterCfg.Preset.Value = p, on);
            }
            Menu.Chips(4, chips);
            if (WaterCfg.Preset.Value != "Game") Menu.SliderRow(WaterCfg.Tint, "Colour strength", 0f, 1f, "", "", on, percent: true);
            if (WaterCfg.Preset.Value == "Custom") Menu.SliderRow(WaterCfg.Hue, "Custom hue", 0f, 360f, "0", " deg", on);
            Menu.SliderRow(WaterCfg.Brightness, "Brightness", 0.3f, 2f, "", "", on, percent: true);
            Menu.SliderRow(WaterCfg.Clarity, "Clarity", 0.25f, 4f, "0.00", "x", on);
            Menu.SliderRow(WaterCfg.Reflections, "Reflections", 0f, 2f, "", "", on, percent: true);
            Menu.SliderRow(WaterCfg.SunGlint, "Sun glint", 0f, 3f, "", "", on, percent: true);
            Menu.SliderRow(WaterCfg.WaveHeight, "Wave height", 0f, 4f, "", "", on, percent: true);
            Menu.SliderRow(WaterCfg.WaveSpeed, "Wave / flow speed", 0f, 3f, "", "", on, percent: true);
            Menu.SliderRow(WaterCfg.Ripples, "Ripple detail", 0f, 3f, "", "", on, percent: true);
            Menu.SliderRow(WaterCfg.Foam, "Foam", 0f, 3f, "", "", on, percent: true);

            var ssr = WaterCfg.SsrModes;
            var ssrChips = new (string, Action, bool)[ssr.Length];
            for (int i = 0; i < ssr.Length; i++)
            {
                string m = ssr[i];
                ssrChips[i] = ("SSR " + (WaterCfg.Ssr.Value == m ? "> " + m + " <" : m), () => WaterCfg.Ssr.Value = m, on);
            }
            Menu.Chips(3, ssrChips);

            Menu.KeyValue("Water materials", on ? $"{WaterSurface.MaterialCount} found" : "off");
            if (on && WaterSurface.MaterialCount > 0) Menu.KeyValue("Adjustable", WaterSurface.Supported);
            if (on) Menu.KeyValue("Screen-space refl.", WaterSurface.SsrState);
            Menu.Chips(3,
                ("Reset water", () =>
                {
                    WaterCfg.ResetToDefault(WaterCfg.Preset, WaterCfg.Tint, WaterCfg.Hue, WaterCfg.Brightness, WaterCfg.Clarity, WaterCfg.Reflections,
                        WaterCfg.SunGlint, WaterCfg.WaveHeight, WaterCfg.WaveSpeed, WaterCfg.Ripples, WaterCfg.Foam, WaterCfg.Ssr);
                    Menu.SetStatus("Water settings reset");
                }, true),
                ("Rescan", () => { WaterSurface.ForceRescan(); Menu.SetStatus("Looking for water again"); }, on),
                ("Log shader", () => Menu.SetStatus(WaterSurface.LogShaderProperties()), on));
        });
    }

    private static void DrawWeather()
    {
        Menu.Card("Rain and snow", "Weather particle density, drop size and splashes. More particles cost frame rate.", () =>
        {
            Menu.Switch(WaterCfg.WeatherOn, "Adjust weather particles", "Off restores the game's particles", true);
            bool on = WaterCfg.WeatherOn.Value;
            Menu.SliderRow(WaterCfg.RainDensity, "Rain density", 0f, 3f, "", "", on, percent: true);
            Menu.SliderRow(WaterCfg.SnowDensity, "Snow / hail density", 0f, 3f, "", "", on, percent: true);
            Menu.SliderRow(WaterCfg.AmbientDensity, "Leaves, dust, bugs", 0f, 3f, "", "", on, percent: true);
            Menu.SliderRow(WaterCfg.DropSize, "Drop / flake size", 0.3f, 2.5f, "", "", on, percent: true);
            Menu.SliderRow(WaterCfg.Splashes, "Impact splashes", 0f, 1f, "", "", on, percent: true);
            Menu.SliderRow(WaterCfg.CarSplash, "Car water splash", 0f, 2.5f, "", "", on, percent: true);
            if (on)
                Menu.KeyValue("Weather effects", $"{WeatherParticles.EffectCount} loaded, {WeatherParticles.ActiveEffects} active, {WeatherParticles.CarSplashCount} car splash");
            Menu.Chips(2, ("Reset weather", () =>
            {
                WaterCfg.ResetToDefault(WaterCfg.RainDensity, WaterCfg.SnowDensity, WaterCfg.AmbientDensity, WaterCfg.DropSize, WaterCfg.Splashes, WaterCfg.CarSplash);
                Menu.SetStatus("Weather particle settings reset");
            }, true), ("No rain or snow", () =>
            {
                WaterCfg.WeatherOn.Value = true;
                WaterCfg.RainDensity.Value = 0f;
                WaterCfg.SnowDensity.Value = 0f;
                Menu.SetStatus("Rain and snow particles hidden (weather itself is unchanged)");
            }, true));
        });
    }

    private static void DrawWetness()
    {
        Menu.Card("Ground wetness", "Wet ground, rooftops and plants in rain, and terrain puddles. Visual only.", () =>
        {
            Menu.Switch(WaterCfg.WetOn, "Adjust wetness", "Off restores the game's values", true);
            bool on = WaterCfg.WetOn.Value;
            Menu.SliderRow(WaterCfg.WetScale, "Wetness", 0f, 3f, "", "", on, percent: true);
            Menu.SliderRow(WaterCfg.WetMin, "Always at least", 0f, 1f, "", "", on, percent: true);
            Menu.SliderRow(WaterCfg.Puddles, "Puddles", 0f, 3f, "", "", on, percent: true);
            Menu.SliderRow(WaterCfg.PuddleMin, "Puddles at least", 0f, 1f, "", "", on, percent: true);
            Menu.Switch(WaterCfg.RainRipples, "Rain ripples in puddles", null, on);
            if (on)
            {
                Menu.KeyValue("Rain (game)", GroundWetness.LastRain < 0f ? "no update yet" : (GroundWetness.LastRain * 100f).ToString("0") + "%");
                Menu.KeyValue("Terrain puddles", GroundWetness.MicroSplatState);
            }
            Menu.Chips(2, ("Reset wetness", () =>
            {
                WaterCfg.ResetToDefault(WaterCfg.WetScale, WaterCfg.WetMin, WaterCfg.Puddles, WaterCfg.PuddleMin, WaterCfg.RainRipples);
                Menu.SetStatus("Wetness settings reset");
            }, true), ("Rainy look", () =>
            {
                WaterCfg.WetOn.Value = true;
                WaterCfg.WetMin.Value = 0.6f;
                WaterCfg.PuddleMin.Value = 0.5f;
                Menu.SetStatus("Wet ground and puddles even without rain");
            }, true));
        });
    }

    private static void DrawDirt()
    {
        Menu.Card("Car dirt", "How muddy, snowy and wet cars look on your screen. Only the look changes, for you only.", () =>
        {
            Menu.Switch(WaterCfg.DirtOn, "Adjust car dirt", "Off restores the game's look", true);
            bool on = WaterCfg.DirtOn.Value;
            Menu.SliderRow(WaterCfg.DirtScale, "Dust, mud, sand", 0f, 3f, "", "", on, percent: true);
            Menu.SliderRow(WaterCfg.CarSnow, "Snow on car", 0f, 3f, "", "", on, percent: true);
            Menu.SliderRow(WaterCfg.CarWet, "Wet / raindrops", 0f, 3f, "", "", on, percent: true);
            Menu.Switch(WaterCfg.OnlyMine, "Only my car", "Leave other players' cars as the game shows them", on);
            if (on && CarDirt.CarCount >= 0) Menu.KeyValue("Cars", CarDirt.CarCount.ToString());
            Menu.Chips(3,
                ("Always clean", () =>
                {
                    WaterCfg.DirtOn.Value = true;
                    WaterCfg.DirtScale.Value = 0f;
                    WaterCfg.CarSnow.Value = 0f;
                    WaterCfg.CarWet.Value = 0f;
                    Menu.SetStatus("Cars stay clean (look only)");
                }, true),
                ("Extra muddy", () =>
                {
                    WaterCfg.DirtOn.Value = true;
                    WaterCfg.DirtScale.Value = 2.5f;
                    Menu.SetStatus("Mud look boosted");
                }, true),
                ("Reset dirt", () =>
                {
                    WaterCfg.ResetToDefault(WaterCfg.DirtScale, WaterCfg.CarSnow, WaterCfg.CarWet, WaterCfg.OnlyMine);
                    Menu.SetStatus("Car dirt settings reset");
                }, true));
        });
    }
}
