using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using OverTheHillPlus.ServerBrowser;

namespace OverTheHillPlus;

[BepInPlugin(Guid, Name, Version)]
public class Plugin : BasePlugin
{
    public const string Guid = "com.legom.overthehill.plus.qol";
    public const string Name = "Over The Hill Plus QoL";
    public const string Version = "0.1.0";

    internal static ManualLogSource Logger;
    internal static Harmony Harmony;

    public override void Load()
    {
        Logger = Log;
        ModConfig.Bind(Config);
        FirstSeenTracker.Load();

        Harmony = new Harmony(Guid);
        Harmony.PatchAll(typeof(Plugin).Assembly);
        if (ModConfig.ProfileServerBrowser.Value) BrowserProfiler.Install(Harmony);

        AddComponent<ModBehaviour>();
        Logger.LogInfo($"{Name} {Version} loaded. Press {ModConfig.MenuKey.Value} for the menu.");
    }
}
