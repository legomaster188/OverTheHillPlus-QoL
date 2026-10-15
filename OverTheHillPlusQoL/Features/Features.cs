using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace OverTheHillPlus.Features;

/// <summary>
/// A self-contained feature. Every non-abstract class implementing this is created once at startup and its
/// <see cref="Register"/> called, so a feature lives in its own file and never needs to edit the shared
/// menu, config or per-frame code.
///
/// Conventions:
/// - Features go in their own folder under Features/Public/.
/// - Settings: bind your own entries on <c>ModConfig.File</c> in Register(), with your own section name.
/// - Menu: add cards to an existing tab or a new tab with <see cref="Registry.Section"/>; draw with <c>Ui</c>.
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
        public int Order;
        public Action Draw;
        public string Owner;
    }

    private sealed class Hook
    {
        public string Owner;
        public Action Action;
        public bool Failed;
    }

    private static readonly List<Hook> Ticks = new();
    private static readonly List<Hook> Guis = new();
    internal static readonly List<SectionEntry> Sections = new();
    private static string _registering = "?";

    /// <summary>Runs every frame (Update). Exceptions are caught and the hook is disabled after logging once.</summary>
    public static void OnTick(Action action) => Ticks.Add(new Hook { Owner = _registering, Action = action });

    /// <summary>Runs in OnGUI every frame, before the menu, even when the menu is closed (for overlays).</summary>
    public static void OnGui(Action action) => Guis.Add(new Hook { Owner = _registering, Action = action });

    /// <summary>
    /// Adds a block of menu content to a tab (built-in tabs: QoL, Lobby, Servers). A new tab name creates a new tab.
    /// Built-in content of an existing tab draws first, then sections by <paramref name="order"/>.
    /// </summary>
    public static void Section(int mode, string tab, Action draw, int order = 0)
    {
        Sections.Add(new SectionEntry { Mode = mode, Tab = tab, Order = order, Draw = draw, Owner = _registering });
        SectionCache.Clear();
    }

    // The menu asks for a tab's sections on every OnGUI event (several per frame): cache the filtered, sorted
    // list instead of running LINQ and allocating each time.
    private static readonly Dictionary<(int, string), List<SectionEntry>> SectionCache = new();

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

    internal static void DiscoverAndRegister()
    {
        var types = typeof(Registry).Assembly.GetTypes()
            .Where(t => typeof(IFeature).IsAssignableFrom(t) && !t.IsAbstract && !t.IsInterface)
            .OrderBy(t => t.FullName);
        foreach (var t in types)
        {
            _registering = t.Name;
            var before = SnapshotConfigKeys();
            try
            {
                ((IFeature)Activator.CreateInstance(t)).Register();
                Plugin.Logger.LogInfo($"Feature registered: {t.Name}");
            }
            catch (Exception e) { Plugin.Logger.LogError($"Feature {t.Name} failed to register: {e}"); }
            RecordConfigOwner(before, t);
        }
        _registering = "?";
    }

    /// <summary>Which feature bound each config entry (entries bound by ModConfig itself are not listed).</summary>
    internal static readonly Dictionary<BepInEx.Configuration.ConfigDefinition, Type> ConfigOwners = new();

    private static HashSet<BepInEx.Configuration.ConfigDefinition> SnapshotConfigKeys()
    {
        try { return ModConfig.File == null ? null : new HashSet<BepInEx.Configuration.ConfigDefinition>(ModConfig.File.Keys); }
        catch { return null; }
    }

    private static void RecordConfigOwner(HashSet<BepInEx.Configuration.ConfigDefinition> before, Type owner)
    {
        if (before == null) return;
        try
        {
            foreach (var key in ModConfig.File.Keys)
                if (!before.Contains(key) && !ConfigOwners.ContainsKey(key)) ConfigOwners[key] = owner;
        }
        catch { /* bookkeeping only */ }
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

    private static void Run(List<Hook> hooks, string what, bool gui)
    {
        for (int i = 0; i < hooks.Count; i++)
        {
            var h = hooks[i];
            if (h.Failed) continue;
            if (gui && i > 0) ResetGuiState();
            try { h.Action(); }
            catch (Exception e)
            {
                h.Failed = true;
                Plugin.Logger.LogError($"Feature {h.Owner} {what} failed and was disabled: {e}");
            }
        }
    }
}
