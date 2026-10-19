using System;
using System.Collections.Generic;
using UnityEngine;
using ItemType = InventoryItemScriptableObject.ItemType;
using Object = UnityEngine.Object;

namespace OverTheHillPlus.Cars;

/// <summary>
/// A modded car's CarInfo: a copy of a stock car's (vehicle class, tyre/suspension setup, tool lists) with its own item
/// id, name, icon and price 0 (it is given to the player, see CarSave.Grant), registered in the game's item catalog.
/// That is all the garage needs to list it and all SetupCar needs to spawn it (address Prefabs/Cars/Car_&lt;name&gt;,
/// built from the name).
/// <para>Upgrades: the base car's cosmetics (every VehicleCosmetic: bull bars, roof loads, light bars, horns, mudflaps,
/// side tools, plushies, ...; the game has no rollcage cosmetic) and its mechanical upgrades (engine, health,
/// suspension lift, tyres, winch, horn, water resistance = the snorkel, tool slots, protection) are kept: CarBody places
/// their slots on the model. The tool slots have no model at all; the protection stages only change a damage modifier
/// on the car, and their model, the base car's rollcage (Performance/Rollcage, shaped for its body), stays hidden.
/// Dropped: the base car's liveries (painted for its UV layout; the car's own liveries take their place, see
/// <see cref="CarLiveryParts"/>), bed cosmetics unless
/// the car has a bed, cosmetics whose every model sits in a slot or item the car hides (<see cref="DropUnplaceable"/>),
/// and the drone and extra-headlight kinds (models on the donor's body; no stock car of the donor classes lists them).</para>
/// <para>The suspension lift sets every wheel's travel to the CarInfo's base travel plus the stage's lift, so the copy
/// gets the car's own travel (<see cref="CarUpgradeFit.LiftBaseTravel"/>) and the lift adds to it.</para>
/// </summary>
internal sealed class CarCatalog
{
    private readonly RuntimeCar _car;

    public CarInfo Info { get; private set; }
    public CarInfo Base { get; private set; }
    public string BaseName => Base != null ? Base.name : _car.Config.GarageBaseName;
    /// <summary>One line for the log: what was left out of the base car's upgrade lists.</summary>
    public string Dropped { get; private set; }

    private IntPtr _registeredIn;
    private bool _off;

    private static readonly HashSet<UpgradeType> KeptUpgrades = new()
    {
        UpgradeType.Default, UpgradeType.Engine, UpgradeType.Health, UpgradeType.Suspension, UpgradeType.Tire,
        UpgradeType.Winch, UpgradeType.Horn, UpgradeType.WaterResistance, UpgradeType.Inventory, UpgradeType.Protection,
    };

    /// <summary>Returned by <see cref="Prepare"/> when the base car can't be found yet but may be later.</summary>
    public const string NotYet = "not yet";

    public CarCatalog(RuntimeCar car) { _car = car; }

    /// <summary>Addressables keys to try for the base car's CarInfo: the game's own address helper, then the keys the
    /// game's catalog lists for its cars (the helper's address has not always resolved after game updates).</summary>
    private static IEnumerable<string> BaseKeys(string want)
    {
        string helper = null;
        try { helper = GameConstants.ADDRESSABLES_PATHS.GetCarInfoAddress(want); } catch (Exception ex) { DebugLog.Error("core", ex, "CarCatalog.BaseKeys"); }
        if (!string.IsNullOrEmpty(helper)) yield return helper;
        if (helper != want) yield return want;
        yield return "Data/Game/Cars/" + want;
    }

