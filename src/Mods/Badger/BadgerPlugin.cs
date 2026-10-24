using System;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using OverTheHillPlus.Cars;

namespace OverTheHillPlus.Badger;

// After the namespace line on purpose: the game has its own global ModManager (mod.io).
using OverTheHillPlus.Core.Manager;

/// <summary>
/// Badger: a 2-tonne cab-over 4x4 army truck with a canvas-covered body as a new networked car. Its definition
/// (<see cref="BadgerDefinition"/>) and its model and textures (Assets/) go to core's car framework, which builds it from
/// the Mammoth's network and physics skeleton and does everything else as for the other modded cars: the garage entry
/// (free), the saves (never named in them), the join rules, the tuning, paint, skins and engine sound and their sync.
/// </summary>
[BepInPlugin(Guid, Name, Version)]
// Core 1.1.0 has the car framework (an older core can't load this mod).
[BepInDependency(Core.CorePlugin.Guid, ">=1.1.0")]
public class BadgerPlugin : BasePlugin
{
    public const string Guid = "com.oth.badger";
    public const string Name = "Badger";
    public const string Version = "1.0.0";

    /// <summary>Mod id: names the config file (oth.badger.cfg), the MODS page entry and the enabled switch.</summary>
    internal const string ModId = BadgerDefinition.Id;

    internal static ManualLogSource Logger;

    public override void Load()
    {
        Logger = Log;
        ModManager.Register(new ModInfo
        {
            Id = ModId,
            Side = ModSide.Server,
            Name = Name,
            Description = "A brand-new Expedition car: a 2-tonne cab-over army truck with tall single tyres and a canvas tilt over " +
                          "a drop-side body, lighter and quicker than the Mammoth. Buy it in the garage or drive it from this page. " +
                          "Everyone in the session needs this mod.",
            Version = Version,
            DrawSimple = BadgerCar.DrawPage,
        });
        if (!ModManager.IsEnabled(ModId))
        {
            Logger.LogInfo($"{Name} is switched off on the MODS page: nothing of it runs this session.");
            return;
        }
        try
        {
            string why = BadgerCar.Start(typeof(BadgerPlugin).Assembly, ModManager.ConfigFor(ModId), Logger);
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
internal static class BadgerCar
{
    private static CarDefinition _def;
    private static CarDefinition Def => _def ??= BadgerDefinition.Create();

    public static void DrawPage() => ModdedCars.DrawPage(Def);

    public static string Start(System.Reflection.Assembly assets, BepInEx.Configuration.ConfigFile config, ManualLogSource log) =>
        ModdedCars.Start(Def, assets, config, log);
}
