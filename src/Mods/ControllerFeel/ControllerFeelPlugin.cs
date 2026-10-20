using System;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;

namespace OverTheHillPlus.ControllerFeel;

// After the namespace line on purpose: the game has its own global ModManager (mod.io).
using OverTheHillPlus.Core.Manager;

/// <summary>
/// Controller Feel: gamepad rumble from what the car is doing. Surface texture (gravel, rock, snow, sand, mud...) from the
/// wheels' ground contact and speed, suspension hits and bottoming out, landings after jumps, wheelspin, lock-ups and
/// skids, the engine's idle shake and the winch rope's strain, each with its own strength. It adds to the game's own
/// controller rumble (ControllerVibrationManager) and can replace it. Local only: nothing is sent to anyone. See
/// <see cref="ControllerFeelCore"/>, <see cref="FeelMixer"/> and <see cref="PadOutput"/>.
/// </summary>
[BepInPlugin(Guid, Name, Version)]
[BepInDependency(Core.CorePlugin.Guid)]
public class ControllerFeelPlugin : BasePlugin
{
    public const string Guid = "com.oth.controllerfeel";
    public const string Name = "Controller Feel";
    public const string Version = "1.0.0";

    /// <summary>Mod id: names the config file (oth.controllerfeel.cfg), the MODS page entry and the enabled switch.</summary>
    internal const string ModId = "controllerfeel";

    internal static ManualLogSource Logger;
    internal static Harmony Harmony;

    private static bool _loggedTick;

    public override void Load()
    {
        Logger = Log;
        ModManager.Register(new ModInfo
        {
            Id = ModId,
            Side = ModSide.Client,
            Name = Name,
            Description = "Gamepad rumble from what the car is doing: the surface under the wheels (gravel, rock, snow, sand), " +
                          "bumps and bottoming out, landings, wheelspin, lock-ups, the engine at idle and the winch rope. A strength " +
                          "for each, quiet in menus and on pause. Adds to the game's own rumble. Only you feel it.",
            Version = Version,
            DrawSimple = ControllerFeelSimple.Draw,
        });
        if (!ModManager.IsEnabled(ModId))
        {
            Logger.LogInfo($"{Name} is switched off on the MODS page: nothing of it runs this session.");
            return;
        }
        try
        {
            ControllerFeelConfig.Bind(ModManager.ConfigFor(ModId)); // BepInEx/config/oth.controllerfeel.cfg
            ControllerFeelConfig.File.Save(); // writes the defaults on first start
            ControllerFeelCore.Install();
            CoreBehaviour.AfterFeatureTicks += Tick;
            Harmony = new Harmony(Guid);
            PatchReport.PatchAllSafely(Harmony, typeof(ControllerFeelPlugin).Assembly, ModId);
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
        try { ControllerFeelCore.Tick(); }
        catch (Exception e)
        {
            if (_loggedTick) return;
            _loggedTick = true;
            Logger.LogError("Controller Feel tick failed (further errors suppressed): " + e);
        }
    }
}
