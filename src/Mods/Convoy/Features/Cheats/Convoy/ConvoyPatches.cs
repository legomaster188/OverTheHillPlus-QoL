using System;
using HarmonyLib;
using OverTheHillPlus.Convoy;
using VehiclePhysics;

namespace OverTheHillPlus.Features.Cheats.Convoy;

/// <summary>
/// Writes the AI's steering/pedals/gear onto an AI car's data bus right before VPP's blocks read it (once per physics
/// step per car). The same hook (no parameters besides the instance) the driving assists use.
/// </summary>
[HarmonyPatch(typeof(VPVehicleController), nameof(VPVehicleController.DoUpdateBlocks))]
internal static class ConvoyDoUpdateBlocksPatch
{
    private static bool _loggedError;

    private static void Prefix(VPVehicleController __instance)
    {
        try { ConvoyAi.PhysicsStep(__instance); }
        catch (Exception e)
        {
            if (_loggedError) return;
            _loggedError = true;
            ConvoyPlugin.Logger.LogError("[Convoy] physics step failed (further errors suppressed): " + e);
        }
    }
}

/// <summary>Quitting to the menu: despawn the AI cars while the session still exists.</summary>
[HarmonyPatch(typeof(OverlandNetworkRunner), nameof(OverlandNetworkRunner.QuitToMenuAsync))]
internal static class ConvoyQuitToMenuPatch
{
    private static void Prefix()
    {
        try { ConvoyAi.OnLeavingSession(); }
        catch (Exception e) { DebugLog.Error("convoy", e, "ConvoyQuitToMenuPatch.Prefix"); ConvoyAi.LogOnce("quit-menu", "cleanup on quit to menu: " + e.Message); }
    }
}

/// <summary>Quitting to the desktop from a session.</summary>
[HarmonyPatch(typeof(OverlandNetworkRunner), nameof(OverlandNetworkRunner.QuitGameAsync))]
internal static class ConvoyQuitGamePatch
{
    private static void Prefix()
    {
        try { ConvoyAi.OnLeavingSession(); }
        catch (Exception e) { DebugLog.Error("convoy", e, "ConvoyQuitGamePatch.Prefix"); ConvoyAi.LogOnce("quit-game", "cleanup on quit: " + e.Message); }
    }
}
