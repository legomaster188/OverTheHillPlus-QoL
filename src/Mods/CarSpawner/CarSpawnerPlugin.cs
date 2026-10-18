using System;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;

namespace OverTheHillPlus.CarSpawner;

// After the namespace line on purpose: the game has its own global ModManager (mod.io).
using OverTheHillPlus.Core.Manager;

/// <summary>
/// Car Spawner (private, Host): a MODS page that lists every car, the game's and the modded ones, and spawns one in
/// front of you or switches you into it. See <see cref="SpawnerCars"/> (list and actions), <see cref="SpawnerSimple"/>
/// (the page) and the pure <see cref="SpawnerLogic"/>.
/// </summary>
[BepInPlugin(Guid, Name, Version)]
[BepInDependency(Core.CorePlugin.Guid)]
public class CarSpawnerPlugin : BasePlugin
{
    public const string Guid = "com.oth.carspawner";
    public const string Name = "Car Spawner";
    public const string Version = "1.0.0";

    /// <summary>Mod id: the MODS page entry and the enabled switch.</summary>
    internal const string ModId = "carspawner";

    internal static ManualLogSource Logger;

    public override void Load()
    {
        Logger = Log;
        ModManager.Register(new ModInfo
        {
            Id = ModId,
            Side = ModSide.Host,
            Name = Name,
            Description = "Spawn any car, the game's or a modded one, in front of you, or drive it right away. " +
                          "Solo or host only (spawned cars are networked, so everyone sees them); guests don't need it.",
            Version = Version,
            Private = false,
            DrawSimple = SpawnerSimple.Draw,
        });
        if (!ModManager.IsEnabled(ModId))
        {
            Logger.LogInfo($"{Name} is switched off on the MODS page: nothing of it runs this session.");
            return;
        }
        CoreBehaviour.AfterFeatureTicks += SpawnerPlace.Tick;
        CoreBehaviour.AfterGuis += SpawnerPlace.DrawGui;
        Logger.LogInfo($"{Name} {Version} loaded.");
        DebugLog.Info(ModId, $"loaded {Version}");
    }
}
