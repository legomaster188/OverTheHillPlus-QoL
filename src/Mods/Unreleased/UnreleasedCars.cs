using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using Fusion;
using FunCore.Managers;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;
using UnityEngine.Localization;
using Object = UnityEngine.Object;

namespace OverTheHillPlus.Unreleased;

/// <summary>One of the four cars the game ships but doesn't sell.</summary>
internal sealed class UnreleasedCar
{
    /// <summary>The car's internal name: its catalog name, and Prefabs/Cars/Car_&lt;Name&gt; is its prefab address.</summary>
    public string Name;
    /// <summary>The prefab's name in Fusion's prefab table.</summary>
    public string Prefab;
    public string Description;
    /// <summary>Stock cars to copy a CarInfo from, best first (null: the game ships the car's own CarInfo).</summary>
    public string[] Donors;
    /// <summary>The item id known before the game is up (the shipped CarInfo's id; 0 for the cars we give one).</summary>
    public uint KnownId;
    public ConfigEntry<bool> Enabled;

    // ---- this session
    public CarInfo Info;
    public uint Id;
    public GameObject PrefabObject;
    public GameObject DummyStandIn;
    /// <summary>How many times the stand-in preview was tried (it is retried while the donor's preview isn't loadable yet).</summary>
    public int DummyTries;
    public string DonorUsed;
    /// <summary>The donor's item id (the stand-in a host spawns while a vanilla player is in the session).</summary>
    public uint DonorId;
    /// <summary>This session's LocalizedStrings whose text is ours (dropped from the overrides when set up again).</summary>
    public IntPtr NameText, DescriptionText;
    public bool Registered;
    /// <summary>Why the car isn't available (null while it is, or before the catalog is up).</summary>
    public string Problem;

    public bool IsOn => Enabled == null || Enabled.Value;
    public bool HasOwnInfo => Donors == null;
    /// <summary>
    /// Its CarInfo exists only with this mod (Brigand, Cordillera): on a vanilla game the prefab's CarInfo is null
    /// and VehicleCustomizationController.InitVehicleUpgrades throws in Spawned, so it is solo only (ruling P15; see
    /// <see cref="GuestGate"/>).
    /// </summary>
    public bool SoloOnly => Donors != null;
}

/// <summary>
/// Puts the unreleased cars into the game's item catalog, so the garage can list them and the host can spawn them.
///
/// Findings (Cpp2IL of the updated game and the spike in .superpowers/sdd/2026-10-17-round5/new-car-spike.md):
/// - All four prefabs are in Fusion's networked prefab table (Car_Baguette 62, Car_Brigand 67, Car_Cordillera 70,
///   Car_Grizzly 74), so every game, modded or not, can instantiate them: they spawn as normal networked cars.
/// - OverlandNetworkRunner.SetupCar / SpawnPlayer look the car id up in InventoryItemsCatalog.IdToSO and load
///   "Prefabs/Cars/Car_" + carInfo.name through ResourcesManager (which checks its cache before Addressables).
///   InventoryUtility.GetAllVehicles (the garage list) enumerates IdToSO. PlayerInventoryManager.ItemIdLookup
///   lower-cases the name before it reads NameToId, so NameToId keys are lower case.
/// - Grizzly and Baguette are Addressable (Prefabs/Cars/Car_Grizzly, MerchantDummies/Car_Grizzly_Dummy exist) and
///   their VehicleCustomizationController points at a CarInfo the catalog leaves out ("Grizzly[CatalogExcluded]").
///   That asset is renamed to the plain name (the address is built from it) and registered under its own id.
/// - Brigand and Cordillera are serialized straight into resources.assets (StaticLazy table entries): no address,
///   no preview dummy, no CarInfo (their controller's CarInfo is null, and InitVehicleUpgrades needs one). They get
///   a copy of a similar stock car's CarInfo (Lynx-sized and Cumbre-sized bodies), without the donor's liveries
///   (painted for the donor's body), under an id hashed from the name (<see cref="UnreleasedRules.AllocateItemId"/>).
///   The prefab is loaded through Fusion's table and served to ResourcesManager under its address; a copy of the
///   donor's preview dummy stands in for the missing one (garage, lobby and main menu load it by address).
/// </summary>
internal static class UnreleasedCars
{
    // Read from the shipped CarInfos' raw bytes (spike); the loaded asset's own id wins if it differs.
    private const uint GrizzlyId = 0xAF43C970, BaguetteId = 0x7BDA7203;

