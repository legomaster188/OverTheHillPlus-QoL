using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using BepInEx.Configuration;

namespace OverTheHillPlus.Features.Public.GraphicsProfiles;

internal enum GraphicsTier { Beauty, Balanced, Performance, Potato }

/// <summary>
/// Works out what the one-click tiers (Beauty / Balanced / Performance / Potato) set for each graphics setting.
///
/// Three sources, first match wins:
/// 1. <see cref="Declare"/>: a graphics feature states its own tier values in code (best, exact).
/// 2. A tag in the setting's description: "[tiers: beauty|balanced|performance|potato]", values in the config
///    file's own text form, "-" (or empty) = leave alone, "default" = the setting's default. Lets a feature opt in
///    without referencing this class.
/// 3. A conservative guess from the setting's name and type: only things that cost frame time (shadows, fog,
///    clouds, distances, densities, sample counts, quality levels...) are touched; colours, intensities, looks,
///    overlays and anything unrecognised are left exactly as they are. The preview shows every change first.
/// </summary>
internal static class GraphicsTiers
{
    public static readonly object Default = new();
    public static readonly string[] Names = { "Beauty", "Balanced", "Performance", "Potato" };

    internal sealed class Change
    {
        public ConfigEntryBase Entry;
        public string From, To, Source;
    }

    private static readonly Dictionary<string, object[]> Declared = new(StringComparer.Ordinal);

    /// <summary>
    /// Declares the tier values of a setting (null = leave alone, <see cref="Default"/> = the default value).
    /// Values may be of the setting's type or already serialized strings.
    /// </summary>
    public static void Declare(ConfigEntryBase entry, object beauty, object balanced, object performance, object potato)
    {
        if (entry?.Definition == null) return;
        Declared[Id(entry)] = new[] { beauty, balanced, performance, potato };
    }

    private static string Id(ConfigEntryBase e) => e.Definition.Section + "\n" + e.Definition.Key;

    /// <summary>Every change the tier would make right now (entries already at the target are left out).</summary>
    public static List<Change> Plan(GraphicsTier tier)
    {
        var list = new List<Change>();
        foreach (var e in GraphicsCatalog.Entries())
        {
            try
            {
                string to = Target(e, tier, out string source);
                if (to == null) continue;
                string from = e.GetSerializedValue();
                if (string.Equals(from, to, StringComparison.Ordinal)) continue;
                list.Add(new Change { Entry = e, From = from, To = to, Source = source });
            }
            catch (Exception ex)
            {
                DebugLog.Error("graphics", ex, "GraphicsTiers.Plan");
                GraphicsCatalog.LogOnce("tier:" + Id(e), $"Graphics tiers: skipped {e.Definition}: {ex.Message}");
            }
        }
        return list;
    }

    /// <summary>How many settings each tier knows how to set (for the card).</summary>
    public static int CoveredCount()
    {
        int n = 0;
        foreach (var e in GraphicsCatalog.Entries())
        {
            for (int t = 0; t < 4; t++)
            {
                string v;
                try { v = Target(e, (GraphicsTier)t, out _); } catch (Exception ex2) { DebugLog.Error("graphics", ex2, "GraphicsTiers.CoveredCount"); v = null; }
                if (v != null) { n++; break; }
            }
        }
        return n;
    }

    public static GraphicsCatalog.Values ToValues(List<Change> changes)
    {
        var v = new GraphicsCatalog.Values();
        foreach (var c in changes) v.Set(c.Entry.Definition.Section, c.Entry.Definition.Key, c.To);
        return v;
    }

    // ---- per-entry target ----

    private static string Target(ConfigEntryBase e, GraphicsTier tier, out string source)
    {
        int t = (int)tier;
        if (Declared.TryGetValue(Id(e), out var declared))
        {
            source = "declared";
            return Serialize(e, declared[t]);
        }
        var tagged = FromTag(e);
        if (tagged != null)
        {
            source = "tagged";
            string s = tagged[t];
            if (string.IsNullOrWhiteSpace(s) || s.Trim() == "-") return null;
            return s.Trim().Equals("default", StringComparison.OrdinalIgnoreCase) ? Serialize(e, Default) : Normalize(e, s.Trim());
        }
        source = "auto";
        return Serialize(e, Guess(e, tier));
    }

    private static string Serialize(ConfigEntryBase e, object value)
    {
        if (value == null) return null;
        if (ReferenceEquals(value, Default)) value = e.DefaultValue;
        if (value is string s && e.SettingType != typeof(string)) return Normalize(e, s);
        if (value.GetType() != e.SettingType)
        {
            try { value = Convert.ChangeType(value, e.SettingType, System.Globalization.CultureInfo.InvariantCulture); }
            catch (Exception ex) { DebugLog.Error("graphics", ex, "GraphicsTiers.Serialize"); return null; }
        }
        value = Clamp(e, value);
        return TomlTypeConverter.ConvertToString(value, e.SettingType);
    }

