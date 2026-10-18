using System;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;

namespace OverTheHillPlus.Tag;

// After the namespace line on purpose: the game has its own global ModManager (mod.io).
using OverTheHillPlus.Core.Manager;

/// <summary>
/// Tag & Hide and Seek: party rounds the host runs for the whole session. Tag: whoever is "it" is marked on
/// everyone's map and HUD, and passes it on by touching another car (no tag-backs for a few seconds); least time as
/// it wins. Hide and seek: seekers wait out a head start with their screen blacked out and their car held, their map
/// hides the hiders, a touch finds a hider (who then seeks too), and the last hider wins.
/// The host decides everything (it simulates every car, so it sees every touch); modded players get the round over
/// the mod network, and vanilla players get the host's pop-up announcements.
/// </summary>
[BepInPlugin(Guid, Name, Version)]
[BepInDependency(Core.CorePlugin.Guid)]
public class TagPlugin : BasePlugin
{
    public const string Guid = "com.oth.tag";
    public const string Name = "Tag & Hide and Seek";
    public const string Version = "1.0.0";

    /// <summary>Mod id: names the config file (oth.tag.cfg), the MODS page entry and the enabled switch.</summary>
    internal const string ModId = "tag";

    internal static ManualLogSource Logger;
    internal static Harmony Harmony;

    public override void Load()
    {
        Logger = Log;
        // Listed on the MODS page even when switched off, so it can be switched back on.
        ModManager.Register(new ModInfo
        {
            Id = ModId,
            Side = ModSide.Server,
            Name = Name,
            Description = "Tag and hide and seek rounds for the whole session, run by the host: \"it\" shown on " +
                          "everyone's map and HUD, tags by touching cars, a round timer and scores; hide and seek " +
                          "with a head start, hidden hiders on the seekers' map, and the last hider wins.",
            Version = Version,
            DrawSimple = TagSimple.Draw,
            DrawAdvanced = TagSimple.DrawAdvanced,
        });
        if (!ModManager.IsEnabled(ModId))
        {
            Logger.LogInfo($"{Name} is switched off on the MODS page: nothing of it runs this session.");
            return;
        }
        try
        {
            TagConfig.Bind(ModManager.ConfigFor(ModId)); // BepInEx/config/oth.tag.cfg
            Features.Registry.DiscoverAndRegister(typeof(TagPlugin).Assembly, ModId, TagConfig.File);
            TagConfig.File.Save(); // writes the defaults of every entry on first start
            Harmony = new Harmony(Guid);
            PatchReport.PatchAllSafely(Harmony, typeof(TagPlugin).Assembly, ModId);
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