    public static readonly UnreleasedCar[] All =
    {
        new() { Name = "Grizzly", Prefab = "Car_Grizzly", KnownId = GrizzlyId,
                Description = "An unreleased car found in the game files, with the developer's own setup and liveries." },
        new() { Name = "Baguette", Prefab = "Car_Baguette", KnownId = BaguetteId,
                Description = "An unreleased car found in the game files, with the developer's own setup and liveries." },
        new() { Name = "Brigand", Prefab = "Car_Brigand", Donors = new[] { "Lynx", "Trailrat", "Boar" },
                Description = "An unreleased car found in the game files. The game has no setup for it, so it drives with a similar car's." },
        new() { Name = "Cordillera", Prefab = "Car_Cordillera", Donors = new[] { "Cumbre", "TrailBurner", "Marshal" },
                Description = "An unreleased car found in the game files. The game has no setup for it, so it drives with a similar car's." },
    };

    /// <summary>Every item id this mod adds to the catalog that the game itself doesn't have (cars and their parts).</summary>
    public static readonly HashSet<uint> OurIds = new() { GrizzlyId, BaguetteId };
    /// <summary>The car ids only.</summary>
    public static readonly HashSet<uint> CarIds = new() { GrizzlyId, BaguetteId };

    /// <summary>LocalizedString (by pointer) → the English text shown for it: the cars' names and descriptions.</summary>
    internal static readonly Dictionary<IntPtr, string> TextOverrides = new();

    private static IntPtr _catalog;
    private static float _nextCheck, _nextCache;
    private static GameObject _holder;

    /// <summary>The catalog is up and every car was tried.</summary>
    public static bool Ready { get; private set; }

    public static bool AnyRegistered
    {
        get
        {
            foreach (var c in All) if (c.Registered) return true;
            return false;
        }
    }

    public static UnreleasedCar Find(uint id)
    {
        if (id == 0) return null;
        foreach (var c in All)
            if (c.Id == id || c.KnownId == id) return c;
        return null;
    }

    public static bool IsOurCar(uint id) => id != 0 && CarIds.Contains(id);

    // ---------------------------------------------------------------- per frame

    internal static void Tick()
    {
        float now = Time.unscaledTime;
        if (now >= _nextCheck)
        {
            _nextCheck = now + 0.5f;
            try { CheckCatalog(); }
            catch (Exception e) { DebugLog.Error("unreleased", e, "UnreleasedCars.Tick"); UnreleasedPlugin.LogOnce("catalog", "catalog check failed: " + e.Message); }
        }
        if (Ready && now >= _nextCache)
        {
            _nextCache = now + 0.25f;
            try { EnsureCached(); }
            catch (Exception e) { DebugLog.Error("unreleased", e, "UnreleasedCars.Tick"); UnreleasedPlugin.LogOnce("cache", "address cache failed: " + e.Message); }
        }
    }

