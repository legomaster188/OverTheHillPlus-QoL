#if !MELON
using BepInEx;
using BepInEx.Unity.IL2CPP;
#endif
using System;
using OverTheHillPlus.Loader;
using OverTheHillPlus.Cars;

namespace OverTheHillPlus.Stoat;

// After the namespace line on purpose: the game has its own global ModManager (mod.io).
using OverTheHillPlus.Core.Manager;

/// <summary>
/// Stoat: a walk-in delivery step van as a new networked car. Its definition (<see cref="StoatDefinition"/>) and its model and
/// textures (Assets/) go to core's car framework, which builds it from the Boar's network and physics skeleton and does everything
/// else as for the other modded cars: the garage entry, the saves (never named in them), the join rules, the tuning, paint, skins and
/// engine sound and their sync. This mod adds what is the van's own: rear-wheel drive (<see cref="StoatRig"/>).
/// </summary>
#if MELON
public class StoatPlugin : OverTheHillPlus.Loader.OthMod
#else
[BepInPlugin(Guid, Name, Version)]
// Core 1.1.0 has the car framework (an older core can't load this mod).
[BepInDependency(Core.CorePlugin.Guid, ">=1.1.0")]
public class StoatPlugin : BasePlugin
#endif
{
    public const string Guid = "com.oth.stoat";
    public const string Name = "Stoat";
    public const string Version = "1.0.0";

    /// <summary>Mod id: names the config file (oth.stoat.cfg), the MODS page entry and the enabled switch.</summary>
    internal const string ModId = StoatDefinition.Id;

    internal static ModLog Logger;

    /// <summary>Puts it up for sale at the career dealer (core's Api.Career; nothing happens without a career).</summary>
    private static void ListAtDealer()
    {
        try { OverTheHillPlus.Api.Career.Dealer.List(ModId, StoatDefinition.DealerOffer()); }
        catch (Exception e) { Logger.LogWarning($"{Name}: couldn't list it at the career dealer: {e.Message}"); }
    }

    public override void Load()
    {
        Logger = Log;
        ModManager.Register(new ModInfo
        {
            Id = ModId,
            Side = ModSide.Server,
            Name = Name,
            Description = "A walk-in delivery step van from the 1960s: a tall square body on a light-truck chassis, sliding cab doors, double " +
                          "rear doors and shelves in the back. Slow, heavy and tippy. Buy it in the garage or drive it from this page. " +
                          "Everyone in the session needs this mod.",
            Version = Version,
            DrawSimple = StoatCar.DrawPage,
        });
        if (!ModManager.IsEnabled(ModId))
        {
            Logger.LogInfo($"{Name} is switched off on the MODS page: nothing of it runs this session.");
            return;
        }
        try
        {
            string why = StoatCar.Start(typeof(StoatPlugin).Assembly, ModManager.ConfigFor(ModId), Logger);
            if (why != null) return;
            ListAtDealer();
            Logger.LogInfo($"{Name} {Version} started.");
        }
        catch (Exception e)
        {
            ModManager.MarkFailed(ModId, e.Message);
            Logger.LogError($"{Name} failed to start: {e}");
        }
    }
}

/// <summary>Holds the car's definition. Kept out of the plugin class, so nothing of core's car framework is touched before Load runs.</summary>
internal static class StoatCar
{
    private static CarDefinition _def;
    private static CarDefinition Def => _def ??= StoatDefinition.Create();

    public static void DrawPage() => ModdedCars.DrawPage(Def);

    public static string Start(System.Reflection.Assembly assets, OverTheHillPlus.Config.ConfigFile config, ModLog log) =>
        ModdedCars.Start(Def, assets, config, log, new CarHooks { AfterTemplate = StoatRig.AfterTemplate });
}
