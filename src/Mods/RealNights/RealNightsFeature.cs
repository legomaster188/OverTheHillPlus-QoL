using System;
using OverTheHillPlus.Features;
using UnityEngine;
using static OverTheHillPlus.Menu;

namespace OverTheHillPlus.RealNights;

/// <summary>Real Nights' per-frame work (the lamp lights and the headlight boost) and its advanced cards.</summary>
internal sealed class RealNightsFeature : IFeature
{
    public void Register()
    {
        Registry.OnTick(Tick);
        Registry.Section(Registry.PlusMode, "Real Nights", DrawAdvanced);
    }

    // Timed and error-guarded by core (a feature hook); the sky itself runs in COZY's own update (SkyPatch).
    private static void Tick()
    {
        LampLights.Tick(Time.deltaTime);
        HeadlightBoost.Tick();
    }

    private static void DrawAdvanced()
    {
        Card("Night", "The sky and the light are changed only while you look at them; the game's time and weather run as before.", () =>
        {
            Switch(RealNightsConfig.DarkNights, "Darker nights", "Off: the game's own night light", true);
            SliderRow(RealNightsConfig.Darkness, "Darkness", 0f, 1f, "0.00", "", true);
            SliderRow(RealNightsConfig.StormDarkness, "Storm darkness", 0f, 1f, "0.00", "", true);
            SliderRow(RealNightsConfig.TwilightKeep, "Keep dusk and dawn", 0f, 1f, "0.00", "", true);
            Small("Keep dusk and dawn: higher = the game's sunset colours last longer before the dark arrives.");
        });
        Card("Sky", "On clear nights only. With Graphics+ on, these replace its star settings at night; it keeps the day.", () =>
        {
            Switch(RealNightsConfig.Stars, "Starry sky", "Off: the game's own stars", true);
            SliderRow(RealNightsConfig.StarBrightness, "Star brightness", 0f, 4f, "0.0", "x", true);
            SliderRow(RealNightsConfig.MilkyWay, "Milky Way", 0f, 4f, "0.0", "x", true);
        });
        Card("Lamps", "Real lights for the lamps on cars, from a small pool (nearest cars first).", () =>
        {
            Switch(RealNightsConfig.Lamps, "Lamps light the ground", null, true);
            SliderRow(RealNightsConfig.LampIntensity, "Brightness", 0f, 3f, "0.00", "x", true);
            SliderRow(RealNightsConfig.LampRange, "Range", 0.3f, 3f, "0.00", "x", true);
            Switch(RealNightsConfig.LampShadows, "Shadows from my front lamp", "Other cars' lamps never cast shadows", true);
            Slider("Most lights at once", RealNightsConfig.MaxLights, 0, 16, 1);
            Slider("Most lights per car", RealNightsConfig.LightsPerCar, 1, 6, 1);
            SliderRow(RealNightsConfig.CullDistance, "Nearest-car distance", 20f, 300f, "0", " m", true);
            SliderRow(RealNightsConfig.HeadlightBoost, "Headlight boost", 0.5f, 3f, "0.00", "x", true);
            KeyValue("Now", LampLights.Status);
            KeyValue("Headlights boosted", HeadlightBoost.Count.ToString());
        });
        Card("Status", null, () =>
        {
            KeyValue("Sky hook", NightSky.Hooked ? "on" : "waiting for a world");
            KeyValue("Time", $"{NightSky.HourNow:0.0} h (night {Math.Round(NightSky.NightNow * 100)}%)");
            KeyValue("Storm / clear sky", $"{Math.Round(NightSky.StormNow * 100)}% / {Math.Round(NightSky.ClearNow * 100)}%");
            KeyValue("Last problem", string.IsNullOrEmpty(NightSky.LastError) ? "none" : NightSky.LastError);
        });
    }

    private static void Slider(string label, BepInEx.Configuration.ConfigEntry<int> entry, int min, int max, int step)
    {
        float v = RawSlider(label, entry.Value, min, max, entry.Value.ToString(), true);
        int rounded = Mathf.Clamp(Mathf.RoundToInt(v / step) * step, min, max);
        if (rounded != entry.Value) entry.Value = rounded;
    }
}
