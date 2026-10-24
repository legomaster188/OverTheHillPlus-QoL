using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using UnityEngine.InputSystem;

namespace OverTheHillPlus;

/// <summary>
/// Settings shared by several mods, bound on core's own file (BepInEx/config/oth.core.cfg) with the section and key
/// names they had in the old single config, so the settings migration can copy them over. Each mod keeps its own
/// settings in its own file; <see cref="Track"/> lists every file the shared tools (autosave, keybind editor) cover.
/// </summary>
internal static class CoreConfig
{
    /// <summary>Opens / closes MODS ([Core] OpenKey; None turns the shortcut off, the ESC menu entry stays).</summary>
    public static ConfigEntry<Key> OpenKey;
    /// <summary>Whether a mod's page shows its Advanced fold open.</summary>
    public static ConfigEntry<bool> AdvancedMenu;
    /// <summary>MODS opens inside the game's MOD BROWSER with the game's settings widgets ([Core] GameMenus); off: in
    /// the full view window.</summary>
    public static ConfigEntry<bool> GameMenus;

    /// <summary>Core's own config file.</summary>
    public static ConfigFile File;

    private static readonly List<ConfigFile> TrackedFiles = new();

    /// <summary>Every config file the shared tools cover: core's, then each mod's in the order they were tracked.</summary>
    public static IReadOnlyList<ConfigFile> Files => TrackedFiles;

    /// <summary>Raised once for each file added with <see cref="Track"/>.</summary>
    public static event Action<ConfigFile> FileTracked;

    public static void Bind(ConfigFile cfg)
    {
        File = cfg;
        // Sliders change values every frame; ConfigAutosave writes the file once things are quiet instead.
        cfg.SaveOnConfigSet = false;
        OpenKey = cfg.Bind("Core", "OpenKey", Key.F7, "Opens/closes MODS (also in the ESC menu and in the main menu's MOD BROWSER). None turns the shortcut off.");
        AdvancedMenu = cfg.Bind("General", "AdvancedMenu", false,
            "MODS page: show each mod's Advanced settings unfolded. Off: a mod's page shows the settings most players use, with Advanced folded under them.");
        GameMenus = cfg.Bind("Core", "GameMenus", true,
            "MODS opens inside the game's MOD BROWSER (INSTALLED MODS / AVAILABLE MODS), with the game's own settings widgets. " +
            "Off: MODS opens in its own window (the full view), as before.");
        // [Debug]: the debug log, the hang watchdog and hook timing (Logging/DebugConfig).
        Logging.DebugConfig.Bind(cfg);
        Track(cfg);
        cfg.Save();
    }

    /// <summary>
    /// A setting of any mod by section and key, from the first file in <see cref="Files"/> that has it, or null when
    /// no loaded mod binds it (that mod is switched off or not installed). Lets a mod read another mod's setting
    /// without referencing that mod.
    /// </summary>
    public static ConfigEntryBase Find(string section, string key)
    {
        if (section == null || key == null) return null;
        try
        {
            var def = new ConfigDefinition(section, key);
            foreach (var f in TrackedFiles)
                if (f != null && f.ContainsKey(def)) return f[def];
        }
        catch (Exception ex) { DebugLog.Error("core", ex, "CoreConfig.Find"); /* a file being rebuilt: treat as missing */ }
        return null;
    }

    /// <summary>
    /// Adds a mod's config file to the shared tools: it is saved by ConfigAutosave (so set SaveOnConfigSet = false
    /// on it) and its key settings show in the keybind editor. Tracking the same file twice does nothing.
    /// </summary>
    public static void Track(ConfigFile cfg)
    {
        if (cfg == null || TrackedFiles.Contains(cfg)) return;
        TrackedFiles.Add(cfg);
        FileTracked?.Invoke(cfg);
    }
}
