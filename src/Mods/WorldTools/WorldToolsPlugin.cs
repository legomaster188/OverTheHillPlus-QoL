using System;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;

namespace OverTheHillPlus.WorldTools;

// After the namespace line on purpose: the game has its own global ModManager (mod.io).
using OverTheHillPlus.Core.Manager;

/// <summary>
/// World Tools+ (private, Host): gravity for the whole world (with a moon mode), game speed, day length and a weather
/// lock, for everyone in your session. Only the host needs it: the host's game simulates the world, and the time and
/// weather go through the game's own networked state, so vanilla guests follow too. See <see cref="WorldControl"/>
/// for how each one works and why the game speed uses the game's own time-scale RPC.
/// </summary>
[BepInPlugin(Guid, Name, Version)]
[BepInDependency(Core.CorePlugin.Guid)]
public class WorldToolsPlugin : BasePlugin
{
    public const string Guid = "com.oth.worldtools";
    public const string Name = "World Tools+";
    public const string Version = "1.0.0";

    /// <summary>Mod id: names the config file (oth.worldtools.cfg), the MODS page entry and the enabled switch.</summary>
    internal const string ModId = "worldtools";

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
            Description = "Gravity (with a moon mode), game speed, day length and a weather lock for everyone in your " +
                          "session. Works while you host or play solo; guests don't need it.",
            Version = Version,
            Private = false,
            DrawSimple = WorldToolsSimple.Draw,
            DrawAdvanced = WorldToolsSimple.DrawAdvanced,
        });
        if (!ModManager.IsEnabled(ModId))
        {
            Logger.LogInfo($"{Name} is switched off on the MODS page: nothing of it runs this session.");
            return;
        }
        try
        {
            WorldToolsConfig.Bind(ModManager.ConfigFor(ModId)); // BepInEx/config/oth.worldtools.cfg
            WorldToolsConfig.File.Save(); // writes the defaults on first start
            Harmony = new Harmony(Guid);
            PatchReport.PatchAllSafely(Harmony, typeof(WorldToolsPlugin).Assembly, ModId); // the quit prefixes
            CoreBehaviour.AfterFeatureTicks += Tick;
            CoreBehaviour.OnQuit += WorldControl.RestoreAll;
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
        try { WorldControl.Tick(); }
        catch (Exception e)
        {
            if (_loggedTick) return;
            _loggedTick = true;
            Logger.LogError("World Tools+ tick failed (further errors suppressed): " + e);
        }
    }
}
