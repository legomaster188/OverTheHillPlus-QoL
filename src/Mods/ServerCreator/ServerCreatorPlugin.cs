using System;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;

namespace OverTheHillPlus.ServerCreator;

// After the namespace line on purpose: the game has its own global ModManager (mod.io).
using OverTheHillPlus.Core.Manager;

/// <summary>
/// Server Creator+: the server settings as rows on the game's Create Server screen (every mod's, see
/// <see cref="ServerSettings"/>), typing the server name right there, server name colours and styles, the join
/// message, player names in the server listing, and the PLUS LOBBY SETTINGS button.
/// </summary>
[BepInPlugin(Guid, Name, Version)]
[BepInDependency(Core.CorePlugin.Guid)]
public class ServerCreatorPlugin : BasePlugin
{
    public const string Guid = "com.oth.servercreator";
    public const string Name = "Server Creator+";
    public const string Version = "1.2.1";

    /// <summary>Mod id: names the config file (oth.servercreator.cfg), the MODS page entry and the enabled switch.</summary>
    internal const string ModId = "servercreator";

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
            Description = "Server settings right on the Create Server screen: type a server name, give it colours and " +
                          "styles, greet players who join, and list who is in your server in the browser.",
            Version = Version,
            DrawSimple = ServerCreatorSimple.Draw,
        });
        if (!ModManager.IsEnabled(ModId))
        {
            Logger.LogInfo($"{Name} is switched off on the MODS page: nothing of it runs this session.");
            return;
        }
        try
        {
            ServerCreatorConfig.Bind(ModManager.ConfigFor(ModId)); // BepInEx/config/oth.servercreator.cfg
            ServerCreatorFrame.Install();
            Features.Registry.DiscoverAndRegister(typeof(ServerCreatorPlugin).Assembly, ModId, ServerCreatorConfig.File);
            ServerCreatorConfig.File.Save(); // writes the defaults of every entry on first start
            Harmony = new Harmony(Guid);
            PatchReport.PatchAllSafely(Harmony, typeof(ServerCreatorPlugin).Assembly, ModId);
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
