using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Logging;
using FunCore.Managers;
using Fusion;
using HarmonyLib;
using UnityEngine;

namespace OverTheHillPlus.Cars;

// After the namespace line on purpose: the game has its own global ModManager (mod.io).
using OverTheHillPlus.Core.Manager;

/// <summary>
/// Core's modded-car framework: a car mod gives a <see cref="CarDefinition"/> (and its embedded assets) to
/// <see cref="Start"/> from its plugin's Load, and everything a networked car needs runs here, the same way for every
/// car: the network prefab (core's NetPrefabRegistry, in its fixed order, with its join gate and its wake of spawned
/// copies), the template and garage model built from a donor (CarBody, CarTuning), the CarInfo in the game's catalog
/// (CarCatalog, CarText), Addressables serving (RuntimeAddressables), the save filter and sidecar (CarSave), the join
/// data guard and "Drive the car" (CarDrive), each player's paint, skin and engine sound and their sync (CarLooks,
/// CarLookNet on one channel for all cars), the skin list (<see cref="Api.Cars"/>) and the MODS page rows (CarPage).
/// <para>The game hooks are installed once, by hand (never by core's own patch pass), when the first car starts; the
/// join-data guard and the save filter are required: without them no car starts.</para>
/// </summary>
internal static class ModdedCars
{
    private static readonly List<RuntimeCar> Cars = new();
    private static readonly Dictionary<string, Func<CarDefinition>> Declared = new(StringComparer.OrdinalIgnoreCase);
    private static Harmony _harmony;
    private static string _hooksFailed;
    private static bool _hooksIn, _ticking;
    private static int _hooksOk, _hooksFailedCount;

    internal static IReadOnlyList<RuntimeCar> All => Cars;

    /// <summary>
    /// Starts a modded car (call it once from your plugin's Load, after ModManager.Register and the enabled check).
    /// Binds its settings in <paramref name="config"/>, reads its model from <paramref name="assets"/> (resources
    /// "&lt;AssetName&gt;/&lt;file&gt;"), declares its network prefab and registers its skins with Api.Cars. Null on
    /// success, otherwise why the car stays off (the mod is then marked failed with that reason).
    /// </summary>
    internal static string Start(CarDefinition definition, Assembly assets, BepInEx.Configuration.ConfigFile config,
        ManualLogSource log = null, CarHooks hooks = null)
    {
        string why;
        try { why = StartCore(definition, assets, config, log, hooks); }
        catch (Exception e) { DebugLog.Error("core", e, "ModdedCars.Start"); why = "it failed to start: " + e.Message; }
        if (why != null && definition != null && ApiRulesOk(definition.Id))
        {
            // Declared but not running: its prefab entry comes back out of the join check (or, once registration
            // began, the failure says it stays in until a restart).
            // Not when a car with this id runs already (a second copy of the mod): those entries are the first one's.
            string refused = null;
            if (ById(definition.Id) == null)
                try { refused = Core.Shared.NetPrefabRegistry.Withdraw(definition.Id); } catch (Exception ex) { DebugLog.Error("core", ex, "ModdedCars.Start"); }
            if (refused != null) why += " (" + refused + ")";
            ModManager.MarkFailed(definition.Id, why);
            (log ?? Core.CorePlugin.Logger)?.LogWarning($"{definition.DisplayName}: {why}. The car stays off this session.");
        }
        return why;
    }

    private static bool ApiRulesOk(string id) => Api.ApiRules.IsValidModId(id);

