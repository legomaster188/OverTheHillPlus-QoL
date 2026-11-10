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

namespace OverTheHillPlus.Grayling;

// After the namespace line on purpose: the game has its own global ModManager (mod.io).
using OverTheHillPlus.Core.Manager;

/// <summary>
/// Grayling: a small aluminium fishing boat with a tiller outboard, as a new networked vehicle. Core's car framework
/// builds it from the Polyana's network and physics skeleton (garage, saves, join rules, paint, skins and engine sound and
/// their sync); this mod floats it (<see cref="BoatPhysics"/>), turns its outboard (<see cref="BoatLive"/>) and gets it in
/// and out of the water at a shore or off its trailer (<see cref="BoatLaunch"/>).
/// </summary>
#if MELON
public class GraylingPlugin : OverTheHillPlus.Loader.OthMod
#else
[BepInPlugin(Guid, Name, Version)]
// Core 1.1.0 has the car framework (an older core can't load this mod).
[BepInDependency(Core.CorePlugin.Guid, ">=1.1.0")]
public class GraylingPlugin : BasePlugin
#endif
{
    public const string Guid = "com.oth.grayling";
    public const string Name = "Grayling";
    public const string Version = "1.0.1";

    /// <summary>Mod id: names the config file (oth.grayling.cfg), the MODS page entry and the enabled switch.</summary>
    internal const string ModId = GraylingDefinition.Id;

    internal static ModLog Logger;
    internal static Harmony Harmony;
    private static readonly HashSet<string> Logged = new();

    internal static void LogOnce(string key, string text)
    {
        if (Logged.Add(key)) Logger?.LogWarning("[Grayling] " + text + " (further ones suppressed)");
    }

    /// <summary>Puts it up for sale at the career dealer (core's Api.Career; nothing happens without a career).</summary>
    private static void ListAtDealer()
    {
        try { OverTheHillPlus.Api.Career.Dealer.List(ModId, GraylingDefinition.DealerOffer()); }
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
            Description = "A 14 ft aluminium fishing boat with a tiller outboard, built to the real ones' size. It floats on the " +
                          "lakes and rivers. Take it out at a shore from this page, or back its trailer into the water. Everyone " +
                          "in the session needs this mod.",
            Version = Version,
            DrawSimple = GraylingCar.DrawPage,
        });
        if (!ModManager.IsEnabled(ModId))
        {
            Logger.LogInfo($"{Name} is switched off on the MODS page: nothing of it runs this session.");
            return;
        }
        try
        {
            string why = GraylingCar.Start(typeof(GraylingPlugin).Assembly, ModManager.ConfigFor(ModId), Logger);
            if (why != null) return;
            Harmony = new Harmony(Guid);
            PatchReport.PatchAllSafely(Harmony, typeof(GraylingPlugin).Assembly, ModId);
            Features.Registry.OnTick(GraylingCar.Tick, ModId);
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
internal static class GraylingCar
{
    private static CarDefinition _def;
    private static CarDefinition Def => _def ??= GraylingDefinition.Create();

    public static void DrawPage() => ModdedCars.DrawPage(Def);

    public static string Start(System.Reflection.Assembly assets, OverTheHillPlus.Config.ConfigFile config, ModLog log)
    {
        var blob = ReadBlob(assets);
        string why = blob == null ? "its model (grayling.mesh) could not be read" : BoatRig.Prepare(blob);
        if (why != null)
        {
            ModManager.MarkFailed(GraylingPlugin.ModId, why);
            log?.LogWarning($"Grayling: {why}. The boat stays off this session.");
            return why;
        }
        return ModdedCars.Start(Def, assets, config, log, new CarHooks { AfterModel = BoatRig.AfterModel, DriveBlocker = GraylingCore.DriveBlocker });
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
        catch (Exception e) { DebugLog.Error(GraylingPlugin.ModId, e, "GraylingCar.ReadBlob"); return null; }
    }

    public static void Tick()
    {
        try { GraylingCore.Tick(); }
        catch (Exception e) { DebugLog.Error(GraylingPlugin.ModId, e, "GraylingCar.Tick"); GraylingPlugin.LogOnce("tick", "the frame update failed: " + e.Message); }
    }
}

/// <summary>The boat's water physics goes in with the car's physics step (VPVehicleController.DoUpdateBlocks), before the
/// blocks read the inputs (on land it cuts the throttle there).</summary>
[HarmonyPatch(typeof(VPVehicleController), nameof(VPVehicleController.DoUpdateBlocks))]
internal static class GraylingStepPatch
{
    private static bool _loggedError;

    [HarmonyPriority(Priority.Normal)]
    private static void Prefix(VPVehicleController __instance)
    {
        try { BoatPhysics.Step(__instance); }
        catch (Exception e)
        {
            if (_loggedError) return;
            _loggedError = true;
            GraylingPlugin.Logger?.LogError("Grayling physics step failed (further errors suppressed): " + e);
        }
    }
}
