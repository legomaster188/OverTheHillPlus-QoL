using System;
using Fusion;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Localization;

namespace OverTheHillPlus.Unreleased;

/// <summary>Unreleased Cars' game hooks (none on per-frame methods). Each one is a no-op until the cars are set up.</summary>
internal static class UnreleasedPatches
{
    internal static bool Owns(uint id)
    {
        try { return SaveGuard.Inventory()?.ContainsItem(id) ?? false; } catch (Exception ex) { DebugLog.Error("unreleased", ex, "UnreleasedPatches.Owns"); return false; }
    }
}

/// <summary>
/// The garage's vehicle list (StateMerchantMenu.InitializeSelectVehicleScreen), also used by a new profile's starting
/// items and the game's "unlock all cars": our cars are listed only once the player added them (and switched them
/// on), so the game never sells or hands them out; a guest also needs a host that runs this mod.
/// </summary>
[HarmonyPatch(typeof(InventoryUtility), nameof(InventoryUtility.GetAllVehicles))]
internal static class GetAllVehiclesPatch
{
    private static void Postfix(Il2CppSystem.Collections.Generic.List<InventoryItemScriptableObject> __result)
    {
        if (__result == null || !UnreleasedCars.Ready) return;
        try
        {
            for (int i = __result.Count - 1; i >= 0; i--)
            {
                var so = __result[i];
                if (so != null && UnreleasedCars.IsOurCar(so.ItemId)) __result.RemoveAt(i);
            }
            bool authority = GameState.IsAuthority;
            bool hostHas = GuestGate.HostRunsMod;
            bool solo = GuestGate.IsSolo;
            uint inUse = SaveGuard.CurrentVehicleId();
            foreach (var car in UnreleasedCars.All)
                if (car.Registered && UnreleasedRules.ShowInGarage(car.IsOn, UnreleasedPatches.Owns(car.Id), authority, hostHas,
                        car.SoloOnly, solo, inUse == car.Id))
                    __result.Add(car.Info);
        }
        catch (Exception e) { DebugLog.Error("unreleased", e, "GetAllVehiclesPatch.Postfix"); UnreleasedPlugin.LogOnce("list", "garage list: " + e.Message); }
    }
}

[HarmonyPatch(typeof(StateMerchantMenu), nameof(StateMerchantMenu.InitializeSelectVehicleScreen))]
internal static class SelectVehicleScreenPatch
{
    private static void Postfix()
    {
        try { GuestGate.AfterGarageBuilt(); } catch (Exception ex) { DebugLog.Error("unreleased", ex, "SelectVehicleScreenPatch.Postfix"); }
    }
}

/// <summary>
/// The cars' names and descriptions: the CarInfos' LocalizedStrings have no (or the donor's) table entries, so the
/// English text is returned for exactly those objects (by pointer; every other string runs the game's code).
/// </summary>
[HarmonyPatch(typeof(LocalizedString), nameof(LocalizedString.GetLocalizedString), new Type[0])]
internal static class CarNamePatch
{
    private static bool Prefix(LocalizedString __instance, ref string __result)
    {
        if (UnreleasedCars.TextOverrides.Count == 0 || __instance == null) return true;
        if (!UnreleasedCars.TextOverrides.TryGetValue(__instance.Pointer, out var text)) return true;
        __result = text;
        return false;
    }
}

/// <summary>
/// The host spawning a car (garage change, lobby, host migration): the Brigand's and Cordillera's addresses are
/// served from the cache; a car of ours that couldn't be set up here becomes the starting car instead of no car;
/// a Brigand or Cordillera outside solo becomes its donor car (solo only, ruling P15; only what is spawned changes).
/// Only for ids that are ours: once the game sells a car itself, its id is left alone.
/// <para>SetupCar takes structs by value (Vector3, Quaternion, BitSet256), which core's car framework avoids as a
/// hook. It is acceptable here, and only here: SetupCar runs once per car spawn (garage change, lobby, host
/// migration), never per frame, so the cost of Il2CppInterop's trampoline for by-value structs doesn't add up; this
/// patch declares only the uint it rewrites (no struct parameter in the patch method itself); and the in-game log
/// shows the trampoline working on this game build (the solo-only swap and the cache refresh run). There is no hook
/// with class-only parameters that sees the car id before the spawn: SpawnPlayer (below) only covers the first
/// spawn. Don't copy this to a per-frame method or add struct parameters to the patch.</para>
/// </summary>
[HarmonyPatch(typeof(OverlandNetworkRunner), nameof(OverlandNetworkRunner.SetupCar))]
internal static class SetupCarPatch
{
    private static void Prefix(ref uint carItemId)
    {
        try
        {
            SaveGuard.EnsureRestored();
            if (!UnreleasedCars.IsOurCar(carItemId)) return;
            var car = UnreleasedCars.Find(carItemId);
            if (car == null) return;
            if (car.Registered)
            {
                uint donor = GuestGate.SessionSwap(carItemId);
                if (donor != 0)
                {
                    UnreleasedPlugin.Logger.LogInfo($"[Unreleased] the {car.Name} is solo only: spawning the {car.DonorUsed} in this session");
                    carItemId = donor;
                    return;
                }
                UnreleasedCars.EnsureCached();
                return;
            }
            uint stock = UnreleasedCars.StartingCarId();
            if (stock == 0) return;
            UnreleasedPlugin.Logger.LogWarning($"[Unreleased] the {car.Name} isn't set up here ({car.Problem ?? "not ready"}): spawning car {stock} instead");
            carItemId = stock;
        }
        catch (Exception e) { DebugLog.Error("unreleased", e, "SetupCarPatch.Prefix"); UnreleasedPlugin.LogOnce("setupcar", "SetupCar: " + e.Message); }
    }
}