    private static string StartCore(CarDefinition given, Assembly assets, BepInEx.Configuration.ConfigFile config, ManualLogSource log, CarHooks hooks)
    {
        if (given == null) return "no car definition";
        // Our own copy: the caller's object can't change a running car.
        var def = given.Clone();
        string bad = def.Validate();
        if (bad != null) return bad;
        if (assets == null || config == null) return "no assets or config file";
        foreach (var c in Cars)
        {
            string clash = CarDefinition.Clash(def, c.Def);
            if (clash != null) return clash;
        }
        RuntimeCar car;
        try
        {
            CrashTrail.Mark(def.Id + ": start");
            car = new RuntimeCar(def, hooks, log, assets, config);
        }
        catch (Exception e) { DebugLog.Error("core", e, "ModdedCars.StartCore"); return "its settings could not be read: " + e.Message; }
        // The model is checked first: without it nothing is registered anywhere (no prefab entry, no catalog item).
        string why = car.Assets.Load();
        if (why != null) return why;
        why = InstallHooks();
        if (why != null) return why;
        // Its skins (livery packs for its car name), listed for anyone through core's modded-car API (the Livery Packs
        // page shows them too). Refused (a livery name another car took): the car doesn't start.
        if (!Api.Cars.Register(new Api.Cars.ModdedCar
            {
                Id = def.Id,
                DisplayName = def.DisplayName,
                LiveryCar = def.CarName,
                CurrentSkin = () => car.Config.Skin.Value,
                ApplySkin = key => car.Config.Skin.Value = key ?? "",
            }))
            return "its skins could not be registered (see the [Api] line in the log)";
        // The car's network prefab, registered by core in its fixed order (core also runs the join gate).
        why = car.Declare();
        if (why != null)
        {
            Api.Cars.Unregister(def.Id);
            return why;
        }
        Cars.Add(car);
        // Core's list of cars a stock game can't spawn: never a stand-in for anyone (core's fallback, other mods' save filters).
        Core.Shared.NonStockCars.Add("cars." + def.Id, def.ItemId, def.DisplayName);
        // Patch failures show on the car's MODS page like any mod's (the hooks are shared by every car).
        PatchReport.Record(def.Id, Cars.Count == 1 ? _hooksOk : 0, _hooksFailedCount, countInTotals: Cars.Count == 1);
        Features.Registry.OnTick(car.Tick, def.Id);
        if (!_ticking)
        {
            _ticking = true;
            CarLookNet.Register();
            Api.Cars.Rescanned += CarLooks.SkinsRescanned;
            Features.Registry.OnTick(SharedTick, "cars");
        }
        CarLookNet.FitBudget(Cars.Count);
        try { config.Save(); } catch (Exception ex) { DebugLog.Error("core", ex, "ModdedCars.StartCore"); }
        car.Info($"{def.DisplayName} loaded (car id 0x{def.ItemId:X8}, donor {car.Config.Donor.Value}).");
        return null;
    }

    /// <summary>The MODS page rows of a car (ModInfo.DrawSimple = () => ModdedCars.DrawPage(definition)): its state,
    /// "Drive the car", the garage switch and its look.</summary>
    internal static void DrawPage(CarDefinition definition)
    {
        if (definition == null) return;
        CarPage.Draw(definition, ById(definition.Id));
    }

    private static void SharedTick()
    {
        if (!AnyReady()) return;
        CarLooks.Tick();
        CarLookNet.Tick();
    }

    // ---------------------------------------------------------------- lookups

    internal static bool AnyReady()
    {
        foreach (var c in Cars) if (c.Ready) return true;
        return false;
    }

    internal static RuntimeCar ById(string id)
    {
        if (id == null) return null;
        foreach (var c in Cars) if (string.Equals(c.Id, id, StringComparison.OrdinalIgnoreCase)) return c;
        return null;
    }

    internal static RuntimeCar ByItem(uint itemId)
    {
        foreach (var c in Cars) if (c.Def.ItemId == itemId) return c;
        return null;
    }

    internal static RuntimeCar ByCarName(string carName)
    {
        if (carName == null) return null;
        foreach (var c in Cars) if (string.Equals(c.Def.CarName, carName, StringComparison.OrdinalIgnoreCase)) return c;
        return null;
    }

    internal static RuntimeCar ByCar(VehicleCustomizationController vcc)
    {
        if (vcc == null) return null;
        foreach (var c in Cars) if (c.Catalog.IsThis(vcc)) return c;
        return null;
    }

    /// <summary>A modded car's item id (never a fallback, never kept in a save).</summary>
    internal static bool IsModdedItem(uint itemId) => ByItem(itemId) != null;

    /// <summary>The cars that may never stand in for <paramref name="car"/>: every other non-stock car (the other
    /// modded cars and the cars other mods registered with core's <see cref="Core.Shared.NonStockCars"/>).</summary>
    internal static List<uint> OtherItemIds(RuntimeCar car)
    {
        var l = Core.Shared.NonStockCars.AllExcept(car.Def.ItemId);
        foreach (var c in Cars) if (c != car && !l.Contains(c.Def.ItemId)) l.Add(c.Def.ItemId);
        return l;
    }

    // ---------------------------------------------------------------- the game's hooks

