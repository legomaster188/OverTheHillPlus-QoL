using System;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;

namespace OverTheHillPlus.ServerUtils;

// After the namespace line on purpose: the game has its own global ModManager (mod.io).
using OverTheHillPlus.Core.Manager;

/// <summary>
/// Server Utils: tools for hosting and playing online. The host performance governor, the ping monitor and high-ping
/// rule, moderation (join rules, AFK rule), autosave, smooth joins (car preloading), the host network tweaks, and
/// sharing settings as a code.
/// </summary>
[BepInPlugin(Guid, Name, Version)]
[BepInDependency(Core.CorePlugin.Guid)]
public class ServerUtilsPlugin : BasePlugin
{
    public const string Guid = "com.oth.serverutils";
    public const string Name = "Server Utils";
    public const string Version = "1.2.0";

    /// <summary>Mod id: names the config file (oth.serverutils.cfg), the MODS page entry and the enabled switch.</summary>
    internal const string ModId = "serverutils";

    internal static ManualLogSource Logger;
    internal static Harmony Harmony;

    public override void Load()
    {
        Logger = Log;
        // Listed on the MODS page even when switched off, so it can be switched back on.
        ModManager.Register(new ModInfo
        {
            Id = ModId,
            Side = ModSide.Host,
            Name = Name,
            Description = "Hosting and online tools: keeps a busy server smooth, shows everyone's ping, join and AFK rules, " +
                          "autosave, faster joins, network tweaks, and sharing your settings as a code.",
            Version = Version,
            DrawSimple = ServerUtilsSimple.Draw,
        });
        if (!ModManager.IsEnabled(ModId))
        {
            Logger.LogInfo($"{Name} is switched off on the MODS page: nothing of it runs this session.");
            return;
        }
        try
        {
            ServerUtilsConfig.Bind(ModManager.ConfigFor(ModId)); // BepInEx/config/oth.serverutils.cfg
            ServerUtilsFrame.Install();
            Features.Registry.DiscoverAndRegister(typeof(ServerUtilsPlugin).Assembly, ModId, ServerUtilsConfig.File);
            ServerUtilsConfig.File.Save(); // writes the defaults of every entry on first start
            Harmony = new Harmony(Guid);
            PatchReport.PatchAllSafely(Harmony, typeof(ServerUtilsPlugin).Assembly, ModId);
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