    private static void CheckCatalog()
    {
        InventoryItemsCatalog cat;
        try { cat = PlayerInventoryManager.InventoryItemsCatalog; } catch (Exception ex) { DebugLog.Error("unreleased", ex, "UnreleasedCars.CheckCatalog"); return; }
        if (cat == null || cat.IdToSO == null || cat.IdToSO.Count == 0) return;
        if (cat.Pointer != _catalog)
        {
            _catalog = cat.Pointer;
            foreach (var car in All) Register(cat, car);
            Ready = true;
            SyncNonStock();
            DebugLog.Info(UnreleasedPlugin.ModId, $"item catalog seen ({cat.IdToSO.Count} items): " + Summary());
            UnreleasedPlugin.Logger.LogInfo("[Unreleased] " + Summary());
            return;
        }
        foreach (var car in All)
        {
            if (!car.Registered) continue;
            // An asset unloaded under us (Unity's null check): set the car up again.
            if (car.Info == null || car.PrefabObject == null)
            {
                // Our own entries, or the "the game sells it now" checks would take them for the game's.
                cat.IdToSO.Remove(car.Id);
                cat.IdToName?.Remove(car.Id);
                cat.NameToId?.Remove(car.Name.ToLowerInvariant());
                DebugLog.Info(UnreleasedPlugin.ModId, $"{car.Name}: an asset was unloaded under it, setting it up again");
                Register(cat, car);
                SyncNonStock();
                continue;
            }
            // Something rebuilt the dictionaries in place: put the car back.
            if (!cat.IdToSO.ContainsKey(car.Id)) AddToCatalog(cat, car.Id, car.Info, car.Name);
        }
    }

    /// <summary>
    /// Tells core which car ids are ours (core's NonStockCars), so core's car framework never picks one as the stock
    /// fallback (join data, save) and we never pick one of core's modded cars. Shown to others (Discord+) as the
    /// stock car it stands in for in sessions, or as "a car".
    /// </summary>
    private static void SyncNonStock()
    {
        var cars = new List<(uint id, string name)>();
        foreach (uint id in CarIds) cars.Add((id, Find(id)?.DonorUsed));
        Core.Shared.NonStockCars.Set("unreleased", cars);
    }

    internal static string Summary()
    {
        var parts = new List<string>();
        foreach (var c in All) parts.Add(c.Name + (c.Registered ? $" ok (id {c.Id}{(c.DonorUsed != null ? ", setup of the " + c.DonorUsed : "")})" : ": " + c.Problem));
        return string.Join("; ", parts);
    }

    // ---------------------------------------------------------------- registration

    private static void Register(InventoryItemsCatalog cat, UnreleasedCar car)
    {
        car.Registered = false;
        car.Problem = null;
        if (car.NameText != IntPtr.Zero) TextOverrides.Remove(car.NameText);
        if (car.DescriptionText != IntPtr.Zero) TextOverrides.Remove(car.DescriptionText);
        car.NameText = car.DescriptionText = IntPtr.Zero;
        try
        {
            if (car.HasOwnInfo) RegisterShipped(cat, car);
            else RegisterCopy(cat, car);
        }
        catch (Exception e)
        {
            DebugLog.Error("unreleased", e, "UnreleasedCars.Register");
            car.Problem = "could not be set up (see the log)";
            UnreleasedPlugin.Logger.LogWarning($"[Unreleased] {car.Name}: {e}");
        }
    }

    private static GameObject LoadPrefab(UnreleasedCar car)
    {
        var table = NetworkProjectConfig.Global?.PrefabTable;
        if (table == null) throw new InvalidOperationException("Fusion's prefab table isn't loaded");
        var id = table.GetId(car.Prefab);
        if (!id.IsValid) throw new InvalidOperationException(car.Prefab + " is not in Fusion's prefab table any more");
        var no = table.Load(id, true);
        if (no == null) throw new InvalidOperationException(car.Prefab + " did not load");
        return no.gameObject;
    }

    private static VehicleCustomizationController Controller(GameObject go) =>
        go.GetComponent<VehicleCustomizationController>() ?? go.GetComponentInChildren<VehicleCustomizationController>(true);

