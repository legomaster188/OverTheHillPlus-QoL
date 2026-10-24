using System;
using HarmonyLib;

namespace OverTheHillPlus.Features.Public.GarageLift;

// The garage's own methods the lift preview listens to (StateMerchantMenu). Class parameters only (never a struct by
// value); every body catches its own errors so the garage itself is never disturbed.

/// <summary>A button in the Performance screen was highlighted.</summary>
[HarmonyPatch(typeof(StateMerchantMenu), nameof(StateMerchantMenu.OnUpgradeButtonSelected))]
internal static class LiftSelectedPatch
{
    private static void Postfix(StateMerchantMenu __instance, UIMerchantButtonSelector __0)
    {
        try { GarageLift.Selected(__instance, __0); }
        catch (Exception e) { DebugLog.Error("drivingqol", e, "LiftSelectedPatch"); }
    }
}

/// <summary>A button was left (only the one being previewed matters; the next highlight sets the new one).</summary>
[HarmonyPatch(typeof(StateMerchantMenu), nameof(StateMerchantMenu.OnMerchantButtonDeselected))]
internal static class LiftDeselectedPatch
{
    private static void Postfix(UIMerchantButtonSelector __0)
    {
        try { GarageLift.Deselected(__0); }
        catch (Exception e) { DebugLog.Error("drivingqol", e, "LiftDeselectedPatch"); }
    }
}

/// <summary>An upgrade was clicked: bought, fitted or taken off.</summary>
[HarmonyPatch(typeof(StateMerchantMenu), nameof(StateMerchantMenu.OnUpgradeButtonClickOrSubmit))]
internal static class LiftClickedPatch
{
    private static void Postfix(StateMerchantMenu __instance, UIMerchantButtonSelector __0)
    {
        try { GarageLift.Clicked(__instance, __0); }
        catch (Exception e) { DebugLog.Error("drivingqol", e, "LiftClickedPatch"); }
    }
}

/// <summary>The game wrote the description of the highlighted upgrade.</summary>
[HarmonyPatch(typeof(StateMerchantMenu), nameof(StateMerchantMenu.UpdateUpgradeDescription))]
internal static class LiftDescriptionPatch
{
    private static void Postfix(StateMerchantMenu __instance, UIMerchantButtonSelector __0)
    {
        try { GarageLift.DescriptionWritten(__instance, __0); }
        catch (Exception e) { DebugLog.Error("drivingqol", e, "LiftDescriptionPatch"); }
    }
}

/// <summary>A garage model was made (another car): the garage is open.</summary>
[HarmonyPatch(typeof(StateMerchantMenu), nameof(StateMerchantMenu.RefreshDummyCar))]
internal static class LiftModelPatch
{
    private static void Postfix(StateMerchantMenu __instance)
    {
        try { GarageLift.ScreenChanged(__instance); }
        catch (Exception e) { DebugLog.Error("drivingqol", e, "LiftModelPatch"); }
    }
}

/// <summary>The garage opened.</summary>
[HarmonyPatch(typeof(StateMerchantMenu), nameof(StateMerchantMenu.Open))]
internal static class LiftOpenPatch
{
    private static void Postfix(StateMerchantMenu __instance)
    {
        try { GarageLift.ScreenChanged(__instance); }
        catch (Exception e) { DebugLog.Error("drivingqol", e, "LiftOpenPatch"); }
    }
}

/// <summary>The garage closes: the body goes back where the game put it.</summary>
[HarmonyPatch(typeof(StateMerchantMenu), nameof(StateMerchantMenu.Close))]
internal static class LiftClosePatch
{
    private static void Prefix()
    {
        try { GarageLift.Closed(); }
        catch (Exception e) { DebugLog.Error("drivingqol", e, "LiftClosePatch"); }
    }
}

/// <summary>Another screen of the garage is being built: nothing is highlighted yet.</summary>
[HarmonyPatch(typeof(StateMerchantMenu), nameof(StateMerchantMenu.InitializePerformanceUpgradesScreen))]
internal static class LiftPerformanceScreenPatch
{
    private static void Prefix(StateMerchantMenu __instance)
    {
        try { GarageLift.ScreenChanged(__instance); }
        catch (Exception e) { DebugLog.Error("drivingqol", e, "LiftPerformanceScreenPatch"); }
    }
}

[HarmonyPatch(typeof(StateMerchantMenu), nameof(StateMerchantMenu.InitializeCosmeticUpgradesScreen))]
internal static class LiftCosmeticScreenPatch
{
    private static void Prefix(StateMerchantMenu __instance)
    {
        try { GarageLift.ScreenChanged(__instance); }
        catch (Exception e) { DebugLog.Error("drivingqol", e, "LiftCosmeticScreenPatch"); }
    }
}

[HarmonyPatch(typeof(StateMerchantMenu), nameof(StateMerchantMenu.InitializeLiveriesScreen))]
internal static class LiftLiveriesScreenPatch
{
    private static void Prefix(StateMerchantMenu __instance)
    {
        try { GarageLift.ScreenChanged(__instance); }
        catch (Exception e) { DebugLog.Error("drivingqol", e, "LiftLiveriesScreenPatch"); }
    }
}

[HarmonyPatch(typeof(StateMerchantMenu), nameof(StateMerchantMenu.InitializeToolsScreen))]
internal static class LiftToolsScreenPatch
{
    private static void Prefix(StateMerchantMenu __instance)
    {
        try { GarageLift.ScreenChanged(__instance); }
        catch (Exception e) { DebugLog.Error("drivingqol", e, "LiftToolsScreenPatch"); }
    }
}

[HarmonyPatch(typeof(StateMerchantMenu), nameof(StateMerchantMenu.InitializeSelectVehicleScreen))]
internal static class LiftVehicleScreenPatch
{
    private static void Prefix(StateMerchantMenu __instance)
    {
        try { GarageLift.ScreenChanged(__instance); }
        catch (Exception e) { DebugLog.Error("drivingqol", e, "LiftVehicleScreenPatch"); }
    }
}
