using System;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using OverTheHillPlus.Core.Shared;

namespace OverTheHillPlus.Trailers;

// After the namespace line on purpose: the game has its own global ModManager (mod.io).
using OverTheHillPlus.Core.Manager;

/// <summary>
/// Trailers: four networked trailers (utility, teardrop camper, car hauler, boat) to tow behind your car. Each is its
/// own Fusion prefab entry (core's network prefab registry, built at runtime from the game's WinchablePlank physics
/// prop with this mod's models), spawned by the host with the towing player as its input authority so that player's
/// game predicts car and trailer together. The hitch is a joint on the host and the owner; wheels, grip and the
/// overrun brake run in Fusion's physics step. Everyone in the session needs the mod (core refuses joiners without it).
/// </summary>
[BepInPlugin(Guid, Name, Version)]
// Core 1.1.0: the mesh blob reader lives in core (OverTheHillPlus.Cars.MeshBlob).
[BepInDependency(Core.CorePlugin.Guid, ">=1.1.0")]
public class TrailersPlugin : BasePlugin
{
    public const string Guid = "com.oth.trailers";
    public const string Name = "Trailers";
    public const string Version = "1.0.1";

    /// <summary>Mod id: names the config file (oth.trailers.cfg), the MODS page entry and the enabled switch.</summary>
    internal const string ModId = "trailers";

    internal static ManualLogSource Logger;
    internal static Harmony Harmony;

    private static readonly System.Collections.Generic.HashSet<string> Logged = new();

    /// <summary>Logs a warning the first time <paramref name="key"/> is seen (later ones are suppressed).</summary>
    internal static void LogOnce(string key, string text)
    {
        if (Logged.Add(key)) Logger?.LogWarning(text + " (further ones suppressed)");
    }

    public override void Load()
    {
        Logger = Log;
        ModManager.Register(new ModInfo
        {
            Id = ModId,
            Side = ModSide.Server,
            Name = Name,
            Description = "Tow a trailer behind your car: a utility trailer, a teardrop camper, a car hauler or a boat on " +
                          "its trailer. Hitch, unhitch and put it back behind you from this page. Everyone in the session needs this mod.",
            Version = Version,
            DrawSimple = TrailersSimple.Draw,
        });
        if (!ModManager.IsEnabled(ModId))
        {
            Logger.LogInfo($"{Name} is switched off on the MODS page: nothing of it runs this session.");
            return;
        }
        try
        {
            TrailersConfig.Bind(ModManager.ConfigFor(ModId)); // BepInEx/config/oth.trailers.cfg
            string why = TrailerAssets.Load();
            if (why != null)
            {
                ModManager.MarkFailed(ModId, why);
                Logger.LogWarning($"{Name}: {why}. Trailers stay off this session.");
                return;
            }
            // The four prefab entries, registered by core in its fixed order (core also runs the join gate).
            why = TrailerPrefabs.Declare();
            if (why != null)
            {
                Fail(why + Withdrawn());
                return;
            }
            Harmony = new Harmony(Guid);
            PatchReport.PatchAllSafely(Harmony, typeof(TrailersPlugin).Assembly, ModId);
            Features.Registry.DiscoverAndRegister(typeof(TrailersPlugin).Assembly, ModId, TrailersConfig.File);
            TrailersConfig.File.Save();
            Logger.LogInfo($"{Name} {Version} loaded.");
            DebugLog.Info(ModId, $"loaded {Version}");
        }
        catch (Exception e)
        {
            // Declared but not running: take the entries back, or the join gate would refuse players for a mod that
            // doesn't run here.
            Fail(e.Message + Withdrawn());
            Logger.LogError($"{Name} failed to start: {e}");
        }
    }

    private static void Fail(string why)
    {
        ModManager.MarkFailed(ModId, why);
        Logger.LogError($"{Name}: {why}. Trailers stay off this session.");
    }

    /// <summary>Takes this mod's declared prefab entries back; "" when done (or none), else what the player must know.</summary>
    private static string Withdrawn()
    {
        try
        {
            string refused = NetPrefabRegistry.Withdraw(ModId);
            return refused == null ? "" : " (" + refused + ")";
        }
        catch (Exception e) { DebugLog.Error("trailers", e, "TrailersPlugin.Withdrawn"); return " (its network prefabs could not be taken back: " + e.Message + ")"; }
    }
}
