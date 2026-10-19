using System;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;

namespace OverTheHillPlus.Discord;

// After the namespace line on purpose: the game has its own global ModManager (mod.io).
using OverTheHillPlus.Core.Manager;

/// <summary>
/// Discord Presence: shows the map, car, session and player count (and the server's name, only when allowed) on your
/// Discord profile, over the Discord desktop app's local IPC pipe. Nothing goes over the internet from this mod.
/// </summary>
[BepInPlugin(Guid, Name, Version)]
[BepInDependency(Core.CorePlugin.Guid)]
public class DiscordPlugin : BasePlugin
{
    public const string Guid = "com.oth.discord";
    public const string Name = "Discord Presence";
    public const string Version = "1.2.0";

    /// <summary>Mod id: names the config file (oth.discord.cfg), the MODS page entry and the enabled switch.</summary>
    internal const string ModId = "discord";

    internal static ManualLogSource Logger;

    public override void Load()
    {
        Logger = Log;
        // Listed on the MODS page even when switched off, so it can be switched back on.
        ModManager.Register(new ModInfo
        {
            Id = ModId,
            Side = ModSide.Client,
            Name = Name,
            Description = "Shows your map, car and player count on your Discord profile, through the Discord app on " +
                          "this PC. The server's name only shows if you allow it.",
            Version = Version,
            DrawSimple = DiscordSimple.Draw,
        });
        if (!ModManager.IsEnabled(ModId))
        {
            Logger.LogInfo($"{Name} is switched off on the MODS page: nothing of it runs this session.");
            return;
        }
        try
        {
            DiscordConfig.Bind(ModManager.ConfigFor(ModId)); // BepInEx/config/oth.discord.cfg
            Features.Registry.DiscoverAndRegister(typeof(DiscordPlugin).Assembly, ModId, DiscordConfig.File);
            DiscordConfig.File.Save(); // writes the defaults of every entry on first start
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
