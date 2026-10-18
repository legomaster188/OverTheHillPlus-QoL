using System;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;

namespace OverTheHillPlus.Rules;

// After the namespace line on purpose: the game has its own global ModManager (mod.io).
using OverTheHillPlus.Core.Manager;

/// <summary>
/// Server Rules &amp; MOTD (host): a welcome message and the rules for each player who joins (players without mods get
/// them through core's host notices), timed announcements, and vote kick among the players who run this mod. All text
/// passes the word filter.
/// </summary>
[BepInPlugin(Guid, Name, Version)]
[BepInDependency(Core.CorePlugin.Guid)]
public class RulesPlugin : BasePlugin
{
    public const string Guid = "com.oth.rules";
    public const string Name = "Server Rules & MOTD";
    public const string Version = "1.0.0";

    /// <summary>Mod id: names the config file (oth.rules.cfg), the MODS page entry and the enabled switch.</summary>
    internal const string ModId = "rules";

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
            Description = "Welcome message and rules on join (players without mods see them too), timed " +
                          "announcements, and vote kick. Only the host needs it; players who run it too get a rules " +
                          "window and can vote.",
            Version = Version,
            DrawSimple = RulesSimple.Draw,
        });
        if (!ModManager.IsEnabled(ModId))
        {
            Logger.LogInfo($"{Name} is switched off on the MODS page: nothing of it runs this session.");
            return;
        }
        try
        {
            RulesConfig.Bind(ModManager.ConfigFor(ModId)); // BepInEx/config/oth.rules.cfg
            Features.Registry.DiscoverAndRegister(typeof(RulesPlugin).Assembly, ModId, RulesConfig.File);
            RulesConfig.File.Save(); // writes the defaults of every entry on first start
            Harmony = new Harmony(Guid);
            PatchReport.PatchAllSafely(Harmony, typeof(RulesPlugin).Assembly, ModId);
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
