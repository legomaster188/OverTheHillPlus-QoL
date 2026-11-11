#if !MELON
using BepInEx;
using BepInEx.Unity.IL2CPP;
#endif
using System;
using System.Collections.Generic;
using System.IO;
using OverTheHillPlus.Loader;
using HarmonyLib;

namespace OverTheHillPlus.Core;

// After the namespace line on purpose: the game has its own global ModManager (mod.io), which a using placed above
// the namespace would lose to. Other namespaces must do the same or write Core.Manager.ModManager.
using OverTheHillPlus.Core.Manager;

#if MELON
public class CorePlugin : OverTheHillPlus.Loader.OthMod
#else
[BepInPlugin(Guid, Name, Version)]
public class CorePlugin : BasePlugin
#endif
{
    public const string Guid = "com.oth.core";
    public const string Name = "OTH Mod Manager";
    public const string Version = "1.4.3";

    internal static ModLog Logger;
    internal static Harmony Harmony;

    /// <summary>Where the mods live and the installer puts downloads: BepInEx/plugins/OverTheHillMods (MelonLoader: the
    /// game's Mods folder).</summary>
    internal static string PluginDir => OthLoader.ModsDir;

    public override void Load()
    {
        Logger = Log;
        // Values the settings files can't read (and settings handlers that throw) go to core's log.
        OverTheHillPlus.Config.ConfigFile.Warn = m => Logger?.LogWarning("[Config] " + m);
        // Before anything else: report how the last session ended if it crashed, and start this session's trail.
        CrashTrail.Start(m => Log.LogWarning(m));
        ErrorTap.Start(Log);
        // Before any patch or menu touches the game's UI classes: see the class for why.
        GameTypePreload.Run();
        // MelonLoader has loaded every mod's DLL by now: their models are read next to the mods' own loading.
        ReadModelsAhead();
        global::OverTheHillPlus.Shared.RaceEvents.LogWarning = m => Logger?.LogWarning(m);
        Api.Career.Log = m => Logger?.LogWarning(m);
        Api.WorldPlaces.Warn = m => Logger?.LogWarning("[WorldPlaces] " + m);
        // First of all: updates and uninstalls queued last session, while the other mods' DLLs aren't loaded yet
        // (they depend on core, so BepInEx loads them after this). Before the migration, so a freshly installed DLL
        // counts as installed.
        // (MelonLoader: nothing is ever queued, the AVAILABLE tab installs nothing there.)
        if (OthLoader.CanInstall)
        {
            try { Install.PendingActions.ApplyAll(PluginDir); }
            catch (System.Exception e) { Log.LogError("[Core] pending mod changes: " + e); }
        }
        // Before anything binds a config file: the old plugin's settings move into the mods' own files.
        GuardAndMigrate();
        ModManager.Register(new ModInfo
        {
            Id = UI.ModPageView.CoreId,
            Side = ModSide.Client,
            Name = "Mod Manager",
            Description = "Settings > MODS (every mod's settings), the mod network, the name filter and the look of these pages. Always on.",
            Version = Version,
        });
        CoreConfig.Bind(ModManager.ConfigFor("core")); // BepInEx/config/oth.core.cfg
        // The debug log (BepInEx/oth-debug, [Debug] Level) and the errors every plugin writes to the BepInEx log.
        DebugLog.Start(Logging.DebugConfig.Level, Logging.DebugConfig.KeepFiles);
        Logging.ErrorListener.Install();
        LogPendingReport();
        Logging.ConfigLog.Install();
        Logging.Lifecycle.Install();
        // The per-frame driver first: the MODS page (and F7) work even if a feature below fails.
        AddComponent<CoreBehaviour>();
        try { UI.AvailableTab.Register(PluginDir); } // MODS page: AVAILABLE tab, install / update / uninstall
        catch (System.Exception e) { Logger.LogError("[Core] AVAILABLE tab: " + e); }
        try
        {
            Harmony = new Harmony(Guid);
            if (ModManager.Blocked)
            {
                // The old plugin runs its own copy of these features (and the same mod-network channel): only what
                // the MODS page needs runs next to it, so it can say why the mods are off.
                Features.Registry.DiscoverAndRegister(typeof(CorePlugin).Assembly, "core", CoreConfig.File, PageOnlyFeatures.Contains);
                PatchReport.PatchAllSafely(Harmony, typeof(CorePlugin).Assembly, "core", PageOnlyPatches.Contains);
            }
            else
            {
                // Core's own features: the mod network, the name filter, menu style and UX, config autosave.
                Features.Registry.DiscoverAndRegister(typeof(CorePlugin).Assembly, "core", CoreConfig.File);
                PatchReport.PatchAllSafely(Harmony, typeof(CorePlugin).Assembly, "core");
                // The join gate for mods' runtime network prefabs (patched by hand: mods check it before declaring).
                Shared.NetPrefabRegistry.InstallPatches(Harmony);
                // A car coming or going moves the stamp the mods' world searches wait for.
                WorldScan.InstallPatches(Harmony);
            }
        }
        catch (System.Exception e)
        {
            ModManager.MarkFailed("core", e.Message);
            Logger.LogError("[Core] " + e);
        }
        if (ModManager.Blocked)
            Logger.LogInfo("[Core] loaded (MODS page only, the old plugin is loaded). Settings > MODS opens it.");
        else
            Logger.LogInfo("[Core] loaded. The mods' settings are in the game's Settings menu, under MODS" +
                           (CoreConfig.OpenKey.Value != UnityEngine.InputSystem.Key.None ? $" (and on {CoreConfig.OpenKey.Value}, [Debug] ModsKey)." : "."));
    }

    private static void ReadModelsAhead()
    {
        try
        {
            var mods = new List<System.Reflection.Assembly>();
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
                if (!a.IsDynamic && (a.GetName().Name ?? "").StartsWith("OTH.", StringComparison.Ordinal)) mods.Add(a);
            OverTheHillPlus.Cars.AssetPrefetch.ReadModels(mods);
        }
        catch (Exception e) { Logger.LogWarning("[Core] reading the models ahead: " + e.Message); }
    }

    /// <summary>What the start-up pass over the queued installs, updates and removals did (it ran before the debug log started).</summary>
    private static void LogPendingReport()
    {
        try
        {
            var r = Install.PendingActions.LastReport;
            if (r == null) return;
            if (r.Applied.Count + r.StillQueued.Count + r.Held.Count + r.Errors.Count == 0 && r.Quarantined == null) return;
            DebugLog.Info("core", $"queued mod changes at start: applied [{string.Join(", ", r.Applied)}], swapped [{string.Join(", ", r.Swapped)}], " +
                                  $"still locked [{string.Join(", ", r.StillQueued)}], held [{string.Join(", ", r.Held)}]");
            foreach (var err in r.Errors) DebugLog.Error("core", "queued mod change dropped: " + err);
            if (r.Quarantined != null) DebugLog.Error("core", "the queue file was damaged and moved to " + r.Quarantined);
        }
        catch (System.Exception e) { DebugLog.Error("core", e, "CorePlugin.LogPendingReport"); }
    }

    /// <summary>
    /// Core's features that run while <see cref="ModManager.Blocked"/>: the MODS page's own look and settings (menu
    /// style, menu UX and keybind editor) and saving its config. Not the mod network, the name filter or anything else
    /// the old plugin also does.
    /// </summary>
    private static readonly HashSet<Type> PageOnlyFeatures = new()
    {
        typeof(Features.Public.MenuStyle.MenuStyleFeature),
        typeof(Features.Public.MenuUxFeature),
        typeof(Features.Public.Stability.ConfigAutosave),
        typeof(Features.Public.DebugCard.DebugCardFeature),
    };

    /// <summary>Core's patches that run while <see cref="ModManager.Blocked"/>: the Settings menu's MODS button and the
    /// MODS screen (and Enter / clicks on our rows). Not the host notice, roster, achievement guard, server browser or mod
    /// network patches.</summary>
    private static readonly HashSet<Type> PageOnlyPatches = new()
    {
        typeof(UI.GameMenu.SettingsMenuModsButtonPatch),
        typeof(UI.GameMenu.ModsScreenOpenPatch),
        typeof(UI.GameMenu.ModsScreenClosePatch),
        typeof(UI.GameMenu.ModsScreenSavePatch),
        typeof(UI.GameMenu.ModsScreenLanguagePatch),
        typeof(UI.GameMenu.LiverySearchSubmitPatch),
        typeof(UI.GameMenu.JoinByCodeSubmitPatch),
        typeof(UI.GameMenu.ServerSearchSubmitPatch),
        typeof(UI.GameMenu.FeedbackSubmitPatch),
        typeof(UI.GameMenu.GameRowSubmitPatch),
        typeof(UI.GameMenu.GameRowClickPatch),
        typeof(UI.GameMenu.FunTextUpdateStringPatch),
        typeof(UI.GameMenu.FunTextEnableGuardPatch),
        typeof(UI.GameMenu.FunTextDisableGuardPatch),
        typeof(UI.GameMenu.FunTextDestroyGuardPatch),
    };

    /// <summary>
    /// Blocks every mod while the old OverTheHillPlus plugin is loaded (see LegacyDetector), and migrates its config
    /// file (see ConfigMigration): fully once it's gone, only core's part while it's still there and using that file.
    /// </summary>
#if MELON
    private static void GuardAndMigrate()
    {
        // The old plugin is a BepInEx plugin, so it never runs next to the MelonLoader mods: nothing to block. Its
        // settings file moves as under BepInEx if it is put into UserData/OverTheHill.
        // The BepInEx build's data (car sidecars, stash, stats, ghosts) is copied over once, before anything reads it.
        try { BepInExImport.Run(Path.GetFullPath(Path.Combine(OthLoader.ConfigDir, "..", "..", "BepInEx", "config")), OthLoader.ConfigDir, m => Logger.LogInfo(m)); }
        catch (Exception e) { Logger.LogError("[Core] could not import the BepInEx data: " + e); }
        var guids = new List<string>(OthLoader.Plugins.Keys);
        try { ConfigMigration.Run(OthLoader.ConfigDir, id => IsInstalled(id, guids)); }
        catch (Exception e) { Logger.LogError("[Core] could not move the settings of the old plugin: " + e); }
    }
#else
    private static void GuardAndMigrate()
    {
        var plugins = KnownPlugins(out bool scanned);
        PluginInfo legacy = null;
        var guids = new List<string>();
        foreach (var p in plugins)
        {
            string guid = p?.Metadata?.GUID;
            if (guid == null) continue;
            guids.Add(guid);
            if (legacy == null && LegacyDetector.ShouldBlock(new[] { guid })) legacy = p;
        }
        if (legacy != null)
        {
            string dll = string.IsNullOrEmpty(legacy.Location) ? null : Path.GetFileName(legacy.Location);
            ModManager.Block(dll);
            Logger.LogWarning($"[Core] the old plugin {legacy.Metadata.GUID} ({dll ?? LegacyDetector.DefaultDll}) is loaded: " +
                              "every mod stays off this session. " + ModManager.BlockedText + ".");
        }
        if (!scanned && !ModManager.Blocked)
            Logger.LogWarning("[Core] could not check BepInEx/plugins for the old plugin: its settings move fully once " +
                              "that check works; for now only the MODS page's own settings are copied.");
        try
        {
            // Only a full scan proves the old plugin isn't about to load (it may come after core): until then, keep its
            // file where it is and copy only core's part, which core binds now.
            if (ModManager.Blocked || !scanned) ConfigMigration.RunFor(OthLoader.ConfigDir, "core");
            else ConfigMigration.Run(OthLoader.ConfigDir, id => IsInstalled(id, guids));
        }
        catch (Exception e)
        {
            Logger.LogError("[Core] could not move the settings of the old plugin: " + e);
        }
    }

    /// <summary>
    /// The plugins loaded so far plus every plugin in BepInEx/plugins (the old plugin may come after core in the load
    /// order, and a mod's DLL being there is what "installed" means for the migration). <paramref name="scanned"/> is
    /// false when the folder could not be scanned, so the list may miss the old plugin.
    /// </summary>
    private static List<PluginInfo> KnownPlugins(out bool scanned)
    {
        scanned = false;
        var list = new List<PluginInfo>();
        var chain = IL2CPPChainloader.Instance;
        try
        {
            foreach (var kv in chain.Plugins) list.Add(kv.Value);
        }
        catch (Exception e) { DebugLog.Error("core", e, "CorePlugin.KnownPlugins"); Logger.LogWarning("[Core] could not list the loaded plugins: " + e.Message); }
        int loadedSoFar = list.Count;
        try
        {
            // BepInEx's own scan of the folder (protected, so through reflection); no cache name: its cache stays as is.
            var discover = AccessTools.Method(typeof(BepInEx.Bootstrap.BaseChainloader<BasePlugin>), "DiscoverPluginsFrom");
            if (discover?.Invoke(chain, new object[] { OthLoader.PluginsDir, null }) is IEnumerable<PluginInfo> found)
            {
                list.AddRange(found);
                scanned = true;
            }
        }
        catch (Exception e) { DebugLog.Error("core", e, "CorePlugin.KnownPlugins"); Logger.LogWarning("[Core] could not list the plugins folder: " + e.Message); }
        // The MODS page's list of other creators' plugins: which loaded before core, and which were found.
        ThirdPartyScan.NoteStartup(list.GetRange(0, loadedSoFar).ConvertAll(OthLoader.From),
                                   list.GetRange(loadedSoFar, list.Count - loadedSoFar).ConvertAll(OthLoader.From));
        return list;
    }
#endif

    /// <summary>A mod is installed when its plugin (com.oth.&lt;id&gt;) or its DLL (OTH.&lt;id&gt;.dll) is in BepInEx/plugins.</summary>
    private static bool IsInstalled(string id, List<string> guids)
    {
        if (string.Equals(id, UI.ModPageView.CoreId, StringComparison.OrdinalIgnoreCase)) return true;
        string guid = "com.oth." + id;
        foreach (var g in guids)
            if (string.Equals(g, guid, StringComparison.OrdinalIgnoreCase)) return true;
        try
        {
            return Directory.EnumerateFiles(OthLoader.PluginsDir, $"OTH.{id}.dll", SearchOption.AllDirectories).GetEnumerator().MoveNext();
        }
        catch (Exception ex) { DebugLog.Error("core", ex, "CorePlugin.IsInstalled"); return false; }
    }
}