    /// <summary>Installs the hooks once (null when they are in). The required ones (join data, save write / read,
    /// profile load) fail the whole set; the others are logged and skipped.</summary>
    private static string InstallHooks()
    {
        if (_hooksIn) return null;
        if (_hooksFailed != null) return _hooksFailed;
        _harmony = new Harmony("com.oth.core.cars");
        var required = new (Type type, string method, string prefix, string postfix, string finalizer)[]
        {
            // Priority.First: the modded car becomes its stock fallback before any other mod's prefix looks at the id
            // (a mod that swaps its own cars, e.g. for a session, patches with Priority.Last so it sees the final id).
            (typeof(RuntimePlayer), nameof(RuntimePlayer.Serialize), nameof(RuntimePlayerPrefix), null, null),
            (typeof(PlayerInventoryJsonConverter), nameof(PlayerInventoryJsonConverter.WriteJson), nameof(SaveWritePrefix), null, nameof(SaveWriteFinalizer)),
            (typeof(PlayerInventoryJsonConverter), nameof(PlayerInventoryJsonConverter.ReadJson), null, nameof(SaveReadPostfix), null),
            (typeof(ProfileSave), nameof(ProfileSave.Deserialize), null, nameof(ProfileLoadPostfix), null),
        };
        foreach (var r in required)
        {
            try { Patch(r.type, r.method, r.prefix, r.postfix, r.finalizer); }
            catch (Exception e)
            {
                try { _harmony.UnpatchSelf(); } catch (Exception ex) { DebugLog.Error("core", ex, "ModdedCars.InstallHooks"); }
                _hooksFailed = $"a required game hook ({r.type.Name}.{r.method}) could not be installed: {e.GetBaseException().Message}";
                Core.CorePlugin.Logger?.LogError("[Cars] " + _hooksFailed + ". No modded car runs this session.");
                return _hooksFailed;
            }
        }
        var optional = new (Type type, string method, string prefix, string postfix)[]
        {
            (typeof(NetworkRunner), nameof(NetworkRunner.StartGame), nameof(StartGamePrefix), null),
            (typeof(ResourcesManager), nameof(ResourcesManager.UnloadCachedObjects), null, nameof(EnsureCachePostfix)),
            (typeof(ResourcesManager), nameof(ResourcesManager.ReleaseAddressables), null, nameof(EnsureCachePostfix)),
            (typeof(OverlandNetworkRunner), nameof(OverlandNetworkRunner.SpawnPlayer), nameof(EnsureCachePrefix), null),
            (typeof(StateMerchantMenu), nameof(StateMerchantMenu.RefreshDummyCar), nameof(EnsureCachePrefix), nameof(GarageDummyPostfix)),
            (typeof(ResourcesManager), nameof(ResourcesManager.ReleaseAddressable), nameof(ReleasePrefix), null),
            (typeof(ResourcesManager), nameof(ResourcesManager.ReleaseAddressableHandle), nameof(ReleasePrefix), null),
            (typeof(ResourcesManager), nameof(ResourcesManager.Unload), nameof(UnloadPrefix), null),
            (typeof(InventoryUtility), nameof(InventoryUtility.GetAllVehicles), null, nameof(GarageListPostfix)),
            (typeof(CarDamageSystem), nameof(CarDamageSystem.EngineTierChanged), nameof(GameReadsBasePrefix), null),
            (typeof(CarDamageSystem), nameof(CarDamageSystem.GetBaseValuesFromVehicleConfig), nameof(GameReadsBasePrefix), null),
            (typeof(PropertyBlockSetter), nameof(PropertyBlockSetter.SetLiveryTexture), null, nameof(LiveryGuardPostfix)),
            (typeof(ModLiveryEquipper), nameof(ModLiveryEquipper.Unequip), null, nameof(LiveryUnequipPostfix)),
            (typeof(ModLiveryEquipper), nameof(ModLiveryEquipper.Equip), null, nameof(LiveryEquipPostfix)),
        };
        int failed = 0;
        foreach (var o in optional)
        {
            try { Patch(o.type, o.method, o.prefix, o.postfix, null); _hooksOk++; }
            catch (Exception e)
            {
                failed++;
                Core.CorePlugin.Logger?.LogError($"[Cars] hook {o.type.Name}.{o.method} failed and is skipped: {e.GetBaseException().Message}");
            }
        }
        try { Patch(typeof(CarAudioManager), nameof(CarAudioManager.MountedHornChanged), null, null, nameof(HornFinalizer)); _hooksOk++; }
        catch (Exception e)
        {
            failed++;
            Core.CorePlugin.Logger?.LogError($"[Cars] hook CarAudioManager.MountedHornChanged failed and is skipped: {e.GetBaseException().Message}");
        }
        if (failed > 0) Core.CorePlugin.Logger?.LogWarning($"[Cars] {failed} game hook(s) could not be installed; the related parts are off this session.");
        _hooksOk += required.Length;
        _hooksFailedCount = failed;
        _hooksIn = true;
        return null;
    }

