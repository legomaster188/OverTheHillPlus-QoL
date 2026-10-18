using System;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;

namespace OverTheHillPlus.Traffic;

// After the namespace line on purpose: the game has its own global ModManager (mod.io).
using OverTheHillPlus.Core.Manager;

/// <summary>
/// AI Traffic (private, Host): driverless AI cars wander the roads around the players, with a density per player,
/// spawn and despawn distances and a top speed. The driving is a trimmed copy of Convoy AI's driver. See
/// <see cref="TrafficAi"/> (manager), <see cref="RoadSource"/> (where the roads come from) and the pure
/// <see cref="TrafficPlanner"/> / <see cref="RoadNet"/>.
/// </summary>
[BepInPlugin(Guid, Name, Version)]
[BepInDependency(Core.CorePlugin.Guid)]
public class TrafficPlugin : BasePlugin
{
    public const string Guid = "com.oth.traffic";
    public const string Name = "AI Traffic";
    public const string Version = "1.0.0";

    /// <summary>Mod id: names the config file (oth.traffic.cfg), the MODS page entry and the enabled switch.</summary>
    internal const string ModId = "traffic";

    internal static ManualLogSource Logger;
    internal static Harmony Harmony;

    private static bool _loggedTick;

    public override void Load()
    {
        Logger = Log;
        ModManager.Register(new ModInfo
        {
            Id = ModId,
            Side = ModSide.Host,
            Name = Name,
            Description = "Driverless AI cars wander the roads around every player, with density, spawn and despawn " +
                          "distances and a top speed. Host or solo only; guests don't need it.",
            Version = Version,
            Private = false,
            DrawSimple = TrafficSimple.Draw,
            DrawAdvanced = TrafficSimple.DrawAdvanced,
        });
        if (!ModManager.IsEnabled(ModId))
        {
            Logger.LogInfo($"{Name} is switched off on the MODS page: nothing of it runs this session.");
            return;
        }
        try
        {
            TrafficConfig.Bind(ModManager.ConfigFor(ModId)); // BepInEx/config/oth.traffic.cfg
            TrafficConfig.File.Save(); // writes the defaults on first start
            Harmony = new Harmony(Guid);
            PatchReport.PatchAllSafely(Harmony, typeof(TrafficPlugin).Assembly, ModId);
            CoreBehaviour.AfterFeatureTicks += Tick;
            Logger.LogInfo($"{Name} {Version} loaded.");
            DebugLog.Info(ModId, $"loaded {Version}");
        }
        catch (Exception e)
        {
            // The page says "Failed to start"; core and the other mods keep running.
            ModManager.MarkFailed(ModId, e.Message);
            Logger.LogError($"{Name} failed to start: {e}");
        }
    }

    private static void Tick()
    {
        // Caught here: a throw would otherwise skip the other mods' hooks this frame.
        try { TrafficAi.Tick(); }
        catch (Exception e)
        {
            if (_loggedTick) return;
            _loggedTick = true;
            Logger.LogError("AI Traffic tick failed (further errors suppressed): " + e);
        }
    }
}
