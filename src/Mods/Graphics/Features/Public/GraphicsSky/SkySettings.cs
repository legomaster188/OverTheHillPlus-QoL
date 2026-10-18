using System;
using BepInEx.Configuration;
using UnityEngine;
using OverTheHillPlus.Graphics;

namespace OverTheHillPlus.Features.Public.GraphicsSky;

/// <summary>Config entries for the sky and cloud overrides (section "Graphics.Sky") plus the built-in looks.</summary>
internal static class SkySettings
{
    public const string Section = "Graphics.Sky";

    public static readonly string[] CloudStyles = { "Game", "Cozy", "Soft", "Painted", "Luxury", "Ghibli", "Single texture" };

    public static ConfigEntry<bool> Enabled;
    public static ConfigEntry<float> CloudAmount, CloudBoost, HighClouds, CloudThickness;
    public static ConfigEntry<string> CloudStyle;
    public static ConfigEntry<float> SkyBrightness, HorizonSoftness;
    public static ConfigEntry<string> SkyColor, HorizonColor, CloudColor;
    public static ConfigEntry<float> SkyTint, HorizonTint, CloudTint;
    public static ConfigEntry<float> SunSize, MoonSize, Stars, MilkyWay, Aurora, AuroraBoost;
    public static ConfigEntry<bool> CloudShadows;
    public static ConfigEntry<float> ShadowStrength, ShadowCoverage, ShadowScale, ShadowSpeed, ShadowDirection;

    /// <summary>Set whenever any entry in this section changes; the applier pushes the sky right away.</summary>
    public static volatile bool Dirty = true;

    public static void Bind(ConfigFile f)
    {
        Enabled = f.Bind(Section, "Enabled", false, "Master switch for the sky and cloud overrides (local visuals only; off restores the game's sky).");

        CloudAmount = f.Bind(Section, "CloudAmount", 1f, "Multiplier on the main (cumulus and nimbus) cloud cover from the weather. 1 = game.");
        CloudBoost = f.Bind(Section, "CloudBoost", 0f, "Extra cumulus cover added on top of the weather, so clear days can still have clouds. 0 = game.");
        HighClouds = f.Bind(Section, "HighClouds", 1f, "Multiplier on high-altitude clouds (cirrus, cirrostratus, altocumulus, chemtrails). 1 = game.");
        CloudThickness = f.Bind(Section, "CloudThickness", 1f, "Multiplier on cloud thickness / density. 1 = game.");
        CloudStyle = f.Bind(Section, "CloudStyle", "Game", new ConfigDescription(
            "COZY cloud renderer style (experimental: swaps the cloud material). Game = leave as the game set it.",
            new AcceptableValueList<string>(CloudStyles)));

        SkyBrightness = f.Bind(Section, "SkyBrightness", 1f, "Brightness multiplier on sky and horizon colours. 1 = game.");
        HorizonSoftness = f.Bind(Section, "HorizonSoftness", 1f, "Multiplier on the sky gradient exponent (how fast the horizon colour fades into the sky colour). 1 = game.");
        SkyColor = f.Bind(Section, "SkyColor", "#2F6FD6", "Tint colour for the top of the sky (hex). Brightness follows time of day; only the hue is taken.");
        SkyTint = f.Bind(Section, "SkyTint", 0f, "How strongly SkyColor tints the sky (0 = game colours).");
        HorizonColor = f.Bind(Section, "HorizonColor", "#A9CFF2", "Tint colour for the horizon (hex).");
        HorizonTint = f.Bind(Section, "HorizonTint", 0f, "How strongly HorizonColor tints the horizon (0 = game colours).");
        CloudColor = f.Bind(Section, "CloudColor", "#FFFFFF", "Tint colour for the clouds (hex).");
        CloudTint = f.Bind(Section, "CloudTint", 0f, "How strongly CloudColor tints the clouds (0 = game colours).");

        SunSize = f.Bind(Section, "SunSize", 1f, "Sun disc size multiplier. 1 = game.");
        MoonSize = f.Bind(Section, "MoonSize", 1f, "Moon size multiplier (scales the moon object). 1 = game.");
        Stars = f.Bind(Section, "Stars", 1f, "Star brightness multiplier. 1 = game.");
        MilkyWay = f.Bind(Section, "MilkyWay", 1f, "Galaxy / milky way intensity multiplier. 1 = game.");
        Aurora = f.Bind(Section, "Aurora", 1f, "Multiplier on COZY's light columns (the aurora-like glow). 1 = game.");
        AuroraBoost = f.Bind(Section, "AuroraBoost", 0f, "Adds a green aurora glow to the light columns even when the weather has none. 0 = game.");

        CloudShadows = f.Bind(Section, "CloudShadows", false, "Experimental: moving cloud shadows from a light cookie on the sun (only shaders that support URP light cookies show them).");
        ShadowStrength = f.Bind(Section, "ShadowStrength", 0.45f, "How dark the cloud shadows are (0..1).");
        ShadowCoverage = f.Bind(Section, "ShadowCoverage", 0.5f, "How much of the ground is shaded (0..1).");
        ShadowScale = f.Bind(Section, "ShadowScale", 900f, "Size of the shadow pattern in metres.");
        ShadowSpeed = f.Bind(Section, "ShadowSpeed", 4f, "How fast the shadows drift (m/s).");
        ShadowDirection = f.Bind(Section, "ShadowDirection", 45f, "Drift direction in degrees.");

        f.SettingChanged += (object sender, SettingChangedEventArgs e) =>
        {
            try { if (e?.ChangedSetting?.Definition?.Section == Section) Dirty = true; }
            catch (Exception ex) { DebugLog.Error("graphics", ex, "SkySettings.Bind"); /* never let a config event throw */ }
        };
    }

