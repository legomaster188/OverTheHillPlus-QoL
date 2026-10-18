using System;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;

namespace OverTheHillPlus.Convoy;

// After the namespace line on purpose: the game has its own global ModManager (mod.io).
using OverTheHillPlus.Core.Manager;

/// <summary>
/// Convoy AI (private): AI cars that follow a leader as a convoy and help each other and the players when stuck: they
/// wait, tow with the winch, repair, flip cars back and pull meteorites out. Host or solo only.
/// </summary>
[BepInPlugin(Guid, Name, Version)]
[BepInDependency(Core.CorePlugin.Guid)]
public class ConvoyPlugin : BasePlugin
{
    public const string Guid = "com.oth.convoy";
    public const string Name = "Convoy AI";
    public const string Version = "1.0.0";

    /// <summary>Mod id: names the config file (oth.convoy.cfg), the MODS page entry and the enabled switch.</summary>
    internal const string ModId = "convoy";

    internal static ManualLogSource Logger;
    internal static Harmony Harmony;

    public override void Load()
    {
        Logger = Log;
        // Listed on the MODS page even when switched off, so it can be switched back on.
        ModManager.Register(new ModInfo
        {
            Id = ModId,
            Side = ModSide.Host,
            Name = Name,
            Description = "AI cars that drive behind you as a convoy and help out: they wait, tow with the winch, repair, " +
                          "flip cars back and pull meteorites out. Solo or host.",
            Version = Version,
            Private = false,
            DrawSimple = ConvoySimple.Draw,
        });
        if (!ModManager.IsEnabled(ModId))
        {
            Logger.LogInfo($"{Name} is switched off on the MODS page: nothing of it runs this session.");
            return;
        }
        try
        {
            ConvoyConfig.Bind(ModManager.ConfigFor(ModId)); // BepInEx/config/oth.convoy.cfg
            Features.Registry.DiscoverAndRegister(typeof(ConvoyPlugin).Assembly, ModId, ConvoyConfig.File);
            ConvoyConfig.File.Save(); // writes the defaults of every entry on first start
            Harmony = new Harmony(Guid);
            PatchReport.PatchAllSafely(Harmony, typeof(ConvoyPlugin).Assembly, ModId);
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
