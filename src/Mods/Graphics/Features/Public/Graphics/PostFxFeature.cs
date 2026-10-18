using BepInEx.Configuration;
using OverTheHillPlus.UI;
using UnityEngine;
using OverTheHillPlus.Graphics;

namespace OverTheHillPlus.Features.Public.Graphics;

/// <summary>
/// Graphics tab: post-processing overhaul (bloom, tonemapping, colour, white balance, lift/gamma/gain, vignette,
/// film grain, chromatic aberration) with presets. Purely local visuals, allowed in every session.
/// </summary>
internal sealed class PostFxFeature : IFeature
{
    private const string Tab = "Graphics";

    public void Register()
    {
        PostFxSettings.Bind(GraphicsConfig.File);
        GraphicsConfig.File.SettingChanged += OnSettingChanged;
        Registry.OnTick(PostFxEngine.Tick);
        Registry.Section(Registry.PlusMode, Tab, DrawMain, 0);
        Registry.Section(Registry.PlusMode, Tab, DrawLight, 1);
        Registry.Section(Registry.PlusMode, Tab, DrawColor, 2);
        Registry.Section(Registry.PlusMode, Tab, DrawLens, 3);
        Registry.Section(Registry.PlusMode, Tab, DrawDiagnostics, 90);
    }

    private static void OnSettingChanged(object sender, SettingChangedEventArgs e)
    {
        try
        {
            if (e?.ChangedSetting?.Definition.Section == PostFxSettings.Section) PostFxEngine.MarkDirty();
        }
        catch (System.Exception ex) { DebugLog.Error("graphics", ex, "PostFxFeature.OnSettingChanged"); /* never let a config event throw */ }
    }

    private static bool On => PostFxSettings.Enabled.Value;

    private static void DrawMain()
    {
        PostFxEngine.NoteMenuVisible();
        Menu.Card("Post-processing", "Your own look on top of the game's renderer. Turning it off restores the original image instantly.", () =>
        {
            Menu.Switch(PostFxSettings.Enabled, "Post-processing overhaul", "Adds the mod's volume; the game's own settings are never changed", true);
            Menu.Chips(5,
                ("Vivid", () => Preset("Vivid"), true),
                ("Cinematic", () => Preset("Cinematic"), true),
                ("Natural", () => Preset("Natural"), true),
                ("Retro", () => Preset("Retro"), true),
                ("Off", () => Preset("Off"), true));
            Menu.SliderRow(PostFxSettings.Strength, "Strength", 0f, 1f, "0.00", "", On, percent: true);
            Menu.Switch(PostFxSettings.PauseInPhotoMode, "Pause in photo mode", "The game's photo-mode sliders work while it is open", On);

            string state;
            if (!On) state = "Off (original look)";
            else if (PostFxEngine.LastError != null && !PostFxEngine.Active) state = "<color=#E5534B>Failed</color>";
            else if (PostFxEngine.PhotoModeActive && PostFxSettings.PauseInPhotoMode.Value) state = "Paused (photo mode)";
            else state = PostFxEngine.Active ? "<color=#4CC38A>Active</color>" : "Starting...";
            Menu.KeyValue("Status", state);
            Menu.KeyValue("Preset", PostFxSettings.Preset.Value);
            if (On && PostFxEngine.CameraPostProcessing == false)
                Menu.Banner("The game camera has post-processing turned off right now, so these effects will not show. Raise Post-processing quality in the game's graphics settings.", Theme.Warn);
        });
    }

    private static void Preset(string name)
    {
        PostFxSettings.ApplyPreset(name);
        PostFxEngine.MarkDirty();
        Menu.SetStatus(name == "Off" ? "Post-processing off: original look restored" : $"Post-processing preset: {name}");
    }

    private static void DrawLight()
    {
        Menu.Card("Bloom and tonemapping", "Uses the game's own Beautify effects.", () =>
        {
            Menu.Switch(PostFxSettings.BloomOn, "Bloom", "Glow around bright lights and the sky", On);
            bool b = On && PostFxSettings.BloomOn.Value;
            Menu.SliderRow(PostFxSettings.BloomIntensity, "Intensity", 0f, 5f, "0.00", "", b);
            Menu.SliderRow(PostFxSettings.BloomThreshold, "Threshold", 0f, 2f, "0.00", "", b);

            Menu.Switch(PostFxSettings.TonemapOn, "Tonemapping", "How bright colours are squeezed into the screen range", On);
            bool t = On && PostFxSettings.TonemapOn.Value;
            int mode = Mathf.Clamp(PostFxSettings.TonemapMode.Value, 0, 3);
            float m = Menu.RawSlider("Mode", mode, 0f, 3f, PostFxSettings.TonemapNames[mode], t);
            int nm = Mathf.Clamp(Mathf.RoundToInt(m), 0, 3);
            if (nm != mode) PostFxSettings.TonemapMode.Value = nm;
            Menu.SliderRow(PostFxSettings.TonemapExposure, "Exposure (pre)", 0.2f, 3f, "0.00", "x", t);
            Menu.SliderRow(PostFxSettings.TonemapBrightness, "Brightness (post)", 0.2f, 2f, "0.00", "x", t);

            Menu.Switch(PostFxSettings.SharpenOn, "Sharpen", "Beautify's sharpen filter strength", On);
            Menu.SliderRow(PostFxSettings.SharpenIntensity, "Sharpen", 0f, 10f, "0.0", "", On && PostFxSettings.SharpenOn.Value);
        });
    }

