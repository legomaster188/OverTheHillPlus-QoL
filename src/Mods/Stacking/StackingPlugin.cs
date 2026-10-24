using System;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;

namespace OverTheHillPlus.Stacking;

// After the namespace line on purpose: the game has its own global ModManager (mod.io).
using OverTheHillPlus.Core.Manager;

/// <summary>
/// Stacking+: in the garage's cosmetics screen, Shift + click puts a cosmetic on without taking off the one of
/// its type already on (two roof loads, several plushies, ...); a plain click works as in the game, and clicking a
/// fitted one takes only that one off. A hint line at the bottom of the screen says so. The stack is the game's own
/// per-car "in use" flags, saved and sent as usual, so everyone sees it (also players without the mod). Local only.
/// </summary>
[BepInPlugin(Guid, Name, Version)]
[BepInDependency(Core.CorePlugin.Guid)]
public class StackingPlugin : BasePlugin
{
    public const string Guid = "com.oth.stacking";
    public const string Name = "Stacking+";
    public const string Version = "1.0.1";

    /// <summary>Mod id: names the config file (oth.stacking.cfg), the MODS page entry and the enabled switch.</summary>
    internal const string ModId = "stacking";

    internal static ManualLogSource Logger;
    internal static Harmony Harmony;

    public override void Load()
    {
        Logger = Log;
        ModManager.Register(new ModInfo
        {
            Id = ModId,
            Side = ModSide.Client,
            Name = Name,
            Description = "Shift + click a cosmetic in the garage to add it without removing the one already on " +
                          "(several roof loads, plushies, light bars, ...). Everyone sees the stack.",
            Version = Version,
            DrawSimple = StackingSimple.Draw,
        });
        if (!ModManager.IsEnabled(ModId))
        {
            Logger.LogInfo($"{Name} is switched off on the MODS page: nothing of it runs this session.");
            return;
        }
        try
        {
            StackingConfig.Bind(ModManager.ConfigFor(ModId)); // BepInEx/config/oth.stacking.cfg
            Features.Registry.DiscoverAndRegister(typeof(StackingPlugin).Assembly, ModId, StackingConfig.File);
            StackingConfig.File.Save();
            Harmony = new Harmony(Guid);
            PatchReport.PatchAllSafely(Harmony, typeof(StackingPlugin).Assembly, ModId);
            Logger.LogInfo($"{Name} {Version} loaded.");
            DebugLog.Info(ModId, $"loaded {Version}");
        }
        catch (Exception e)
        {
            ModManager.MarkFailed(ModId, e.Message);
            Logger.LogError($"{Name} failed to start: {e}");
        }
    }
}
