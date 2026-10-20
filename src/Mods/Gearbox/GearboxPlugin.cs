using System;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;

namespace OverTheHillPlus.GearboxPlus;

// After the namespace line on purpose: the game has its own global ModManager (mod.io).
using OverTheHillPlus.Core.Manager;

/// <summary>
/// Gearbox+: more forward gears for every car (3 to 5, continuing each car's own step from 1st to 2nd, or the real car's
/// gearbox, with 4H / 4L as before), a real neutral between reverse and 1st on the manual gearbox, extra shift keys, a gear
/// indicator, and the differentials (open, limited slip or locked, with a lock key; see <see cref="CarDiffs"/>).
///
/// <para>A Server mod: the gear table decides how the car drives, and the host simulates every car while each player's
/// game predicts their own, so everyone in the session needs the same gears (the same number set). Guests run it only
/// while the host lists it; other players need nothing to see your car's revs and sound (the car's RPM and gear number
/// are already part of the network state), but a player without it, in a session that runs it, would drive 2-gear
/// cars against a host with more and get pulled back by the host.</para>
/// </summary>
[BepInPlugin(Guid, Name, Version)]
[BepInDependency(Core.CorePlugin.Guid)]
public class GearboxPlugin : BasePlugin
{
    public const string Guid = "com.oth.gearbox";
    public const string Name = "Gearbox+";
    public const string Version = "1.0.0";

    /// <summary>Mod id: names the config file (oth.gearbox.cfg), the MODS page entry and the enabled switch.</summary>
    internal const string ModId = "gearbox";

    internal static ManualLogSource Logger;
    internal static Harmony Harmony;

    public override void Load()
    {
        Logger = Log;
        ModManager.Register(new ModInfo
        {
            Id = ModId,
            Side = ModSide.Server,
            Name = Name,
            Description = "More gears for every car: a 3rd to 5th forward gear that continues each car's own step, or the real " +
                          "car's gearbox (a close 5-speed, a truck's 4 or 5 speeds, a vintage 3-speed, a kei truck's low first); " +
                          "4H and 4L as before. A real neutral, extra shift keys, a gear indicator, and open, limited-slip or locked " +
                          "differentials with a lock key.",
            Version = Version,
            DrawSimple = GearboxSimple.Draw,
        });
        if (!ModManager.IsEnabled(ModId))
        {
            Logger.LogInfo($"{Name} is switched off on the MODS page: nothing of it runs this session.");
            return;
        }
        try
        {
            GearboxConfig.Bind(ModManager.ConfigFor(ModId)); // BepInEx/config/oth.gearbox.cfg
            GearboxFrame.Install();
            CarDiffs.Install();
            Features.Registry.DiscoverAndRegister(typeof(GearboxPlugin).Assembly, ModId, GearboxConfig.File);
            GearboxConfig.File.Save();
            Harmony = new Harmony(Guid);
            PatchReport.PatchAllSafely(Harmony, typeof(GearboxPlugin).Assembly, ModId);
            Logger.LogInfo($"{Name} {Version} loaded.");
            DebugLog.Info(ModId, $"loaded {Version}");
        }
        catch (Exception e)
        {
            ModManager.MarkFailed(ModId, e.Message);
            Logger.LogError($"{Name} failed to start: {e}");
        }
    }
}
