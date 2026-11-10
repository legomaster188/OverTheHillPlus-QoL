#if !MELON
using BepInEx;
using BepInEx.Unity.IL2CPP;
#endif
using System;
using OverTheHillPlus.Loader;
using OverTheHillPlus.Cars;

namespace OverTheHillPlus.Mastiff;

// After the namespace line on purpose: the game has its own global ModManager (mod.io).
using OverTheHillPlus.Core.Manager;

/// <summary>
/// Mastiff: a medium-duty 4x4 wrecker as a new networked car. Its definition (<see cref="MastiffDefinition"/>) and its model and
/// textures (Assets/) go to core's car framework, which builds it from the Mammoth's network and physics skeleton and does
/// everything else as for the other modded cars: the garage entry (free), the saves (never named in them), the join rules, the
/// tuning, paint, skins and engine sound and their sync. The career dealer sells it as a truck.
/// </summary>
#if MELON
public class MastiffPlugin : OverTheHillPlus.Loader.OthMod
#else
[BepInPlugin(Guid, Name, Version)]
// Core 1.1.0 has the car framework (an older core can't load this mod).
[BepInDependency(Core.CorePlugin.Guid, ">=1.1.0")]
public class MastiffPlugin : BasePlugin
#endif
{
    public const string Guid = "com.oth.mastiff";
    public const string Name = "Mastiff";
    public const string Version = "1.0.0";

    /// <summary>Mod id: names the config file (oth.mastiff.cfg), the MODS page entry and the enabled switch.</summary>
    internal const string ModId = MastiffDefinition.Id;

    internal static ModLog Logger;

    /// <summary>Puts it up for sale at the career dealer (core's Api.Career; nothing happens without a career).</summary>
    private static void ListAtDealer()
    {
        try { OverTheHillPlus.Api.Career.Dealer.List(ModId, MastiffDefinition.DealerOffer()); }
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
            Description = "A medium-duty 4x4 wrecker: a square cab on a heavy frame, a steel tow body with side lockers, a twin-boom crane " +
                          "with its cable, sheaves and hook blocks, a push bumper and dual rear wheels. Very heavy, geared low, tows 12 t. " +
                          "Buy it in the garage or drive it from this page. Everyone in the session needs this mod.",
            Version = Version,
            DrawSimple = MastiffCar.DrawPage,
        });
        if (!ModManager.IsEnabled(ModId))
        {
            Logger.LogInfo($"{Name} is switched off on the MODS page: nothing of it runs this session.");
            return;
        }
        try
        {
            string why = MastiffCar.Start(typeof(MastiffPlugin).Assembly, ModManager.ConfigFor(ModId), Logger);
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

/// <summary>Holds the car's definition. Kept out of the plugin class, so nothing of core's car framework is touched before Load runs
/// (with a core that lacks it, BepInEx refuses the plugin through the versioned dependency instead).</summary>
internal static class MastiffCar
{
    private static CarDefinition _def;
    private static CarDefinition Def => _def ??= MastiffDefinition.Create();

    public static void DrawPage() => ModdedCars.DrawPage(Def);

    public static string Start(System.Reflection.Assembly assets, OverTheHillPlus.Config.ConfigFile config, ModLog log) =>
        ModdedCars.Start(Def, assets, config, log);
}
