using System;
using BepInEx.Configuration;
using OverTheHillPlus.UI;
using UnityEngine;
using OverTheHillPlus.Graphics;

namespace OverTheHillPlus.Features.Public.GraphicsSky;

/// <summary>
/// Graphics tab: sky and clouds through COZY (cloud cover, style, thickness, sky / horizon / cloud colours, sun and
/// moon size, stars, milky way, aurora light columns, experimental cloud shadows) and a few one-click looks.
/// Purely local visuals: nothing is networked and the game's own sky state is never changed.
/// </summary>
internal sealed class SkyFeature : IFeature
{
    private static readonly Color[] Palette =
    {
        Theme.Hex(0x2C6BE0), Theme.Hex(0x5FA8F0), Theme.Hex(0x9FD0F5), Theme.Hex(0x7A5CD6), Theme.Hex(0xE07AA8),
        Theme.Hex(0xFFB45E), Theme.Hex(0xE5683C), Theme.Hex(0x7FD69A), Theme.Hex(0x7D8792), Theme.Hex(0xFFFFFF),
    };

    public void Register()
    {
        SkySettings.Bind(GraphicsConfig.File);
        Registry.OnTick(SkyApplier.Tick);
        Registry.OnTick(CloudShadows.Tick);
        Registry.Section(Registry.PlusMode, "Graphics", Draw, 20);
    }

    private static void Draw()
    {
        bool on = SkySettings.Enabled.Value;

        Menu.Card("Sky & clouds", "COZY sky overrides. Only you see this; turning it off puts the game's sky straight back.", () =>
        {
            Menu.Switch(SkySettings.Enabled, "Custom sky", "Apply the settings below on top of the live weather and time of day", true);
            Menu.KeyValue("Sky system", SkyStatus());
            if (!string.IsNullOrEmpty(SkyApplier.LastError)) Menu.Small($"<color=#E5534B>{SkyApplier.LastError}</color>");

            Menu.Small("Looks");
            var looks = SkySettings.Looks;
            Menu.Chips(5,
                (looks[0].Name, () => Look(looks[0]), true),
                (looks[1].Name, () => Look(looks[1]), true),
                (looks[2].Name, () => Look(looks[2]), true),
                (looks[3].Name, () => Look(looks[3]), true),
                ("Game sky", () => { SkySettings.ResetToGame(); Menu.SetStatus("Sky reset to the game's own"); }, true));
        });

        Menu.Card("Sky: clouds", "Multipliers on what the weather asks for, so storms still roll in and clear days stay clear-ish.", () =>
        {
            Menu.SliderRow(SkySettings.CloudAmount, "Cloud cover", 0f, 2.5f, "0.00", "x", on);
            Menu.SliderRow(SkySettings.CloudBoost, "Extra clouds", 0f, 1.5f, "0.00", "", on);
            Menu.SliderRow(SkySettings.HighClouds, "High clouds", 0f, 2.5f, "0.00", "x", on);
            Menu.SliderRow(SkySettings.CloudThickness, "Thickness", 0.2f, 3f, "0.00", "x", on);

            Menu.Small($"Cloud style (experimental, swaps COZY's cloud material). Current: {SkyApplier.CurrentStyleName()}");
            var styles = SkySettings.CloudStyles;
            var items = new (string, Action, bool)[styles.Length];
            for (int i = 0; i < styles.Length; i++)
            {
                string s = styles[i];
                bool sel = string.Equals(SkySettings.CloudStyle.Value, s, StringComparison.Ordinal);
                items[i] = (sel ? "> " + s : s, () => { SkySettings.CloudStyle.Value = s; Menu.SetStatus($"Cloud style: {s}"); }, on);
            }
            Menu.Chips(4, items);
        });

        Menu.Card("Sky: colours", "Tints keep the brightness of the time of day (nights stay dark); only the hue changes.", () =>
        {
            Menu.SliderRow(SkySettings.SkyBrightness, "Sky brightness", 0.4f, 1.6f, "0.00", "x", on);
            Menu.SliderRow(SkySettings.HorizonSoftness, "Horizon blend", 0.25f, 3f, "0.00", "x", on);
            ColorEditor("Sky", SkySettings.SkyColor, SkySettings.SkyTint, on);
            ColorEditor("Horizon", SkySettings.HorizonColor, SkySettings.HorizonTint, on);
            ColorEditor("Clouds", SkySettings.CloudColor, SkySettings.CloudTint, on);
        });

        Menu.Card("Sky: sun, moon & stars", null, () =>
        {
            Menu.SliderRow(SkySettings.SunSize, "Sun size", 0.25f, 4f, "0.00", "x", on);
            Menu.SliderRow(SkySettings.MoonSize, "Moon size", 0.25f, 4f, "0.00", "x", on);
            Menu.SliderRow(SkySettings.Stars, "Stars", 0f, 4f, "0.00", "x", on);
            Menu.SliderRow(SkySettings.MilkyWay, "Milky way", 0f, 4f, "0.00", "x", on);
            Menu.SliderRow(SkySettings.Aurora, "Aurora (light columns)", 0f, 4f, "0.00", "x", on);
            Menu.SliderRow(SkySettings.AuroraBoost, "Aurora boost", 0f, 2f, "0.00", "", on);
            if (on && SkySettings.MoonSize.Value != 1f)
                Menu.Small(SkyApplier.MoonCount > 0 ? $"Scaling {SkyApplier.MoonCount} moon object(s)." : "Looking for the moon...");
        });

        Menu.Card("Sky: cloud shadows", "Experimental: drifting shadows from a texture on the sun light. Surfaces whose shader ignores URP light cookies (possibly the terrain) will not show them.", () =>
        {
            Menu.Switch(SkySettings.CloudShadows, "Cloud shadows", null, on);
            bool sh = on && SkySettings.CloudShadows.Value;
            Menu.SliderRow(SkySettings.ShadowStrength, "Darkness", 0f, 1f, "0.00", "", sh, percent: true);
            Menu.SliderRow(SkySettings.ShadowCoverage, "Coverage", 0f, 1f, "0.00", "", sh, percent: true);
            Menu.SliderRow(SkySettings.ShadowScale, "Pattern size", 200f, 3000f, "0", " m", sh);
            Menu.SliderRow(SkySettings.ShadowSpeed, "Drift speed", 0f, 30f, "0.0", " m/s", sh);
            Menu.SliderRow(SkySettings.ShadowDirection, "Drift direction", 0f, 360f, "0", " deg", sh);
            Menu.KeyValue("Status", CloudShadows.Status);
            Menu.KeyValue("Renderer cookies", CloudShadows.RendererSupport());
        });
    }

