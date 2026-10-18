using System;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;

namespace OverTheHillPlus.CarScale;

// After the namespace line on purpose: the game has its own global ModManager (mod.io).
using OverTheHillPlus.Core.Manager;

/// <summary>
/// Car Scale (private, Server): mini and giant cars, for you, for everyone or per player. The host resizes the cars
/// with their physics; modded players show the same sizes from the host's table (ModNet channel 12). See
/// <see cref="CarScaleCore"/> and <see cref="ScaledCar"/>.
/// </summary>
[BepInPlugin(Guid, Name, Version)]
[BepInDependency(Core.CorePlugin.Guid)]
public class CarScalePlugin : BasePlugin
{
    public const string Guid = "com.oth.carscale";
    public const string Name = "Car Scale";
    public const string Version = "1.0.0";

    /// <summary>Mod id: names the config file (oth.carscale.cfg), the MODS page entry and the enabled switch.</summary>
    internal const string ModId = "carscale";

    internal static ManualLogSource Logger;

    private static bool _loggedTick;

    public override void Load()
    {
        Logger = Log;
        ModManager.Register(new ModInfo
        {
            Id = ModId,
            Side = ModSide.Server,
            Name = Name,
            Description = "Mini and giant cars: your car, everyone's, or a size per player. The weight, tyres, " +
                          "suspension and brakes follow the size. Everyone in the session needs it to see the sizes.",
            Version = Version,
            Private = false,
            DrawSimple = CarScaleSimple.Draw,
            DrawAdvanced = CarScaleSimple.DrawAdvanced,
        });
        if (!ModManager.IsEnabled(ModId))
        {
            Logger.LogInfo($"{Name} is switched off on the MODS page: nothing of it runs this session.");
            return;
        }
        try
        {
            CarScaleConfig.Bind(ModManager.ConfigFor(ModId)); // BepInEx/config/oth.carscale.cfg
            CarScaleConfig.File.Save(); // writes the defaults on first start
            CarScaleCore.Install();
            CoreBehaviour.AfterFeatureTicks += Tick;
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
        try { CarScaleCore.Tick(); }
        catch (Exception e)
        {
            if (_loggedTick) return;
            _loggedTick = true;
            Logger.LogError("Car Scale tick failed (further errors suppressed): " + e);
        }
    }
}