    /// <summary>Grizzly, Baguette: the prefab's own (catalog-excluded) CarInfo.</summary>
    private static void RegisterShipped(InventoryItemsCatalog cat, UnreleasedCar car)
    {
        var go = LoadPrefab(car);
        var vcc = Controller(go);
        var info = vcc?.currentCarInfo;
        if (info == null) { car.Problem = "the game files have no setup for it"; return; }
        uint id = info.ItemId;
        if (id != car.KnownId)
        {
            UnreleasedPlugin.Logger.LogInfo($"[Unreleased] {car.Name}: item id {id} (expected {car.KnownId})");
            // The loaded asset's id is the real one: the constant would only match some other item.
            CarIds.Remove(car.KnownId);
            OurIds.Remove(car.KnownId);
            car.KnownId = id;
            CarIds.Add(id);
            OurIds.Add(id);
        }
        if (cat.IdToSO.ContainsKey(id) && cat.IdToSO[id]?.Pointer != info.Pointer)
        {
            car.Problem = "the game sells this car now: get it from the garage";
            CarIds.Remove(id);
            OurIds.Remove(id);
            return;
        }
        if (!DlcAllowed(info)) { car.Problem = "needs a DLC this game doesn't have"; return; }

        info.name = car.Name; // the prefab address is built from it ("Grizzly[CatalogExcluded]" otherwise)
        info.hideFlags |= HideFlags.DontUnloadUnusedAsset;
        Finish(cat, car, info, id, go);
    }

    /// <summary>Brigand, Cordillera: a stock car's CarInfo, copied, without its liveries.</summary>
    private static void RegisterCopy(InventoryItemsCatalog cat, UnreleasedCar car)
    {
        if (cat.NameToId.ContainsKey(car.Name.ToLowerInvariant()))
        {
            car.Problem = "the game sells this car now: get it from the garage";
            return;
        }
        uint id = UnreleasedRules.AllocateItemId(car.Name, i => cat.IdToSO.ContainsKey(i));
        if (id == 0) { car.Problem = "no free item id"; return; }
        car.KnownId = id;
        CarIds.Add(id);
        OurIds.Add(id);

        CarInfo donor = null;
        foreach (var name in car.Donors)
        {
            donor = FindStockCar(cat, name);
            if (donor != null && DlcAllowed(donor) && donor.RequiredDlc == DlcId.None) { car.DonorUsed = name; car.DonorId = donor.ItemId; break; }
            donor = null;
        }
        if (donor == null) { car.Problem = "none of the cars it copies its setup from was found"; return; }

        var go = LoadPrefab(car);
        var vcc = Controller(go);
        if (vcc == null) { car.Problem = "the prefab has no customization controller"; return; }

        var info = Object.Instantiate(donor);
        info.name = car.Name;
        info.hideFlags = HideFlags.DontUnloadUnusedAsset;
        info.ItemId = id;
        info.RequiredDlc = DlcId.None;
        // Own objects (the texts are matched by object), pointing at the donor's entries so code that skips empty
        // strings still asks for them.
        info.Name = TextLike(donor.Name);
        info.Description = TextLike(donor.Description);
        var picture = Picture(car.Name); // its own picture, not the donor's
        if (picture != null) info.Icon = picture;
        if (donor.UpgradesLists != null)
        {
            var lists = Object.Instantiate(donor.UpgradesLists);
            lists.hideFlags = HideFlags.DontUnloadUnusedAsset;
            lists.Parts = WithoutLiveries(donor.UpgradesLists.Parts);
            info.UpgradesLists = lists;
        }
        info.DefaultMountedUpgrades = WithoutLiveries(donor.DefaultMountedUpgrades);
        vcc.currentCarInfo = info; // InitVehicleUpgrades (VehicleCustomizationController.Spawned) needs one

        car.PrefabObject = go;
        car.DummyStandIn = MakeDummyStandIn(car);
        Finish(cat, car, info, id, go);
    }

