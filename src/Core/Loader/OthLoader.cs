using System;
using System.Collections.Generic;
using System.Reflection;

namespace OverTheHillPlus.Loader;

/// <summary>
/// The one place that knows which mod loader runs the mods: paths, the loaded plugins, logs, the per-frame component,
/// IL2CPP type registration and native hooks. OthLoader.BepInEx.cs is compiled for BepInEx 6 (the default build),
/// OthLoader.Melon.cs for MelonLoader 0.7 (-p:Loader=Melon). Config files are the same in both
/// (<see cref="OverTheHillPlus.Config.ConfigFile"/>) and Harmony is HarmonyX in both.
/// </summary>
public static partial class OthLoader
{
    /// <summary>"BepInEx" or "MelonLoader".</summary>
    public static string Name => LoaderText.LoaderName;

    /// <summary>A loaded plugin (BepInEx) or melon (MelonLoader), by the GUID our plugin classes declare
    /// (com.oth.&lt;id&gt;; another creator's melon has com.melon.&lt;its name&gt;).</summary>
    public static IReadOnlyDictionary<string, LoadedPlugin> Plugins => LoadedPlugins();

    /// <summary>Mods whose dependencies were missing (BepInEx's list; empty under MelonLoader).</summary>
    public static IReadOnlyList<string> DependencyErrors => DependencyErrorList();

    /// <summary>Our plugin classes' GUID constant (public const string Guid), or null.</summary>
    internal static string GuidOf(Type pluginType)
    {
        try
        {
            var f = pluginType?.GetField("Guid", BindingFlags.Public | BindingFlags.Static);
            return f != null && f.IsLiteral ? f.GetRawConstantValue() as string : null;
        }
        catch { return null; }
    }
}

/// <summary>What core needs to know about a loaded plugin or melon.</summary>
public sealed class LoadedPlugin
{
    public string Guid { get; set; }
    public string Name { get; set; }
    public string Version { get; set; }
    /// <summary>Full path of its DLL, or null.</summary>
    public string Location { get; set; }
    /// <summary>The plugin object (BepInEx BasePlugin or MelonMod), or null before it loaded.</summary>
    public object Instance { get; set; }
    public Assembly Assembly => Instance?.GetType().Assembly;
}
