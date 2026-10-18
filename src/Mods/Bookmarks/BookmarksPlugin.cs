using System;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using UnityEngine.InputSystem;

namespace OverTheHillPlus.Bookmarks;

// After the namespace line on purpose: the game has its own global ModManager (mod.io).
using OverTheHillPlus.Core.Manager;

/// <summary>
/// Teleport Bookmarks (private): save and name the spots you like on each map, then jump back to them (solo or as
/// the host), with safe ground placement and a "back" step. Bookmarks live in BepInEx/config/oth.bookmarks.json.
/// </summary>
[BepInPlugin(Guid, Name, Version)]
[BepInDependency(Core.CorePlugin.Guid)]
public class BookmarksPlugin : BasePlugin
{
    public const string Guid = "com.oth.bookmarks";
    public const string Name = "Teleport Bookmarks";
    public const string Version = "1.0.0";

    /// <summary>Mod id: names the config file (oth.bookmarks.cfg), the MODS page entry and the enabled switch.</summary>
    internal const string ModId = "bookmarks";

    internal static ManualLogSource Logger;

    private static bool _loggedKeys;

    public override void Load()
    {
        Logger = Log;
        ModManager.Register(new ModInfo
        {
            Id = ModId,
            Side = ModSide.Client,
            Name = Name,
            Description = "Save and name spots on each map, then jump back to them (solo or as the host). Safe ground " +
                          "is found near the spot, and you can step back to where you were.",
            Version = Version,
            Private = false,
            DrawSimple = BookmarksSimple.Draw,
            DrawAdvanced = BookmarksSimple.DrawAdvanced,
        });
        if (!ModManager.IsEnabled(ModId))
        {
            Logger.LogInfo($"{Name} is switched off on the MODS page: nothing of it runs this session.");
            return;
        }
        try
        {
            BookmarksConfig.Bind(ModManager.ConfigFor(ModId)); // BepInEx/config/oth.bookmarks.cfg
            BookmarksConfig.File.Save(); // writes the defaults on first start
            CoreBehaviour.Hotkeys += Hotkeys;
            Logger.LogInfo($"{Name} {Version} loaded ({BookmarkJump.Store.Total} bookmark(s)).");
            DebugLog.Info(ModId, $"loaded {Version}");
        }
        catch (Exception e)
        {
            // The page says "Failed to start"; core and the other mods keep running.
            ModManager.MarkFailed(ModId, e.Message);
            Logger.LogError($"{Name} failed to start: {e}");
        }
    }

    private static void Hotkeys(Keyboard kb)
    {
        try
        {
            if (CoreBehaviour.Pressed(kb, BookmarksConfig.SaveKey.Value)) Notify(BookmarkJump.SaveHere(null));
            if (CoreBehaviour.Pressed(kb, BookmarksConfig.BackKey.Value)) Notify(BookmarkJump.Back());
        }
        catch (Exception e)
        {
            if (_loggedKeys) return;
            _loggedKeys = true;
            Logger.LogError("Bookmarks hotkeys failed (further errors suppressed): " + e);
        }
    }

    private static void Notify(string message)
    {
        Menu.SetStatus(message);
        Logger.LogInfo("[Bookmarks] " + message);
    }
}
