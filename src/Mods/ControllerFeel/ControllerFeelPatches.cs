using System;
using HarmonyLib;
using VehiclePhysics;

namespace OverTheHillPlus.ControllerFeel;

/// <summary>
/// After the game's own rumble update has written its motors, ours are stacked on top in the same frame (no struct
/// parameters: the patch only gets the manager).
/// </summary>
[HarmonyPatch(typeof(ControllerVibrationManager), nameof(ControllerVibrationManager.Update))]
internal static class ControllerFeelUpdatePatch
{
    private static void Postfix(ControllerVibrationManager __instance) => ControllerFeelCore.AfterGameUpdate(__instance);
}

/// <summary>The local car's physics step: the hardest suspension hit since the last frame (a hit can be shorter than a frame).</summary>
[HarmonyPatch(typeof(VPVehicleController), nameof(VPVehicleController.DoUpdateBlocks))]
internal static class ControllerFeelStepPatch
{
    private static bool _loggedError;

    private static void Prefix(VPVehicleController __instance)
    {
        try
        {
            if (GameState.IsLocalCar(__instance)) FeelSampler.Step(__instance);
        }
        catch (Exception e)
        {
            if (_loggedError) return;
            _loggedError = true;
            ControllerFeelPlugin.Logger.LogError("Rumble+ step failed (further errors suppressed): " + e);
        }
    }
}
