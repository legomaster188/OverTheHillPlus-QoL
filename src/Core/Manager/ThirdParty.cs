using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using OverTheHillPlus.Core.Install;

namespace OverTheHillPlus.Core.Manager;

/// <summary>What BepInEx (or <see cref="ThirdParty.ReadPluginFile"/>) says about one plugin.</summary>
internal sealed class PluginFacts
{
    public string Guid;
    public string Name;
    public string Version;
    /// <summary>Full path of the plugin's file (for a switched-off plugin, its .dll.disabled file).</summary>
    public string Location;
}

internal enum PluginState
{
    /// <summary>Running this session.</summary>
    Loaded,
    /// <summary>Its .dll is there but BepInEx didn't load it (a missing dependency, an error in its Load...).</summary>
    Failed,
    /// <summary>Switched off: renamed to .dll.disabled, which BepInEx doesn't load.</summary>
    Disabled,
}

/// <summary>One plugin of another creator, as the MODS page lists it.</summary>
internal sealed class ThirdPartyPlugin
{
    public string Guid;
    public string Name;
    public string Version;
    /// <summary>Path relative to BepInEx/plugins with '/' between folders, always the .dll name (also when it is
    /// switched off); null when the file is outside the plugins folder (then it can't be switched).</summary>
    public string File;
    public PluginState State;
    /// <summary>Why it didn't load (<see cref="PluginState.Failed"/>), or null.</summary>
    public string Problem;
    /// <summary>It was loaded before the Mod Manager this session, so it will be next time too: a rename queued for
    /// the next start happens after it is loaded again, and it stops only at the start after that.</summary>
    public bool LoadedBeforeCore;
}

/// <summary>
/// Plugins of other creators on the MODS page: every BepInEx plugin that isn't one of ours (com.oth.*, or the old
/// single-DLL plugin). Pure logic with no BepInEx or Unity types, so it can be unit tested; ThirdPartyScan feeds it
/// from BepInEx.
///
/// The on/off switch renames the plugin's file, because BepInEx only loads *.dll: off is &lt;file&gt;.dll.disabled.
/// A plugin loaded this session is in use, so switching it off is queued (PendingActions, applied by core's Load at the
/// next start). A plugin that isn't loaded (switched off, or it failed) is renamed right away: switching it on then
/// takes effect at the next start.
/// </summary>
internal static class ThirdParty
{
    // ---------------------------------------------------------------- who is ours

    /// <summary>Our own mods (com.oth.*) and the old single-DLL plugin: never listed as another creator's.</summary>
    public static bool IsOurGuid(string guid)
    {
        if (string.IsNullOrWhiteSpace(guid)) return false;
        guid = guid.Trim();
        return guid.StartsWith("com.oth.", StringComparison.OrdinalIgnoreCase) || LegacyDetector.FindLegacy(new[] { guid }) != null;
    }

    // ---------------------------------------------------------------- mods that use the public API

    private static readonly Dictionary<string, string> ApiMods = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Remembers a mod registered through OverTheHillPlus.Api.Mods.Register and the file of the assembly that
    /// registered it: it is listed with our mods (with its own page and ON/OFF switch), not in the rename list.</summary>
    public static void NoteApiMod(string id, string assemblyFile)
    {
        if (string.IsNullOrEmpty(id)) return;
        ApiMods[id] = assemblyFile ?? "";
    }

    /// <summary>True when the mod with this id registered through the public API (another creator's mod).</summary>
    public static bool IsApiMod(string id) => !string.IsNullOrEmpty(id) && ApiMods.ContainsKey(id);

    /// <summary>
    /// True when <paramref name="id"/> was registered through the public API from another assembly than
    /// <paramref name="assemblyFile"/>: two mods can't share an id (they would share a config file and a switch).
    /// </summary>
    public static bool ApiIdTakenByOther(string id, string assemblyFile) =>
        !string.IsNullOrEmpty(id) && ApiMods.TryGetValue(id, out var file) &&
        !string.Equals(Full(file ?? ""), Full(assemblyFile ?? ""), StringComparison.OrdinalIgnoreCase);