    /// <summary>The first of <paramref name="keys"/> that loads, through the game's ResourcesManager and then
    /// Addressables directly; null when none does (<paramref name="tried"/> lists the keys, for the error).</summary>
    internal static T LoadByKeys<T>(FunCore.Managers.ResourcesManager rm,
        Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStringArray tags, IEnumerable<string> keys, out string tried)
        where T : Object
    {
        tried = "";
        foreach (var key in keys)
        {
            if (string.IsNullOrEmpty(key)) continue;
            tried += (tried.Length > 0 ? ", " : "") + key;
            T found = null;
            try { found = rm.Load<T>(key, tags); } catch (Exception ex) { DebugLog.Error("core", ex, "CarCatalog.LoadByKeys"); found = null; }
            if (found != null) return found;
            // Only keys some catalog has (an unknown key makes Addressables log an InvalidKeyException), and a load
            // that brings nothing is released again.
            if (!OverTheHillPlus.Shared.RuntimeAddressables.HasKey(key)) continue;
            try
            {
                var h = UnityEngine.AddressableAssets.Addressables.LoadAssetAsync<T>((Il2CppSystem.String)key);
                found = h.WaitForCompletion();
                if (found == null && h.IsValid()) h.Release();
            }
            catch (Exception ex) { DebugLog.Error("core", ex, "CarCatalog.LoadByKeys"); found = null; }
            if (found != null) return found;
        }
        return null;
    }

    /// <summary>Keys to try for a car's garage model (the game's address helper first, then the catalog's names).</summary>
    internal static IEnumerable<string> DummyKeys(string car)
    {
        string helper = null;
        try { helper = GameConstants.ADDRESSABLES_PATHS.GetCarDummyPrefabAddress(car); } catch (Exception ex) { DebugLog.Error("core", ex, "CarCatalog.DummyKeys"); }
        if (!string.IsNullOrEmpty(helper)) yield return helper;
        string bare = car.StartsWith("Car_", StringComparison.Ordinal) ? car.Substring(4) : car;
        yield return "Car_" + bare + "_Dummy";
        yield return "Car_" + bare + "_Dummy.prefab";
    }

    /// <summary>
    /// Makes the CarInfo (not registered yet) from the garage base car's: from the item catalog when it is loaded,
    /// otherwise from Addressables (the catalog may only exist once a world loads, and the car's network part must be
    /// ready before any session starts). Null on success, otherwise why not.
    /// </summary>
    public string Prepare(FunCore.Managers.ResourcesManager rm, Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStringArray tags)
    {
        var def = _car.Def;
        string want = _car.Config.GarageBaseName;
        CarInfo baseInfo = null;
        var cat = PlayerInventoryManager.InventoryItemsCatalog;
        if (cat?.IdToSO != null)
            foreach (var kv in cat.IdToSO)
            {
                var ci = kv.Value?.TryCast<CarInfo>();
                if (ci != null && string.Equals(ci.name, want, StringComparison.OrdinalIgnoreCase)) { baseInfo = ci; break; }
            }
        string tried = "";
        if (baseInfo == null) baseInfo = LoadByKeys<CarInfo>(rm, tags, BaseKeys(want), out tried);
        // Not loadable yet and the item catalog isn't there either: ask again later (it loads with the game's data).
        if (baseInfo == null && cat?.IdToSO == null) return NotYet;
        if (baseInfo == null) return $"the garage base car {want} could not be loaded (tried {tried}, and the item catalog)";
        Base = baseInfo;

        var info = Object.Instantiate(baseInfo);
        info.name = def.CarName;
        info.ItemId = def.ItemId;
        info.Price = 0; // free: the garage gives it to the player (CarSave.Grant); removing the mod loses nothing
        info.RequiredDlc = DlcId.None;
        info.hasBed = def.HasBed;
        info.Name = _car.Text.Name(baseInfo.Name);
        info.Description = _car.Text.Description(baseInfo.Description);
        // The picture (a render) when the car has one, else the line drawing. A 512 px picture at Unity's default 100
        // px per unit shows at the size of the game's 256 px icons at 50.
        var icon = _car.Assets.Picture ?? _car.Assets.Icon;
        if (icon != null) info.Icon = Sprite.Create(icon, new Rect(0, 0, icon.width, icon.height), new Vector2(0.5f, 0.5f));

        var dropped = new Dictionary<string, int>();
        string liveries = null;
        if (info.DefaultMountedUpgrades != null) Filter(info.DefaultMountedUpgrades, dropped);
        if (baseInfo.UpgradesLists != null)
        {
            var lists = Object.Instantiate(baseInfo.UpgradesLists);
            lists.name = def.CarName + "_Upgrades";
            Filter(lists.Parts, dropped);
            // The car's own liveries right after the kept parts, before any other mod can append to the list: their
            // bits are the same on every peer with this car mod.
            try { liveries = _car.Liveries.Build(baseInfo, info, lists, info.DefaultMountedUpgrades); }
            catch (Exception e) { DebugLog.Error(_car.Id, e, "CarCatalog.Prepare liveries"); liveries = "no liveries: " + e.Message; }
            lists.hideFlags = HideFlags.DontUnloadUnusedAsset;
            Object.DontDestroyOnLoad(lists);
            info.UpgradesLists = lists;
        }
        var sb = new System.Text.StringBuilder();
        foreach (var kv in dropped) sb.Append(sb.Length > 0 ? ", " : "").Append(kv.Value).Append(' ').Append(kv.Key);
        Dropped = sb.Length == 0 ? "nothing" : sb.ToString();

        info.hideFlags = HideFlags.DontUnloadUnusedAsset;
        Object.DontDestroyOnLoad(info);
        Info = info;
        _car.Info($"garage data from {baseInfo.name}; upgrades left out: {Dropped}." + (liveries != null ? " Liveries: " + liveries + "." : ""));
        return null;
    }

