using System;
using HarmonyLib;

namespace OverTheHillPlus.CustomCosmetics;

// Patches only take classes, ints, bools and uints (no struct by value: those garble on IL2CPP). Every body is wrapped:
// whatever goes wrong here, the game's own code runs as if the mod were not there.

// ---------------------------------------------------------------- the car's part list and objects

/// <summary>The networked car registers its upgrade objects: ours go in right after the game's own pass.</summary>
[HarmonyPatch(typeof(VehicleCustomizationController), nameof(VehicleCustomizationController.InitVehicleUpgrades))]
internal static class InitVehicleUpgradesPatch
{
    private static void Prefix(VehicleCustomizationController __instance)
    {
        // Our parts must be on the car's list before the game builds its dictionary from it.
        try { PartObjects.EnsureCar(__instance?.currentCarInfo); }
        catch (Exception e) { DebugLog.Error(CustomCosmeticsPlugin.ModId, e, "InitVehicleUpgradesPatch.Prefix"); }
    }

    private static void Postfix(VehicleCustomizationController __instance)
    {
        try { PartObjects.Attach(__instance.upgradesParent, __instance.currentCarInfo, __instance.totalPartsDictionary, __instance.propertyBlockSetter, garage: false); }
        catch (Exception e) { DebugLog.Error(CustomCosmeticsPlugin.ModId, e, "InitVehicleUpgradesPatch.Postfix"); }
    }
}

/// <summary>A networked car's mounted parts changed (or it was just built): ours are checked first (cheap).</summary>
[HarmonyPatch(typeof(VehicleCustomizationController), nameof(VehicleCustomizationController.RefreshMountedUpgrades))]
internal static class RefreshCarPatch
{
    private static void Prefix(VehicleCustomizationController __instance)
    {
        try { PartObjects.Attach(__instance.upgradesParent, __instance.currentCarInfo, __instance.totalPartsDictionary, __instance.propertyBlockSetter, garage: false); }
        catch (Exception e) { DebugLog.Error(CustomCosmeticsPlugin.ModId, e, "RefreshCarPatch.Prefix"); }
    }

    private static void Postfix(VehicleCustomizationController __instance)
    {
        try { PartObjects.Report(__instance.currentCarInfo, __instance.totalPartsDictionary, garage: false); }
        catch (Exception e) { DebugLog.Error(CustomCosmeticsPlugin.ModId, e, "RefreshCarPatch.Postfix"); }
    }
}

/// <summary>The garage's model registers its parts when it wakes.</summary>
[HarmonyPatch(typeof(LocalVehicleCustomizationController), nameof(LocalVehicleCustomizationController.InitParts))]
internal static class InitPartsPatch
{
    private static void Prefix(LocalVehicleCustomizationController __instance)
    {
        try { PartObjects.EnsureCar(__instance?.currentCarInfo); }
        catch (Exception e) { DebugLog.Error(CustomCosmeticsPlugin.ModId, e, "InitPartsPatch.Prefix"); }
    }

    private static void Postfix(LocalVehicleCustomizationController __instance)
    {
        try { PartObjects.Attach(__instance.upgradesParent, __instance.currentCarInfo, __instance.totalPartsDictionary, __instance.propertyBlockSetter, garage: true); }
        catch (Exception e) { DebugLog.Error(CustomCosmeticsPlugin.ModId, e, "InitPartsPatch.Postfix"); }
    }
}

/// <summary>The garage refreshes its model for a car: the model's CarInfo may only be set by now, so check again.</summary>
[HarmonyPatch(typeof(LocalVehicleCustomizationController), nameof(LocalVehicleCustomizationController.RefreshMountedUpgrades))]
internal static class RefreshGaragePatch
{
    private static void Prefix(LocalVehicleCustomizationController __instance, Car __0)
    {
        try
        {
            var info = __instance.currentCarInfo ?? __0?.CarInfo;
            PartObjects.Attach(__instance.upgradesParent, info, __instance.totalPartsDictionary, __instance.propertyBlockSetter, garage: true);
        }
        catch (Exception e) { DebugLog.Error(CustomCosmeticsPlugin.ModId, e, "RefreshGaragePatch.Prefix"); }
    }

    private static void Postfix(LocalVehicleCustomizationController __instance, Car __0)
    {
        try { PartObjects.Report(__instance.currentCarInfo ?? __0?.CarInfo, __instance.totalPartsDictionary, garage: true); }
        catch (Exception e) { DebugLog.Error(CustomCosmeticsPlugin.ModId, e, "RefreshGaragePatch.Postfix"); }
    }
}

