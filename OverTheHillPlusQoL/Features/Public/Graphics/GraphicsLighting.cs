using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using OverTheHillPlus.UI;
using TND.Upscaling.Framework;
using UnityEngine;
using A = OverTheHillPlus.Features.Public.Gfx.LightingApply;
using S = OverTheHillPlus.Features.Public.Gfx.LightingScene;

namespace OverTheHillPlus.Features.Public.Gfx;

/// <summary>
/// Graphics tab, lighting and image quality: anti-aliasing and the game's TND upscalers (DLSS / FSR / SGSR),
/// render scale, URP upscale filter, MSAA, HBAO ambient occlusion, LSPP god rays and the post-process sharpen.
/// Every option defaults to "Game" (untouched); switching one back hands the value back to the game.
/// Purely local rendering, so it is allowed in any session.
/// </summary>
internal sealed class GraphicsLighting : IFeature
{
    internal const string Section = "Graphics.Lighting";

    internal static ConfigEntry<string> AntiAliasing, UpscaleQuality, UpscaleFilter, AmbientOcclusion, AoQuality, GodRays;
    internal static ConfigEntry<int> AaQuality, Msaa, GodRaySamples;
    internal static ConfigEntry<bool> UpscalerSharpen, RenderScaleOn, AoTune, SharpenOn;
    internal static ConfigEntry<float> UpscalerSharpness, RenderScale, FsrSharpness, AoIntensity, AoRadius, GodRayStrength, GodRayLength, Sharpen;

    public void Register()
    {
        var f = ModConfig.File;
        AntiAliasing = Watch(f.Bind(Section, "AntiAliasing", "Game",
            "Game = use the game's option. Off, FXAA, SMAA, TAA, or an upscaler name your GPU supports (DLSS4, DLSS3, FSR3, FSR2, SGSR2, ...)."));
        AaQuality = Watch(f.Bind(Section, "AntiAliasingQuality", 2, new ConfigDescription("SMAA/TAA quality: 0 low, 1 medium, 2 high.", new AcceptableValueRange<int>(0, 2))));
        UpscaleQuality = Watch(f.Bind(Section, "UpscalerQuality", "Quality",
            "Upscaler quality: NativeAA, UltraQuality, Quality, Balanced, Performance, UltraPerformance."));
        UpscalerSharpen = Watch(f.Bind(Section, "UpscalerSharpenOverride", false, "Set the upscaler's own sharpening (RCAS)."));
        UpscalerSharpness = Watch(f.Bind(Section, "UpscalerSharpness", 0.5f, new ConfigDescription("Upscaler sharpening 0..1 (0 turns it off).", new AcceptableValueRange<float>(0f, 1f))));

        RenderScaleOn = Watch(f.Bind(Section, "RenderScaleOverride", false, "Use a custom URP render scale (ignored while an upscaler is on)."));
        RenderScale = Watch(f.Bind(Section, "RenderScale", 1f, new ConfigDescription("Render scale: below 1 is faster, above 1 supersamples.", new AcceptableValueRange<float>(0.3f, 2f))));
        UpscaleFilter = Watch(f.Bind(Section, "UpscaleFilter", "Game", "URP upscale filter for render scale below 1: Game, Auto, Linear, Point, FSR (FSR 1)."));
        FsrSharpness = Watch(f.Bind(Section, "FsrSharpness", 0.92f, new ConfigDescription("FSR 1 sharpness 0..1.", new AcceptableValueRange<float>(0f, 1f))));
        Msaa = Watch(f.Bind(Section, "Msaa", 0, new ConfigDescription("MSAA samples: 0 = game, 1 = off, 2, 4, 8.", new AcceptableValueList<int>(0, 1, 2, 4, 8))));

        AmbientOcclusion = Watch(f.Bind(Section, "AmbientOcclusion", "Game", "HBAO ambient occlusion: Game, On, Off."));
        AoTune = Watch(f.Bind(Section, "AoCustomStrength", false, "Use the intensity and radius below."));
        AoIntensity = Watch(f.Bind(Section, "AoIntensity", 1f, "HBAO intensity (how dark the contact shadows are)."));
        AoRadius = Watch(f.Bind(Section, "AoRadius", 1.2f, "HBAO radius in metres (how far the shadowing spreads)."));
        AoQuality = Watch(f.Bind(Section, "AoQuality", "Game", "HBAO quality: Game, Lowest, Low, Medium, High, Highest."));

        GodRays = Watch(f.Bind(Section, "GodRays", "Game", "Screen-space light scattering (god rays): Game, Off, Tuned."));
        GodRayStrength = Watch(f.Bind(Section, "GodRayStrength", 1f, "Tuned: multiplies the game's scattering density."));
        GodRayLength = Watch(f.Bind(Section, "GodRayLength", 1f, "Tuned: multiplies how far the rays reach."));
        GodRaySamples = Watch(f.Bind(Section, "GodRaySamples", 0, "Tuned: ray samples (0 = game). More = smoother, slower."));

        SharpenOn = Watch(f.Bind(Section, "SharpenOverride", false, "Set the game's post-process sharpen filter."));
        Sharpen = Watch(f.Bind(Section, "Sharpen", 1f, "Post-process sharpen strength (0 = off)."));

        Registry.Section(Registry.PlusMode, "Graphics", Draw, 10);
        Registry.OnTick(A.Tick);
    }