    private void Filter(Il2CppSystem.Collections.Generic.List<VehiclePartSO> parts, Dictionary<string, int> dropped)
    {
        for (int i = parts.Count - 1; i >= 0; i--)
        {
            var p = parts[i];
            string why = Keep(p);
            if (why == null) continue;
            parts.RemoveAt(i);
            dropped[why] = dropped.TryGetValue(why, out int n) ? n + 1 : 1;
        }
    }

    /// <summary>Null when the part stays on the car, otherwise the kind it was dropped as.</summary>
    private string Keep(VehiclePartSO p)
    {
        if (p == null) return "missing";
        if (p.Type is ItemType.Livery or ItemType.SpecialLivery) return "liveries";
        if (p.Type == ItemType.VehicleCosmetic) return KeepCosmetic(p.TryCast<VehicleCosmetic>());
        var up = p.TryCast<VehicleUpgrade>();
        if (up == null) return "other parts";
        return KeptUpgrades.Contains(up.UpgradeType) ? null : up.UpgradeType.ToString().ToLowerInvariant() + " upgrades";
    }

    /// <summary>
    /// Every cosmetic stays. The game types cosmetics by CosmeticType (FrontSection, RearSection, SideSection,
    /// FrontBumper, RearBumper, Hood, Roof, Bed, RoofLights, Fenders, SideStep, Antenna, BushCables, Horns, Hidden): none
    /// is a rollcage. The rollcage is the model of the protection upgrades (VehicleUpgrade, UpgradeType.Protection),
    /// dropped in <see cref="Keep"/>; its slot is hidden by CarBody. A bed cosmetic (a pickup's load bed) has no slot on
    /// a car without a bed and goes.
    /// </summary>
    private string KeepCosmetic(VehicleCosmetic c)
    {
        if (c == null) return null;
        return c.CosmeticType == CosmeticType.Bed && !_car.Def.HasBed ? "bed cosmetics" : null;
    }

    /// <summary>The body size the game uses for spawn checks, from the model's body (same convention as the base car's:
    /// full size when its numbers look like a car's length, otherwise half-extents).</summary>
    public void FitToLayout(CarLayout lay)
    {
        if (Info == null || lay == null) return;
        var b = Base.carColliderBounds;
        bool fullSize = b.z > 3.2f;
        Info.carColliderBounds = fullSize ? lay.Body.size : lay.Body.extents;
        Info.carColliderBoundsCenter = lay.Body.center;
        if (_car.Tuning.Tyres != null) Info.tireAsset = _car.Tuning.Tyres;
        FitSuspensionLift();
    }