    private static void DrawColor()
    {
        Menu.Card("Color grading", null, () =>
        {
            Menu.Switch(PostFxSettings.ColorOn, "Color adjustments", "Exposure, contrast, saturation and hue", On);
            bool c = On && PostFxSettings.ColorOn.Value;
            Menu.SliderRow(PostFxSettings.Exposure, "Exposure", -3f, 3f, "+0.00;-0.00;0.00", " EV", c);
            Menu.SliderRow(PostFxSettings.Contrast, "Contrast", -100f, 100f, "+0;-0;0", "", c);
            Menu.SliderRow(PostFxSettings.Saturation, "Saturation", -100f, 100f, "+0;-0;0", "", c);
            Menu.SliderRow(PostFxSettings.HueShift, "Hue shift", -180f, 180f, "+0;-0;0", "°", c);

            Menu.Switch(PostFxSettings.WhiteBalanceOn, "White balance", "Warmer or cooler light", On);
            bool w = On && PostFxSettings.WhiteBalanceOn.Value;
            Menu.SliderRow(PostFxSettings.Temperature, "Temperature", -100f, 100f, "+0;-0;0", "", w);
            Menu.SliderRow(PostFxSettings.Tint, "Tint", -100f, 100f, "+0;-0;0", "", w);

            Menu.Switch(PostFxSettings.LggOn, "Lift / gamma / gain", "Shadows, midtones and highlights", On);
            bool l = On && PostFxSettings.LggOn.Value;
            Menu.SliderRow(PostFxSettings.Lift, "Lift (shadows)", -0.5f, 0.5f, "+0.00;-0.00;0.00", "", l);
            Menu.SliderRow(PostFxSettings.Gamma, "Gamma (midtones)", -0.5f, 0.5f, "+0.00;-0.00;0.00", "", l);
            Menu.SliderRow(PostFxSettings.Gain, "Gain (highlights)", -0.5f, 0.5f, "+0.00;-0.00;0.00", "", l);
            Menu.SliderRow(PostFxSettings.SplitTone, "Split tone", -1f, 1f, "+0.00;-0.00;0.00", "", l);
            Menu.Small("Split tone: positive = teal shadows and warm highlights, negative = the reverse.");
        });
    }

    private static void DrawLens()
    {
        Menu.Card("Lens effects", null, () =>
        {
            Menu.Switch(PostFxSettings.VignetteOn, "Vignette", "Darker screen corners", On);
            bool v = On && PostFxSettings.VignetteOn.Value;
            Menu.SliderRow(PostFxSettings.VignetteIntensity, "Intensity", 0f, 1f, "0.00", "", v);
            Menu.SliderRow(PostFxSettings.VignetteSmoothness, "Smoothness", 0.01f, 1f, "0.00", "", v);

            Menu.Switch(PostFxSettings.GrainOn, "Film grain", "Uses the game's Beautify grain", On);
            Menu.SliderRow(PostFxSettings.GrainIntensity, "Amount", 0f, 1f, "0.00", "", On && PostFxSettings.GrainOn.Value);

            Menu.Switch(PostFxSettings.ChromaOn, "Chromatic aberration", "Colour fringing towards the edges", On);
            Menu.SliderRow(PostFxSettings.ChromaIntensity, "Amount", 0f, 1f, "0.00", "", On && PostFxSettings.ChromaOn.Value);
        });
    }

    private static void DrawDiagnostics()
    {
        if (!DevDetails.On)
        {
            // For players: no internals, only a way out when something went wrong.
            if (PostFxEngine.LastError == null) return;
            Menu.Card("Post-processing problem", null, () =>
            {
                Menu.Small($"<color=#E5534B>{PostFxEngine.LastError}</color>");
                Menu.Chips(2, ("Retry", PostFxEngine.Recreate, true), ("Dismiss", () => PostFxEngine.LastError = null, true));
            });
            return;
        }
        Menu.Card("Post-processing details", "What the mod found in the current scene (refreshes every few seconds).", () =>
        {
            Menu.KeyValue("Game volumes", PostFxEngine.GameVolumes.Count.ToString());
            Menu.KeyValue("Beautify in game", PostFxEngine.GameHasBeautify ? "yes" : "no");
            Menu.KeyValue("Camera post FX", PostFxEngine.CameraPostProcessing == null ? "?" : PostFxEngine.CameraPostProcessing.Value ? "on" : "off");
            Menu.KeyValue("Volume updates", PostFxEngine.CameraUpdateMode);
            int shown = 0;
            foreach (var v in PostFxEngine.GameVolumes)
            {
                if (shown++ >= 6) { Menu.Small($"...and {PostFxEngine.GameVolumes.Count - 6} more"); break; }
                string head = $"{v.Name} - {v.Profile} ({(v.Global ? "global" : "local")}, prio {v.Priority:0.#}, weight {v.Weight:0.##}{(v.Enabled ? "" : ", disabled")})";
                Menu.Small(string.IsNullOrEmpty(v.Components) ? head : head + "\n" + v.Components);
            }
            if (PostFxEngine.LastError != null)
            {
                Menu.Small($"<color=#E5534B>Last error: {PostFxEngine.LastError}</color>");
                Menu.Chips(2, ("Retry", PostFxEngine.Recreate, true), ("Clear error", () => PostFxEngine.LastError = null, true));
            }
        });
    }
}