    /// <summary>
    /// The car's picture for the garage and car lists: the embedded "unreleased/&lt;name&gt;_picture.png" (512 × 512,
    /// rendered by Liveries/tools/vera/render_picture.py; shows at the size of the game's 256 px icons), or null.
    /// </summary>
    private static Sprite Picture(string name)
    {
        try
        {
            using var s = typeof(UnreleasedCars).Assembly.GetManifestResourceStream("unreleased/" + name.ToLowerInvariant() + "_picture.png");
            if (s == null) return null;
            using var ms = new System.IO.MemoryStream();
            s.CopyTo(ms);
            var t = new Texture2D(2, 2, TextureFormat.RGBA32, true) { name = name + "_picture" };
            if (!ImageConversion.LoadImage(t, new Il2CppStructArray<byte>(ms.ToArray()), true))
            {
                Object.Destroy(t);
                UnreleasedPlugin.LogOnce("picture" + name, $"the {name}'s picture is not a readable PNG: it keeps the donor's");
                return null;
            }
            t.wrapMode = TextureWrapMode.Clamp;
            t.hideFlags = HideFlags.DontUnloadUnusedAsset;
            Object.DontDestroyOnLoad(t);
            var sprite = Sprite.Create(t, new Rect(0, 0, t.width, t.height), new Vector2(0.5f, 0.5f));
            sprite.name = name + "_Picture";
            sprite.hideFlags = HideFlags.DontUnloadUnusedAsset;
            return sprite;
        }
        catch (Exception e)
        {
            DebugLog.Error("unreleased", e, "UnreleasedCars.Picture");
            return null;
        }
    }

    private static void Finish(InventoryItemsCatalog cat, UnreleasedCar car, CarInfo info, uint id, GameObject go)
    {
        car.Info = info;
        car.Id = id;
        if (car.HasOwnInfo) car.PrefabObject = go; // keeps Fusion's load (and so the CarInfo) alive
        if (info.Name == null || info.Name.IsEmpty) info.Name = TextLike(AnyStockCar(cat)?.Name);
        if (info.Description == null || info.Description.IsEmpty) info.Description = TextLike(AnyStockCar(cat)?.Description);
        // The garage's vehicle list groups cars by category (StateMerchantMenu.InitializeSelectVehicleScreen looks each
        // car's carCategorySetup up in a dictionary): a shipped-but-unfinished CarInfo without one made that lookup throw,
        // and the whole vehicle screen stayed on its placeholder text ("VEHICLE NAME", lorem ipsum).
        try
        {
            if (info.carCategorySetup == null)
            {
                var like = AnyStockCarWithCategory(cat);
                if (like != null)
                {
                    info.carCategorySetup = like.carCategorySetup;
                    UnreleasedPlugin.Logger.LogInfo($"[Unreleased] the {car.Name} had no vehicle category: using the {like.name}'s");
                }
            }
        }
        catch (Exception e) { DebugLog.Error("unreleased", e, "UnreleasedCars.Finish category"); }
        car.NameText = info.Name.Pointer;
        car.DescriptionText = info.Description.Pointer;
        TextOverrides[car.NameText] = car.Name;
        TextOverrides[car.DescriptionText] = car.Description;
        AddToCatalog(cat, id, info, car.Name);
        AddParts(cat, info);
        car.Registered = true;
    }

    private static void AddToCatalog(InventoryItemsCatalog cat, uint id, InventoryItemScriptableObject so, string name)
    {
        cat.IdToSO[id] = so;
        if (cat.IdToName != null) cat.IdToName[id] = name;
        if (cat.NameToId != null) cat.NameToId[name.ToLowerInvariant()] = id;
    }