    /// <summary>The suspension lift's base travel: the base car's times the car's own multiplier (see the class notes).</summary>
    private void FitSuspensionLift()
    {
        try
        {
            var mine = Info.suspensionSetup;
            var theirs = Base?.suspensionSetup;
            if (mine == null || theirs == null) return;
            if (mine.Pointer == theirs.Pointer)
            {
                // Instantiate copies serialized classes by value; if it ever shares this one, never change the base car's
                mine = new VehicleControllerSetup.SuspensionSetup
                {
                    radius = theirs.radius, inertia = theirs.inertia, center = theirs.center, suspensionTravel = theirs.suspensionTravel,
                    suspensionAnchor = theirs.suspensionAnchor, springRate = theirs.springRate, damperRate = theirs.damperRate,
                    preloadDistance = theirs.preloadDistance, unsprungMass = theirs.unsprungMass, bumpStopRate = theirs.bumpStopRate,
                    bumpTravelOffset = theirs.bumpTravelOffset, numberOfExtraRays = theirs.numberOfExtraRays,
                    physicsGroundPenetration = theirs.physicsGroundPenetration,
                };
                Info.suspensionSetup = mine;
            }
            float baseTravel = theirs.suspensionTravel;
            mine.suspensionTravel = CarUpgradeFit.LiftBaseTravel(baseTravel, _car.Tuning.Profile.SuspensionTravel);
            // The rest of the setup is the base car's wheel too: whatever game code applies it (a wheel upgrade) must put the
            // car's own tuned wheel back, not the donor's (a 8.6 t truck on 42 kN/m springs sits on its bump stops).
            var w = _car.Tuning.Wheel;
            if (w != null)
            {
                mine.radius = w.Radius;
                mine.inertia = w.Inertia;
                mine.springRate = w.SpringRate;
                mine.damperRate = w.DamperRate;
                mine.unsprungMass = w.UnsprungMass;
                mine.bumpStopRate = w.BumpStopRate;
            }
            _car.Info($"suspension lift base travel {mine.suspensionTravel:0.###} m (the {Base.name}'s {baseTravel:0.###} m); " +
                      $"CarInfo wheel {(w != null ? w.Describe() : "the base car's")}.");
        }
        catch (Exception e) { DebugLog.Error("core", e, "CarCatalog.FitSuspensionLift"); _car.LogOnce("lift", "suspension lift: " + e.Message); }
    }

    /// <summary>Leaves out the cosmetics in <paramref name="ids"/>: every model of theirs sits in a slot or item the car
    /// hides, so they would sell nothing visible. Called while the template is built, before the car's part list is first
    /// used (the template's and the garage model's part checks run after it). Mechanical upgrades are never dropped here.</summary>
    public void DropUnplaceable(ICollection<uint> ids)
    {
        if (Info == null || ids == null || ids.Count == 0) return;
        var names = new List<string>();
        void Drop(Il2CppSystem.Collections.Generic.List<VehiclePartSO> parts)
        {
            if (parts == null) return;
            for (int i = parts.Count - 1; i >= 0; i--)
            {
                var p = parts[i];
                if (p == null || p.Type != ItemType.VehicleCosmetic || !ids.Contains(p.ItemId)) continue;
                if (!names.Contains(p.name)) names.Add(p.name);
                parts.RemoveAt(i);
            }
        }
        Drop(Info.UpgradesLists?.Parts);
        Drop(Info.DefaultMountedUpgrades);
        if (names.Count == 0) return;
        names.Sort(StringComparer.Ordinal);
        Dropped = (Dropped == "nothing" ? "" : Dropped + ", ") + names.Count + " cosmetics with no place on the body";
        _car.Info($"cosmetics left out (no place on the {_car.Def.CarName}): {string.Join(", ", names)}.");
    }

    /// <summary>The catalog is loaded (cars can be registered now).</summary>
    public static bool CatalogLoaded => PlayerInventoryManager.InventoryItemsCatalog?.IdToSO != null;

    /// <summary>Registered in the catalog: the garage lists it and SetupCar can spawn it.</summary>
    public bool Registered => _registeredIn != IntPtr.Zero && !_off;

    /// <summary>Adds the CarInfo to the catalog's three lookups (after checking nothing else uses its id or name).
    /// Null on success.</summary>
    public string Register()
    {
        var def = _car.Def;
        var cat = PlayerInventoryManager.InventoryItemsCatalog;
        if (cat?.IdToSO == null || Info == null) return "the item catalog is gone";
        if (cat.IdToSO.ContainsKey(def.ItemId)) return $"another item already uses the {def.CarName}'s id 0x{def.ItemId:X8}";
        if (cat.NameToId.ContainsKey(def.CarName)) return "another item is already named " + def.CarName;
        // A livery whose id another item has is left out (logged); the car itself still goes in.
        string clash = _car.Liveries.Clash(cat);
        if (clash != null) _car.Warn(clash + ": that livery is left out of the catalog.");
        Add(cat);
        _car.Info("in the item catalog (free).");
        return null;
    }

    private void Add(InventoryItemsCatalog cat)
    {
        var def = _car.Def;
        cat.IdToSO[def.ItemId] = Info;
        cat.IdToName[def.ItemId] = def.CarName;
        cat.NameToId[def.CarName] = def.ItemId;
        _car.Liveries.AddTo(cat);
        _registeredIn = cat.Pointer;
    }

    /// <summary>Takes the CarInfo out of the catalog again (the car was switched off after it was registered), so
    /// nothing can ask for it any more; <see cref="EnsureRegistered"/> stops putting it back.</summary>
    public void Unregister()
    {
        var def = _car.Def;
        try
        {
            var cat = PlayerInventoryManager.InventoryItemsCatalog;
            if (cat?.IdToSO != null && cat.IdToSO.ContainsKey(def.ItemId) && Info != null && cat.IdToSO[def.ItemId]?.Pointer == Info.Pointer)
            {
                cat.IdToSO.Remove(def.ItemId);
                cat.IdToName.Remove(def.ItemId);
                cat.NameToId.Remove(def.CarName);
            }
            if (cat?.IdToSO != null) _car.Liveries.RemoveFrom(cat);
        }
        catch (Exception ex) { DebugLog.Error("core", ex, "CarCatalog.Unregister"); }
        _registeredIn = IntPtr.Zero;
        _off = true;
    }

    /// <summary>Puts the CarInfo back if the game rebuilt its catalog (cheap: one lookup).</summary>
    public void EnsureRegistered()
    {
        if (Info == null || _off) return;
        var def = _car.Def;
        try
        {
            var cat = PlayerInventoryManager.InventoryItemsCatalog;
            if (cat?.IdToSO == null) return;
            if (cat.Pointer == _registeredIn && cat.IdToSO.ContainsKey(def.ItemId)) return;
            if (cat.IdToSO.ContainsKey(def.ItemId))
            {
                var have = cat.IdToSO[def.ItemId];
                if (have != null && have.Pointer != Info.Pointer) { _car.LogOnce("catalog-clash", $"another item took the {def.CarName}'s id"); return; }
            }
            Add(cat);
            _car.Info("registered again in the rebuilt item catalog.");
        }
        catch (Exception e) { DebugLog.Error("core", e, "CarCatalog.EnsureRegistered"); _car.LogOnce("catalog", "catalog check: " + e.Message); }
    }

    public bool IsThis(InventoryItemScriptableObject so) => so != null && so.ItemId == _car.Def.ItemId;

    /// <summary>Is this car object one of ours (its customization controller points at our CarInfo)?</summary>
    public bool IsThis(VehicleCustomizationController vcc)
    {
        if (vcc == null || Info == null) return false;
        try
        {
            var ci = vcc.currentCarInfo;
            return ci != null && ci.Pointer == Info.Pointer;
        }
        catch (Exception ex) { DebugLog.Error("core", ex, "CarCatalog.IsThis"); return false; }
    }
}