    private static void Patch(Type type, string method, string prefix, string postfix, string finalizer)
    {
        var original = AccessTools.Method(type, method) ?? throw new MissingMethodException(type.Name, method);
        // The join data's order between mods is fixed (see the comment on the RuntimePlayer.Serialize hook).
        int priority = type == typeof(RuntimePlayer) ? Priority.First : Priority.Normal;
        HarmonyMethod H(string name) => name == null ? null : new HarmonyMethod(AccessTools.Method(typeof(ModdedCars), name)) { priority = priority };
        _harmony.Patch(original, prefix: H(prefix), postfix: H(postfix), finalizer: H(finalizer));
    }

    // Patch bodies: class parameters only (never a struct by value), every one catches its own errors.

    private static void RuntimePlayerPrefix(RuntimePlayer __instance)
    {
        foreach (var c in Cars)
        {
            try { c.Drive.BeforeSerialize(__instance); }
            catch (Exception e) { DebugLog.Error("core", e, "ModdedCars.RuntimePlayerPrefix"); c.LogOnce("runtime-player", "join data: " + e.Message); }
        }
    }

    private static readonly List<(RuntimeCar car, CarSave.Undo undo)> PendingUndo = new();
    private static PlayerInventory _lastRead;

    private static void SaveWritePrefix(Il2CppSystem.Object value)
    {
        if (PendingUndo.Count > 0) SaveWriteFinalizer(); // a previous write threw before its finalizer: put that back first
        Il2CppSystem.Collections.Generic.List<BaseInventoryItem> items;
        try { items = value?.TryCast<PlayerInventory>()?.Items; } catch (Exception ex) { DebugLog.Error("core", ex, "ModdedCars.SaveWritePrefix"); items = null; }
        if (items == null) return;
        foreach (var c in Cars)
        {
            try
            {
                var u = c.Save.BeforeWrite(items);
                if (u != null) PendingUndo.Add((c, u));
            }
            catch (Exception e) { DebugLog.Error("core", e, "ModdedCars.SaveWritePrefix"); c.LogOnce("save-prefix", "save filter: " + e.Message); }
        }
    }

    /// <summary>Runs even when the serializer throws: every car's items go back, the last car's first.</summary>
    private static void SaveWriteFinalizer()
    {
        for (int i = PendingUndo.Count - 1; i >= 0; i--)
        {
            var (c, u) = PendingUndo[i];
            c.Save.AfterWrite(u);
        }
        PendingUndo.Clear();
    }

    /// <summary>
    /// CarAudioManager.MountedHornChanged(null) (no horn mounted any more: VehicleCustomizationController.RefreshMountedHorn
    /// passes the new horn, null for none) looks the horn up in its sound dictionary and throws ArgumentNullException (key)
    /// from Fusion's Render, after it has already cleared the current horn sound. The throw changes nothing else, so it is
    /// swallowed for a null horn (logged once); any other exception goes on.
    /// </summary>
    private static Exception HornFinalizer(Exception __exception, VehicleCosmetic horn)
    {
        if (__exception == null || horn != null) return __exception;
        if (!_hornNullLogged)
        {
            _hornNullLogged = true;
            Core.CorePlugin.Logger?.LogInfo("[Cars] the game's horn change with no horn threw (" + __exception.GetType().Name + "); ignored.");
        }
        return null;
    }

    private static bool _hornNullLogged;

    private static void SaveReadPostfix(Il2CppSystem.Object __result)
    {
        try { _lastRead = __result?.TryCast<PlayerInventory>(); } catch (Exception ex) { DebugLog.Error("core", ex, "ModdedCars.SaveReadPostfix"); }
    }

    private static void ProfileLoadPostfix(ProfileSave __result)
    {
        var inv = _lastRead;
        _lastRead = null;
        if (inv == null || __result == null) return;
        foreach (var c in Cars)
        {
            try { c.Save.AfterProfileLoaded(inv, __result); } catch (Exception ex) { DebugLog.Error("core", ex, "ModdedCars.ProfileLoadPostfix"); }
        }
    }

