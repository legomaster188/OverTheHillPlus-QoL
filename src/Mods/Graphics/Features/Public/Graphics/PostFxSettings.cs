using BepInEx.Configuration;

namespace OverTheHillPlus.Features.Public.Graphics;

/// <summary>
/// Settings for the post-processing overhaul (config section "Graphics.Post") and the built-in presets.
/// Every group has an "Override" switch: when it is off the game's own value for that effect is used untouched.
/// </summary>
internal static class PostFxSettings
{
    public const string Section = "Graphics.Post";

    public static ConfigEntry<bool> Enabled, PauseInPhotoMode;
    public static ConfigEntry<float> Strength;
    public static ConfigEntry<string> Preset;

    // Beautify (the game's own bloom / tonemapping / film grain)
    public static ConfigEntry<bool> BloomOn, TonemapOn, GrainOn, SharpenOn;
    public static ConfigEntry<float> BloomIntensity, BloomThreshold;
    public static ConfigEntry<int> TonemapMode; // 0 Linear (none), 1 ACES fitted, 2 ACES, 3 AgX
    public static ConfigEntry<float> TonemapExposure, TonemapBrightness;
    public static ConfigEntry<float> GrainIntensity;
    public static ConfigEntry<float> SharpenIntensity;

    // URP volume components
    public static ConfigEntry<bool> ColorOn, WhiteBalanceOn, LggOn, VignetteOn, ChromaOn;
    public static ConfigEntry<float> Exposure, Contrast, Saturation, HueShift;
    public static ConfigEntry<float> Temperature, Tint;
    public static ConfigEntry<float> Lift, Gamma, Gain, SplitTone;
    public static ConfigEntry<float> VignetteIntensity, VignetteSmoothness;
    public static ConfigEntry<float> ChromaIntensity;

    public static readonly string[] TonemapNames = { "None (linear)", "ACES fitted", "ACES", "AgX" };

    public static void Bind(ConfigFile cfg)
    {
        const string s = Section;
        Enabled = cfg.Bind(s, "Enabled", false, "Applies the mod's post-processing on top of the game's. Off = the game's original look.");
        Strength = cfg.Bind(s, "Strength", 1f, new ConfigDescription("Blend between the game's look (0) and the mod's settings (1).", new AcceptableValueRange<float>(0f, 1f)));
        PauseInPhotoMode = cfg.Bind(s, "PauseInPhotoMode", true, "Steps aside while the game's photo mode is open so its own sliders work.");
        Preset = cfg.Bind(s, "Preset", "Custom", "Last applied preset (informational).");

        BloomOn = cfg.Bind(s, "BloomOverride", false, "Override the game's bloom (Beautify).");
        BloomIntensity = cfg.Bind(s, "BloomIntensity", 1f, "Bloom intensity.");
        BloomThreshold = cfg.Bind(s, "BloomThreshold", 0.75f, "Brightness above which things glow.");

        TonemapOn = cfg.Bind(s, "TonemapOverride", false, "Override the game's tonemapping (Beautify).");
        TonemapMode = cfg.Bind(s, "TonemapMode", 1, new ConfigDescription("0 None (linear), 1 ACES fitted, 2 ACES, 3 AgX.", new AcceptableValueRange<int>(0, 3)));
        TonemapExposure = cfg.Bind(s, "TonemapExposure", 1f, "Exposure multiplier before tonemapping.");
        TonemapBrightness = cfg.Bind(s, "TonemapBrightness", 1f, "Brightness multiplier after tonemapping.");

        ColorOn = cfg.Bind(s, "ColorOverride", false, "Override exposure / contrast / saturation / hue (URP Color Adjustments).");
        Exposure = cfg.Bind(s, "Exposure", 0f, "Post exposure in EV.");
        Contrast = cfg.Bind(s, "Contrast", 0f, "Contrast, -100..100.");
        Saturation = cfg.Bind(s, "Saturation", 0f, "Saturation, -100..100.");
        HueShift = cfg.Bind(s, "HueShift", 0f, "Hue shift in degrees, -180..180.");

        WhiteBalanceOn = cfg.Bind(s, "WhiteBalanceOverride", false, "Override white balance (URP White Balance).");
        Temperature = cfg.Bind(s, "Temperature", 0f, "Colour temperature, -100 (cool) .. 100 (warm).");
        Tint = cfg.Bind(s, "Tint", 0f, "Tint, -100 (green) .. 100 (magenta).");

        LggOn = cfg.Bind(s, "LiftGammaGainOverride", false, "Override lift / gamma / gain (URP Lift Gamma Gain).");
        Lift = cfg.Bind(s, "Lift", 0f, "Shadows offset, -1..1.");
        Gamma = cfg.Bind(s, "Gamma", 0f, "Midtones offset, -1..1.");
        Gain = cfg.Bind(s, "Gain", 0f, "Highlights offset, -1..1.");
        SplitTone = cfg.Bind(s, "SplitTone", 0f, "Teal shadows + warm highlights (positive) or the reverse (negative), -1..1.");

        VignetteOn = cfg.Bind(s, "VignetteOverride", false, "Override the vignette (URP Vignette).");
        VignetteIntensity = cfg.Bind(s, "VignetteIntensity", 0.25f, "Vignette darkness, 0..1.");
        VignetteSmoothness = cfg.Bind(s, "VignetteSmoothness", 0.4f, "Vignette softness, 0..1.");

        GrainOn = cfg.Bind(s, "FilmGrainOverride", false, "Override the film grain (Beautify).");
        GrainIntensity = cfg.Bind(s, "FilmGrainIntensity", 0.2f, "Film grain amount, 0 turns it off.");

        ChromaOn = cfg.Bind(s, "ChromaticAberrationOverride", false, "Override chromatic aberration (URP).");
        ChromaIntensity = cfg.Bind(s, "ChromaticAberrationIntensity", 0.15f, "Colour fringing at the screen edges, 0..1.");

        SharpenOn = cfg.Bind(s, "SharpenOverride", false, "Override Beautify's sharpen strength.");
        SharpenIntensity = cfg.Bind(s, "SharpenIntensity", 2f, "Sharpen strength (Beautify units).");
    }