    /// <summary>The files of the assemblies that registered mods through the public API.</summary>
    public static IEnumerable<string> ApiModFiles => ApiMods.Values;

    // ---------------------------------------------------------------- the list

    /// <summary>
    /// The plugins of other creators, by name: <paramref name="loaded"/> (BepInEx's loaded plugins), the plugins of
    /// <paramref name="discovered"/> (every .dll BepInEx found at startup) that didn't load, with the matching line
    /// of <paramref name="dependencyErrors"/>, and <paramref name="disabledFiles"/> (*.dll.disabled, read with
    /// <see cref="ReadPluginFile"/>). Ours (<see cref="IsOurGuid"/>) and the assemblies in
    /// <paramref name="apiModFiles"/> are left out; a GUID is listed once. Every argument may be null.
    /// </summary>
    public static List<ThirdPartyPlugin> Build(string pluginsRoot, IEnumerable<PluginFacts> loaded, IEnumerable<PluginFacts> discovered,
        IEnumerable<PluginFacts> disabledFiles, IEnumerable<string> dependencyErrors, ICollection<string> loadedBeforeCore,
        IEnumerable<string> apiModFiles)
    {
        var list = new List<ThirdPartyPlugin>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var skipFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (apiModFiles != null)
            foreach (var f in apiModFiles)
                if (!string.IsNullOrEmpty(f)) skipFiles.Add(Full(f));
        var errors = dependencyErrors == null ? new List<string>() : new List<string>(dependencyErrors);

        bool Skip(PluginFacts p) =>
            p == null || IsOurGuid(p.Guid) || (!string.IsNullOrEmpty(p.Location) && skipFiles.Contains(Full(p.Location)));

        if (loaded != null)
            foreach (var p in loaded)
            {
                if (Skip(p) || string.IsNullOrEmpty(p.Guid) || !seen.Add(p.Guid)) continue;
                list.Add(Entry(pluginsRoot, p, PluginState.Loaded, null,
                    loadedBeforeCore != null && loadedBeforeCore.Contains(p.Guid)));
            }
        if (discovered != null)
            foreach (var p in discovered)
            {
                if (Skip(p) || string.IsNullOrEmpty(p.Guid) || !seen.Add(p.Guid)) continue;
                list.Add(Entry(pluginsRoot, p, PluginState.Failed, ProblemFor(errors, p), false));
            }
        if (disabledFiles != null)
            foreach (var p in disabledFiles)
            {
                if (Skip(p) || string.IsNullOrEmpty(p.Location)) continue;
                string key = string.IsNullOrEmpty(p.Guid) ? "file:" + p.Location : p.Guid;
                // An unreadable file of ours (OTH.*.dll.disabled) still isn't another creator's.
                if (string.IsNullOrEmpty(p.Guid) && Path.GetFileName(p.Location).StartsWith("OTH.", StringComparison.OrdinalIgnoreCase)) continue;
                if (!seen.Add(key)) continue;
                list.Add(Entry(pluginsRoot, p, PluginState.Disabled, null, false));
            }
        list.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return list;
    }

    private static string Full(string path)
    {
        try { return Path.GetFullPath(path); }
        catch (Exception ex) { DebugLog.Error("core", ex, "ThirdParty.Full"); return path; }
    }

    private static ThirdPartyPlugin Entry(string pluginsRoot, PluginFacts p, PluginState state, string problem, bool beforeCore)
    {
        string file = RelativeDll(pluginsRoot, p.Location);
        string name = string.IsNullOrWhiteSpace(p.Name) ? FileTitle(p.Location) : p.Name.Trim();
        return new ThirdPartyPlugin
        {
            Guid = p.Guid, Name = name, Version = p.Version, File = file, State = state, Problem = problem, LoadedBeforeCore = beforeCore,
        };
    }

