using System;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;

namespace OverTheHillPlus.Minimap;

// After the namespace line on purpose: the game has its own global ModManager (mod.io).
using OverTheHillPlus.Core.Manager;

/// <summary>
/// Minimap: a corner map centred on your car, with the other players, map pins and the route to your pin, drawn on
/// the game's own map picture and fog of war. Local reads only; nothing is sent to anyone.
/// </summary>
[BepInPlugin(Guid, Name, Version)]
[BepInDependency(Core.CorePlugin.Guid)]
public class MinimapPlugin : BasePlugin
{
    public const string Guid = "com.oth.minimap";
    public const string Name = "Minimap";
    public const string Version = "1.0.0";

    /// <summary>Mod id: names the config file (oth.minimap.cfg), the MODS page entry and the enabled switch.</summary>
    internal const string ModId = "minimap";

    internal static ManualLogSource Logger;

    public override void Load()
    {
        Logger = Log;
        ModManager.Register(new ModInfo
        {
            Id = ModId,
            Side = ModSide.Client,
            Name = Name,
            Description = "A corner minimap with the other players, map pins and the route to your pin, on the game's " +
                          "own map, with zoom, size and opacity.",
            Version = Version,
            DrawSimple = MinimapSimple.Draw,
        });
        if (!ModManager.IsEnabled(ModId))
        {
            Logger.LogInfo($"{Name} is switched off on the MODS page: nothing of it runs this session.");
            return;
        }
        try
        {
            MinimapConfig.Bind(ModManager.ConfigFor(ModId)); // BepInEx/config/oth.minimap.cfg
            MinimapFrame.Install();
            Features.Registry.DiscoverAndRegister(typeof(MinimapPlugin).Assembly, ModId, MinimapConfig.File);
            MinimapConfig.File.Save();
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
