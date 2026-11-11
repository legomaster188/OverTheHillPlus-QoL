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

namespace OverTheHillPlus.Meerkat;

// After the namespace line on purpose: the game has its own global ModManager (mod.io).
using OverTheHillPlus.Core.Manager;

/// <summary>
/// Meerkat: a lifted safari-rally roadster as a new networked car. Its definition (<see cref="MeerkatDefinition"/>) and
/// its model and textures (Assets/) go to core's car framework, which builds it from the Brick's network and physics
/// skeleton (the garage entry, the saves, the join rules, the tuning, paint, skins and engine sound and their sync). This
/// mod adds what is the Meerkat's own: rear-wheel drive, the pop-up headlamps, the build (lift stages, tyres, the top,
/// the bolt-on parts, engine stages) as career upgrades and free-play choices, shown on every peer (mod-net channel 50),
/// and the tyres' grip and size.
/// </summary>
#if MELON
public class MeerkatPlugin : OverTheHillPlus.Loader.OthMod
#else
[BepInPlugin(Guid, Name, Version)]
// Core 1.1.0 has the car framework (an older core can't load this mod).
[BepInDependency(Core.CorePlugin.Guid, ">=1.1.0")]
public class MeerkatPlugin : BasePlugin
#endif
{
    public const string Guid = "com.oth.meerkat";
    public const string Name = "Meerkat";
    public const string Version = "1.0.7";

    /// <summary>Mod id: names the config file (oth.meerkat.cfg), the MODS page entry and the enabled switch.</summary>
    internal const string ModId = MeerkatDefinition.Id;

    internal static ModLog Logger;
    internal static Harmony Harmony;
    private static readonly HashSet<string> Logged = new();

    internal static void LogOnce(string key, string text)
    {
        if (Logged.Add(key)) Logger?.LogWarning("[Meerkat] " + text + " (further ones suppressed)");
    }

    public override void Load()
    {
        Logger = Log;
        ModManager.Register(new ModInfo
        {
            Id = ModId,
            Side = ModSide.Server,
            Name = Name,
            Description = "A little two-seat roadster with pop-up headlamps, lifted and kitted out for the rough: all-terrains, a " +
                          "hardtop with a rack and lamps, a skid plate, flares and flaps. Change the lift, the tyres, the top and the " +
                          "parts on this page (or buy them in a career). Everyone in the session needs this mod.",
            Version = Version,
            DrawSimple = MeerkatCar.DrawPage,
        });
        if (!ModManager.IsEnabled(ModId))
        {
            Logger.LogInfo($"{Name} is switched off on the MODS page: nothing of it runs this session.");
            return;
        }
        try
        {
            var cfg = ModManager.ConfigFor(ModId);
            MeerkatConfig.Bind(cfg);
            string why = MeerkatCar.Start(typeof(MeerkatPlugin).Assembly, cfg, Logger);
            if (why != null) return;
            Harmony = new Harmony(Guid);
            PatchReport.PatchAllSafely(Harmony, typeof(MeerkatPlugin).Assembly, ModId);
            MeerkatNet.Install();
            MeerkatCareer.Install();
            Features.Registry.OnTick(MeerkatCar.Tick, ModId);
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
internal static class MeerkatCar
{
    private static CarDefinition _def;
    internal static CarDefinition Def => _def ??= MeerkatDefinition.Create();

    public static void DrawPage()
    {
        ModdedCars.DrawPage(Def);
        try { MeerkatPage.Draw(); }
        catch (Exception e) { DebugLog.Error(MeerkatPlugin.ModId, e, "MeerkatPage.Draw"); }
    }

    public static string Start(System.Reflection.Assembly assets, OverTheHillPlus.Config.ConfigFile config, ModLog log)
    {
        var blob = ReadBlob(assets);
        string why = blob == null ? "its model (meerkat.mesh) could not be read" : MeerkatRig.Prepare(blob);
        if (why != null)
        {
            ModManager.MarkFailed(MeerkatPlugin.ModId, why);
            log?.LogWarning($"Meerkat: {why}. The car stays off this session.");
            return why;
        }
        return ModdedCars.Start(Def, assets, config, log, new CarHooks
        {
            AfterModel = MeerkatRig.AfterModel,
            AfterTemplate = MeerkatRig.AfterTemplate,
        });
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
        catch (Exception e) { DebugLog.Error(MeerkatPlugin.ModId, e, "MeerkatCar.ReadBlob"); return null; }
    }

    public static void Tick()
    {
        try { MeerkatLive.Tick(); }
        catch (Exception e) { DebugLog.Error(MeerkatPlugin.ModId, e, "MeerkatCar.Tick"); MeerkatPlugin.LogOnce("tick", "the frame update failed: " + e.Message); }
    }
}

/// <summary>The street and mud tyres' grip goes in with the car's physics step (VPVehicleController.DoUpdateBlocks), after
/// Terrain+'s prefix (which publishes the ground factor this multiplies in), as the one writer of each wheel while no other
/// writer (Tyre Pressure) has it.</summary>
[HarmonyPatch(typeof(VPVehicleController), nameof(VPVehicleController.DoUpdateBlocks))]
internal static class MeerkatStepPatch
{
    private static bool _loggedError;

    [HarmonyPriority(Priority.Normal)]
    private static void Prefix(VPVehicleController __instance)
    {
        try { MeerkatPhysics.Step(__instance); }
        catch (Exception e)
        {
            if (_loggedError) return;
            _loggedError = true;
            MeerkatPlugin.Logger?.LogError("Meerkat physics step failed (further errors suppressed): " + e);
        }
    }
}
