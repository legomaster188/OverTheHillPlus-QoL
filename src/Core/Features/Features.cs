using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx.Configuration;

namespace OverTheHillPlus.Features;

/// <summary>
/// A self-contained feature. Every non-abstract class implementing this in a mod's assembly is created once at
/// startup (<see cref="Registry.DiscoverAndRegister"/>, called by the mod's plugin) and its <see cref="Register"/>
/// called, so a feature lives in its own file and never needs to edit the shared menu, config or per-frame code.
///
/// Conventions:
/// - Features go in their own folder under Features/Public/.
/// - Settings: bind your own entries on your mod's config file in Register(), with your own section name.
/// - MODS page: add cards with <see cref="Registry.Section"/> (they show under the tab name on the mod's page).
/// - Per-frame work: <see cref="Registry.OnTick"/>; overlays (HUDs, markers): <see cref="Registry.OnGui"/>.
/// </summary>
internal interface IFeature
{
    void Register();
}

internal static class Registry
{
    public const int PlusMode = 0;

    internal sealed class SectionEntry
    {
        public int Mode;
        public string Tab;
        public string Group; // the old menu's Advanced tab (null = the default grouping of Tab); kept for the migration
        public int Order;
        public Action Draw;
        public string Owner;
        /// <summary>Id of the mod whose feature added this section (its MODS page shows it).</summary>
        public string ModId;
    }

    private sealed class Hook
    {
        /// <summary>This hook's crash-trail line, made on its first run.</summary>
        public string Trail;
        public string Owner;
        public string ModId;
        public Action Action;
        public bool Failed;
        /// <summary>This hook's timing (Logging/HookTiming), made on its first timed run.</summary>
        public Logging.HookStat Stat;
    }

    private static readonly List<Hook> Ticks = new();
    private static readonly List<Hook> Guis = new();
    internal static readonly List<SectionEntry> Sections = new();
    private static string _registering = "?";
    // The mod whose features are registering right now (DiscoverAndRegister); hooks added outside it get "?".
    private static string _registeringMod = "?";

    /// <summary>Runs every frame (Update). Exceptions are caught and the hook is disabled after logging once.
    /// <paramref name="modId"/> tags a hook added outside <see cref="DiscoverAndRegister"/> (the public API).</summary>
    public static void OnTick(Action action, string modId = null) => Ticks.Add(NewHook(action, modId));

    /// <summary>Runs in OnGUI every frame, before the MODS page, even when the page is closed (for overlays).</summary>
    public static void OnGui(Action action, string modId = null) => Guis.Add(NewHook(action, modId));

    private static Hook NewHook(Action action, string modId) => string.IsNullOrEmpty(modId)
        ? new Hook { Owner = _registering, ModId = _registeringMod, Action = action }
        : new Hook { Owner = action?.Method?.DeclaringType?.Name ?? modId, ModId = modId, Action = action };

    /// <summary>
    /// Adds a block of settings (usually Menu.Card calls) to the mod's MODS page. The page shows its sections in the
    /// Advanced fold, grouped under their <paramref name="tab"/> name as a heading; within a tab they draw by
    /// <paramref name="order"/>.
    /// <paramref name="advancedTab"/> is kept for the settings migration and not used by the page.
    /// The section is tagged with the mod whose features are registering (see <see cref="SectionsForMod"/>), or with
    /// <paramref name="modId"/> when given (sections a plugin adds outside its features, e.g. before
    /// <see cref="DiscoverAndRegister"/>).
    /// The everyday settings go in the mod's ModInfo.DrawSimple instead (UI/SimpleMenu rows).
    /// </summary>
    public static void Section(int mode, string tab, Action draw, int order = 0, string advancedTab = null, string modId = null)
    {
        Sections.Add(new SectionEntry
        {
            Mode = mode, Tab = tab, Group = advancedTab, Order = order, Draw = draw,
            Owner = _registering != "?" ? _registering : draw?.Method?.DeclaringType?.Name ?? "?",
            ModId = string.IsNullOrEmpty(modId) ? _registeringMod : modId,
        });
        SectionCache.Clear();
        ModSectionCache.Clear();
    }

    // The menu asks for a tab's sections on every OnGUI event (several per frame): cache the filtered, sorted
    // list instead of running LINQ and allocating each time.
    private static readonly Dictionary<(int, string), List<SectionEntry>> SectionCache = new();
    private static readonly Dictionary<string, List<SectionEntry>> ModSectionCache = new(StringComparer.OrdinalIgnoreCase);

    internal static IReadOnlyList<SectionEntry> SectionsFor(int mode, string tab)
    {
        if (!SectionCache.TryGetValue((mode, tab), out var list))
        {
            // OrderBy is stable: equal orders keep registration order.
            list = Sections.Where(s => s.Mode == mode && s.Tab == tab).OrderBy(s => s.Order).ToList();
            SectionCache[(mode, tab)] = list;
        }
        return list;
    }

    /// <summary>Every section one mod's features added, in registration order (its MODS page groups them by tab).</summary>
    internal static IReadOnlyList<SectionEntry> SectionsForMod(string modId)
    {
        modId ??= "";
        if (!ModSectionCache.TryGetValue(modId, out var list))
        {
            list = Sections.Where(s => string.Equals(s.ModId, modId, StringComparison.OrdinalIgnoreCase)).ToList();
            ModSectionCache[modId] = list;
        }
        return list;
    }

