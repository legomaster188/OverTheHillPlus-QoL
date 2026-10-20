using System;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using OverTheHillPlus.Cars;

namespace OverTheHillPlus.Gemse;

// After the namespace line on purpose: the game has its own global ModManager (mod.io).
using OverTheHillPlus.Core.Manager;

/// <summary>
/// Gemse 6x6: a 1970s Alpine air-cooled six-wheeler with a hardtop as a new networked car. Its definition
/// (<see cref="GemseDefinition"/>) and its model and textures (Assets/) go to core's car framework, which builds it from
/// the Marshal's network and physics skeleton and does everything else as for the other modded cars: the garage entry
/// (free), the saves (never named in them), the join rules, the tuning, paint, skins and engine sound and their sync.
/// The two rear axles are one physical axle drawn twice (core's CarTandem).
/// </summary>
[BepInPlugin(Guid, Name, Version)]
// Core 1.1.0 has the car framework (an older core can't load this mod).
[BepInDependency(Core.CorePlugin.Guid, ">=1.1.0")]
public class GemsePlugin : BasePlugin
{
    public const string Guid = "com.oth.gemse";
    public const string Name = "Gemse 6x6";
    public const string Version = "1.0.0";

    /// <summary>Mod id: names the config file (oth.gemse.cfg), the MODS page entry and the enabled switch.</summary>
    internal const string ModId = GemseDefinition.Id;

    internal static ManualLogSource Logger;

    public override void Load()
    {
        Logger = Log;
        ModManager.Register(new ModInfo
        {
            Id = ModId,
            Side = ModSide.Server,
            Name = Name,
            Description = "A brand-new car: a 1970s Alpine six-wheeler with a hardtop, a short sloped nose, round lamps, " +
                          "swing axles with portal hubs and six driven wheels, made in the game's own style. Light, slow and " +
                          "low-geared, it crawls almost anywhere. Buy it in the garage or drive it from this page. Everyone in " +
                          "the session needs this mod.",
            Version = Version,
            DrawSimple = GemseCar.DrawPage,
        });
        if (!ModManager.IsEnabled(ModId))
        {
            Logger.LogInfo($"{Name} is switched off on the MODS page: nothing of it runs this session.");
            return;
        }
        try
        {
            string why = GemseCar.Start(typeof(GemsePlugin).Assembly, ModManager.ConfigFor(ModId), Logger);
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
internal static class GemseCar
{
    private static CarDefinition _def;
    private static CarDefinition Def => _def ??= GemseDefinition.Create();

    public static void DrawPage() => ModdedCars.DrawPage(Def);

    public static string Start(System.Reflection.Assembly assets, BepInEx.Configuration.ConfigFile config, ManualLogSource log) =>
        ModdedCars.Start(Def, assets, config, log);
}