    private static ConfigEntry<T> Watch<T>(ConfigEntry<T> e)
    {
        e.SettingChanged += OnChanged;
        return e;
    }

    private static void OnChanged(object sender, EventArgs args) => A.Dirty = true;

    // ---------------- menu ----------------

    private static readonly string[] Qualities = { "NativeAA", "UltraQuality", "Quality", "Balanced", "Performance", "UltraPerformance" };
    private static readonly string[] QualityLabels = { "Native AA", "Ultra quality", "Quality", "Balanced", "Performance", "Ultra perf." };

    private static void Draw()
    {
        bool upscaling = !A.IsStatic(AntiAliasing.Value) && AntiAliasing.Value != "Game";
        bool tnd = S.TndActive;

        Menu.Card("Anti-aliasing & upscaling", "Overrides the game's anti-aliasing option. Upscalers render fewer pixels and rebuild a sharp image: more FPS.", () =>
        {
            Menu.KeyValue("Now", A.AaNow);
            var values = new List<string> { "Game", "Off", "FXAA", "SMAA", "TAA" };
            var labels = new List<string> { "Game", "Off", "FXAA", "SMAA", "TAA" };
            foreach (var n in S.Supported)
            {
                if (n == UpscalerName.None) continue;
                values.Add(n.ToString());
                labels.Add(A.UpscalerLabel(n));
            }
            if (!values.Contains(AntiAliasing.Value))
            {
                values.Add(AntiAliasing.Value);
                labels.Add(AntiAliasing.Value + " (n/a)");
            }
            Pick(AntiAliasing, "Mode", values.ToArray(), labels.ToArray(), 5);

            if (S.Supported.Count == 0)
                Menu.Small("Upscalers your GPU supports (DLSS, FSR, SGSR) show up here once you're in a world.");
            else if (upscaling && !A.UpscalerUsable(AntiAliasing.Value))
                Menu.Banner($"{AntiAliasing.Value} isn't available on this PC right now, so the game's setting is kept.", Theme.Warn);

            if (AntiAliasing.Value is "SMAA" or "TAA")
            {
                string cur = AaQuality.Value.ToString();
                string picked = Picker("Quality", cur, new[] { "0", "1", "2" }, new[] { "Low", "Medium", "High" }, 3, true);
                if (picked != cur && int.TryParse(picked, out int q)) AaQuality.Value = q;
            }
            if (upscaling)
            {
                Pick(UpscaleQuality, "Upscaler quality", Qualities, QualityLabels, 3);
                Menu.Small("Native AA = full resolution with the upscaler's anti-aliasing (best image). Performance modes render at half resolution or less.");
                Menu.Switch(UpscalerSharpen, "Upscaler sharpening", "The upscaler's own sharpen pass (RCAS)", true);
                if (UpscalerSharpen.Value)
                    Menu.SliderRow(UpscalerSharpness, "Sharpness", 0f, 1f, "0.00", "", true, percent: true);
            }
            if (AntiAliasing.Value != "Game")
                Menu.Small("While this is on, the game's own anti-aliasing option is ignored.");
        });

        Menu.Card("Render resolution", "Render scale, the upscale filter and MSAA, straight on the render pipeline.", () =>
        {
            Menu.KeyValue("Now", A.ResNow);
            Menu.Switch(RenderScaleOn, "Custom render scale", tnd ? "Not used while an upscaler is on: it picks the resolution itself" : "Below 100% = faster and softer, above = supersampling (sharp, slow)", !tnd);
            if (RenderScaleOn.Value)
                Menu.SliderRow(RenderScale, "Render scale", 0.3f, 2f, "0.00", "", !tnd, percent: true);
            Pick(UpscaleFilter, "Upscale filter", new[] { "Game", "Auto", "Linear", "Point", "FSR" }, new[] { "Game", "Auto", "Bilinear", "Point", "FSR 1" }, 5);
            if (UpscaleFilter.Value == "FSR")
                Menu.SliderRow(FsrSharpness, "FSR sharpness", 0f, 1f, "0.00", "", true, percent: true);
            string msaa = Msaa.Value.ToString();
            if (tnd && Msaa.Value > 0) Menu.Small("MSAA is left to the game while an upscaler is on.");
            string pm = Picker("MSAA", msaa, new[] { "0", "1", "2", "4", "8" }, new[] { "Game", "Off", "2x", "4x", "8x" }, 5, true);
            if (pm != msaa && int.TryParse(pm, out int m)) Msaa.Value = m;
        });

        Menu.Card("Ambient occlusion (HBAO)", "Soft contact shadows where surfaces meet: under cars, rocks, trees.", () =>
        {
            Menu.KeyValue("Now", A.AoNow);
            Pick(AmbientOcclusion, "HBAO", new[] { "Game", "On", "Off" }, new[] { "Game", "On", "Off" }, 3);
            Menu.Switch(AoTune, "Custom strength", "Intensity and radius below instead of the game's", true);
            if (AoTune.Value)
            {
                Menu.SliderRow(AoIntensity, "Intensity", 0f, A.AoIntensityMax, "0.00", "", true);
                Menu.SliderRow(AoRadius, "Radius", A.AoRadiusMin, A.AoRadiusMax, "0.00", " m", true);
            }
            Pick(AoQuality, "Quality", new[] { "Game", "Lowest", "Low", "Medium", "High", "Highest" }, new[] { "Game", "Lowest", "Low", "Medium", "High", "Highest" }, 3);
        });

        Menu.Card("God rays (light scattering)", "Screen-space light shafts from the sun through trees, dust and fog.", () =>
        {
            Menu.KeyValue("Now", A.GodRaysNow);
            Pick(GodRays, "God rays", new[] { "Game", "Off", "Tuned" }, new[] { "Game", "Off", "Tuned" }, 3);
            if (GodRays.Value == "Tuned")
            {
                Menu.SliderRow(GodRayStrength, "Strength", 0f, 4f, "0.00", "x", true);
                Menu.SliderRow(GodRayLength, "Ray length", 0.25f, 3f, "0.00", "x", true);
                float s = Menu.RawSlider("Samples", GodRaySamples.Value, 0f, A.SamplesMax,
                    GodRaySamples.Value <= 0 ? "game" : GodRaySamples.Value.ToString(), true, keyStep: 1f);
                int si = s < 4f ? 0 : Mathf.RoundToInt(s);
                if (GodRaySamples.Value <= 0 && s > 0f && s < 4f) si = 4; // a key step up from "game"
                if (si != GodRaySamples.Value) GodRaySamples.Value = si;
                if (A.GodRaysZero)
                    Menu.Small("The game has scattering at zero right now (time of day / weather), so strength has nothing to multiply.");
            }
        });

        Menu.Card("Sharpening", "The game's post-process sharpen. Pairs well with TAA or upscalers, which soften the image.", () =>
        {
            Menu.KeyValue("Now", A.SharpenNow);
            Menu.Switch(SharpenOn, "Custom sharpening", null, true);
            if (SharpenOn.Value)
                Menu.SliderRow(Sharpen, "Strength", 0f, A.SharpenMax, "0.00", "", true);
        });

        Menu.Card("Lighting & quality overrides", null, () =>
        {
            Menu.Chips(2,
                ("Hand everything back to the game", ResetAll, true),
                ("Reload game graphics settings", ReloadGameSettings, true));
            Menu.Small("Reload re-applies your saved in-game graphics options (the screen may flicker once). Overrides above stay on top of them.");
        });
    }

