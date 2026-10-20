using System;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;

namespace OverTheHillPlus.Flares;

// After the namespace line on purpose: the game has its own global ModManager (mod.io).
using OverTheHillPlus.Core.Manager;

/// <summary>
/// Flares+: keep track of friends while exploring. Name tags over other players' cars stay up much farther away
/// (fading and shrinking with distance, with the distance in metres), and a key fires a flare that climbs with a glow
/// and a smoke trail in your player colour, which every player with the mod sees from far away (also as a ping on the
/// map, compass and minimap). Optionally a flare goes up by itself when you lie stuck on your side or roof. See
/// <see cref="FlaresCore"/> and <see cref="NameTags"/>.
/// </summary>
[BepInPlugin(Guid, Name, Version)]
[BepInDependency(Core.CorePlugin.Guid)]
public class FlaresPlugin : BasePlugin
{
    public const string Guid = "com.oth.flares";
    public const string Name = "Flares+";
    public const string Version = "1.0.1";

    /// <summary>Mod id: names the config file (oth.flares.cfg), the MODS page entry and the enabled switch.</summary>
    internal const string ModId = "flares";

    internal static ManualLogSource Logger;
    internal static Harmony Harmony;

    private static bool _loggedTick;

    public override void Load()
    {
        Logger = Log;
        // Listed on the MODS page even when switched off, so it can be switched back on.
        ModManager.Register(new ModInfo
        {
            Id = ModId,
            Side = ModSide.Client,
            Name = Name,
            Description = "Keep track of friends: name tags over other players' cars stay visible much farther away, with the " +
                          "distance in metres, and a key fires a flare in your player colour that every player with the mod " +
                          "sees from far away. A flare can go up by itself when you are stuck on your side or roof.",
            Version = Version,
            DrawSimple = FlaresSimple.Draw,
        });
        if (!ModManager.IsEnabled(ModId))
        {
            Logger.LogInfo($"{Name} is switched off on the MODS page: nothing of it runs this session.");
            return;
        }
        try
        {
            FlaresConfig.Bind(ModManager.ConfigFor(ModId)); // BepInEx/config/oth.flares.cfg
            FlaresConfig.File.Save(); // writes the defaults on first start
            FlaresCore.Install();
            CoreBehaviour.AfterFeatureTicks += Tick;
            CoreBehaviour.Hotkeys += Keys;
            CoreBehaviour.AfterGuis += FlaresHud.Draw;
            Harmony = new Harmony(Guid);
            PatchReport.PatchAllSafely(Harmony, typeof(FlaresPlugin).Assembly, ModId);
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
        try { FlaresCore.Tick(); }
        catch (Exception e)
        {
            if (_loggedTick) return;
            _loggedTick = true;
            Logger.LogError("Flares+ tick failed (further errors suppressed): " + e);
        }
    }

    private static void Keys(UnityEngine.InputSystem.Keyboard kb)
    {
        try { FlaresCore.Keys(kb); }
        catch (Exception e) { DebugLog.Error(ModId, e, "FlaresPlugin.Keys"); }
    }
}
