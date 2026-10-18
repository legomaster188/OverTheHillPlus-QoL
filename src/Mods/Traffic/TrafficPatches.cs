using System;
using HarmonyLib;
using VehiclePhysics;

namespace OverTheHillPlus.Traffic;

/// <summary>
/// Writes an AI Traffic car's pedals and steering onto its data bus right before VPP's blocks read it (once per
/// physics step per car). The same hook (no parameters besides the instance) Convoy AI and the driving assists use;
/// every one of them only touches its own cars.
/// </summary>
[HarmonyPatch(typeof(VPVehicleController), nameof(VPVehicleController.DoUpdateBlocks))]
internal static class TrafficDoUpdateBlocksPatch
{
    private static bool _loggedError;

    private static void Prefix(VPVehicleController __instance)
    {
        try { TrafficAi.PhysicsStep(__instance); }
        catch (Exception e)
        {
            if (_loggedError) return;
            _loggedError = true;
            TrafficPlugin.Logger?.LogError("[Traffic] physics step failed (further errors suppressed): " + e);
        }
    }
}

/// <summary>Quitting to the menu: take the AI cars away while the session still exists.</summary>
[HarmonyPatch(typeof(OverlandNetworkRunner), nameof(OverlandNetworkRunner.QuitToMenuAsync))]
internal static class TrafficQuitToMenuPatch
{
    private static void Prefix()
    {
        try { TrafficAi.OnLeavingSession(); }
        catch (Exception e) { DebugLog.Error("traffic", e, "TrafficQuitToMenuPatch.Prefix"); TrafficAi.LogOnce("quit-menu", "cleanup on quit to menu: " + e.Message); }
    }
}

/// <summary>Quitting to the desktop from a session.</summary>
[HarmonyPatch(typeof(OverlandNetworkRunner), nameof(OverlandNetworkRunner.QuitGameAsync))]
internal static class TrafficQuitGamePatch
{
    private static void Prefix()
    {
        try { TrafficAi.OnLeavingSession(); }
        catch (Exception e) { DebugLog.Error("traffic", e, "TrafficQuitGamePatch.Prefix"); TrafficAi.LogOnce("quit-game", "cleanup on quit: " + e.Message); }
    }
}