    private static void ResetAll()
    {
        foreach (var e in new ConfigEntryBase[]
                 {
                     AntiAliasing, AaQuality, UpscaleQuality, UpscalerSharpen, UpscalerSharpness, RenderScaleOn, RenderScale,
                     UpscaleFilter, FsrSharpness, Msaa, AmbientOcclusion, AoTune, AoIntensity, AoRadius, AoQuality,
                     GodRays, GodRayStrength, GodRayLength, GodRaySamples, SharpenOn, Sharpen,
                 })
            e.BoxedValue = e.DefaultValue;
        try { A.ApplyAll(); } catch { }
        Menu.SetStatus("Graphics lighting & quality handed back to the game");
    }

    private static void ReloadGameSettings()
    {
        try
        {
            GraphicsSettingsInitializer.LoadUserGraphicSettings();
            A.Dirty = true;
            Menu.SetStatus("Re-applied the game's graphics settings");
        }
        catch (Exception e)
        {
            Menu.SetStatus("Failed to reload graphics settings: " + e.Message);
        }
    }

    // ---------------- segmented picker ----------------

    private static GUIStyle _selected;
    private static GUIStyle _selectedFor;

    private static void Pick(ConfigEntry<string> entry, string label, string[] values, string[] labels, int perRow)
    {
        string v = Picker(label, entry.Value, values, labels, perRow, true);
        if (v != entry.Value) entry.Value = v;
    }

