using System;
using HarmonyLib;
using VehiclePhysics;

namespace OverTheHillPlus.GearboxPlus;

/// <summary>
/// The input tick of every car (host and prediction run it for each car): the forward table is set before the game
/// copies it into the gearbox. The body never throws, as nothing may unwind into the game's native caller.
/// </summary>
[HarmonyPatch(typeof(FunselektorFusionVPStandardInput), nameof(FunselektorFusionVPStandardInput.ApplyVehicleInput))]
internal static class ApplyInputPatch
{
    private static void Prefix(FunselektorFusionVPStandardInput __instance)
    {
        try { CarGears.Ensure(__instance); }
        catch (Exception e) { DebugLog.Error(GearboxPlugin.ModId, e, "ApplyInputPatch.Prefix"); }
    }
}

/// <summary>
/// The game reads the gear buttons here (inside ApplyVehicleInput) and steps SelectedManualGear: 1 to reverse and reverse
/// to 1 in one press, never stopping at 0, the neutral the gearbox has. When a press made exactly that step it is set to
/// 0 instead, so the next press goes on. SelectedManualGear is part of the car's network state and the buttons are part
/// of its input, so this gives the same result on the host and in the prediction.
/// </summary>
[HarmonyPatch(typeof(FunselektorFusionVPStandardInput), nameof(FunselektorFusionVPStandardInput.ReadInputButtons))]
internal static class ReadButtonsPatch
{
    private static void Prefix(FunselektorFusionVPStandardInput __instance, out int __state)
    {
        __state = int.MinValue;
        try
        {
            if (CarGears.Active && GearboxConfig.Neutral.Value) __state = __instance.SelectedManualGear;
        }
        catch (Exception e) { DebugLog.Error(GearboxPlugin.ModId, e, "ReadButtonsPatch.Prefix"); }
    }

    private static void Postfix(FunselektorFusionVPStandardInput __instance, int __state)
    {
        if (__state == int.MinValue) return;
        try
        {
            int after = __instance.SelectedManualGear;
            int fixedGear = GearMath.NeutralBetween(__state, after, true);
            if (fixedGear != after) __instance.SelectedManualGear = fixedGear;
        }
        catch (Exception e) { DebugLog.Error(GearboxPlugin.ModId, e, "ReadButtonsPatch.Postfix"); }
    }
}
