using System;
using System.Collections.Generic;

namespace OverTheHillPlus.Core.Manager;

/// <summary>How finished a mod is, as the MODS page, the AVAILABLE list and the installer show it.</summary>
public enum MaturityLevel
{
    /// <summary>Not known (another creator's mod that doesn't say): no label.</summary>
    Unknown,
    /// <summary>Used in game for a while and works.</summary>
    Stable,
    /// <summary>New, or not tested in game much yet.</summary>
    Beta,
    /// <summary>Works badly; kept for those who want it anyway.</summary>
    Rough,
}

/// <summary>
/// The maturity of every mod of ours: a mod's own <see cref="ModInfo.Maturity"/> when it names one, else this table
/// (Stable and Rough are listed; every other mod of ours is Beta). Pure logic, unit tested; tools/release.py reads the
/// three id lists and <see cref="AiNote"/> from this file for mods.json, so keep them plain string arrays.
/// </summary>
public static class ModMaturity
{
    /// <summary>The note shown with the AI-driver mods.</summary>
    public const string AiNote = "AI drivers are rough: expect them to get stuck and drive badly.";

    /// <summary>The note shown with other Rough mods.</summary>
    public const string RoughNote = "Rough: expect it to misbehave.";

    /// <summary>The older mods, used in game for a while (the Mod Manager is "core").</summary>
    public static readonly string[] StableIds =
    {
        "core", "drivingqol", "graphics", "photo", "biglobbies", "chat", "serverbrowser", "servercreator", "serverutils",
    };

    /// <summary>Mods whose cars are driven by our AI (AI traffic, the AI convoy): Rough, with <see cref="AiNote"/>.</summary>
    public static readonly string[] AiDriverIds = { };

    /// <summary>Other Rough mods (none yet), with <see cref="RoughNote"/>.</summary>
    public static readonly string[] RoughIds = { };

    private static readonly HashSet<string> Stable = new(StableIds, StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> Ai = new(AiDriverIds, StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> RoughSet = new(RoughIds, StringComparer.OrdinalIgnoreCase);

    /// <summary>"Stable", "Beta" or "Rough" (any case, trimmed) → the level; anything else → Unknown.</summary>
    public static MaturityLevel Parse(string text)
    {
        switch ((text ?? "").Trim().ToLowerInvariant())
        {
            case "stable": return MaturityLevel.Stable;
            case "beta": return MaturityLevel.Beta;
            case "rough": return MaturityLevel.Rough;
            default: return MaturityLevel.Unknown;
        }
    }

    /// <summary>
    /// A mod's level: <paramref name="declared"/> (its ModInfo.Maturity, or the release list's field) when it names one;
    /// else, for a mod of ours (<paramref name="ours"/>), the table (Beta when it isn't listed); else Unknown.
    /// </summary>
    public static MaturityLevel Of(string id, string declared, bool ours)
    {
        var own = Parse(declared);
        if (own != MaturityLevel.Unknown) return own;
        if (!ours) return MaturityLevel.Unknown;
        id ??= "";
        if (Ai.Contains(id) || RoughSet.Contains(id)) return MaturityLevel.Rough;
        if (Stable.Contains(id)) return MaturityLevel.Stable;
        return MaturityLevel.Beta;
    }

    /// <summary>A mod's level from its info (other creators' API mods only have the one they declare).</summary>
    public static MaturityLevel Of(ModInfo m, bool ours) => m == null ? MaturityLevel.Unknown : Of(m.Id, m.Maturity, ours);

    /// <summary>"Stable", "Beta", "Rough", or empty.</summary>
    public static string Label(MaturityLevel level) => level switch
    {
        MaturityLevel.Stable => "Stable",
        MaturityLevel.Beta => "Beta",
        MaturityLevel.Rough => "Rough",
        _ => "",
    };

    /// <summary>The label in capitals, for the cards' badges ("BETA"), or empty.</summary>
    public static string Badge(MaturityLevel level) => Label(level).ToUpperInvariant();

    /// <summary>The one-line note for a Rough mod (the AI one for the AI-driver mods), else null.</summary>
    public static string Note(string id, MaturityLevel level)
    {
        if (level != MaturityLevel.Rough) return null;
        return Ai.Contains(id ?? "") ? AiNote : RoughNote;
    }

    /// <summary>A mod's description with its Rough note in front (so a list that shows only the description still
    /// warns); other levels leave it as it is.</summary>
    public static string Describe(string description, string id, MaturityLevel level)
    {
        string note = Note(id, level);
        if (note == null) return description ?? "";
        if (string.IsNullOrEmpty(description)) return note;
        return description.StartsWith(note, StringComparison.Ordinal) ? description : note + " " + description;
    }

    /// <summary>"v1.2.0  ·  Beta" (either part may be missing).</summary>
    public static string VersionAndLabel(string version, MaturityLevel level)
    {
        string v = string.IsNullOrEmpty(version) ? "" : version.StartsWith("v", StringComparison.OrdinalIgnoreCase) ? version : "v" + version;
        string l = Label(level);
        if (v.Length == 0) return l;
        return l.Length == 0 ? v : v + "  ·  " + l;
    }
}
