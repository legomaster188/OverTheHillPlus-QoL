using System;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using OverTheHillPlus.Cars;

namespace OverTheHillPlus.Pixie;

// After the namespace line on purpose: the game has its own global ModManager (mod.io).
using OverTheHillPlus.Core.Manager;

/// <summary>
/// Pixie: a kei-class mini van as a new networked car. Its definition (<see cref="PixieDefinition"/>)
/// and its model and textures (Assets/) go to core's car framework, which builds it from the Boar's network and physics
/// skeleton and does everything else as for the Vera and the Hilo 89: the garage entry (free), the saves (never named in
/// them), the join rules, the tuning, paint, skins and engine sound and their sync.
/// </summary>
[BepInPlugin(Guid, Name, Version)]
// Core 1.1.0 has the car framework (an older core can't load this mod).
[BepInDependency(Core.CorePlugin.Guid, ">=1.1.0")]
public class PixiePlugin : BasePlugin
{
    public const string Guid = "com.oth.pixie";
    public const string Name = "Pixie";
    public const string Version = "1.0.0";

    /// <summary>Mod id: names the config file (oth.pixie.cfg), the MODS page entry and the enabled switch.</summary>
    internal const string ModId = PixieDefinition.Id;

    internal static ManualLogSource Logger;

    public override void Load()
    {
        Logger = Log;
        ModManager.Register(new ModInfo
        {
            Id = ModId,
            Side = ModSide.Server,
            Name = Name,
            Description = "A brand-new car: a tiny kei-class work van, light, slow and nimble, with sliding side doors and a hatch, " +
                          "made in the game's own style. Buy it in the garage or drive it from this page. Everyone in the session needs this mod. " +
                          "This work is based on \"Daihatsu Hijet\" (https://sketchfab.com/3d-models/daihatsu-hijet-13c80ca6d94e4600b0440c59281367f5) by Jazmin_BeamNG_Lover (https://sketchfab.com/aacps) licensed under CC-BY-4.0 (http://creativecommons.org/licenses/by/4.0/)",
            Version = Version,
            DrawSimple = PixieCar.DrawPage,
        });
        if (!ModManager.IsEnabled(ModId))
        {
            Logger.LogInfo($"{Name} is switched off on the MODS page: nothing of it runs this session.");
            return;
        }
        try
        {
            string why = PixieCar.Start(typeof(PixiePlugin).Assembly, ModManager.ConfigFor(ModId), Logger);
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
internal static class PixieCar
{
    private static CarDefinition _def;
    private static CarDefinition Def => _def ??= PixieDefinition.Create();

    public static void DrawPage() => ModdedCars.DrawPage(Def);

    public static string Start(System.Reflection.Assembly assets, BepInEx.Configuration.ConfigFile config, ManualLogSource log) =>
        ModdedCars.Start(Def, assets, config, log);
}
