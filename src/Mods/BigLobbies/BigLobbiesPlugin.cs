using System;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;

namespace OverTheHillPlus.BigLobbies;

// After the namespace line on purpose: the game has its own global ModManager (mod.io).
using OverTheHillPlus.Core.Manager;

/// <summary>
/// Lobby+: up to 16 players (more typed), and everything that keeps lobbies above the game's 4 player slots
/// working: crash guards, spawn points, fall rescue, slot keeping, overflow names, extra player colours, player
/// table guards, the player-slot janitor and the lobby garage for players 5 and up.
/// </summary>
[BepInPlugin(Guid, Name, Version)]
[BepInDependency(Core.CorePlugin.Guid)]
public class BigLobbiesPlugin : BasePlugin
{
    public const string Guid = "com.oth.biglobbies";
    public const string Name = "Lobby+";
    public const string Version = "1.2.1";

    /// <summary>Mod id: names the config file (oth.biglobbies.cfg), the MODS page entry and the enabled switch.</summary>
    internal const string ModId = "biglobbies";

    internal static ManualLogSource Logger;
    internal static Harmony Harmony;

    public override void Load()
    {
        Logger = Log;
        // Listed on the MODS page even when switched off, so it can be switched back on.
        ModManager.Register(new ModInfo
        {
            Id = ModId,
            Side = ModSide.Server,
            Name = Name,
            Description = "Lobbies above 4 players: up to 16 in Create Server (or an exact size), with crash guards, " +
                          "spawn spots, colours, names and lobby cars for everyone past the game's 4 player slots.",
            Version = Version,
            DrawSimple = BigLobbiesSimple.Draw,
        });
        if (!ModManager.IsEnabled(ModId))
        {
            Logger.LogInfo($"{Name} is switched off on the MODS page: nothing of it runs this session.");
            return;
        }
        try
        {
            BigLobbiesConfig.Bind(ModManager.ConfigFor(ModId)); // BepInEx/config/oth.biglobbies.cfg
            Features.Registry.DiscoverAndRegister(typeof(BigLobbiesPlugin).Assembly, ModId, BigLobbiesConfig.File);
            BigLobbiesConfig.File.Save(); // writes the defaults of every entry on first start
            Harmony = new Harmony(Guid);
            PatchReport.PatchAllSafely(Harmony, typeof(BigLobbiesPlugin).Assembly, ModId);
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
