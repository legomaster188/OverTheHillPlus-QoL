using System;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using OverTheHillPlus.Cars;

namespace OverTheHillPlus.Tercel;

// After the namespace line on purpose: the game has its own global ModManager (mod.io).
using OverTheHillPlus.Core.Manager;

/// <summary>
/// Terrier 4WD Wagon: a small early-80s four-wheel-drive wagon as a new networked car. Its definition
/// (<see cref="TercelDefinition"/>) and its model and textures (Assets/) go to core's car framework, which builds it from
/// the Brick's network and physics skeleton and does everything else as for the Vera: the garage entry (free), the
/// saves (never named in them), the join rules, the tuning, paint, skins and engine sound and their sync.
/// </summary>
[BepInPlugin(Guid, Name, Version)]
// Core 1.1.0 has the car framework (an older core can't load this mod).
[BepInDependency(Core.CorePlugin.Guid, ">=1.1.0")]
public class TercelPlugin : BasePlugin
{
    public const string Guid = "com.oth.tercel";
    public const string Name = "Terrier 4WD Wagon";
    public const string Version = "1.0.0";

    /// <summary>Mod id: names the config file (oth.tercel.cfg), the MODS page entry and the enabled switch.</summary>
    internal const string ModId = TercelDefinition.Id;

    internal static ManualLogSource Logger;

    public override void Load()
    {
        Logger = Log;
        ModManager.Register(new ModInfo
        {
            Id = ModId,
            Side = ModSide.Server,
            Name = Name,
            Description = "A brand-new car: a small early-80s wagon with part-time four-wheel drive, light and narrow, made in the " +
                          "game's own style. Buy it in the garage or drive it from this page. Everyone in the session needs this mod.",
            Version = Version,
            DrawSimple = TercelCar.DrawPage,
        });
        if (!ModManager.IsEnabled(ModId))
        {
            Logger.LogInfo($"{Name} is switched off on the MODS page: nothing of it runs this session.");
            return;
        }
        try
        {
            string why = TercelCar.Start(typeof(TercelPlugin).Assembly, ModManager.ConfigFor(ModId), Logger);
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
internal static class TercelCar
{
    private static CarDefinition _def;
    private static CarDefinition Def => _def ??= TercelDefinition.Create();

    public static void DrawPage() => ModdedCars.DrawPage(Def);

    public static string Start(System.Reflection.Assembly assets, BepInEx.Configuration.ConfigFile config, ManualLogSource log) =>
        ModdedCars.Start(Def, assets, config, log);
}
