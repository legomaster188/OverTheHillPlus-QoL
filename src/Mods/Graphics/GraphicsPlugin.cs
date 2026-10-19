using System;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;

namespace OverTheHillPlus.Graphics;

// After the namespace line on purpose: the game has its own global ModManager (mod.io).
using OverTheHillPlus.Core.Manager;

/// <summary>
/// Graphics+: a better-looking (or faster) game. Quality tiers and saved graphics profiles, render scale, model detail
/// and shadows, lighting, colour looks (post-processing), sky and clouds, fog, water and wet weather. Only changes what
/// you see.
/// </summary>
[BepInPlugin(Guid, Name, Version)]
[BepInDependency(Core.CorePlugin.Guid)]
public class GraphicsPlugin : BasePlugin
{
    public const string Guid = "com.oth.graphics";
    public const string Name = "Graphics+";
    public const string Version = "1.2.0";

    /// <summary>Mod id: names the config file (oth.graphics.cfg), the MODS page entry and the enabled switch.</summary>
    internal const string ModId = "graphics";

    internal static ManualLogSource Logger;
    internal static Harmony Harmony;

    public override void Load()
    {
        Logger = Log;
        // Listed on the MODS page even when switched off, so it can be switched back on.
        ModManager.Register(new ModInfo
        {
            Id = ModId,
            Side = ModSide.Client,
            Name = Name,
            Description = "Graphics options past the game's own: quality tiers and profiles, render scale, detail and " +
                          "shadows, lighting, colour looks, sky, fog, water and wet weather. Only changes what you see.",
            Version = Version,
            DrawSimple = GraphicsSimple.Draw,
        });
        if (!ModManager.IsEnabled(ModId))
        {
            Logger.LogInfo($"{Name} is switched off on the MODS page: nothing of it runs this session.");
            return;
        }
        try
        {
            GraphicsConfig.Bind(ModManager.ConfigFor(ModId)); // BepInEx/config/oth.graphics.cfg
            Features.Registry.DiscoverAndRegister(typeof(GraphicsPlugin).Assembly, ModId, GraphicsConfig.File);
            GraphicsConfig.File.Save(); // writes the defaults of every entry on first start
            Harmony = new Harmony(Guid);
            PatchReport.PatchAllSafely(Harmony, typeof(GraphicsPlugin).Assembly, ModId);
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