    /// <summary>Parses a serialized value through the entry's own converter and clamp so only valid values come out.</summary>
    private static string Normalize(ConfigEntryBase e, string s)
    {
        try
        {
            object v = TomlTypeConverter.ConvertToValue(s, e.SettingType);
            v = Clamp(e, v);
            return TomlTypeConverter.ConvertToString(v, e.SettingType);
        }
        catch (Exception ex) { DebugLog.Error("graphics", ex, "GraphicsTiers.Normalize"); return null; }
    }

    private static object Clamp(ConfigEntryBase e, object v)
    {
        var acc = e.Description?.AcceptableValues;
        return acc == null || v == null ? v : acc.Clamp(v);
    }

    private static readonly Regex TagRx = new(@"\[tiers?\s*:\s*([^\]]*)\]", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static string[] FromTag(ConfigEntryBase e)
    {
        string d = e.Description?.Description;
        if (string.IsNullOrEmpty(d)) return null;
        var m = TagRx.Match(d);
        if (!m.Success) return null;
        var parts = m.Groups[1].Value.Split('|');
        return parts.Length == 4 ? parts : null;
    }

    // ---- the guess ----

    private enum Cost { None, Light, Medium, Heavy, Saver, Remove, RemoveHeavy }

    // Never touched by the guess: looks and taste, not frame time.
    // Words starting with '=' must be a whole token; other words of 4+ letters may be part of a word.
    private static readonly string[] LookWords =
    {
        "color", "colour", "tint", "hue", "saturation", "contrast", "exposure", "temperature", "gamma", "=lift", "=gain",
        "brightness", "intensity", "strength", "threshold", "preset", "offset", "speed", "=time", "=hour", "=angle",
        "rotation", "fov", "=height", "opacity", "=alpha", "override", "debug", "log", "=show", "hud", "overlay", "label",
        "=text", "=name", "vignette", "=grain", "chromatic", "tonemap", "sharpen", "=amount", "=size", "smooth",
        "falloff", "=seed", "=style", "=texture", "=path", "=file", "url", "camera", "photo", "ui", "=menu", "=volume",
        "audio", "sound", "=wind", "direction", "position", "radius", "=weight", "=blend", "mix", "=curve", "=tiling",
        "=scale",
    };

    // Switches that exist to save frame time: on in Performance and Potato.
    private static readonly string[] SaverWords =
    {
        "reduce", "=low", "=lite", "=fast", "cheap", "optimi", "cull", "=limit", "performance", "=perf",
        "potato", "simplif", "=simple", "downsampl", "halfres", "=skip", "fps", "budget",
    };

    // Switches that remove something ("NoFog", "DisableGrass"): on in Potato; in Performance only for heavy things.
    private static readonly string[] RemoveWords = { "disable", "no", "remove", "=hide", "off", "=without" };

    private static readonly string[] HeavyWords =
    {
        "volumetric", "cloud", "clouds", "shadow", "shadows", "reflection", "reflections", "ssr", "raytrac", "hbao", "ssao",
        "ambientocclusion", "ao", "gi", "globalillumination", "godray", "godrays", "lightshaft", "sunshaft", "scattering",
        "tessellation", "caustic", "caustics", "refraction", "grass", "foliage", "vegetation", "=tree", "=trees",
        "highquality", "hq", "ultra", "buto",
    };

    private static readonly string[] MediumWords =
    {
        "fog", "motionblur", "dof", "depthoffield", "particle", "particles", "=rain", "snow", "weather", "water", "foam",
        "=wave", "=waves", "shader", "shaders", "msaa", "antialias", "taa", "smaa", "fxaa", "detail", "lod", "puddle",
        "puddles", "wet", "wetness", "splash", "=dust", "spray", "decal", "decals", "=cozy",
    };

    private static readonly string[] LightWords =
    {
        "=post", "postfx", "postprocess", "bloom", "lensflare", "=flare", "sky", "skybox", "=stars",
        "aurora", "=moon", "sun", "lut", "=grade", "grading", "outline",
    };

    private static readonly string[] NumericCostWords =
    {
        "distance", "density", "resolution", "samples", "sample", "steps", "step", "cascades", "cascade", "lodbias",
        "lod", "quality", "detail", "iterations", "rays", "renderscale", "resolutionscale", "maxlights", "drawdistance",
        "viewdistance", "count", "layers",
    };

    private static object Guess(ConfigEntryBase e, GraphicsTier tier)
    {
        string key = e.Definition.Key ?? "";
        var keyTok = Tokens(key, out string keyJoined);
        if (HasAny(keyTok, keyJoined, LookWords) && !keyJoined.Contains("renderscale") && !keyJoined.Contains("resolutionscale"))
            return null;

        var type = e.SettingType;
        if (type == typeof(bool)) return GuessBool(e, tier, keyTok, keyJoined);
        if (type.IsEnum) return GuessEnum(e, tier, keyTok, keyJoined);
        if (type == typeof(float) || type == typeof(double) || type == typeof(int)) return GuessNumber(e, tier, keyTok, keyJoined);
        return null;
    }

    private static object GuessBool(ConfigEntryBase e, GraphicsTier tier, HashSet<string> keyTok, string keyJoined)
    {
        bool master = keyJoined is "enabled" or "enable" or "on" or "active";
        Cost cost;
        if (master)
        {
            // A section's master switch: judge by the area name ("Graphics.Clouds" → clouds).
            var areaTok = Tokens(GraphicsCatalog.AreaOf(e.Definition.Section), out string areaJoined);
            cost = Classify(areaTok, areaJoined);
        }
        else cost = Classify(keyTok, keyJoined);

        bool def = e.DefaultValue is bool b && b;
        return cost switch
        {
            Cost.Heavy => tier switch { GraphicsTier.Beauty => true, GraphicsTier.Balanced => def, _ => false },
            Cost.Medium => tier switch
            {
                GraphicsTier.Beauty => true, GraphicsTier.Balanced => def, GraphicsTier.Potato => false, _ => null,
            },
            Cost.Light => tier == GraphicsTier.Potato ? false : null,
            Cost.Saver or Cost.RemoveHeavy => tier switch { GraphicsTier.Beauty => false, GraphicsTier.Balanced => def, _ => true },
            Cost.Remove => tier switch
            {
                GraphicsTier.Beauty => false, GraphicsTier.Balanced => def, GraphicsTier.Potato => true, _ => null,
            },
            _ => null,
        };
    }

    private static object GuessNumber(ConfigEntryBase e, GraphicsTier tier, HashSet<string> keyTok, string keyJoined)
    {
        if (!HasAny(keyTok, keyJoined, NumericCostWords)) return null;
        if (Classify(keyTok, keyJoined) is Cost.Saver or Cost.Remove or Cost.RemoveHeavy) return null; // "CullDistance": direction unclear
        bool renderScale = keyJoined.Contains("renderscale") || keyJoined.Contains("resolutionscale");
        bool isInt = e.SettingType == typeof(int);
        double def = Convert.ToDouble(e.DefaultValue, System.Globalization.CultureInfo.InvariantCulture);
        if (tier == GraphicsTier.Balanced) return e.DefaultValue;

        double? min = null, max = null;
        switch (e.Description?.AcceptableValues)
        {
            case AcceptableValueRange<float> rf: min = rf.MinValue; max = rf.MaxValue; break;
            case AcceptableValueRange<double> rd: min = rd.MinValue; max = rd.MaxValue; break;
            case AcceptableValueRange<int> ri: min = ri.MinValue; max = ri.MaxValue; break;
            case null: break;
            default: return null; // a list of allowed values: an option index, not a quantity
        }

        double v;
        if (min.HasValue && max.HasValue && max > min)
        {
            double lo = min.Value, hi = max.Value;
            bool smallSteps = isInt && hi - lo <= 6; // quality levels, cascade counts
            v = tier switch
            {
                GraphicsTier.Beauty => smallSteps ? hi : def + (hi - def) * 0.4,
                GraphicsTier.Performance => smallSteps ? def - 1 : def - (def - lo) * 0.35,
                _ => smallSteps ? lo : def - (def - lo) * 0.7,
            };
            if (renderScale)
            {
                // Supersampling is ruinous; Beauty asks for a modest bump, never below native.
                if (tier == GraphicsTier.Beauty) v = Math.Min(hi, Math.Max(def, Math.Min(def * 1.25, 1.5)));
                else v = Math.Max(v, Math.Max(lo, tier == GraphicsTier.Potato ? 0.5 : 0.7));
            }
            v = Math.Max(lo, Math.Min(hi, v));
        }
        else
        {
            // No declared range: only scale float distances/densities, which are safe to multiply.
            if (isInt || renderScale || def <= 0) return null;
            bool scalable = keyJoined.Contains("distance") || keyJoined.Contains("density");
            if (!scalable) return null;
            v = def * (tier switch { GraphicsTier.Beauty => 1.25, GraphicsTier.Performance => 0.75, _ => 0.5 });
        }

        if (isInt) return (int)Math.Round(v);
        if (e.SettingType == typeof(double)) return Math.Round(v, 3);
        return (float)Math.Round(v, 3);
    }

    private static object GuessEnum(ConfigEntryBase e, GraphicsTier tier, HashSet<string> keyTok, string keyJoined)
    {
        var type = e.SettingType;
        var names = Enum.GetNames(type);
        var ranked = new List<(string name, int rank)>();
        foreach (var n in names)
        {
            int r = Rank(n);
            if (r >= 0) ranked.Add((n, r));
        }
        if (ranked.Select(x => x.rank).Distinct().Count() < 2) return null;
        var cost = Classify(keyTok, keyJoined);
        bool qualityish = keyJoined.Contains("quality") || keyJoined.Contains("detail") || keyJoined.Contains("level")
                          || cost is Cost.Heavy or Cost.Medium;
        if (!qualityish || cost is Cost.Saver or Cost.Remove or Cost.RemoveHeavy) return null;
        if (tier == GraphicsTier.Balanced) return e.DefaultValue;

        string pick;
        if (tier == GraphicsTier.Beauty) pick = ranked.OrderByDescending(x => x.rank).First().name;
        else if (tier == GraphicsTier.Performance)
            pick = (ranked.FirstOrDefault(x => x.rank == 2).name) ?? ranked.Where(x => x.rank > 0).OrderBy(x => x.rank).Select(x => x.name).FirstOrDefault();
        else
        {
            bool canOff = cost is Cost.Heavy or Cost.Medium;
            pick = (canOff ? ranked.FirstOrDefault(x => x.rank == 0).name : null)
                   ?? ranked.Where(x => x.rank > 0).OrderBy(x => x.rank).Select(x => x.name).FirstOrDefault();
        }
        return pick == null ? null : Enum.Parse(type, pick);
    }

    /// <summary>Quality rank of an enum name: off 0, lowest 1, low 2, medium 3, high 4, ultra 5; -1 = not a level.</summary>
    private static int Rank(string name)
    {
        string n = name.ToLowerInvariant().Replace("_", "");
        return n switch
        {
            "off" or "none" or "disabled" => 0,
            "lowest" or "verylow" or "minimum" or "min" => 1,
            "low" => 2,
            "medium" or "normal" or "mid" => 3,
            "high" => 4,
            "veryhigh" or "ultra" or "epic" or "max" or "maximum" or "extreme" or "cinematic" or "highest" => 5,
            _ => -1,
        };
    }

    private static Cost Classify(HashSet<string> tok, string joined)
    {
        if (HasAny(tok, joined, SaverWords)) return Cost.Saver;
        bool heavy = HasAny(tok, joined, HeavyWords);
        if (HasAny(tok, joined, RemoveWords)) return heavy ? Cost.RemoveHeavy : Cost.Remove;
        if (heavy) return Cost.Heavy;
        if (HasAny(tok, joined, MediumWords)) return Cost.Medium;
        if (HasAny(tok, joined, LightWords)) return Cost.Light;
        return Cost.None;
    }

    /// <summary>
    /// Short words (≤ 3 letters) and '='-words must be a whole token ("ao" ≠ "chaos", "rain" ≠ "terrain"); longer
    /// words may be part of one ("shadow" in "shadowdistance").
    /// </summary>
    private static bool HasAny(HashSet<string> tokens, string joined, string[] words)
    {
        foreach (var w in words)
        {
            if (w[0] == '=') { if (tokens.Contains(w.Substring(1))) return true; }
            else if (w.Length <= 3 ? tokens.Contains(w) : joined.Contains(w)) return true;
        }
        return false;
    }

    /// <summary>"HBAOQualityLevel2" → {hbao, quality, level, 2}; joined = "hbaoqualitylevel2".</summary>
    private static HashSet<string> Tokens(string s, out string joined)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        var sb = new StringBuilder();
        var all = new StringBuilder();
        s ??= "";
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (!char.IsLetterOrDigit(c)) { Flush(); continue; }
            bool boundary = sb.Length > 0 && (
                (char.IsUpper(c) && char.IsLower(sb[sb.Length - 1]))
                || (char.IsUpper(c) && i + 1 < s.Length && char.IsLower(s[i + 1]) && char.IsUpper(sb[sb.Length - 1]))
                || (char.IsDigit(c) != char.IsDigit(sb[sb.Length - 1])));
            if (boundary) Flush();
            sb.Append(c);
            all.Append(char.ToLowerInvariant(c));
        }
        Flush();
        joined = all.ToString();
        return set;

        void Flush()
        {
            if (sb.Length > 0) set.Add(sb.ToString().ToLowerInvariant());
            sb.Clear();
        }
    }
}