    private static void Look(SkySettings.Look l)
    {
        SkySettings.ApplyLook(l);
        Menu.SetStatus($"Sky look: {l.Name}");
    }

    private static string SkyStatus()
    {
        if (!SkySettings.Enabled.Value) return "off";
        if (!SkyApplier.Hooked) return "starting...";
        if (!SkyApplier.Alive(SkyApplier.Weather)) return "waiting for COZY (loads with the world)";
        return SkyApplier.SeenRecently ? "COZY active, overrides applied" : "COZY found (idle)";
    }

    /// <summary>One tint colour: "◂ colour ▸" over the palette with a #RRGGBB box, the hue and the strength.</summary>
    private static void ColorEditor(string label, ConfigEntry<string> hex, ConfigEntry<float> strength, bool enabled)
    {
        var c = SkySettings.ParseHex(hex.Value, Color.white);
        SimpleMenu.ColorChoice(label + " tint", $"The colour the {label.ToLowerInvariant()} is tinted towards.", c, Palette, null, picked =>
        {
            hex.Value = SkySettings.ToHex(picked);
            if (strength.Value <= 0f) strength.Value = 0.5f;
        }, enabled);

        Color.RGBToHSV(c, out float h, out float s, out float v);
        float nh = Menu.RawSlider("   Hue", h, 0f, 1f, Mathf.RoundToInt(h * 360f) + " deg", enabled && s > 0.02f);
        if (Mathf.Abs(nh - h) > 0.002f && s > 0.02f) hex.Value = SkySettings.ToHex(Color.HSVToRGB(nh, s, v));
        Menu.SliderRow(strength, "   Strength", 0f, 1f, "0.00", "", enabled, percent: true);
    }
}
