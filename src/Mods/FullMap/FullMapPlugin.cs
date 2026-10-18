using System;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;

namespace OverTheHillPlus.FullMap;

// After the namespace line on purpose: the game has its own global ModManager (mod.io).
using OverTheHillPlus.Core.Manager;

/// <summary>
/// Explore Full Map (private): reveals the whole map, once or every time a map loads. Local only, so it works solo,
/// hosting and as a guest.
///
/// Findings (Cpp2IL of the updated game, interop checked with the member dumper):
/// - The map's fog is MapManager.Instance.FogOfWarManager, a plain object holding the fog texture (FogOfWarTexture);
///   FogOfWarConfig only holds the player's reveal radii and the update interval. Its Update coroutine only ever
///   clears fog around the player, it never adds fog back.
/// - FogOfWarManager.ClearFullFogOfWar() clears every pixel, applies the texture and calls SaveFogOfWarTexture (the
///   game writes its own fog file). The game's RevealFogOfWar command and TestAreaUnlocker call exactly this (plus a
///   full OverlandSaver.SaveGame, not needed for the fog). ResetFogOfWar is the opposite and is not used.
/// - Cabin.RevealFogOfWar / RevealMapInRadiusDelayed reveal a radius with UpdateFogOfWarTexture and then
///   MarkAllRevealedPOIsAsDiscovered (progress). This mod does not use them: places still need a visit to count.
/// - A new world (or a new fog texture, MapManager.OnFogOfWarTextureCreated) gives a new manager or texture object;
///   "Keep map revealed" watches for that and reveals once per new map.
/// </summary>
[BepInPlugin(Guid, Name, Version)]
[BepInDependency(Core.CorePlugin.Guid)]
public class FullMapPlugin : BasePlugin
{
    public const string Guid = "com.oth.fullmap";
    public const string Name = "Explore Full Map";
    public const string Version = "1.0.0";

    /// <summary>Mod id: names the config file (oth.fullmap.cfg), the MODS page entry and the enabled switch.</summary>
    internal const string ModId = "fullmap";

    internal static ManualLogSource Logger;

    public override void Load()
    {
        Logger = Log;
        ModManager.Register(new ModInfo
        {
            Id = ModId,
            Side = ModSide.Client,
            Name = Name,
            Description = "Reveals the whole map with the game's own reveal, once or every time a map loads. " +
                          "Only your own map, so it works as a guest too.",
            Version = Version,
            Private = false,
            DrawSimple = FullMapSimple.Draw,
        });
        if (!ModManager.IsEnabled(ModId))
        {
            Logger.LogInfo($"{Name} is switched off on the MODS page: nothing of it runs this session.");
            return;
        }
        try
        {
            FullMapConfig.Bind(ModManager.ConfigFor(ModId)); // BepInEx/config/oth.fullmap.cfg
            FullMapConfig.File.Save(); // writes the defaults on first start
            MapReveal.Install();
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
}
