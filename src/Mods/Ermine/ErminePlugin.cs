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

namespace OverTheHillPlus.Ermine;

// After the namespace line on purpose: the game has its own global ModManager (mod.io).
using OverTheHillPlus.Core.Manager;

/// <summary>
/// Ermine: a trail snowmobile as a new networked vehicle. Its definition (<see cref="ErmineDefinition"/>) and its model and
/// textures (Assets/) go to core's car framework, which builds it from the Polyana's network and physics skeleton (the
/// garage entry, the saves, the join rules, the tuning, paint, skins and engine sound and their sync); this mod adds what
/// makes it a sled: its moving skis, suspension, handlebars and track (<see cref="ErmineLive"/>) and a sled's grip on each
/// ground (<see cref="ErmineCore"/>).
/// </summary>
#if MELON
public class ErminePlugin : OverTheHillPlus.Loader.OthMod
#else
[BepInPlugin(Guid, Name, Version)]
// Core 1.1.0 has the car framework (an older core can't load this mod).
[BepInDependency(Core.CorePlugin.Guid, ">=1.1.0")]
public class ErminePlugin : BasePlugin
#endif
{
    public const string Guid = "com.oth.ermine";
    public const string Name = "Ermine";
    public const string Version = "1.0.1";

    /// <summary>Mod id: names the config file (oth.ermine.cfg), the MODS page entry and the enabled switch.</summary>
    internal const string ModId = ErmineDefinition.Id;

    internal static ModLog Logger;
    internal static Harmony Harmony;
    private static readonly HashSet<string> Logged = new();

    internal static void LogOnce(string key, string text)
    {
        if (Logged.Add(key)) Logger?.LogWarning("[Ermine] " + text + " (further ones suppressed)");
    }

    /// <summary>Puts it up for sale at the career dealer (core's Api.Career; nothing happens without a career).</summary>
    private static void ListAtDealer()
    {
        try { OverTheHillPlus.Api.Career.Dealer.List(ModId, ErmineDefinition.DealerOffer()); }
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
            Description = "A trail snowmobile: skis, a rubber track, handlebars and a seat, built to the real ones' size. It grips " +
                          "and floats on snow and ice and scrapes to a halt on bare ground. Buy it in the garage or ride it from " +
                          "this page. Everyone in the session needs this mod.",
            Version = Version,
            DrawSimple = ErmineCar.DrawPage,
        });
        if (!ModManager.IsEnabled(ModId))
        {
            Logger.LogInfo($"{Name} is switched off on the MODS page: nothing of it runs this session.");
            return;
        }
        try
        {
            string why = ErmineCar.Start(typeof(ErminePlugin).Assembly, ModManager.ConfigFor(ModId), Logger);
            if (why != null) return;
            Harmony = new Harmony(Guid);
            PatchReport.PatchAllSafely(Harmony, typeof(ErminePlugin).Assembly, ModId);
            Features.Registry.OnTick(ErmineCar.Tick, ModId);
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
internal static class ErmineCar
{
    private static CarDefinition _def;
    private static CarDefinition Def => _def ??= ErmineDefinition.Create();

    public static void DrawPage() => ModdedCars.DrawPage(Def);

    public static string Start(System.Reflection.Assembly assets, OverTheHillPlus.Config.ConfigFile config, ModLog log)
    {
        // the model's moving-part geometry first (read from the embedded model; without it the sled would be a statue)
        var blob = ReadBlob(assets);
        string why = blob == null ? "its model (ermine.mesh) could not be read" : ErmineRig.Prepare(blob);
        if (why != null)
        {
            ModManager.MarkFailed(ErminePlugin.ModId, why);
            log?.LogWarning($"Ermine: {why}. The sled stays off this session.");
            return why;
        }
        return ModdedCars.Start(Def, assets, config, log, new CarHooks { AfterModel = ErmineRig.AfterModel });
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
        catch (Exception e) { DebugLog.Error(ErminePlugin.ModId, e, "ErmineCar.ReadBlob"); return null; }
    }

    public static void Tick()
    {
        try { ErmineCore.Tick(); }
        catch (Exception e) { DebugLog.Error(ErminePlugin.ModId, e, "ErmineCar.Tick"); ErminePlugin.LogOnce("tick", "the frame update failed: " + e.Message); }
    }
}

/// <summary>The sled's grip and drag go in with the car's physics step (VPVehicleController.DoUpdateBlocks), after
/// Terrain+'s prefix (which publishes the ground factor this multiplies in) and alongside Tyre Pressure's (which leaves
/// vehicles without tyres alone).</summary>
[HarmonyPatch(typeof(VPVehicleController), nameof(VPVehicleController.DoUpdateBlocks))]
internal static class ErmineStepPatch
{
    private static bool _loggedError;

    [HarmonyPriority(Priority.Normal)]
    private static void Prefix(VPVehicleController __instance)
    {
        try { ErmineCore.PhysicsStep(__instance); }
        catch (Exception e)
        {
            if (_loggedError) return;
            _loggedError = true;
            ErminePlugin.Logger?.LogError("Ermine physics step failed (further errors suppressed): " + e);
        }
    }
}
