using System;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;

namespace OverTheHillPlus.Chat;

// After the namespace line on purpose: the game has its own global ModManager (mod.io).
using OverTheHillPlus.Core.Manager;

/// <summary>
/// Chat: text chat with the other players who run the mods, over core's mod network (through the host), with the
/// word filter, muting and the on-screen chat box.
/// </summary>
[BepInPlugin(Guid, Name, Version)]
[BepInDependency(Core.CorePlugin.Guid)]
public class ChatPlugin : BasePlugin
{
    public const string Guid = "com.oth.chat";
    public const string Name = "Chat";
    public const string Version = "1.0.0";

    /// <summary>Mod id: names the config file (oth.chat.cfg), the MODS page entry and the enabled switch.</summary>
    internal const string ModId = "chat";

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
            Description = "Text chat with the other players who run these mods. Players without them never see it, " +
                          "and every message passes the word filter.",
            Version = Version,
            DrawSimple = ChatSimple.Draw,
        });
        if (!ModManager.IsEnabled(ModId))
        {
            Logger.LogInfo($"{Name} is switched off on the MODS page: nothing of it runs this session.");
            return;
        }
        try
        {
            ChatConfig.Bind(ModManager.ConfigFor(ModId)); // BepInEx/config/oth.chat.cfg
            ChatFrame.Install();
            Features.Registry.DiscoverAndRegister(typeof(ChatPlugin).Assembly, ModId, ChatConfig.File);
            ChatConfig.File.Save(); // writes the defaults of every entry on first start
            Harmony = new Harmony(Guid);
            PatchReport.PatchAllSafely(Harmony, typeof(ChatPlugin).Assembly, ModId);
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
