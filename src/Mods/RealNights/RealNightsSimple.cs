using System;
using OverTheHillPlus.UI;

namespace OverTheHillPlus.RealNights;

/// <summary>Real Nights' everyday settings, first on its MODS page (the rest is in its Advanced fold).</summary>
internal static class RealNightsSimple
{
    private static readonly float[] DarkSteps = { 0f, 0.25f, 0.4f, 0.5f, 0.6f, 0.75f, 0.9f, 1f };
    private static readonly float[] StarSteps = { 0.5f, 1f, 1.5f, 2f, 3f };
    private static readonly float[] MilkySteps = { 0f, 0.6f, 1.2f, 2f, 3f };
    private static readonly float[] LampSteps = { 0.5f, 0.75f, 1f, 1.5f, 2f, 3f };
    private static readonly int[] LightSteps = { 0, 4, 8, 12, 16 };
    private static string[] _presets;

    internal static void Draw()
    {
        // Not bound when the mod failed to start: nothing to show then.
        if (RealNightsConfig.File == null) return;
        _presets ??= NightPresets.Names;

        SimpleMenu.Heading("Night");
        int preset = NightPresets.Find(RealNightsConfig.DarkNights.Value, RealNightsConfig.Darkness.Value, RealNightsConfig.StormDarkness.Value,
            RealNightsConfig.TwilightKeep.Value, RealNightsConfig.StarBrightness.Value, RealNightsConfig.MilkyWay.Value, RealNightsConfig.HeadlightBoost.Value);
        SimpleMenu.Choice("Look", "Game = the game's night. Playable = dark but easy to drive. Real = moonlight only. Pitch black = you need your lights.",
            preset, _presets, i => Apply(NightPresets.All[i]));
        SimpleMenu.FloatChoice("Darkness", "How dark the night gets. 0 = the game's night, 1 = almost black.",
            RealNightsConfig.Darkness.Value, DarkSteps, v => v <= 0f ? "Game" : Math.Round(v * 100) + "%", v =>
            {
                RealNightsConfig.Darkness.Value = v;
                RealNightsConfig.DarkNights.Value = v > 0f;
            }, RealNightsConfig.DarkNights.Value || RealNightsConfig.Darkness.Value > 0f);
        SimpleMenu.FloatChoice("Storm darkness", "How much storms darken the sky: at night they go pitch black, by day they only dim.",
            RealNightsConfig.StormDarkness.Value, DarkSteps, v => v <= 0f ? "Game" : Math.Round(v * 100) + "%", v => RealNightsConfig.StormDarkness.Value = v,
            RealNightsConfig.DarkNights.Value);

        SimpleMenu.Heading("Sky");
        SimpleMenu.Toggle(RealNightsConfig.Stars, "Starry sky", "Stars and the Milky Way on clear nights (clouds and storms hide them).");
        SimpleMenu.FloatChoice("Star brightness", "How bright the stars are on a clear night.",
            RealNightsConfig.StarBrightness.Value, StarSteps, v => v.ToString("0.0") + "x", v => RealNightsConfig.StarBrightness.Value = v,
            RealNightsConfig.Stars.Value);
        SimpleMenu.FloatChoice("Milky Way", "How bright the Milky Way is on a clear night. Off = none.",
            RealNightsConfig.MilkyWay.Value, MilkySteps, v => v <= 0f ? "Off" : v.ToString("0.0") + "x", v => RealNightsConfig.MilkyWay.Value = v,
            RealNightsConfig.Stars.Value);

        SimpleMenu.Heading("Lights");
        SimpleMenu.Toggle(RealNightsConfig.Lamps, "Lamps light the ground", "Roof light bars, bull-bar lamps and the custom light bars shine on the terrain at night while your headlights are on.");
        SimpleMenu.FloatChoice("Lamp brightness", "Compared with the car's own headlights.",
            RealNightsConfig.LampIntensity.Value, LampSteps, v => v.ToString("0.00") + "x", v => RealNightsConfig.LampIntensity.Value = v,
            RealNightsConfig.Lamps.Value);
        SimpleMenu.FloatChoice("Lamp range", "Compared with the car's own headlights.",
            RealNightsConfig.LampRange.Value, LampSteps, v => v.ToString("0.00") + "x", v => RealNightsConfig.LampRange.Value = v,
            RealNightsConfig.Lamps.Value);
        SimpleMenu.IntChoice("Most lamp lights", "The most lamp lights in the world at once; the nearest cars get them first. Fewer is faster.",
            RealNightsConfig.MaxLights.Value, LightSteps, v => v == 0 ? "None" : v.ToString(), v => RealNightsConfig.MaxLights.Value = v,
            RealNightsConfig.Lamps.Value);
        SimpleMenu.Toggle(RealNightsConfig.LampShadows, "Shadows from my front lamp", "Your own car's front lamp casts shadows (a little slower).", RealNightsConfig.Lamps.Value);
        SimpleMenu.FloatChoice("Headlight boost", "Headlights shine brighter and farther at night. 1.00x = the game's.",
            RealNightsConfig.HeadlightBoost.Value, LampSteps, v => v.ToString("0.00") + "x", v => RealNightsConfig.HeadlightBoost.Value = v);

        SimpleMenu.Note(Status());
    }

    private static void Apply(NightPresets.Preset p)
    {
        RealNightsConfig.DarkNights.Value = p.DarkNights;
        RealNightsConfig.Darkness.Value = p.Darkness;
        RealNightsConfig.StormDarkness.Value = p.StormDarkness;
        RealNightsConfig.TwilightKeep.Value = p.TwilightKeep;
        RealNightsConfig.StarBrightness.Value = p.StarBrightness;
        RealNightsConfig.MilkyWay.Value = p.MilkyWay;
        RealNightsConfig.HeadlightBoost.Value = p.HeadlightBoost;
    }

    /// <summary>One line of what is happening now, for the page (and for reporting a problem).</summary>
    internal static string Status()
    {
        if (!NightSky.Hooked) return "Status: waiting for the sky (load into a world).";
        var l = NightSky.Look;
        return $"Status: {NightSky.HourNow:0.0} h, night {Pct(NightSky.NightNow)}, storm {Pct(NightSky.StormNow)}, clear sky {Pct(NightSky.ClearNow)}; " +
               $"ambient x{l.Ambient:0.00}, stars {Pct(l.Stars)}; lamps: {LampLights.Status}.";
    }

    private static string Pct(float v) => Math.Round(v * 100) + "%";
}
