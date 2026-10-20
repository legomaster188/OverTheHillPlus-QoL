using System;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using OverTheHillPlus.Cars;

namespace OverTheHillPlus.Longhorn;

// After the namespace line on purpose: the game has its own global ModManager (mod.io).
using OverTheHillPlus.Core.Manager;

/// <summary>
/// Longhorn 389: an American long-nose conventional tractor unit (day cab, no trailer) as a new networked car. Its
/// definition (<see cref="LonghornDefinition"/>) and its model and textures (Assets/) go to core's car framework, which
/// builds it from the Blast Wagon's network and physics skeleton and does everything else as for the other modded cars:
/// the garage entry (free), the saves (never named in them), the join rules, the tuning, paint, skins and engine sound
/// and their sync. The tandem drive axle is one physical rear axle drawn twice (core's CarTandem).
/// </summary>
[BepInPlugin(Guid, Name, Version)]
// Core 1.1.0 has the car framework (an older core can't load this mod).
[BepInDependency(Core.CorePlugin.Guid, ">=1.1.0")]
public class LonghornPlugin : BasePlugin
{
    public const string Guid = "com.oth.longhorn";
    public const string Name = "Longhorn 389";
    public const string Version = "1.0.0";

    /// <summary>Mod id: names the config file (oth.longhorn.cfg), the MODS page entry and the enabled switch.</summary>
    internal const string ModId = LonghornDefinition.Id;

    internal static ManualLogSource Logger;

    public override void Load()
    {
        Logger = Log;
        ModManager.Register(new ModInfo
        {
            Id = ModId,
            Side = ModSide.Server,
            Name = Name,
            Description = "A brand-new car: an American long-nose conventional tractor unit, a day cab with a tall chrome grille, " +
                          "dual stacks, a fifth wheel and a tandem drive axle, made in the game's own style. Very heavy, huge torque, " +
                          "slow but steady. Buy it in the garage or drive it from this page. Everyone in the session needs this mod.",
            Version = Version,
            DrawSimple = LonghornCar.DrawPage,
        });
        if (!ModManager.IsEnabled(ModId))
        {
            Logger.LogInfo($"{Name} is switched off on the MODS page: nothing of it runs this session.");
            return;
        }
        try
        {
            string why = LonghornCar.Start(typeof(LonghornPlugin).Assembly, ModManager.ConfigFor(ModId), Logger);
            if (why == null) Logger.LogInfo($"{Name} {Version} started.");
        }
        catch (Exception e)
        {
            ModManager.MarkFailed(ModId, e.Message);
            Logger.LogError($"{Name} failed to start: {e}");
        }
    }
}

/// <summary>Holds the car's definition. Kept out of the plugin class, so nothing of core's car framework is touched
/// before Load runs (with a core that lacks it, BepInEx refuses the plugin through the versioned dependency instead).</summary>
internal static class LonghornCar
{
    private static CarDefinition _def;
    private static CarDefinition Def => _def ??= LonghornDefinition.Create();

    public static void DrawPage() => ModdedCars.DrawPage(Def);

    public static string Start(System.Reflection.Assembly assets, BepInEx.Configuration.ConfigFile config, ManualLogSource log) =>
        ModdedCars.Start(Def, assets, config, log);
}
