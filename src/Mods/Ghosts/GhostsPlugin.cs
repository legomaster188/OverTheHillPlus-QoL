using System;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;

namespace OverTheHillPlus.Ghosts;

// After the namespace line on purpose: the game has its own global ModManager (mod.io).
using OverTheHillPlus.Core.Manager;

/// <summary>
/// Ghost Replays: records your drives (your car's position, rotation and wheels at 20 Hz, compressed), plays them back
/// as a see-through ghost car with no physics, races your best ghost on Race Mode tracks, and keeps a replay list.
/// Local only: nothing is sent to other players and the ghost touches nothing.
/// </summary>
[BepInPlugin(Guid, Name, Version)]
[BepInDependency(Core.CorePlugin.Guid)]
public class GhostsPlugin : BasePlugin
{
    public const string Guid = "com.oth.ghosts";
    public const string Name = "Ghost Replays";
    public const string Version = "1.0.0";

    /// <summary>Mod id: names the config file (oth.ghosts.cfg), the ghost folder (oth.ghosts/), the MODS page entry and the switch.</summary>
    internal const string ModId = "ghosts";

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
            Description = "Record your drives and play them back as a see-through ghost car. Race your best ghost on " +
                          "Race Mode tracks, and keep a list of replays to play, rename or delete.",
            Version = Version,
            DrawSimple = GhostsSimple.Draw,
        });
        if (!ModManager.IsEnabled(ModId))
        {
            Logger.LogInfo($"{Name} is switched off on the MODS page: nothing of it runs this session.");
            return;
        }
        try
        {
            GhostsConfig.Bind(ModManager.ConfigFor(ModId)); // BepInEx/config/oth.ghosts.cfg
            GhostsConfig.File.Save(); // writes the defaults of every entry on first start
            GhostsFrame.Install();
            GhostRace.Hook();
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