    // ---- presets ----

    private sealed class P
    {
        public bool Bloom, Tone, Color, Wb, Lgg, Vig, Grain, Chroma;
        public float BloomI = 1f, BloomT = 0.75f;
        public int ToneMode = 1;
        public float ToneExp = 1f, ToneBright = 1f;
        public float Exp, Con, Sat;
        public float Temp, Tint;
        public float Lift, Gamma, Gain, Split;
        public float VigI = 0.25f, VigS = 0.4f;
        public float GrainI = 0.2f;
        public float ChromaI = 0.15f;
    }

    public static readonly string[] PresetNames = { "Vivid", "Cinematic", "Natural", "Retro", "Off" };

    private static P Get(string name) => name switch
    {
        // Punchy colours and a little glow; keeps the game's tonemapping.
        "Vivid" => new P
        {
            Bloom = true, BloomI = 1.4f, BloomT = 0.7f,
            Color = true, Exp = 0.1f, Con = 18f, Sat = 32f,
            Wb = true, Temp = 4f,
            Vig = true, VigI = 0.2f, VigS = 0.45f,
        },
        // Filmic: ACES, teal/orange split tone, darker edges, light grain.
        "Cinematic" => new P
        {
            Bloom = true, BloomI = 0.9f, BloomT = 0.8f,
            Tone = true, ToneMode = 2, ToneExp = 1.1f,
            Color = true, Exp = -0.1f, Con = 14f, Sat = -8f,
            Wb = true, Temp = 6f, Tint = -2f,
            Lgg = true, Lift = -0.02f, Gain = 0.03f, Split = 0.6f,
            Vig = true, VigI = 0.35f, VigS = 0.45f,
            Grain = true, GrainI = 0.18f,
            Chroma = true, ChromaI = 0.08f,
        },
        // Subtle clean-up that stays close to the original.
        "Natural" => new P
        {
            Color = true, Con = 6f, Sat = 6f,
            Wb = true, Temp = 2f,
            Vig = true, VigI = 0.15f, VigS = 0.5f,
        },
        // Washed-out warm film look: faded blacks, heavy grain and fringing.
        "Retro" => new P
        {
            Bloom = true, BloomI = 0.8f, BloomT = 0.65f,
            Color = true, Exp = 0.05f, Con = 8f, Sat = -35f,
            Wb = true, Temp = 28f, Tint = 6f,
            Lgg = true, Lift = 0.06f, Gamma = 0.02f, Gain = -0.05f, Split = -0.2f,
            Vig = true, VigI = 0.45f, VigS = 0.35f,
            Grain = true, GrainI = 0.5f,
            Chroma = true, ChromaI = 0.35f,
        },
        _ => null,
    };

    /// <summary>Writes a preset into the settings. "Off" turns the overhaul off (the game's original look).</summary>
    public static void ApplyPreset(string name)
    {
        Preset.Value = name;
        if (name == "Off")
        {
            Enabled.Value = false;
            return;
        }
        var p = Get(name);
        if (p == null) return;
        BloomOn.Value = p.Bloom; BloomIntensity.Value = p.BloomI; BloomThreshold.Value = p.BloomT;
        TonemapOn.Value = p.Tone; TonemapMode.Value = p.ToneMode; TonemapExposure.Value = p.ToneExp; TonemapBrightness.Value = p.ToneBright;
        ColorOn.Value = p.Color; Exposure.Value = p.Exp; Contrast.Value = p.Con; Saturation.Value = p.Sat; HueShift.Value = 0f;
        WhiteBalanceOn.Value = p.Wb; Temperature.Value = p.Temp; Tint.Value = p.Tint;
        LggOn.Value = p.Lgg; Lift.Value = p.Lift; Gamma.Value = p.Gamma; Gain.Value = p.Gain; SplitTone.Value = p.Split;
        VignetteOn.Value = p.Vig; VignetteIntensity.Value = p.VigI; VignetteSmoothness.Value = p.VigS;
        GrainOn.Value = p.Grain; GrainIntensity.Value = p.GrainI;
        ChromaOn.Value = p.Chroma; ChromaIntensity.Value = p.ChromaI;
        SharpenOn.Value = false;
        Strength.Value = 1f;
        Enabled.Value = true;
    }
}