/// <summary>Whoever asks which parts a car has mounted (the garage, a spawning car) sees the list with our parts in.</summary>
[HarmonyPatch(typeof(InventoryUtility), nameof(InventoryUtility.GetAllMountedParts))]
internal static class MountedPartsPatch
{
    private static void Prefix(CarInfo __0)
    {
        try { PartObjects.EnsureCar(__0); }
        catch (Exception e) { DebugLog.Error(CustomCosmeticsPlugin.ModId, e, "MountedPartsPatch.Prefix"); }
    }
}

/// <summary>The garage lists a car's cosmetics: ours are in the list first.</summary>
[HarmonyPatch(typeof(StateMerchantMenu), nameof(StateMerchantMenu.InitializeCosmeticUpgradesScreen))]
internal static class CosmeticsScreenPatch
{
    private static void Prefix(StateMerchantMenu __instance)
    {
        try { PartObjects.EnsureCar(__instance?.currentCar?.CarInfo); }
        catch (Exception e) { DebugLog.Error(CustomCosmeticsPlugin.ModId, e, "CosmeticsScreenPatch.Prefix"); }
    }
}

// ---------------------------------------------------------------- ownership: our record, never the game's save

/// <summary>Is the part owned by this car (free parts always are)?</summary>
[HarmonyPatch(typeof(InventoryUtility), nameof(InventoryUtility.IsOwned), typeof(InventoryItemScriptableObject), typeof(uint))]
internal static class IsOwnedPatch
{
    private static bool Prefix(InventoryItemScriptableObject __0, uint __1, ref bool __result)
    {
        try
        {
            if (__0 == null) return true;
            var s = CosmeticState.Of(__0.ItemId);
            if (s == null) return true;
            __result = CosmeticState.Owned(s, __1);
            return false;
        }
        catch (Exception e) { DebugLog.Error(CustomCosmeticsPlugin.ModId, e, "IsOwnedPatch.Prefix"); return true; }
    }
}

[HarmonyPatch(typeof(InventoryUtility), nameof(InventoryUtility.IsInUse), typeof(InventoryItemScriptableObject), typeof(uint))]
internal static class IsInUsePatch
{
    private static bool Prefix(InventoryItemScriptableObject __0, uint __1, ref bool __result)
    {
        try
        {
            if (__0 == null) return true;
            var s = CosmeticState.Of(__0.ItemId);
            if (s == null) return true;
            __result = OwnershipStore.Current.InUse(__1, s.ItemId);
            return false;
        }
        catch (Exception e) { DebugLog.Error(CustomCosmeticsPlugin.ModId, e, "IsInUsePatch.Prefix"); return true; }
    }
}

[HarmonyPatch(typeof(InventoryUtility), nameof(InventoryUtility.IsOwnedAndNotInUse), typeof(InventoryItemScriptableObject), typeof(uint))]
internal static class IsOwnedAndNotInUsePatch
{
    private static bool Prefix(InventoryItemScriptableObject __0, uint __1, ref bool __result)
    {
        try
        {
            if (__0 == null) return true;
            var s = CosmeticState.Of(__0.ItemId);
            if (s == null) return true;
            __result = CosmeticState.Owned(s, __1) && !OwnershipStore.Current.InUse(__1, s.ItemId);
            return false;
        }
        catch (Exception e) { DebugLog.Error(CustomCosmeticsPlugin.ModId, e, "IsOwnedAndNotInUsePatch.Prefix"); return true; }
    }
}

/// <summary>
/// Buyable while it is not owned. The game's own answer for a cosmetic that is not in the inventory is "no" (only upgrades and
/// liveries are buyable from nothing; the stock cosmetics are in the inventory already), so ours answers itself. The click
/// still pays through the game's own TryRemoveItem (money), which refuses when there is not enough.
/// </summary>
[HarmonyPatch(typeof(InventoryUtility), nameof(InventoryUtility.IsAvailableToBuy), typeof(InventoryItemScriptableObject), typeof(uint))]
internal static class IsAvailableToBuyPatch
{
    private static bool Prefix(InventoryItemScriptableObject __0, uint __1, ref bool __result)
    {
        try
        {
            if (__0 == null) return true;
            var s = CosmeticState.Of(__0.ItemId);
            if (s == null) return true;
            __result = !CosmeticState.Owned(s, __1);
            return false;
        }
        catch (Exception e) { DebugLog.Error(CustomCosmeticsPlugin.ModId, e, "IsAvailableToBuyPatch.Prefix"); return true; }
    }
}

