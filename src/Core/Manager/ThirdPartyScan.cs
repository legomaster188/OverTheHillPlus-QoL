using System;
using System.Collections.Generic;
using System.IO;
using BepInEx;
using BepInEx.Unity.IL2CPP;

namespace OverTheHillPlus.Core.Manager;

/// <summary>
/// Feeds <see cref="ThirdParty"/> from BepInEx: what core's Load saw (the plugins loaded before it and every plugin
/// BepInEx found in BepInEx/plugins), then, when the MODS page first shows the list, the loaded plugins, BepInEx's
/// dependency errors and the switched-off *.dll.disabled files.
/// </summary>
internal static class ThirdPartyScan
{
    private static readonly HashSet<string> BeforeCore = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> OurIds = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> ListedIds = new(StringComparer.OrdinalIgnoreCase);
    private static readonly List<PluginFacts> Discovered = new();
    private static List<ThirdPartyPlugin> _list;

    /// <summary>BepInEx/plugins.</summary>
    internal static string PluginsRoot => Paths.PluginPath;

    /// <summary>Where the queue lives (BepInEx/plugins/OverTheHillMods).</summary>
    internal static string PluginDir => CorePlugin.PluginDir;

    /// <summary>Called by core's Load with the plugins loaded so far and every plugin found in BepInEx/plugins.</summary>
    internal static void NoteStartup(IEnumerable<PluginInfo> loadedSoFar, IEnumerable<PluginInfo> found)
    {
        try
        {
            if (loadedSoFar != null)
                foreach (var p in loadedSoFar)
                {
                    string guid = p?.Metadata?.GUID;
                    if (guid != null && !string.Equals(guid, CorePlugin.Guid, StringComparison.OrdinalIgnoreCase)) BeforeCore.Add(guid);
                }
            if (found != null)
                foreach (var p in found)
                {
                    var f = Facts(p);
                    if (f == null) continue;
                    Discovered.Add(f);
                    if (f.Guid.StartsWith("com.oth.", StringComparison.OrdinalIgnoreCase)) OurIds.Add(f.Guid.Substring("com.oth.".Length));
                }
        }
        catch (Exception e) { DebugLog.Error("core", e, "ThirdPartyScan.NoteStartup"); CorePlugin.Logger?.LogWarning("[Core] other creators' plugins: " + e.Message); }
    }

    /// <summary>The ids in our release's mod list, once the AVAILABLE tab fetched it (not yet when plugins load).</summary>
    internal static void NoteListedIds(IEnumerable<string> ids)
    {
        if (ids == null) return;
        foreach (var id in ids)
            if (!string.IsNullOrEmpty(id)) ListedIds.Add(id);
    }

    /// <summary>
    /// True when <paramref name="id"/> belongs to one of our mods: a com.oth.&lt;id&gt; plugin is installed, a mod
    /// already registered with it without the public API, or our release lists it (unless another creator's mod
    /// already registered it before that list arrived). The fixed list of our ids is ApiRules.ReservedIds.
    /// </summary>
    internal static bool IsOurModId(string id)
    {
        if (string.IsNullOrEmpty(id)) return false;
        if (OurIds.Contains(id)) return true;
        if (ThirdParty.IsApiMod(id)) return false;
        return ModManager.Find(id) != null || ListedIds.Contains(id);
    }

    /// <summary>The list for the MODS page, built the first time it is asked for (every plugin has loaded by then).</summary>
    internal static List<ThirdPartyPlugin> List => _list ??= Build();

    private static List<ThirdPartyPlugin> Build()
    {
        var loaded = new List<PluginFacts>();
        var errors = new List<string>();
        var disabled = new List<PluginFacts>();
        try
        {
            var chain = IL2CPPChainloader.Instance;
            foreach (var kv in chain.Plugins)
            {
                var f = Facts(kv.Value);
                if (f != null) loaded.Add(f);
            }
            if (chain.DependencyErrors != null) errors.AddRange(chain.DependencyErrors);
        }
        catch (Exception e) { DebugLog.Error("core", e, "ThirdPartyScan.Build"); CorePlugin.Logger?.LogWarning("[Core] could not list the loaded plugins: " + e.Message); }
        try
        {
            foreach (var file in Directory.EnumerateFiles(PluginsRoot, "*.dll" + Install.PendingActions.DisabledSuffix, SearchOption.AllDirectories))
                disabled.Add(ThirdParty.ReadPluginFile(file));
        }
        catch (Exception e) { DebugLog.Error("core", e, "ThirdPartyScan.Build"); CorePlugin.Logger?.LogWarning("[Core] could not look for switched-off plugins: " + e.Message); }
        try
        {
            return ThirdParty.Build(PluginsRoot, loaded, Discovered, disabled, errors, BeforeCore, ThirdParty.ApiModFiles);
        }
        catch (Exception e)
        {
            DebugLog.Error("core", e, "ThirdPartyScan.Build");
            CorePlugin.Logger?.LogWarning("[Core] could not list other creators' plugins: " + e.Message);
            return new List<ThirdPartyPlugin>();
        }
    }

    private static PluginFacts Facts(PluginInfo p)
    {
        var meta = p?.Metadata;
        if (meta == null || string.IsNullOrEmpty(meta.GUID)) return null;
        return new PluginFacts { Guid = meta.GUID, Name = meta.Name, Version = VersionText(meta), Location = p.Location };
    }

    // BepInPlugin.Version is a SemanticVersioning type, which core doesn't reference: read it as an object.
    private static readonly System.Reflection.PropertyInfo VersionProp = typeof(BepInPlugin).GetProperty("Version");

    private static string VersionText(BepInPlugin meta)
    {
        try { return VersionProp?.GetValue(meta)?.ToString(); }
        catch (Exception ex) { DebugLog.Error("core", ex, "ThirdPartyScan.VersionText"); return null; }
    }
}