    /// <summary>"SomeMod" for ".../SomeMod.dll" or ".../SomeMod.dll.disabled".</summary>
    private static string FileTitle(string location)
    {
        string name = Path.GetFileName(location ?? "") ?? "";
        if (name.EndsWith(PendingActions.DisabledSuffix, StringComparison.OrdinalIgnoreCase))
            name = name.Substring(0, name.Length - PendingActions.DisabledSuffix.Length);
        if (name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) name = name.Substring(0, name.Length - 4);
        return name.Length == 0 ? "?" : name;
    }

    /// <summary>The plugin's .dll relative to the plugins folder ('/' between folders), or null when it is outside.</summary>
    internal static string RelativeDll(string pluginsRoot, string location)
    {
        if (string.IsNullOrEmpty(pluginsRoot) || string.IsNullOrEmpty(location)) return null;
        try
        {
            string rel = Path.GetRelativePath(Path.GetFullPath(pluginsRoot), Path.GetFullPath(location)).Replace('\\', '/');
            if (rel.EndsWith(".dll" + PendingActions.DisabledSuffix, StringComparison.OrdinalIgnoreCase))
                rel = rel.Substring(0, rel.Length - PendingActions.DisabledSuffix.Length);
            return PendingActions.IsSafeRelativeDll(rel) && PendingActions.ResolveInside(pluginsRoot, rel) != null ? rel : null;
        }
        catch (Exception ex) { DebugLog.Error("core", ex, "ThirdParty.RelativeDll"); return null; }
    }

    /// <summary>BepInEx's line about the plugin (it names it "[Name Version]"), or where to look.</summary>
    internal static string ProblemFor(List<string> errors, PluginFacts p)
    {
        // BepInEx's own tag first, across every line; the GUID (which may also appear in another plugin's
        // "missing dependency" line) only when no line has the tag.
        string tag = $"[{p.Name} {p.Version}]";
        foreach (var e in errors)
            if (!string.IsNullOrEmpty(e) && e.Contains(tag)) return ModStatus.FirstLine(e);
        if (!string.IsNullOrEmpty(p.Guid))
            foreach (var e in errors)
                if (!string.IsNullOrEmpty(e) && e.Contains(p.Guid)) return ModStatus.FirstLine(e);
        return "It didn't load: see BepInEx/LogOutput.log for why.";
    }

    // ---------------------------------------------------------------- the switch

    /// <summary>The switch works when the plugin's file is inside the plugins folder.</summary>
    public static bool CanSwitch(ThirdPartyPlugin p) => p?.File != null;

    /// <summary>Whether it was on when the game started (what this session runs).</summary>
    public static bool StartedOn(ThirdPartyPlugin p) => p != null && p.State != PluginState.Disabled;

    /// <summary>Whether it is switched on now: a queued rename decides, else whether its .dll is there.</summary>
    public static bool IsOn(string pluginsRoot, string pluginDir, ThirdPartyPlugin p)
    {
        if (!CanSwitch(p)) return StartedOn(p);
        var queued = PendingActions.FindRename(pluginDir, p.File);
        if (queued != null) return queued.Kind == PendingKind.Enable;
        string dll = PendingActions.ResolveInside(pluginsRoot, p.File);
        return dll != null && File.Exists(dll);
    }

    /// <summary>
    /// Switches the plugin on or off; returns whether it is on now. A loaded plugin's file is in use: switching it
    /// off is queued for the next start (switching it on again just drops that). Any other plugin is renamed right
    /// away, or queued when that fails.
    /// </summary>
    public static bool SetOn(string pluginsRoot, string pluginDir, ThirdPartyPlugin p, bool on)
    {
        if (!CanSwitch(p)) return StartedOn(p);
        var queued = PendingActions.FindRename(pluginDir, p.File);
        var want = on ? PendingKind.Enable : PendingKind.Disable;
        if (queued != null)
        {
            // The queued rename is the opposite one: dropping it leaves the file as it is, which is what's wanted.
            if (queued.Kind != want) PendingActions.RemoveRename(pluginDir, p.File);
            return IsOn(pluginsRoot, pluginDir, p);
        }
        if (IsOn(pluginsRoot, pluginDir, p) == on) return on;
        bool inUse = p.State == PluginState.Loaded && !on;
        if (inUse || !PendingActions.TryRenameNow(pluginsRoot, want, p.File))
            PendingActions.Add(pluginDir, new PendingAction(want, p.File));
        return IsOn(pluginsRoot, pluginDir, p);
    }