    /// <summary>A row of chips with the current value highlighted. Label sits left when it fits, else above.</summary>
    private static string Picker(string label, string current, string[] values, string[] labels, int perRow, bool enabled)
    {
        if (_selected == null || _selectedFor != Theme.Label)
        {
            _selectedFor = Theme.Label;
            _selected = new GUIStyle(Theme.Label);
            _selected.alignment = TextAnchor.MiddleCenter;
            _selected.fontStyle = FontStyle.Bold;
            _selected.normal.textColor = Theme.Hex(0x0E1116);
        }

        const float labelW = 150f, gap = 6f, h = 28f;
        int n = values.Length;
        bool inline = n <= perRow && n <= 5;
        string result = current;

        var first = Menu.NextRow(h);
        float x0 = first.x, width = first.width;
        if (inline)
        {
            GUI.Label(new Rect(first.x + 4f, first.y, labelW - 8f, h), label, Theme.Label);
            x0 = first.x + labelW;
            width = first.width - labelW;
        }
        else
        {
            GUI.Label(new Rect(first.x + 4f, first.y, first.width, h), label, Theme.Label);
        }

        int cols = Math.Min(n, perRow);
        float w = (width - gap * (cols - 1)) / cols;
        Rect row = first;
        for (int i = 0; i < n; i++)
        {
            int col = i % perRow;
            if (!inline && col == 0) row = Menu.NextRow(h);
            var r = new Rect(x0 + col * (w + gap), row.y, w, h);
            if (values[i] == current)
            {
                Theme.Fill(r, enabled ? Theme.Accent : Theme.AccentDim, 6);
                if (GUI.Button(r, labels[i], _selected) && enabled) result = values[i];
            }
            else if (Menu.ChipButton(r, labels[i], enabled)) result = values[i];
        }
        return result;
    }
}
