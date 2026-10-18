using System;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;

namespace OverTheHillPlus.CustomCosmetics;

// After the namespace line on purpose: the game has its own global ModManager (mod.io).
using OverTheHillPlus.Core.Manager;

/// <summary>
/// Custom Cosmetics (private): brand-new cosmetic parts (light bars, bull bars, roof racks, ...) for the garage's cosmetics tabs,
/// on the Mod Manager's own cars first (the Vera, Terrier, Carpati, Hilo, Bastion) and, when switched on, on the
/// game's cars. Each part is a folder of data in the DLL (<see cref="PartLibrary"/>); the parts are real
/// <c>VehicleCosmetic</c> items at the END of each car's part list (<see cref="PartObjects"/>), so the game's own garage
/// buys, mounts, shows and syncs them by its mounted-parts bitset; what a player owns is kept in a sidecar file, never in
/// the game's save (<see cref="OwnershipStore"/>). Nobody is required to have the mod: a player without it ignores the
/// bit, a player with it sees the part.
/// </summary>
[BepInPlugin(Guid, Name, Version)]
[BepInDependency(Core.CorePlugin.Guid)]
public class CustomCosmeticsPlugin : BasePlugin
{
    public const string Guid = "com.oth.customcosmetics";
    public const string Name = "Custom Cosmetics";
    public const string Version = "1.0.0";

    /// <summary>Mod id: names the config file (oth.customcosmetics.cfg), the MODS page entry and the enabled switch.</summary>
    internal const string ModId = "customcosmetics";

    internal static ManualLogSource Logger;
    internal static Harmony Harmony;
    /// <summary>Set when a start step failed: the feature is off and the page says why.</summary>
    internal static string Problem;

    public override void Load()
    {
        Logger = Log;
        ModManager.Register(new ModInfo
        {
            Id = ModId,
            Side = ModSide.Client,
            Name = Name,
            Description = "New cosmetic parts for the garage: light bars, bull bars, roof racks and more, on the Mod Manager's cars " +
                          "(and, if you switch it on, the game's). Players with the mod see your parts; players without it just don't.",
            Version = Version,
            Private = false,
            DrawSimple = CustomCosmeticsSimple.Draw,
        });
        if (!ModManager.IsEnabled(ModId))
        {
            Logger.LogInfo($"{Name} is switched off on the MODS page: nothing of it runs this session.");
            return;
        }
        try
        {
            CustomCosmeticsConfig.Bind(ModManager.ConfigFor(ModId)); // BepInEx/config/oth.customcosmetics.cfg
            Features.Registry.DiscoverAndRegister(typeof(CustomCosmeticsPlugin).Assembly, ModId, CustomCosmeticsConfig.File);
            CustomCosmeticsConfig.File.Save();
            // Ids already used by the game are only known once its catalog loads; the library checks against them then.
            PartLibrary.Load(typeof(CustomCosmeticsPlugin).Assembly, id => CosmeticsFeature.IdTaken(id), Logger);
            Harmony = new Harmony(Guid);
            int failed = PatchReport.PatchAllSafely(Harmony, typeof(CustomCosmeticsPlugin).Assembly, ModId);
            if (failed > 0)
            {
                // A half-patched set could show a part nobody can buy or mount: take the whole set back out.
                Problem = $"{failed} game hook(s) did not apply (a game update?); the parts are switched off";
                Harmony.UnpatchSelf();
                Logger.LogError($"{Name}: {Problem}.");
                return;
            }
            Logger.LogInfo($"{Name} {Version} loaded.");
            DebugLog.Info(ModId, $"loaded {Version}");
        }
        catch (Exception e)
        {
            Problem = e.Message;
            try { Harmony?.UnpatchSelf(); } catch (Exception ex) { DebugLog.Error(ModId, ex, "CustomCosmeticsPlugin.Load"); }
            ModManager.MarkFailed(ModId, e.Message);
            Logger.LogError($"{Name} failed to start: {e}");
        }
    }
}