/// <summary>The garage buys a cosmetic for one car: the game adds a VehicleState to the inventory; ours goes to the sidecar.</summary>
[HarmonyPatch(typeof(PlayerInventory), nameof(PlayerInventory.AddItem), typeof(uint), typeof(VehicleState), typeof(bool))]
internal static class AddVehicleStatePatch
{
    private static bool Prefix(uint __0, VehicleState __1)
    {
        try
        {
            var s = CosmeticState.Of(__0);
            if (s == null) return true;
            CosmeticState.Bought(__1 != null ? __1.VehicleItemId : 0u, s, __1 != null && __1.InUse);
            return false;
        }
        catch (Exception e) { DebugLog.Error(CustomCosmeticsPlugin.ModId, e, "AddVehicleStatePatch.Prefix"); return true; }
    }
}

[HarmonyPatch(typeof(PlayerInventory), nameof(PlayerInventory.AddItem), typeof(InventoryItemScriptableObject), typeof(int), typeof(bool), typeof(bool))]
internal static class AddItemSoPatch
{
    private static bool Prefix(InventoryItemScriptableObject __0)
    {
        try
        {
            var s = __0 == null ? null : CosmeticState.Of(__0.ItemId);
            if (s == null) return true;
            CosmeticState.Bought(0u, s, false);
            return false;
        }
        catch (Exception e) { DebugLog.Error(CustomCosmeticsPlugin.ModId, e, "AddItemSoPatch.Prefix"); return true; }
    }
}

[HarmonyPatch(typeof(PlayerInventory), nameof(PlayerInventory.AddItem), typeof(uint), typeof(int), typeof(bool), typeof(bool))]
internal static class AddItemIdPatch
{
    private static bool Prefix(uint __0)
    {
        try
        {
            var s = CosmeticState.Of(__0);
            if (s == null) return true;
            CosmeticState.Bought(0u, s, false);
            return false;
        }
        catch (Exception e) { DebugLog.Error(CustomCosmeticsPlugin.ModId, e, "AddItemIdPatch.Prefix"); return true; }
    }
}

/// <summary>Mount or take off: the game marks the part in use for the car in the inventory; ours is marked in the sidecar.</summary>
[HarmonyPatch(typeof(VehicleCustomizationControllerHelpers), nameof(VehicleCustomizationControllerHelpers.UpdateInUseStatus))]
internal static class UpdateInUsePatch
{
    private static bool Prefix(Car __0, VehiclePartSO __1, bool __2)
    {
        try
        {
            if (__1 == null) return true;
            var s = CosmeticState.Of(__1.ItemId);
            if (s == null) return true;
            CosmeticState.Mounted(__0?.CarInfo != null ? __0.CarInfo.ItemId : 0u, s, __2);
            return false;
        }
        catch (Exception e) { DebugLog.Error(CustomCosmeticsPlugin.ModId, e, "UpdateInUsePatch.Prefix"); return true; }
    }
}

/// <summary>What the ownership patches share.</summary>
internal static class CosmeticState
{
    /// <summary>The part behind an item id; null for any other item (the game's own path then).</summary>
    public static CustomPart Of(uint itemId) => PartObjects.Off ? null : PartLibrary.ByItemId(itemId);

    public static bool Owned(CustomPart p, uint car) => p.Manifest.Price <= 0 || OwnershipStore.Current.Owns(car, p.ItemId);

    public static void Bought(uint car, CustomPart p, bool inUse)
    {
        var o = OwnershipStore.Current;
        bool changed = o.Own(car, p.ItemId);
        if (inUse) changed |= o.SetInUse(car, p.ItemId, true);
        if (changed) OwnershipStore.Save();
        CustomCosmeticsPlugin.Logger?.LogInfo($"Custom Cosmetics: {p.Id} bought for car 0x{car:X8} (sidecar; the game's save is not touched).");
    }

    public static void Mounted(uint car, CustomPart p, bool inUse)
    {
        if (OwnershipStore.Current.SetInUse(car, p.ItemId, inUse)) OwnershipStore.Save();
        CustomCosmeticsPlugin.Logger?.LogInfo($"Custom Cosmetics: {p.Id} {(inUse ? "mounted on" : "taken off")} car 0x{car:X8}.");
    }
}