    /// <summary>
    /// Creates every <see cref="IFeature"/> in one mod's assembly and calls its Register, tagging each Section,
    /// OnTick and OnGui hook it adds with <paramref name="modId"/>. <paramref name="config"/> is the file the mod's
    /// features bind on (for <see cref="ConfigOwners"/>); null means the mod's own file, <c>ModManager.ConfigFor(modId)</c>.
    /// <paramref name="include"/>, when given, limits it to the feature types it accepts (core while the old plugin is loaded).
    /// </summary>
    internal static void DiscoverAndRegister(Assembly asm, string modId, ConfigFile config = null, Func<Type, bool> include = null)
    {
        if (asm == null || string.IsNullOrEmpty(modId)) return;
        config ??= Core.Manager.ModManager.ConfigFor(modId);
        Type[] all;
        try { all = asm.GetTypes(); }
        catch (ReflectionTypeLoadException e) { all = e.Types.Where(t => t != null).ToArray(); }
        var types = all
            .Where(t => typeof(IFeature).IsAssignableFrom(t) && !t.IsAbstract && !t.IsInterface && (include == null || include(t)))
            .OrderBy(t => t.FullName);
        _registeringMod = modId;
        try
        {
            foreach (var t in types)
            {
                _registering = t.Name;
                var before = SnapshotConfigKeys(config);
                try
                {
                    ((IFeature)Activator.CreateInstance(t)).Register();
                    Core.CorePlugin.Logger?.LogInfo($"Feature registered: {t.Name} ({modId})");
                    int bound = CountNewKeys(config, before);
                    DebugLog.Info(modId, $"feature registered: {t.Name}" + (bound > 0 ? $" ({bound} settings)" : ""));
                }
                catch (Exception e)
                {
                    DebugLog.Error(modId, e, "registering feature " + t.Name);
                    Core.CorePlugin.Logger?.LogError($"Feature {t.Name} ({modId}) failed to register: {e}");
                }
                RecordConfigOwner(config, before, t);
            }
        }
        finally
        {
            _registering = "?";
            _registeringMod = "?";
        }
    }

    /// <summary>
    /// Which feature bound each config entry, per config file (each mod has its own file, and two files may use the
    /// same section and key). Entries a mod binds outside its features are not listed.
    /// </summary>
    internal static readonly Dictionary<(ConfigFile file, ConfigDefinition key), Type> ConfigOwners = new();

    private static HashSet<ConfigDefinition> SnapshotConfigKeys(ConfigFile config)
    {
        try { return config == null ? null : new HashSet<ConfigDefinition>(config.Keys); }
        catch (Exception ex) { DebugLog.Error("core", ex, "Registry.SnapshotConfigKeys"); return null; }
    }

    private static int CountNewKeys(ConfigFile config, HashSet<ConfigDefinition> before)
    {
        if (before == null) return 0;
        try
        {
            int n = 0;
            foreach (var key in config.Keys)
                if (!before.Contains(key)) n++;
            return n;
        }
        catch (Exception ex) { DebugLog.Error("core", ex, "Registry.CountNewKeys"); return 0; }
    }

    private static void RecordConfigOwner(ConfigFile config, HashSet<ConfigDefinition> before, Type owner)
    {
        if (before == null) return;
        try
        {
            foreach (var key in config.Keys)
                if (!before.Contains(key) && !ConfigOwners.ContainsKey((config, key))) ConfigOwners[(config, key)] = owner;
        }
        catch (Exception ex) { DebugLog.Error("core", ex, "Registry.RecordConfigOwner"); /* bookkeeping only */ }
    }

    internal static void RunTicks() => Run(Ticks, "tick", false);

    internal static void RunGuis()
    {
        Run(Guis, "OnGUI", true);
        // Leave a clean state for the HUD and menu even if an overlay forgot to restore it or threw mid-draw.
        if (Guis.Count > 0) ResetGuiState();
    }

    /// <summary>
    /// Each overlay starts from an identity matrix and neutral colours, so one overlay that scales or tints (or
    /// throws halfway through) can't leak that into the next overlay, the HUD or the menu.
    /// </summary>
    private static void ResetGuiState()
    {
        UnityEngine.GUI.matrix = UnityEngine.Matrix4x4.identity;
        UnityEngine.GUI.color = UnityEngine.Color.white;
        UnityEngine.GUI.backgroundColor = UnityEngine.Color.white;
        UnityEngine.GUI.contentColor = UnityEngine.Color.white;
        UnityEngine.GUI.enabled = true;
    }

    /// <summary>
    /// Runs each hook: marks the crash trail, says whose hook is running (CrashTrail.RunningMod: hangs and the game's
    /// errors meanwhile are blamed on that mod) and, unless the debug level is Off, times it (slow hooks are noted).
    /// </summary>
    private static void Run(List<Hook> hooks, string what, bool gui)
    {
        bool timed = DebugLog.On;
        for (int i = 0; i < hooks.Count; i++)
        {
            var h = hooks[i];
            if (h.Failed) continue;
            if (gui && i > 0) ResetGuiState();
            CrashTrail.Mark(h.Trail ??= $"{what} {h.Owner} ({h.ModId})");
            string prev = CrashTrail.RunningMod;
            CrashTrail.RunningMod = h.ModId;
            long t0 = timed ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
            try { h.Action(); }
            catch (Exception e)
            {
                h.Failed = true;
                DebugLog.Info(h.ModId, $"{what} hook of {h.Owner} failed and is switched off for this session");
                Logging.HookTiming.Failed(h.Stat ??= Logging.HookTiming.NewStat(h.ModId, h.Trail), e, h.Trail);
                Core.CorePlugin.Logger?.LogError($"Feature {h.Owner} ({h.ModId}) {what} failed and was disabled: {e}");
            }
            finally
            {
                CrashTrail.RunningMod = prev;
                if (timed) Logging.HookTiming.Record(h.Stat ??= Logging.HookTiming.NewStat(h.ModId, h.Trail), System.Diagnostics.Stopwatch.GetTimestamp() - t0);
            }
        }
    }
}
