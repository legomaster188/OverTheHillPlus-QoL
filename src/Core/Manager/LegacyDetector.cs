using System;
using System.Collections.Generic;
using System.IO;

namespace OverTheHillPlus.Core.Manager;

/// <summary>
/// Spots the old single-DLL plugin (OverTheHillPlus.dll): with it loaded, every feature would run twice, so the mods
/// stay off and the MODS page asks the player to remove it. Pure logic with no BepInEx types, so it can be unit tested.
/// </summary>
internal static partial class LegacyDetector
{
    /// <summary>The old plugin's GUIDs (its config file is BepInEx/config/&lt;GUID&gt;.cfg). Never one of our com.oth.* ids.</summary>
    public static IReadOnlyList<string> Guids => _guids ??= BuildGuids();

    /// <summary>The old plugin's config file names, in the order the migration reads them.</summary>
    public static IReadOnlyList<string> OldConfigFiles => _files ??= BuildFiles();

    /// <summary>The old plugin's DLL name when the actual file is not known.</summary>
    public const string DefaultDll = "OverTheHillPlus.dll";

    private static List<string> _guids;
    private static List<string> _files;

    private static List<string> BuildGuids()
    {
        var g = new List<string> { "com.overthehillplus.qol" }; // the public QoL edition
        AddPrivate(g);
        return g;
    }

    private static List<string> BuildFiles()
    {
        var f = new List<string>();
        foreach (var g in Guids) f.Add(g + ".cfg");
        return f;
    }

    /// <summary>The private build's old GUID (in a folder the public export leaves out).</summary>
    static partial void AddPrivate(List<string> guids);

    /// <summary>True when one of the loaded (or about to load) plugin GUIDs is the old plugin's.</summary>
    public static bool ShouldBlock(IEnumerable<string> loadedGuids) => FindLegacy(loadedGuids) != null;

    /// <summary>The first old-plugin GUID in the list, or null.</summary>
    public static string FindLegacy(IEnumerable<string> loadedGuids)
    {
        if (loadedGuids == null) return null;
        foreach (var id in loadedGuids)
        {
            if (string.IsNullOrEmpty(id)) continue;
            foreach (var old in Guids)
                if (string.Equals(id.Trim(), old, StringComparison.OrdinalIgnoreCase)) return old;
        }
        return null;
    }

    /// <summary>The MODS page's line while the old plugin is loaded, naming its DLL file when known.</summary>
    public static string BlockText(string dllFileName)
    {
        string dll = string.IsNullOrWhiteSpace(dllFileName) ? DefaultDll : Path.GetFileName(dllFileName.Trim());
        return $"Remove {dll} from BepInEx/plugins to use these mods";
    }
}
