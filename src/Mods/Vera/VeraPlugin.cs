using System;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using OverTheHillPlus.Cars;

namespace OverTheHillPlus.Vera;

// After the namespace line on purpose: the game has its own global ModManager (mod.io).
using OverTheHillPlus.Core.Manager;

/// <summary>
/// Vera Crossover: a brand-new networked car. Its definition (<see cref="VeraDefinition"/>) and its model and textures
/// (Assets/) go to core's car framework, which does the rest as for every modded car: every game running this mod
/// appends the same Fusion prefab entry (built at runtime from a donor car's network and physics skeleton, with the
/// Vera's own model, identity and tuning), registers the car in the game's catalog (so it is in the garage) and serves
/// it to the game's own spawn code. A host running it refuses players who couldn't load that entry; saves never name
/// it, so they load without the mod.
/// </summary>
[BepInPlugin(Guid, Name, Version)]
// Core 1.1.0 has the car framework (an older core can't load this mod).
[BepInDependency(Core.CorePlugin.Guid, ">=1.1.0")]
public class VeraPlugin : BasePlugin
{
    public const string Guid = "com.oth.vera";
    public const string Name = "Vera Crossover";
    public const string Version = "1.1.1";

    /// <summary>Mod id: names the config file (oth.vera.cfg), the MODS page entry and the enabled switch.</summary>
    internal const string ModId = VeraDefinition.Id;

    internal static ManualLogSource Logger;

    public override void Load()
    {
        Logger = Log;
        ModManager.Register(new ModInfo
        {
            Id = ModId,
            Side = ModSide.Server,
            Name = Name,
            Description = "A brand-new car: a mid-size seven-seat crossover, heavier and more road-biased than the trucks. " +
                          "Buy it in the garage or drive it from this page. Everyone in the session needs this mod. " +
                          "Model based on \"Hyundai ix55- Veracruz 2011\" by Nieve5677 (Sketchfab), CC BY 4.0.",
            Version = Version,
            DrawSimple = VeraCar.DrawPage,
        });
        if (!ModManager.IsEnabled(ModId))
        {
            Logger.LogInfo($"{Name} is switched off on the MODS page: nothing of it runs this session.");
            return;
        }
        try
        {
            // BepInEx/config/oth.vera.cfg, with the keys it always had ([Vera] Donor / GarageBase / ShowInGarage, [Look]).
            string why = VeraCar.Start(typeof(VeraPlugin).Assembly, ModManager.ConfigFor(ModId), Logger);
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
internal static class VeraCar
{
    private static CarDefinition _def;
    private static CarDefinition Def => _def ??= VeraDefinition.Create();

    public static void DrawPage() => ModdedCars.DrawPage(Def);

    public static string Start(System.Reflection.Assembly assets, BepInEx.Configuration.ConfigFile config, ManualLogSource log) =>
        ModdedCars.Start(Def, assets, config, log);
}
