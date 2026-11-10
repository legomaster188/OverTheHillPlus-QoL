#if !MELON
using BepInEx;
using BepInEx.Unity.IL2CPP;
#endif
using System;
using System.Collections.Generic;
using HarmonyLib;
using OverTheHillPlus.Loader;
using OverTheHillPlus.Cars;
#if MELON
using Il2CppVehiclePhysics;
#else
using VehiclePhysics;
#endif

namespace OverTheHillPlus.Jackal;

// After the namespace line on purpose: the game has its own global ModManager (mod.io).
using OverTheHillPlus.Core.Manager;

/// <summary>
/// Jackal: a two-seat sand rail as a new networked car. Its definition (<see cref="JackalDefinition"/>) and its model and
/// textures (Assets/) go to core's car framework, which builds it from the Brick's network and physics skeleton and does
/// everything else as for the other modded cars. This mod adds what is the rail's own: rear-wheel drive and the weight behind
/// (<see cref="JackalRig"/>), the suspension arms, coil-overs, half shafts and steering wheel that move with the wheels
/// (<see cref="JackalLive"/>), and what its tyres do on each ground (<see cref="JackalCore"/>).
/// </summary>
#if MELON
public class JackalPlugin : OverTheHillPlus.Loader.OthMod
#else
[BepInPlugin(Guid, Name, Version)]
// Core 1.1.0 has the car framework (an older core can't load this mod).
[BepInDependency(Core.CorePlugin.Guid, ">=1.1.0")]
public class JackalPlugin : BasePlugin
#endif
{
    public const string Guid = "com.oth.jackal";
    public const string Name = "Jackal";
    public const string Version = "1.0.0";

    /// <summary>Mod id: names the config file (oth.jackal.cfg), the MODS page entry and the enabled switch.</summary>
    internal const string ModId = JackalDefinition.Id;

    internal static ModLog Logger;
    internal static Harmony Harmony;
    private static readonly HashSet<string> Logged = new();

    internal static void LogOnce(string key, string text)
    {
        if (Logged.Add(key)) Logger?.LogWarning("[Jackal] " + text + " (further ones suppressed)");
    }

    /// <summary>Puts it up for sale at the career dealer (core's Api.Career; nothing happens without a career).</summary>
    private static void ListAtDealer()
    {
        try { OverTheHillPlus.Api.Career.Dealer.List(ModId, JackalDefinition.DealerOffer()); }
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
            Description = "A two-seat sand rail: a bent tube frame and cage, a flat four out in the open behind the axle, ribbed " +
                          "front tyres and paddles behind, rear-wheel drive and long soft suspension. Buy it in the garage or drive it " +
                          "from this page. Everyone in the session needs this mod.",
            Version = Version,
            DrawSimple = JackalCar.DrawPage,
        });
        if (!ModManager.IsEnabled(ModId))
        {
            Logger.LogInfo($"{Name} is switched off on the MODS page: nothing of it runs this session.");
            return;
        }
        try
        {
            string why = JackalCar.Start(typeof(JackalPlugin).Assembly, ModManager.ConfigFor(ModId), Logger);
            if (why != null) return;
            Harmony = new Harmony(Guid);
            PatchReport.PatchAllSafely(Harmony, typeof(JackalPlugin).Assembly, ModId);
            Features.Registry.OnTick(JackalCar.Tick, ModId);
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

/// <summary>Holds the definition and the start (kept out of the plugin class, so nothing of core's car framework is touched
/// before Load runs).</summary>
internal static class JackalCar
{
    private static CarDefinition _def;
    private static CarDefinition Def => _def ??= JackalDefinition.Create();

    public static void DrawPage() => ModdedCars.DrawPage(Def);

    public static string Start(System.Reflection.Assembly assets, OverTheHillPlus.Config.ConfigFile config, ModLog log)
    {
        // the model's running gear first (read from the embedded model; without it the arms would stand still)
        var blob = ReadBlob(assets);
        string why = blob == null ? "its model (jackal.mesh) could not be read" : JackalRig.Prepare(blob);
        if (why != null)
        {
            ModManager.MarkFailed(JackalPlugin.ModId, why);
            log?.LogWarning($"Jackal: {why}. The car stays off this session.");
            return why;
        }
        return ModdedCars.Start(Def, assets, config, log, new CarHooks { AfterModel = JackalRig.AfterModel, AfterTemplate = JackalRig.AfterTemplate });
    }

    private static MeshBlob ReadBlob(System.Reflection.Assembly assets)
    {
        try
        {
            using var s = assets.GetManifestResourceStream(Def.AssetName + "/" + Def.MeshFile);
            if (s == null) return null;
            using var ms = new System.IO.MemoryStream();
            s.CopyTo(ms);
            return MeshBlob.Read(ms.ToArray());
        }
        catch (Exception e) { DebugLog.Error(JackalPlugin.ModId, e, "JackalCar.ReadBlob"); return null; }
    }

    public static void Tick()
    {
        try { JackalCore.Tick(); }
        catch (Exception e) { DebugLog.Error(JackalPlugin.ModId, e, "JackalCar.Tick"); JackalPlugin.LogOnce("tick", "the frame update failed: " + e.Message); }
    }
}

/// <summary>The rail's grip goes in with the car's physics step (VPVehicleController.DoUpdateBlocks), after Terrain+'s prefix
/// (which publishes the ground factor this multiplies in), as the one writer of each wheel while no other writer (Tyre
/// Pressure) has it.</summary>
[HarmonyPatch(typeof(VPVehicleController), nameof(VPVehicleController.DoUpdateBlocks))]
internal static class JackalStepPatch
{
    private static bool _loggedError;

    [HarmonyPriority(Priority.Normal)]
    private static void Prefix(VPVehicleController __instance)
    {
        try { JackalCore.PhysicsStep(__instance); }
        catch (Exception e)
        {
            if (_loggedError) return;
            _loggedError = true;
            JackalPlugin.Logger?.LogError("Jackal physics step failed (further errors suppressed): " + e);
        }
    }
}