    // ---- looks ----

    internal sealed class Look
    {
        public string Name;
        public float Amount, Boost, High, Thick, Bright, Soft, SkyT, HorT, CloudT, Sun, Moon, StarsV, Milky, Aur, AurB, ShStr, ShCov;
        public string Sky, Hor, Cloud;
    }

    public static readonly Look[] Looks =
    {
        new() { Name = "Clear blue", Amount = 0.15f, Boost = 0f, High = 0.35f, Thick = 0.9f, Bright = 1.05f, Soft = 1.1f,
                Sky = "#2C6BE0", SkyT = 0.6f, Hor = "#9FD0F5", HorT = 0.5f, Cloud = "#FFFFFF", CloudT = 0.2f,
                Sun = 1.1f, Moon = 1.1f, StarsV = 1.3f, Milky = 1.3f, Aur = 1f, AurB = 0f, ShStr = 0.25f, ShCov = 0.2f },
        new() { Name = "Dramatic", Amount = 1.35f, Boost = 0.35f, High = 1.5f, Thick = 1.6f, Bright = 0.95f, Soft = 0.7f,
                Sky = "#1D3B78", SkyT = 0.45f, Hor = "#E3A06C", HorT = 0.35f, Cloud = "#5C6475", CloudT = 0.35f,
                Sun = 1.6f, Moon = 1.8f, StarsV = 1.6f, Milky = 1.9f, Aur = 1.5f, AurB = 0.3f, ShStr = 0.6f, ShCov = 0.55f },
        new() { Name = "Golden hour", Amount = 0.8f, Boost = 0.1f, High = 1.2f, Thick = 1.1f, Bright = 1.05f, Soft = 1.4f,
                Sky = "#E59A5C", SkyT = 0.45f, Hor = "#FFB45E", HorT = 0.7f, Cloud = "#FFB27A", CloudT = 0.5f,
                Sun = 1.8f, Moon = 1.2f, StarsV = 1f, Milky = 1f, Aur = 1f, AurB = 0f, ShStr = 0.4f, ShCov = 0.4f },
        new() { Name = "Moody", Amount = 1.6f, Boost = 0.6f, High = 1.3f, Thick = 1.8f, Bright = 0.8f, Soft = 0.8f,
                Sky = "#5A6573", SkyT = 0.7f, Hor = "#7D8792", HorT = 0.7f, Cloud = "#4B515C", CloudT = 0.6f,
                Sun = 0.8f, Moon = 1f, StarsV = 0.6f, Milky = 0.6f, Aur = 0.5f, AurB = 0f, ShStr = 0.7f, ShCov = 0.75f },
    };

