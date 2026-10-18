using System;
using HarmonyLib;

namespace OverTheHillPlus.WorldTools;

/// <summary>Quitting to the menu: put the world back while the session still exists (see WorldControl.OnLeavingSession).</summary>
[HarmonyPatch(typeof(OverlandNetworkRunner), nameof(OverlandNetworkRunner.QuitToMenuAsync))]
internal static class WorldToolsQuitToMenuPatch
{
    private static void Prefix()
    {
        try { WorldControl.OnLeavingSession(); }
        catch (Exception e) { DebugLog.Error("worldtools", e, "WorldToolsQuitToMenuPatch.Prefix"); WorldControl.LogOnce("quit-menu", "cleanup on quit to menu: " + e.Message); }
    }
}

/// <summary>Quitting to the desktop from a session.</summary>
[HarmonyPatch(typeof(OverlandNetworkRunner), nameof(OverlandNetworkRunner.QuitGameAsync))]
internal static class WorldToolsQuitGamePatch
{
    private static void Prefix()
    {
        try { WorldControl.OnLeavingSession(); }
        catch (Exception e) { DebugLog.Error("worldtools", e, "WorldToolsQuitGamePatch.Prefix"); WorldControl.LogOnce("quit-game", "cleanup on quit: " + e.Message); }
    }
}
