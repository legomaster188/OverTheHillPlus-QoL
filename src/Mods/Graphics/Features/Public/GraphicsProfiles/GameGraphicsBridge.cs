using System;
using System.Collections.Generic;

namespace OverTheHillPlus.Features.Public.GraphicsProfiles;

/// <summary>
/// Optional link to the game's own Settings → Graphics page, so a profile can carry the game's quality preset and its
/// advanced options too (shadows, vegetation, AO...). Works exactly like the game's menu does: a fresh
/// GraphicsSettingsState (its constructor loads the saved settings), the same *Changed(option) calls the carousels
/// make, then SaveToDisk. Resolution, display mode, v-sync, frame cap, anti-aliasing and upscaling are never
/// touched: they depend on the monitor and GPU, not on taste.
///
/// Option indices are clamped to the game's own option lists so a shared profile can't push an invalid index.
/// Quality order (from GraphicsSettings.QualitySettingApplied): 0 Lowest, 1 Low, 2 Medium, 3 High, 4 Ultra.
/// </summary>
internal static class GameGraphicsBridge
{
    public const string QualityKey = "Quality";

    /// <summary>Advanced options in the order the game applies them.</summary>
    public static readonly string[] AdvancedKeys =
    {
        "AmbientOcclusion", "Bloom", "FilmGrain", "LensDirt", "MotionBlur", "LightingQuality", "VolumetricLight",
        "ShadowResolution", "ShadowDistance", "AdditionalLightShadows", "VegetationDistance", "VegetationDensity",
        "ParticleQuality", "PostProcessingQuality", "TextureQuality", "TerrainPixelError", "TraxQuality",
    };

    public static string LastError { get; private set; } = "";

    private static GraphicsSettingsState Create()
    {
        var s = new GraphicsSettingsState();
        if (s.defaultGraphicsSettings == null) throw new InvalidOperationException("the game's graphics settings asset is not loaded");
        return s;
    }

    /// <summary>The game's saved graphics options, or null when they can't be read here.</summary>
    public static Dictionary<string, int> Read()
    {
        try
        {
            var s = Create();
            var d = new Dictionary<string, int>(StringComparer.Ordinal) { [QualityKey] = s.currentQuality };
            foreach (var k in AdvancedKeys) d[k] = Get(s, k);
            LastError = "";
            return d;
        }
        catch (Exception e) { DebugLog.Error("graphics", e, "GameGraphicsBridge.Read"); return Fail("read", e); }
    }

    /// <summary>Names of the game's quality presets ("Low", "High"...), lowest first; null if unknown.</summary>
    public static List<string> QualityNames()
    {
        try
        {
            var s = Create();
            var list = new List<string>();
            var opts = s.translatedQualityOptions ?? s.qualityOptions;
            if (opts == null) return null;
            for (int i = 0; i < opts.Count; i++) list.Add(opts[i]);
            return list;
        }
        catch (Exception e) { DebugLog.Error("graphics", e, "GameGraphicsBridge.QualityNames"); Fail("names", e); return null; }
    }

    /// <summary>Applies a saved set of options (quality first, then each advanced option) and saves them like the game does.</summary>
    public static string Apply(Dictionary<string, int> values)
    {
        if (values == null || values.Count == 0) return null;
        try
        {
            var s = Create();
            if (values.TryGetValue(QualityKey, out int q))
            {
                q = ClampIndex(q, Count(s, QualityKey));
                if (q != s.currentQuality) s.ActivateQualityPreset(q);
            }
            int set = 0;
            foreach (var k in AdvancedKeys)
            {
                if (!values.TryGetValue(k, out int v)) continue;
                try
                {
                    v = ClampIndex(v, Count(s, k));
                    Set(s, k, v);
                    set++;
                }
                catch (Exception e) { DebugLog.Error("graphics", e, "GameGraphicsBridge.Apply"); GraphicsCatalog.LogOnce("game:" + k, $"Game graphics option {k} = {v} failed: {e.Message}"); }
            }
            s.SaveToDisk();
            LastError = "";
            DebugLog.Verbose("graphics", () => $"game graphics settings written and saved: quality {Name(s, s.currentQuality)}, {set} advanced option(s)");
            return $"game settings: quality {Name(s, s.currentQuality)}, {set} options";
        }
        catch (Exception e) { DebugLog.Error("graphics", e, "GameGraphicsBridge.Apply"); Fail("apply", e); return null; }
    }

    /// <summary>Activates one of the game's quality presets with its own advanced defaults, then saves.</summary>
    public static string ApplyTier(GraphicsTier tier)
    {
        try
        {
            var s = Create();
            int count = Count(s, QualityKey);
            if (count <= 0) throw new InvalidOperationException("no quality presets");
            int q = tier switch
            {
                GraphicsTier.Beauty => count - 1,
                GraphicsTier.Balanced => s.LoadDefaultQualitySettingWithPlatformOverride(),
                GraphicsTier.Performance => Math.Min(1, count - 1),
                _ => 0,
            };
            q = ClampIndex(q, count);
            s.ActivateQualityPresetAndApplyAdvanced(q); // preset + reset the advanced options to that preset's defaults
            s.ApplyAllAdvancedSettings();
            s.SaveToDisk();
            LastError = "";
            DebugLog.Verbose("graphics", () => $"game quality preset {Name(s, q)} activated for tier {tier}");
            return $"game quality {Name(s, q)}";
        }
        catch (Exception e) { DebugLog.Error("graphics", e, "GameGraphicsBridge.ApplyTier"); Fail("tier", e); return null; }
    }