    /// <summary>Runs a batch of setting writes with one config save at the end instead of one per entry.</summary>
    private static void Batch(Action body)
    {
        var f = Enabled.ConfigFile;
        bool prev = f.SaveOnConfigSet;
        f.SaveOnConfigSet = false;
        try { body(); }
        finally
        {
            f.SaveOnConfigSet = prev;
            try { f.Save(); } catch (Exception e) { DebugLog.Error("graphics", e, "SkySettings.Batch"); GraphicsPlugin.Logger.LogWarning($"Graphics.Sky: config save failed: {e.Message}"); }
        }
        Dirty = true;
    }

    public static void ApplyLook(Look l) => Batch(() =>
    {
        CloudAmount.Value = l.Amount; CloudBoost.Value = l.Boost; HighClouds.Value = l.High; CloudThickness.Value = l.Thick;
        SkyBrightness.Value = l.Bright; HorizonSoftness.Value = l.Soft;
        SkyColor.Value = l.Sky; SkyTint.Value = l.SkyT;
        HorizonColor.Value = l.Hor; HorizonTint.Value = l.HorT;
        CloudColor.Value = l.Cloud; CloudTint.Value = l.CloudT;
        SunSize.Value = l.Sun; MoonSize.Value = l.Moon;
        Stars.Value = l.StarsV; MilkyWay.Value = l.Milky; Aurora.Value = l.Aur; AuroraBoost.Value = l.AurB;
        ShadowStrength.Value = l.ShStr; ShadowCoverage.Value = l.ShCov;
        Enabled.Value = true;
    });

    /// <summary>Puts every override back to neutral (the game's own sky). Leaves the master switch alone.</summary>
    public static void ResetToGame() => Batch(() =>
    {
        CloudAmount.Value = 1f; CloudBoost.Value = 0f; HighClouds.Value = 1f; CloudThickness.Value = 1f;
        CloudStyle.Value = "Game";
        SkyBrightness.Value = 1f; HorizonSoftness.Value = 1f;
        SkyTint.Value = 0f; HorizonTint.Value = 0f; CloudTint.Value = 0f;
        SunSize.Value = 1f; MoonSize.Value = 1f; Stars.Value = 1f; MilkyWay.Value = 1f; Aurora.Value = 1f; AuroraBoost.Value = 0f;
        CloudShadows.Value = false;
    });

    // ---- colour helpers ----

    public static Color ParseHex(string s, Color fallback)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(s)) return fallback;
            s = s.Trim().TrimStart('#');
            if (s.Length != 6 && s.Length != 8) return fallback;
            uint v = Convert.ToUInt32(s, 16);
            if (s.Length == 6) v = (v << 8) | 0xFF;
            return new Color(((v >> 24) & 0xFF) / 255f, ((v >> 16) & 0xFF) / 255f, ((v >> 8) & 0xFF) / 255f, (v & 0xFF) / 255f);
        }
        catch (Exception ex) { DebugLog.Error("graphics", ex, "SkySettings.ParseHex"); return fallback; }
    }

    public static string ToHex(Color c)
    {
        int r = Mathf.Clamp(Mathf.RoundToInt(c.r * 255f), 0, 255);
        int g = Mathf.Clamp(Mathf.RoundToInt(c.g * 255f), 0, 255);
        int b = Mathf.Clamp(Mathf.RoundToInt(c.b * 255f), 0, 255);
        return $"#{r:X2}{g:X2}{b:X2}";
    }
}