/// <summary>
/// A player's first spawn in the world (its own lookup of spawnedCharactersInfo[player].CarItemId, not SetupCar):
/// the address cache, and a solo-only car becomes its donor in a session (in that in-memory RuntimePlayer only).
/// </summary>
[HarmonyPatch(typeof(OverlandNetworkRunner), nameof(OverlandNetworkRunner.SpawnPlayer))]
internal static class SpawnPlayerPatch
{
    private static void Prefix(OverlandNetworkRunner __instance, PlayerRef playerRef)
    {
        try
        {
            if (!UnreleasedCars.Ready) return;
            SaveGuard.EnsureRestored();
            UnreleasedCars.EnsureCached();
            var infos = __instance?.spawnedCharactersInfo;
            if (infos != null && infos.ContainsKey(playerRef)) GuestGate.SwapRuntimePlayer(infos[playerRef], "spawn");
        }
        catch (Exception e) { DebugLog.Error("unreleased", e, "SpawnPlayerPatch.Prefix"); UnreleasedPlugin.LogOnce("spawnplayer", "SpawnPlayer: " + e.Message); }
    }
}

/// <summary>
/// What a guest sends the host (RuntimePlayerInjector.SendRuntimePlayer serialises it): a solo-only car goes out as
/// its donor in a session, so any host, modded or not, spawns a car every game can show.
/// <para>Order on this method (fixed, whatever order the plugins load in): core's car framework runs first
/// (Priority.First: a modded car the session doesn't run becomes its stock fallback, never one of ours, since ours
/// are in core's NonStockCars), then this one last (Priority.Last), so it always sees the id that will be sent.</para>
/// </summary>
[HarmonyPatch(typeof(RuntimePlayer), nameof(RuntimePlayer.Serialize))]
internal static class RuntimePlayerSerializePatch
{
    [HarmonyPriority(Priority.Last)]
    private static void Prefix(RuntimePlayer __instance)
    {
        try { if (UnreleasedCars.Ready) GuestGate.SwapRuntimePlayer(__instance, "sent to the host"); }
        catch (Exception e) { DebugLog.Error("unreleased", e, "RuntimePlayerSerializePatch.Prefix"); UnreleasedPlugin.LogOnce("serialize", "RuntimePlayer.Serialize: " + e.Message); }
    }
}

/// <summary>The garage preview loads Prefabs/Cars/MerchantDummies/Car_&lt;name&gt;_Dummy.</summary>
[HarmonyPatch(typeof(StateMerchantMenu), nameof(StateMerchantMenu.RefreshDummyCar))]
internal static class RefreshDummyCarPatch
{
    private static void Prefix()
    {
        try { if (UnreleasedCars.Ready) UnreleasedCars.EnsureCached(); } catch (Exception ex) { DebugLog.Error("unreleased", ex, "RefreshDummyCarPatch.Prefix"); }
    }

    // One Info line per car, the first time the garage shows it: proves the preview path in the next log.
    private static void Postfix(StateMerchantMenu __instance, CarInfo carInfo)
    {
        try
        {
            var car = carInfo == null ? null : UnreleasedCars.Find(carInfo.ItemId);
            if (car == null) return;
            string address = GameConstants.ADDRESSABLES_PATHS.GetCarDummyPrefabAddress(carInfo.name);
            var obj = __instance.dummyChosenCarGameObject;
            string shown = "no preview object";
            if (obj != null)
            {
                var renderers = obj.GetComponentsInChildren<Renderer>(true);
                int on = 0;
                foreach (var r in renderers) if (r != null && r.enabled && r.gameObject.activeInHierarchy) on++;
                shown = $"preview '{obj.name}' active={obj.activeInHierarchy} at {obj.transform.position}, {on} of {renderers.Length} renderers on";
            }
            UnreleasedPlugin.InfoOnce("preview-" + car.Name,
                $"{car.Name}: garage preview {address} (resolves={OverTheHillPlus.Shared.RuntimeAddressables.Resolves(address)}, stand-in={car.DummyStandIn != null}): {shown}");
        }
        catch (Exception ex) { DebugLog.Error("unreleased", ex, "RefreshDummyCarPatch.Postfix"); }
    }
}

[HarmonyPatch(typeof(OverlandSaver), nameof(OverlandSaver.SaveGame))]
internal static class SaveGamePatch
{
    private static void Prefix() => SaveGuard.BeforeSave();
    private static void Postfix() => SaveGuard.AfterSave();
}

[HarmonyPatch(typeof(RuntimePlayerInjector), nameof(RuntimePlayerInjector.SendRuntimePlayer))]
internal static class SendRuntimePlayerPatch
{
    private static void Prefix(NetworkRunner runner)
    {
        try { SaveGuard.EnsureRestored(); } catch (Exception ex) { DebugLog.Error("unreleased", ex, "SendRuntimePlayerPatch.Prefix"); }
        GuestGate.BeforeSendRuntimePlayer(runner);
    }
}