    /// <summary>The switch's value on the row: ON / OFF, or when it takes effect.</summary>
    public static string SwitchText(string pluginsRoot, string pluginDir, ThirdPartyPlugin p)
    {
        bool on = IsOn(pluginsRoot, pluginDir, p);
        var queued = CanSwitch(p) ? PendingActions.FindRename(pluginDir, p.File) : null;
        return SwitchText(p, on, queued?.Kind == PendingKind.Enable);
    }

    /// <summary>
    /// ON / OFF when nothing changed since the start. Off from a plugin loaded before the Mod Manager takes two
    /// restarts (the rename runs after it already loaded again); on from a rename still queued too (BepInEx lists the
    /// plugins before the Mod Manager renames anything).
    /// </summary>
    internal static string SwitchText(ThirdPartyPlugin p, bool on, bool queuedEnable)
    {
        if (on == StartedOn(p)) return on ? "ON" : "OFF";
        if (!on) return p.State == PluginState.Loaded && p.LoadedBeforeCore ? "OFF after 2 restarts" : "OFF after restart";
        return queuedEnable ? "ON after 2 restarts" : "ON after restart";
    }

    // ---------------------------------------------------------------- reading a switched-off file

    /// <summary>
    /// The GUID, name and version in a plugin file's [BepInPlugin] attribute, read without loading it (BepInEx
    /// doesn't look at .dll.disabled files). Facts with only <see cref="PluginFacts.Location"/> when the file can't be
    /// read or has no plugin. Never throws.
    /// </summary>
    public static PluginFacts ReadPluginFile(string path)
    {
        var facts = new PluginFacts { Location = path };
        try
        {
            using var stream = System.IO.File.OpenRead(path);
            using var pe = new PEReader(stream);
            if (!pe.HasMetadata) return facts;
            var md = pe.GetMetadataReader();
            foreach (var th in md.TypeDefinitions)
            {
                foreach (var ah in md.GetTypeDefinition(th).GetCustomAttributes())
                {
                    var attr = md.GetCustomAttribute(ah);
                    if (!IsBepInPlugin(md, attr)) continue;
                    var blob = md.GetBlobReader(attr.Value);
                    if (blob.ReadUInt16() != 1) continue; // custom attribute prolog
                    facts.Guid = blob.ReadSerializedString();
                    facts.Name = blob.ReadSerializedString();
                    facts.Version = blob.ReadSerializedString();
                    return facts;
                }
            }
        }
        catch (Exception ex)
        {
            DebugLog.Error("core", ex, "ThirdParty.ReadPluginFile");
            // Not an assembly, or damaged: listed by its file name.
        }
        return facts;
    }

    private static bool IsBepInPlugin(MetadataReader md, CustomAttribute attr)
    {
        EntityHandle type;
        if (attr.Constructor.Kind == HandleKind.MemberReference)
            type = md.GetMemberReference((MemberReferenceHandle)attr.Constructor).Parent;
        else if (attr.Constructor.Kind == HandleKind.MethodDefinition)
            type = md.GetMethodDefinition((MethodDefinitionHandle)attr.Constructor).GetDeclaringType();
        else return false;
        if (type.Kind == HandleKind.TypeReference)
        {
            var tr = md.GetTypeReference((TypeReferenceHandle)type);
            return md.StringComparer.Equals(tr.Name, "BepInPlugin") && md.StringComparer.Equals(tr.Namespace, "BepInEx");
        }
        if (type.Kind == HandleKind.TypeDefinition)
        {
            var td = md.GetTypeDefinition((TypeDefinitionHandle)type);
            return md.StringComparer.Equals(td.Name, "BepInPlugin") && md.StringComparer.Equals(td.Namespace, "BepInEx");
        }
        return false;
    }
}