    /// <summary>The car's upgrade and default parts the catalog lacks (the Grizzly's liveries, for one).</summary>
    private static void AddParts(InventoryItemsCatalog cat, CarInfo info)
    {
        var parts = new List<VehiclePartSO>();
        try { if (info.UpgradesLists?.Parts != null) foreach (var p in info.UpgradesLists.Parts) parts.Add(p); } catch (Exception ex) { DebugLog.Error("unreleased", ex, "UnreleasedCars.AddParts"); }
        try { if (info.DefaultMountedUpgrades != null) foreach (var p in info.DefaultMountedUpgrades) parts.Add(p); } catch (Exception ex) { DebugLog.Error("unreleased", ex, "UnreleasedCars.AddParts"); }
        int added = 0;
        foreach (var p in parts)
        {
            if (p == null) continue;
            uint id = p.ItemId;
            if (id == 0 || cat.IdToSO.ContainsKey(id)) continue;
            cat.IdToSO[id] = p;
            string key = (p.name ?? "").ToLowerInvariant();
            if (cat.IdToName != null && !cat.IdToName.ContainsKey(id)) cat.IdToName[id] = p.name;
            if (cat.NameToId != null && key.Length > 0 && !cat.NameToId.ContainsKey(key)) cat.NameToId[key] = id;
            OurIds.Add(id);
            added++;
        }
        if (added > 0) UnreleasedPlugin.Logger.LogInfo($"[Unreleased] {info.name}: {added} parts added to the catalog");
    }

    private static Il2CppSystem.Collections.Generic.List<VehiclePartSO> WithoutLiveries(Il2CppSystem.Collections.Generic.List<VehiclePartSO> parts)
    {
        var list = new Il2CppSystem.Collections.Generic.List<VehiclePartSO>();
        if (parts == null) return list;
        foreach (var p in parts)
            if (p != null && !InventoryUtility.IsLivery(p)) list.Add(p);
        return list;
    }

    /// <summary>A new LocalizedString with the same table entry as <paramref name="like"/> (empty when null).</summary>
    private static LocalizedString TextLike(LocalizedString like)
    {
        try { if (like != null && !like.IsEmpty) return new LocalizedString(like.TableReference, like.TableEntryReference); }
        catch (Exception ex) { DebugLog.Error("unreleased", ex, "UnreleasedCars.TextLike"); }
        return new LocalizedString();
    }

    private static CarInfo AnyStockCar(InventoryItemsCatalog cat)
    {
        foreach (var kv in cat.IdToSO)
        {
            var info = kv.Value?.TryCast<CarInfo>();
            if (info != null && !CarIds.Contains(info.ItemId) && info.Name != null && !info.Name.IsEmpty) return info;
        }
        return null;
    }

    private static CarInfo AnyStockCarWithCategory(InventoryItemsCatalog cat)
    {
        foreach (var kv in cat.IdToSO)
        {
            var info = kv.Value?.TryCast<CarInfo>();
            if (info != null && !CarIds.Contains(info.ItemId) && info.carCategorySetup != null) return info;
        }
        return null;
    }

    private static CarInfo FindStockCar(InventoryItemsCatalog cat, string name)
    {
        foreach (var kv in cat.IdToSO)
        {
            var info = kv.Value?.TryCast<CarInfo>();
            if (info != null && string.Equals(info.name, name, StringComparison.OrdinalIgnoreCase)) return info;
        }
        return null;
    }

    private static bool DlcAllowed(InventoryItemScriptableObject so)
    {
        try { return DlcManager.IsAllowed(so); } catch (Exception ex) { DebugLog.Error("unreleased", ex, "UnreleasedCars.DlcAllowed"); return so.RequiredDlc == DlcId.None; }
    }

    // ---------------------------------------------------------------- addresses for the two cars without one

