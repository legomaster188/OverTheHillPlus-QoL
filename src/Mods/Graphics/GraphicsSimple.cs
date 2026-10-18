using System;
using OverTheHillPlus.Features.Public.Fog;
using OverTheHillPlus.Features.Public.Gfx;
using OverTheHillPlus.Features.Public.GraphicsProfiles;
using OverTheHillPlus.Features.Public.GraphicsSky;
using OverTheHillPlus.UI;
using PostFx = OverTheHillPlus.Features.Public.Graphics.PostFxSettings;

namespace OverTheHillPlus.Graphics;

/// <summary>Graphics+'s everyday settings, first on its MODS page (the rest is in its Advanced fold).</summary>
internal static class GraphicsSimple
{
    private static readonly float[] ScaleSteps = { 0.5f, 0.67f, 0.75f, 0.85f, 1f, 1.25f, 1.5f, 2f };
    private static readonly string[] ColourLooks = { "Game", "Natural", "Vivid", "Cinematic", "Retro" };
    private static string[] _skyLooks;

    internal static void Draw()
    {
        // Not bound when the mod failed to start: nothing to show then.
        if (GraphicsConfig.File == null) return;
        SimpleMenu.Heading("Graphics");

        // Changed since the last tier: shown as "Balanced*", and the arrows step from that tier (not from an end).
        int tier = GraphicsProfilesFeature.CurrentTier(), last = GraphicsProfilesFeature.LastTier();
        var tiers = GraphicsTiers.Names;
        SimpleMenu.Choice("Quality", "All graphics options at once: Beauty, Balanced (the defaults), Performance or Potato.",
            tier, tiers, GraphicsProfilesFeature.ApplyTierAt, wrap: false,
            shown: tier >= 0 ? null : last >= 0 ? tiers[last] + "*" : "Custom", from: last >= 0 ? last : 1);

        if (GraphicsLighting.RenderScaleOn != null && GraphicsLighting.RenderScale != null)
        {
            bool upscaler = LightingScene.TndActive;
            SimpleMenu.FloatChoice("Render scale",
                upscaler ? "The game's upscaler is on and picks the resolution itself (game Settings > Graphics)."
                    : "Below 100% is faster and softer, above it is sharper and slower.",
                GraphicsLighting.RenderScaleOn.Value ? GraphicsLighting.RenderScale.Value : 1f, ScaleSteps,
                v => Math.Abs(v - 1f) < 0.001f ? "100%" : UnityEngine.Mathf.RoundToInt(v * 100f) + "%",
                v =>
                {
                    bool custom = Math.Abs(v - 1f) > 0.001f;
                    GraphicsLighting.RenderScaleOn.Value = custom;
                    if (custom) GraphicsLighting.RenderScale.Value = v;
                }, !upscaler);
        }

        if (PostFx.Enabled != null)
        {
            int colour = 0;
            if (PostFx.Enabled.Value)
            {
                colour = Array.IndexOf(ColourLooks, PostFx.Preset.Value);
                if (colour == 0) colour = -1; // "Game" is not a preset name: on with an unknown preset = custom
            }
            SimpleMenu.Choice("Colour look", "A colour grade on top of the game's picture. Game = the original look.",
                colour, ColourLooks, i => PostFx.ApplyPreset(i == 0 ? "Off" : ColourLooks[i]));
        }

        if (SkySettings.Enabled != null)
        {
            var looks = SkySettings.Looks;
            if (_skyLooks == null)
            {
                _skyLooks = new string[looks.Length + 1];
                _skyLooks[0] = "Game";
                for (int i = 0; i < looks.Length; i++) _skyLooks[i + 1] = looks[i].Name;
            }
            SimpleMenu.Choice("Sky", "Clouds and sky colours. Only you see it; Game = the game's own sky.",
                CurrentSky(looks), _skyLooks, i =>
                {
                    if (i == 0) SkySettings.Enabled.Value = false; // keeps a custom sky for Advanced to switch back on
                    else SkySettings.ApplyLook(looks[i - 1]);
                });
        }

        SimpleMenu.Choice("Fog", "Only changes what you see. Game = the game's own fog.",
            FogAtmosphere.CurrentSimpleLook(), FogAtmosphere.SimpleLooks, FogAtmosphere.ApplySimpleLook);
    }

    private static int CurrentSky(SkySettings.Look[] looks)
    {
        if (!SkySettings.Enabled.Value) return 0;
        for (int i = 0; i < looks.Length; i++)
        {
            var l = looks[i];
            if (Math.Abs(SkySettings.CloudAmount.Value - l.Amount) < 0.005f && Math.Abs(SkySettings.SkyTint.Value - l.SkyT) < 0.005f
                && string.Equals(SkySettings.SkyColor.Value, l.Sky, StringComparison.OrdinalIgnoreCase))
                return i + 1;
        }
        return -1;
    }
}