    private static string Name(GraphicsSettingsState s, int q)
    {
        try
        {
            var opts = s.translatedQualityOptions ?? s.qualityOptions;
            if (opts != null && q >= 0 && q < opts.Count) return opts[q];
        }
        catch (Exception ex) { DebugLog.Error("graphics", ex, "GameGraphicsBridge.Name"); }
        return "#" + q;
    }

    private static int ClampIndex(int v, int count) => count > 0 ? Math.Max(0, Math.Min(count - 1, v)) : Math.Max(0, Math.Min(5, v));

    private static int Get(GraphicsSettingsState s, string k) => k switch
    {
        "AmbientOcclusion" => s.currentAO,
        "Bloom" => s.currentBloom,
        "FilmGrain" => s.currentFilmGrain,
        "LensDirt" => s.currentLensDirt,
        "MotionBlur" => s.currentMotionBlur,
        "LightingQuality" => s.currentLightingQuality,
        "VolumetricLight" => s.currentVolumetricLight,
        "ShadowResolution" => s.currentShadowResolution,
        "ShadowDistance" => s.currentShadowDistance,
        "AdditionalLightShadows" => s.currentAdditionalLightShadows,
        "VegetationDistance" => s.currentVegetationDistance,
        "VegetationDensity" => s.currentVegetationDensity,
        "ParticleQuality" => s.currentParticleQuality,
        "PostProcessingQuality" => s.currentPostProcessingQuality,
        "TextureQuality" => s.currentTextureQuality,
        "TerrainPixelError" => s.currentTerrainPixelError,
        "TraxQuality" => s.currentTraxQuality,
        _ => 0,
    };

    private static void Set(GraphicsSettingsState s, string k, int v)
    {
        switch (k)
        {
            case "AmbientOcclusion": s.AmbientOcclusionChanged(v); break;
            case "Bloom": s.BloomChanged(v); break;
            case "FilmGrain": s.FilmGrainChanged(v); break;
            case "LensDirt": s.LensDirtChanged(v); break;
            case "MotionBlur": s.MotionBlurChanged(v); break;
            case "LightingQuality": s.LightingQualityChanged(v); break;
            case "VolumetricLight": s.VolumetricLightChanged(v); break;
            case "ShadowResolution": s.ShadowResolutionChanged(v); break;
            case "ShadowDistance": s.ShadowDistanceChanged(v); break;
            case "AdditionalLightShadows": s.AdditionalLightShadowsChanged(v); break;
            case "VegetationDistance": s.VegetationDistanceChanged(v); break;
            case "VegetationDensity": s.VegetationDensityChanged(v); break;
            case "ParticleQuality": s.ParticleQualityChanged(v); break;
            case "PostProcessingQuality": s.PostProcessingQualityChanged(v); break;
            case "TextureQuality": s.TextureQualityChanged(v); break;
            case "TerrainPixelError": s.TerrainPixelErrorChanged(v); break;
            case "TraxQuality": s.TraxQualityChanged(v); break;
        }
    }

    /// <summary>Number of choices of an option, from the game's own lists; -1 when unknown.</summary>
    private static int Count(GraphicsSettingsState s, string k)
    {
        try
        {
            var g = s.defaultGraphicsSettings;
            return k switch
            {
                QualityKey or "LightingQuality" => s.qualityOptions?.Count ?? -1,
                "AmbientOcclusion" => g.AmbientOcclusionOptions?.Count ?? -1,
                "Bloom" => g.BloomOptions?.Count ?? -1,
                "FilmGrain" => g.FilmGrainOptions?.Count ?? -1,
                "LensDirt" => g.LensDirtOptions?.Count ?? -1,
                "MotionBlur" => g.MotionBlurOptions?.Count ?? -1,
                "VolumetricLight" => g.VolumetricLightOptions?.Count ?? -1,
                "ShadowResolution" => g.ShadowResolutionOptions?.Count ?? -1,
                "ShadowDistance" => g.ShadowDistanceOptions?.Count ?? -1,
                "AdditionalLightShadows" => g.AdditionalLightShadowsOptions?.Count ?? -1,
                "VegetationDistance" => g.VegetationDistanceOptions?.Count ?? -1,
                "VegetationDensity" => g.VegetationDensityOptions?.Count ?? -1,
                "ParticleQuality" => g.ParticleQualityOptionRange?.Count ?? -1,
                "PostProcessingQuality" => g.BeautifyDownsamplingOptions?.Count ?? -1,
                "TextureQuality" => g.GlobalMipmapLimitOptions?.Count ?? -1,
                "TerrainPixelError" => g.TerrainsHeightmapPixelErrorOptions?.Count ?? -1,
                "TraxQuality" => g.TraxQualityOptions?.Count ?? -1,
                _ => -1,
            };
        }
        catch (Exception ex) { DebugLog.Error("graphics", ex, "GameGraphicsBridge.Count"); return -1; }
    }

    private static Dictionary<string, int> Fail(string what, Exception e)
    {
        LastError = e.Message;
        GraphicsCatalog.LogOnce("game-" + what, $"Graphics profiles: game graphics settings ({what}) failed: {e}");
        return null;
    }
}