    private static GameObject MakeDummyStandIn(UnreleasedCar car)
    {
        try
        {
            var rm = HybridSingleton<ResourcesManager>.Instance;
            if (rm == null) return null;
            // ResourcesManager.Load reads only its cache, which holds just the previews the game showed already (the Cumbre's
            // was, the Lynx's was not): load it through Addressables when it isn't there.
            var donorDummy = Cars.CarCatalog.LoadByKeys<GameObject>(rm, new Il2CppStringArray(0), Cars.CarCatalog.DummyKeys(car.DonorUsed), out string tried);
            if (donorDummy == null)
            {
                UnreleasedPlugin.LogOnce("standin-" + car.Name, $"{car.Name}: the {car.DonorUsed}'s preview could not be loaded (tried {tried})");
                return null;
            }
            if (_holder == null)
            {
                _holder = new GameObject("OTH_UnreleasedStandIns");
                _holder.SetActive(false); // nothing under it wakes up; copies the game makes from it do
                Object.DontDestroyOnLoad(_holder);
            }
            var copy = Object.Instantiate(donorDummy, _holder.transform);
            copy.name = "Car_" + car.Name + "_Dummy";
            return copy;
        }
        catch (Exception e)
        {
            DebugLog.Error("unreleased", e, "UnreleasedCars.MakeDummyStandIn");
            UnreleasedPlugin.Logger.LogWarning($"[Unreleased] {car.Name}: no preview stand-in ({e.Message})");
            return null;
        }
    }

    /// <summary>
    /// Serves the Brigand's and Cordillera's prefab and stand-in preview to ResourcesManager under their addresses (the
    /// preview also through Addressables, which is what the garage's loader reads).
    /// The game drops cache entries when it unloads a scene's tags, so this is checked often and right before the
    /// game loads one of them (SetupCar, the garage preview).
    /// </summary>
    internal static void EnsureCached()
    {
        var rm = HybridSingleton<ResourcesManager>.Instance;
        if (rm == null) return;
        foreach (var car in All)
        {
            if (!car.Registered || car.HasOwnInfo) continue;
            string prefabAddress = GameConstants.ADDRESSABLES_PATHS.GetCarPrefabAddress(car.Name);
            Cache(rm, prefabAddress, car.PrefabObject);
            if (car.DummyStandIn == null && car.DummyTries < 20)
            {
                car.DummyTries++;
                car.DummyStandIn = MakeDummyStandIn(car);
            }
            string dummy = GameConstants.ADDRESSABLES_PATHS.GetCarDummyPrefabAddress(car.Name);
            Cache(rm, dummy, car.DummyStandIn);
            // The prefab too: the spawner and anything else that asks Addressables finds it.
            if (car.PrefabObject != null)
            {
                string why = OverTheHillPlus.Shared.RuntimeAddressables.Serve(prefabAddress, car.PrefabObject);
                if (why != null) UnreleasedPlugin.LogOnce("prefab-" + car.Name, $"{car.Name}: the prefab could not be served ({why})");
            }
            // The garage preview (StateMerchantMenu.RefreshDummyCar) loads through LoadAndCacheAddressablesSync, which never
            // reads ResourcesManager's cache: without a location the load throws InvalidKeyException (then a null key).
            if (car.DummyStandIn != null)
            {
                string why = OverTheHillPlus.Shared.RuntimeAddressables.Serve(dummy, car.DummyStandIn);
                if (why != null) UnreleasedPlugin.LogOnce("dummy-" + car.Name, $"{car.Name}: the garage preview could not be served ({why})");
                else UnreleasedPlugin.InfoOnce("dummy-ok-" + car.Name, $"{car.Name}: preview stand-in (copy of the {car.DonorUsed}'s) served at {dummy}");
            }
        }
    }

    private static void Cache(ResourcesManager rm, string address, Object obj)
    {
        if (obj == null || string.IsNullOrEmpty(address)) return;
        var cached = rm.cachedObjs?.primary;
        if (cached != null && cached.ContainsKey(address)) return;
        rm.AddAddressableToCache(address, obj, new Il2CppStringArray(0));
    }

    /// <summary>A stock car for a host that can't spawn one of ours (it failed to set up here).</summary>
    internal static uint StartingCarId()
    {
        try { return MainMenuCarSpawner.GetStartingCarInfo()?.ItemId ?? 0; } catch (Exception ex) { DebugLog.Error("unreleased", ex, "UnreleasedCars.StartingCarId"); return 0; }
    }
}
