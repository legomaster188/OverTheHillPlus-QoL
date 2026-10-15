using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using OverTheHillPlus.ServerBrowser;

namespace OverTheHillPlus;

[BepInPlugin(Guid, Name, Version)]
public class Plugin : BasePlugin
{
    public const string Guid = "com.overthehillplus.qol";
    public const string Name = "Over The Hill Plus QoL";
    public const string Version = "0.1.0";

    internal static ManualLogSource Logger;
    internal static Harmony Harmony;

    public override void Load()
    {
        Logger = Log;
        ModConfig.Bind(Config);
        FirstSeenTracker.Load();
        Features.Registry.DiscoverAndRegister();

        Harmony = new Harmony(Guid);
        PatchAllSafely();

        AddComponent<ModBehaviour>();
        Logger.LogInfo($"{Name} {Version} loaded. Press {ModConfig.MenuKey.Value} for the menu.");
    }

    /// <summary>
    /// Same as Harmony.PatchAll, one patch class at a time. PatchAll stops at the first class whose target is missing
    /// (e.g. a method renamed by a game update) and throws out of Load, which left the rest of the mod, menu included,
    /// unloaded. Now only that one patch is skipped and logged.
    /// </summary>
    private static void PatchAllSafely()
    {
        int failed = 0;
        foreach (var type in AccessTools.GetTypesFromAssembly(typeof(Plugin).Assembly))
        {
            try { Harmony.CreateClassProcessor(type).Patch(); }
            catch (System.Exception e)
            {
                failed++;
                Logger.LogError($"Patch {type.FullName} failed and is skipped: {e.GetBaseException().Message}");
            }
        }
        if (failed > 0) Logger.LogWarning($"{failed} patch class(es) could not be applied; the related features are off this session.");
    }
}
