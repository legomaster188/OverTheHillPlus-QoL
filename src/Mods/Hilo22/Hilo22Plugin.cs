using System;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using OverTheHillPlus.Cars;

namespace OverTheHillPlus.Hilo22;

// After the namespace line on purpose: the game has its own global ModManager (mod.io).
using OverTheHillPlus.Core.Manager;

/// <summary>
/// Hilo 22: a modern double-cab 4x4 pickup as a new networked car. Its definition (<see cref="Hilo22Definition"/>)
/// and its model and textures (Assets/) go to core's car framework, which builds it from the Burro's network and physics
/// skeleton and does everything else as for the Vera and the Hilo 89: the garage entry (free), the saves (never named in
/// them), the join rules, the tuning, paint, skins and engine sound and their sync.
/// </summary>
[BepInPlugin(Guid, Name, Version)]
// Core 1.1.0 has the car framework (an older core can't load this mod).
[BepInDependency(Core.CorePlugin.Guid, ">=1.1.0")]
public class Hilo22Plugin : BasePlugin
{
    public const string Guid = "com.oth.hilo22";
    public const string Name = "Hilo 22";
    public const string Version = "1.0.0";

    /// <summary>Mod id: names the config file (oth.hilo22.cfg), the MODS page entry and the enabled switch.</summary>
    internal const string ModId = Hilo22Definition.Id;

    internal static ManualLogSource Logger;

    public override void Load()
    {
        Logger = Log;
        ModManager.Register(new ModInfo
        {
            Id = ModId,
            Side = ModSide.Server,
            Name = Name,
            Description = "A brand-new car: a modern double-cab 4x4 pickup, a torquey turbo-diesel with a long open bed, " +
                          "made in the game's own style. Buy it in the garage or drive it from this page. Everyone in the session needs this mod. " +
                          "Model based on \"2022 Toyota Hilux\" by BHP3D (Sketchfab), CC BY 4.0.",
            Version = Version,
            DrawSimple = Hilo22Car.DrawPage,
        });
        if (!ModManager.IsEnabled(ModId))
        {
            Logger.LogInfo($"{Name} is switched off on the MODS page: nothing of it runs this session.");
            return;
        }
        try
        {
            string why = Hilo22Car.Start(typeof(Hilo22Plugin).Assembly, ModManager.ConfigFor(ModId), Logger);
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
internal static class Hilo22Car
{
    private static CarDefinition _def;
    private static CarDefinition Def => _def ??= Hilo22Definition.Create();

    public static void DrawPage() => ModdedCars.DrawPage(Def);

    public static string Start(System.Reflection.Assembly assets, BepInEx.Configuration.ConfigFile config, ManualLogSource log) =>
        ModdedCars.Start(Def, assets, config, log);
}
