using System;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;

namespace OverTheHillPlus.ServerBrowser;

// After the namespace line on purpose: the game has its own global ModManager (mod.io).
using OverTheHillPlus.Core.Manager;

/// <summary>
/// Server Browser+: colour-coded rows with server age, smoother list refreshes, favourites, filters, the quick bar,
/// All regions, friends' servers, auto refresh, and rejoining the last server after a disconnect.
/// </summary>
[BepInPlugin(Guid, Name, Version)]
[BepInDependency(Core.CorePlugin.Guid)]
public class ServerBrowserPlugin : BasePlugin
{
    public const string Guid = "com.oth.serverbrowser";
    public const string Name = "Server Browser+";
    public const string Version = "1.2.0";

    /// <summary>Mod id: names the config file (oth.serverbrowser.cfg), the MODS page entry and the enabled switch.</summary>
    internal const string ModId = "serverbrowser";

    internal static ManualLogSource Logger;
    internal static Harmony Harmony;

    public override void Load()
    {
        Logger = Log;
        // Listed on the MODS page even when switched off, so it can be switched back on.
        ModManager.Register(new ModInfo
        {
            Id = ModId,
            Side = ModSide.Client,
            Name = Name,
            Description = "A better server browser: colour-coded rows with server age, favourites, filters, a quick bar, " +
                          "every region at once, friends' servers, auto refresh, and rejoining after a disconnect.",
            Version = Version,
            DrawSimple = ServerBrowserSimple.Draw,
        });
        if (!ModManager.IsEnabled(ModId))
        {
            Logger.LogInfo($"{Name} is switched off on the MODS page: nothing of it runs this session.");
            return;
        }
        try
        {
            ServerBrowserConfig.Bind(ModManager.ConfigFor(ModId)); // BepInEx/config/oth.serverbrowser.cfg
            FirstSeenTracker.Load();
            ServerBrowserFrame.Install();
            Features.Registry.DiscoverAndRegister(typeof(ServerBrowserPlugin).Assembly, ModId, ServerBrowserConfig.File);
            ServerBrowserConfig.File.Save(); // writes the defaults of every entry on first start
            Harmony = new Harmony(Guid);
            PatchReport.PatchAllSafely(Harmony, typeof(ServerBrowserPlugin).Assembly, ModId);
            Logger.LogInfo($"{Name} {Version} loaded.");
            DebugLog.Info(ModId, $"loaded {Version}");
        }
        catch (Exception e)
        {
            // The page says "Failed to start"; core and the other mods keep running.
            ModManager.MarkFailed(ModId, e.Message);
            Logger.LogError($"{Name} failed to start: {e}");
        }
    }
}