    /// <summary>Registers each car in the catalog (when it can be by now) before a session starts; runs after core's
    /// network prefab check.</summary>
    private static void StartGamePrefix()
    {
        foreach (var c in Cars)
        {
            try { c.BeforeStartGame(); }
            catch (Exception e) { DebugLog.Error("core", e, "ModdedCars.StartGamePrefix"); c.LogOnce("startgame", "start check: " + e.Message); }
        }
    }

    /// <summary>
    /// The game empties its address cache on scene changes (UnloadCachedObjects / ReleaseAddressables by scene tags), and
    /// spawns / the garage need the entries right then: they go straight back in. (SetupCar itself is not patched: its
    /// by-value structs make it a poor hook; the once-a-second check and these cover it.)
    /// </summary>
    private static void EnsureCachePostfix() => EnsureAll();

    private static void EnsureCachePrefix() => EnsureAll();

    private static void EnsureAll()
    {
        foreach (var c in Cars)
        {
            try { c.EnsureCache(); } catch (Exception ex) { DebugLog.Error("core", ex, "ModdedCars.EnsureAll"); }
        }
    }

    /// <summary>The game must never release a modded car's two cached objects (they aren't Addressables assets).</summary>
    private static bool ReleasePrefix(string address) => !IsOurAddress(address);

    private static bool UnloadPrefix(string path) => !IsOurAddress(path);

    private static bool IsOurAddress(string address)
    {
        foreach (var c in Cars) if (c.IsOurAddress(address)) return true;
        return false;
    }

    /// <summary>The garage lists a modded car only when it can be driven here (and the player wants it listed).</summary>
    private static void GarageListPostfix(Il2CppSystem.Collections.Generic.List<InventoryItemScriptableObject> __result)
    {
        try
        {
            if (__result == null) return;
            for (int i = __result.Count - 1; i >= 0; i--)
            {
                var so = __result[i];
                var c = so == null ? null : ByItem(so.ItemId);
                if (c != null && !c.Drive.Listable) __result.RemoveAt(i);
            }
        }
        catch (Exception ex) { DebugLog.Error("core", ex, "ModdedCars.GarageListPostfix"); }
    }

    /// <summary>CarDamageSystem captures the "original" engine values in EngineTierChanged and
    /// GetBaseValuesFromVehicleConfig (spawn, engine-tier upgrade): the game's own values go back first.</summary>
    private static void GameReadsBasePrefix(CarDamageSystem __instance)
    {
        foreach (var c in Cars)
        {
            try { c.Tuning.BeforeGameReadsBase(__instance); } catch (Exception ex) { DebugLog.Error("core", ex, "ModdedCars.GameReadsBasePrefix"); }
        }
    }

    /// <summary>The game set a livery texture on a car's blocks: a modded car gets its own texture back.</summary>
    private static void LiveryGuardPostfix(PropertyBlockSetter __instance, Texture2D texture2D)
    {
        if (Cars.Count == 0) return;
        try { CarLooks.AfterGameSetLivery(__instance, texture2D); }
        catch (Exception e) { DebugLog.Error("core", e, "ModdedCars.LiveryGuardPostfix"); LogOnce("livery-guard", "[Cars] livery guard: " + e.Message); }
    }

    /// <summary>The garage shows a car: remembered, so a modded car's garage model gets your look.</summary>
    private static void GarageDummyPostfix(StateMerchantMenu __instance)
    {
        try { CarLooks.MerchantDummy(__instance); } catch (Exception ex) { DebugLog.Error("core", ex, "ModdedCars.GarageDummyPostfix"); }
    }

    /// <summary>Unequipping a modded car's mod livery in the garage's Livery Mods tab takes its skin off.</summary>
    private static void LiveryUnequipPostfix(string carName)
    {
        try { CarLooks.GameUnequipped(carName); } catch (Exception ex) { DebugLog.Error("core", ex, "ModdedCars.LiveryUnequipPostfix"); }
    }

    /// <summary>A mod livery picked for a modded car in the garage becomes its skin.</summary>
    private static void LiveryEquipPostfix(string carName, ModLivery livery)
    {
        try { CarLooks.GameEquipped(carName, livery); } catch (Exception ex) { DebugLog.Error("core", ex, "ModdedCars.LiveryEquipPostfix"); }
    }

    private static readonly HashSet<string> Logged = new();

    private static void LogOnce(string key, string text)
    {
        if (Logged.Add(key)) Core.CorePlugin.Logger?.LogWarning(text + " (further ones suppressed)");
    }
}
