using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Logging;
using FunCore.Managers;
using Fusion;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using OverTheHillPlus.Core.Shared;
using OverTheHillPlus.Shared;
using UnityEngine;
using Object = UnityEngine.Object;

namespace OverTheHillPlus.Cars;

// After the namespace line on purpose: the game has its own global ModManager (mod.io).
using OverTheHillPlus.Core.Manager;

/// <summary>
/// One modded car at runtime. Brings it into the game, once per process, in an order that never leaves half a car
/// behind:
/// <list type="number">
/// <item>declare the car's network prefab to core's registry (<see cref="NetPrefabRegistry"/>), which registers every
///   mod's runtime prefabs in one fixed order on every peer, outside sessions, and gates joins on them;</item>
/// <item>when the registry asks (<see cref="Build"/>, once the game's asset system is up): make the CarInfo (not
///   registered yet), the networked template (an inactive copy of the donor with the car's identity, model and
///   tuning) and the garage dummy. A failure here leaves nothing registered;</item>
/// <item>registered (<see cref="OnRegistered"/>): Addressables serves both objects under the game's addresses
///   (<see cref="RuntimeAddressables"/>), and the mod tells core it can serve sessions (ModManager.SetServerModReady);</item>
/// <item>as soon as the game's item catalog exists: the CarInfo goes into it (garage, SetupCar).</item>
/// </list>
/// Core's registry wakes every copy Fusion makes of the template the moment it is instantiated (one wake for all runtime
/// templates), so the car has no wake of its own.
/// </summary>
internal sealed class RuntimeCar
{
    public readonly CarDefinition Def;
    public readonly CarHooks Hooks;
    public readonly ManualLogSource Logger;
    public readonly Assembly AssetAssembly;
    /// <summary>"[Vera]": the start of every log line.</summary>
    public readonly string Tag;

    public CarConfig Config { get; }
    public CarAssets Assets { get; }
    public CarCatalog Catalog { get; }
    public CarBody Body { get; }
    public CarText Text { get; }
    public CarSave Save { get; }
    public CarDrive Drive { get; }
    public CarTuning Tuning { get; }
    /// <summary>This player's look for the car (and what others are told).</summary>
    public CarLookState Look { get; }

    /// <summary>The network part is in: template, garage model and prefab entry.</summary>
    public bool NetReady { get; private set; }
    /// <summary>Everything is registered: the car can be spawned, selected and driven.</summary>
    public bool Ready => NetReady && Catalog.Registered;
    /// <summary>Why the car is off this session (null while it is fine or still waiting).</summary>
    public string FailedReason { get; private set; }
    /// <summary>What it is waiting for (shown on the MODS page), null when not waiting.</summary>
    public string Waiting { get; private set; } = "the game to finish loading";

    /// <summary>The car's prefab index (-1 until registered).</summary>
    public int PrefabIndex { get; private set; } = -1;
    public GameObject Template { get; private set; }
    public GameObject Dummy { get; private set; }
    public CarLayout Layout { get; private set; }

    public string PrefabAddress { get; private set; }
    public string DummyAddress { get; private set; }

    private GameObject _holder;
    private NetworkObjectGuid _guid;
    private float _nextTry;
    private bool _loggedCacheClash;
    private readonly HashSet<string> _logged = new();

    private static readonly Il2CppStringArray NoTags = new(0);

    public RuntimeCar(CarDefinition def, CarHooks hooks, ManualLogSource log, Assembly assets, BepInEx.Configuration.ConfigFile cfg)
    {
        Def = def;
        Hooks = hooks ?? new CarHooks();
        Logger = log ?? Core.CorePlugin.Logger;
        AssetAssembly = assets;
        Tag = "[" + def.CarName + "]";
        Config = new CarConfig(this, cfg);
        Assets = new CarAssets(this);
        Catalog = new CarCatalog(this);
        Body = new CarBody(this);
        Text = new CarText(this);
        Save = new CarSave(this);
        Drive = new CarDrive(this);
        Tuning = new CarTuning(this);
        Look = new CarLookState(this);
    }

    public string Id => Def.Id;

    // ---------------------------------------------------------------- logging

    // Each line also goes to the debug log (BepInEx/oth-debug) under the car's id: info at [Debug] Level = Info,
    // errors and LogOnce warnings at Errors, deduplicated there with repeat counts.
    public void Info(string text)
    {
        Logger?.LogInfo(Tag + " " + text);
        DebugLog.Info(Id, text);
    }

    public void Warn(string text)
    {
        Logger?.LogWarning(Tag + " " + text);
        DebugLog.Info(Id, "warning: " + text);
    }

    public void Error(string text)
    {
        DebugLog.Error(Id, text); // first: the debug log's listener then knows the BepInEx line below is this one
        Logger?.LogError(Tag + " " + text);
    }

    /// <summary>Logs a warning the first time <paramref name="key"/> is seen (later ones are suppressed here; the debug
    /// log counts every one).</summary>
    public void LogOnce(string key, string text)
    {
        DebugLog.Error(Id, text);
        if (_logged.Count < 256 && _logged.Add(key)) Logger?.LogWarning(Tag + " " + text + " (further ones suppressed)");
    }

    // ---------------------------------------------------------------- declare (plugin Load)

    /// <summary>Declares the car's prefab entry. Null on success, otherwise why not.</summary>
    public string Declare()
    {
        ModManager.SetServerModReady(Id, false); // not until the prefab entry is in
        return NetPrefabRegistry.Declare(new NetPrefabEntry
        {
            Key = Def.RegistryKey,
            Guid = Def.FusionGuid,
            AssetPath = Def.AssetPath,
            Layout = Config.Layout,
            Build = Build,
            Registered = OnRegistered,
            Lost = why => Fail("its network prefab was lost: " + why),
            // Core gave the entry up (guid taken, Fusion refused it, ...): off, and the unused template goes.
            Failed = why =>
            {
                Fail("its network prefab was not registered: " + why);
                Abandon();
            },
        });
    }

    // ---------------------------------------------------------------- per frame

    public void Tick()
    {
        if (FailedReason != null || !NetReady) return;
        if (Time.unscaledTime >= _nextTry)
        {
            _nextTry = Time.unscaledTime + 1f;
            if (!Catalog.Registered)
            {
                TryRegister();
                return;
            }
            // Once a second: the game rebuilds its catalog and clears its address cache on scene changes, and the spawn
            // and garage hooks make sure of the cache again right before they need it.
            Catalog.EnsureRegistered();
            EnsureCache();
        }
        if (!Ready) return;
        Save.TryRestore();
        Save.Grant();
        Text.Tick();
        Drive.Tick();
        Tuning.Tick();
    }

    /// <summary>Before every StartGame (after core's registry check): the catalog entry, if the catalog is there now.</summary>
    public void BeforeStartGame()
    {
        if (FailedReason != null || !NetReady) return;
        if (!Catalog.Registered) TryRegister();
        EnsureCache();
    }

    // ---------------------------------------------------------------- build (asked by core's registry)

    private NetPrefabBuild Build(int index)
    {
        if (FailedReason != null) return NetPrefabBuild.Fail(FailedReason);
        string modFailed = ModManager.FailedReason(Id);
        if (modFailed != null) return NetPrefabBuild.Fail(modFailed); // the plugin's start failed after declaring
        ResourcesManager rm;
        try { rm = ResourcesManager.Instance; } catch (Exception ex) { DebugLog.Error("core", ex, "RuntimeCar.Build"); rm = null; }
        if (rm == null || !rm.AddressablesInitialized()) return NetPrefabBuild.Later(Waiting = "the game's asset system");
        NetworkPrefabTable table;
        try { table = NetworkProjectConfig.Global?.PrefabTable; } catch (Exception ex) { DebugLog.Error("core", ex, "RuntimeCar.Build"); table = null; }
        if (table == null) return NetPrefabBuild.Later(Waiting = "Fusion's project config");
        Waiting = null;
        try
        {
            _guid = new NetworkObjectGuid(Def.FusionGuid);
            PrefabAddress = GameConstants.ADDRESSABLES_PATHS.GetCarPrefabAddress(Def.CarName);
            DummyAddress = GameConstants.ADDRESSABLES_PATHS.GetCarDummyPrefabAddress(Def.CarName);

            CrashTrail.Mark(Id + ": garage data");
            DebugLog.Verbose(Id, () => $"build: prefab index {index}, donor {Config.Donor.Value}, address {PrefabAddress}");
            string why = Catalog.Prepare(rm, NoTags);
            if (why == CarCatalog.NotYet) return NetPrefabBuild.Later(Waiting = "the game's car list");
            if (why == null)
            {
                _holder = new GameObject("OTH_" + Def.CarName + "Templates");
                _holder.SetActive(false); // nothing below it ever wakes up (no Awake, no OnEnable)
                _holder.hideFlags = HideFlags.HideAndDontSave;
                Object.DontDestroyOnLoad(_holder);
                CrashTrail.Mark(Id + ": build template");
                why = BuildTemplate(table, index);
                CrashTrail.Mark(Id + ": build garage model");
                if (why == null) why = BuildDummy(rm);
            }
            if (why != null)
            {
                Abandon();
                Fail(why);
                return NetPrefabBuild.Fail(why);
            }
            CrashTrail.Mark(Id + ": built");
            DebugLog.Verbose(Id, () => $"built: template {Template?.name}, garage model {Dummy?.name}, garage base {Catalog.BaseName}");
            return NetPrefabBuild.Done(Template.GetComponent<NetworkObject>());
        }
        catch (Exception e)
        {
            Abandon();
            DebugLog.Error(Id, e, "build");
            Fail("could not build the car: " + e.Message);
            Error("build failed: " + e);
            return NetPrefabBuild.Fail(e.Message);
        }
    }

    private void OnRegistered(int index)
    {
        PrefabIndex = index;
        NetReady = true;
        // The game loads both by address through Addressables (never from its own cache): serve them there.
        CrashTrail.Mark(Id + ": serve addresses");
        DebugLog.Verbose(Id, () => $"registered at prefab index {index}: serving {PrefabAddress} and {DummyAddress}");
        string why = RuntimeAddressables.Serve(PrefabAddress, Template) ?? RuntimeAddressables.Serve(DummyAddress, Dummy);
        if (why != null)
        {
            Fail("the game can't load the car by its address: " + why);
            return;
        }
        Info($"Addressables serve {PrefabAddress} and {DummyAddress}.");
        EnsureCache();
        ModManager.SetServerModReady(Id, true);
        Info($"network part ready: prefab {Def.PrefabName} at index {index}, item 0x{Def.ItemId:X8}, " +
             $"donor {Config.Donor.Value}, garage base {Catalog.BaseName}, wheelbase {Layout.FrontZ - Layout.RearZ:0.00} m.");
        TryRegister();
    }

    /// <summary>Registers the CarInfo once the game's item catalog exists.</summary>
    private void TryRegister()
    {
        if (!NetReady || Catalog.Registered || FailedReason != null) return;
        if (!CarCatalog.CatalogLoaded) { Waiting = "the item catalog"; return; }
        try
        {
            string why = Catalog.Register();
            if (why != null) { Fail(why); return; }
            Waiting = null;
            EnsureCache();
            Info("ready.");
        }
        catch (Exception e) { DebugLog.Error("core", e, "RuntimeCar.TryRegister"); Fail("could not add the car to the item catalog: " + e.Message); }
    }

    private string BuildTemplate(NetworkPrefabTable table, int index)
    {
        string donor = Config.Donor.Value;
        var donorId = table.GetId(donor);
        if (!donorId.IsValid) return $"the donor prefab {donor} is not in Fusion's table";
        var donorNo = table.Load(donorId, true);
        if (donorNo == null) return $"the donor prefab {donor} could not be loaded";
        if (donorNo.GetComponent<VehicleCustomizationController>() == null) return $"{donor} is not a car";

        var tpl = Object.Instantiate(donorNo.gameObject, _holder.transform);
        tpl.SetActive(false); // Fusion's runtime-prefab pattern: an inactive template, activated per instance
        tpl.name = Def.PrefabName;
        Template = tpl;

        // identity: our guid and prefab id (never the donor's)
        var pd = tpl.GetComponent<NetworkObjectPrefabData>();
        if (pd == null) return "the donor has no NetworkObjectPrefabData";
        pd.Guid = _guid;
        var no = tpl.GetComponent<NetworkObject>();
        if (no == null) return "the donor has no NetworkObject";
        no.NetworkTypeId = NetworkObjectTypeId.FromPrefabId(NetworkPrefabId.FromIndex(index));

        CrashTrail.Mark(Id + ": skin template");
        string why = Body.Apply(tpl, template: true, out var layout);
        if (why != null) return why;
        Layout = layout;
        CrashTrail.Mark(Id + ": tune template");
        Tuning.BakeTemplate(tpl, layout);
        // Engine sound: the default stock one (each player's choice is applied per car at runtime, CarLooks).
        var audio = tpl.GetComponentInChildren<CarAudioManager>(true);
        if (audio != null && Enum.TryParse<CarAudioManager.VehicleType>(Def.EngineLabel(Def.DefaultEngine), out var vt))
            audio.vehicleType = vt;
        Catalog.FitToLayout(layout);
        // The car's own part list: the networked car registers its upgrade objects from it and indexes its mounted-parts
        // bitset by it (as Car.GetMountedUpgradesBitSet does with the garage's CarInfo, the same object).
        var vcc = tpl.GetComponent<VehicleCustomizationController>();
        var donorInfo = vcc.currentCarInfo;
        vcc.currentCarInfo = Catalog.Info;
        CrashTrail.Mark(Id + ": template parts");
        HideDroppedParts(tpl, donorInfo, "template");
        if (Hooks.AfterTemplate != null)
        {
            CrashTrail.Mark(Id + ": template hook");
            why = Hooks.AfterTemplate(tpl);
            if (why != null) return why;
        }
        return null;
    }

    private string BuildDummy(ResourcesManager rm)
    {
        string baseName = Catalog.BaseName;
        var src = CarCatalog.LoadByKeys<GameObject>(rm, NoTags, CarCatalog.DummyKeys(baseName), out string tried);
        if (src == null) return $"the garage model of {baseName} could not be loaded (tried {tried})";
        var d = Object.Instantiate(src, _holder.transform);
        d.name = Def.PrefabName + "_Dummy";
        string why = Body.Apply(d, template: false, out _);
        if (why != null) return "garage model: " + why;
        // The garage model gets the car's CarInfo too (it kept the base car's): InitParts then registers the car's parts,
        // and the garage's mount check (VehicleCustomizationControllerHelpers.MountDismountPart) uses the car's list, the
        // same list its bitset and the networked car use. Set here, before any copy wakes (the holder is inactive).
        CarInfo donorInfo = null;
        int controllers = 0;
        foreach (var lvcc in d.GetComponentsInChildren<LocalVehicleCustomizationController>(true))
        {
            if (lvcc == null) continue;
            donorInfo ??= lvcc.currentCarInfo;
            lvcc.currentCarInfo = Catalog.Info;
            controllers++;
        }
        if (controllers == 0) Warn("the garage model has no LocalVehicleCustomizationController: its cosmetics can't be switched.");
        CrashTrail.Mark(Id + ": garage model parts");
        HideDroppedParts(d, donorInfo ?? Catalog.Base, "garage model");
        Dummy = d;
        return null;
    }

    /// <summary>
    /// Switches off the upgrade objects of the parts the car's list leaves out (<see cref="CarPartObjects"/>): the game
    /// never registers them, so they would stay on whatever is mounted ("stuck" bull bars, roof loads, ...).
    /// </summary>
    private void HideDroppedParts(GameObject root, CarInfo donorInfo, string what)
    {
        try
        {
            var donorIds = PartIds(donorInfo ?? Catalog.Base);
            var carIds = PartIds(Catalog.Info);
            if (donorIds.Count == 0 || carIds.Count == 0)
            {
                Warn($"{what}: no part list to check the upgrade objects against (base {donorIds.Count}, car {carIds.Count}).");
                return;
            }
            var objects = new List<GameObject>();
            var parts = new List<List<uint>>();
            var index = new Dictionary<IntPtr, int>();
            foreach (var c in root.GetComponentsInChildren<VehicleUpgradeObject>(true))
            {
                if (c == null) continue;
                var go = c.gameObject;
                uint id = 0;
                try
                {
                    var p = c.VehiclePartReference;
                    if (p != null) id = p.ItemId;
                }
                catch (Exception ex) { DebugLog.Error(Id, ex, "RuntimeCar.HideDroppedParts"); }
                if (!index.TryGetValue(go.Pointer, out int k))
                {
                    k = objects.Count;
                    index[go.Pointer] = k;
                    objects.Add(go);
                    parts.Add(new List<uint>());
                }
                parts[k].Add(id);
            }
            var plan = new List<uint[]>(parts.Count);
            foreach (var l in parts) plan.Add(l.ToArray());
            var hide = CarPartObjects.ToHide(plan, donorIds, carIds);
            var names = new List<string>();
            foreach (int i in hide)
            {
                if (objects[i].activeSelf) names.Add(objects[i].name);
                objects[i].SetActive(false);
            }
            Info($"{what}: {objects.Count} upgrade object(s), {hide.Count} of parts the {Def.CarName} leaves out switched off" +
                 (names.Count > 0 ? $" ({names.Count} were on: {string.Join(", ", names)})" : "") + ".");
        }
        catch (Exception e)
        {
            DebugLog.Error(Id, e, "RuntimeCar.HideDroppedParts");
            Warn($"{what}: could not check the upgrade objects: {e.Message}");
        }
    }

    private static HashSet<uint> PartIds(CarInfo info)
    {
        var ids = new HashSet<uint>();
        var list = info?.UpgradesLists?.Parts;
        if (list == null) return ids;
        for (int i = 0; i < list.Count; i++)
        {
            var p = list[i];
            if (p != null) ids.Add(p.ItemId);
        }
        return ids;
    }

    /// <summary>Drops what was built (only before the prefab entry exists).</summary>
    private void Abandon()
    {
        try { if (_holder != null) Object.Destroy(_holder); } catch (Exception ex) { DebugLog.Error("core", ex, "RuntimeCar.Abandon"); }
        _holder = null;
        Template = Dummy = null;
    }

    /// <summary>Switches the car off for the session: out of the catalog and the session advert, and the car that the
    /// save sidecar or the free grant put into the inventory is taken out again (the sidecar file is kept).</summary>
    public void Fail(string why)
    {
        if (FailedReason != null) return;
        bool registered = NetReady;
        FailedReason = why;
        NetReady = false;
        Waiting = null;
        ModManager.SetServerModReady(Id, false);
        Catalog.Unregister(); // nothing may ask for the car any more
        Save.Disable();
        ModManager.MarkFailed(Id, why);
        Error($"{why}. The car is off this session." +
              (registered ? " (Its prefab entry stays in Fusion's table but no car is spawned from it.)" : ""));
    }

    // ---------------------------------------------------------------- address cache

    /// <summary>
    /// Keeps the template and the dummy loadable by address. The game's loaders (SetupCar, the garage's
    /// RefreshDummyCar, lobby and menu dummies) call ResourcesManager.LoadAndCacheAddressablesSync, which goes straight
    /// to Addressables: <see cref="RuntimeAddressables"/> serves both there (and puts back what a catalog reload or
    /// cache clear dropped). They are also kept in ResourcesManager's own cache, which only ResourcesManager.Load reads
    /// (the game empties it on scene changes). Idempotent and cheap.
    /// </summary>
    public void EnsureCache()
    {
        if (Template == null || Dummy == null) return;
        string why = RuntimeAddressables.Ensure();
        if (why != null) LogOnce("addressables", "serving the car's addresses: " + why);
        try
        {
            var rm = ResourcesManager.Instance;
            if (rm == null) return;
            Ensure(rm, PrefabAddress, Template);
            Ensure(rm, DummyAddress, Dummy);
        }
        catch (Exception e) { DebugLog.Error("core", e, "RuntimeCar.EnsureCache"); LogOnce("cache", "address cache: " + e.Message); }
    }

    private void Ensure(ResourcesManager rm, string address, GameObject go)
    {
        var cache = rm.cachedObjs?.primary;
        if (cache != null && cache.TryGetValue(address, out var have) && have != null)
        {
            if (have.Pointer == go.Pointer) return;
            if (!_loggedCacheClash)
            {
                _loggedCacheClash = true;
                Warn($"the game's cache already holds another object for {address}: replacing it.");
            }
            rm.cachedObjs.Remove(address);
        }
        rm.AddAddressableToCache(address, go, NoTags);
    }

    public bool IsOurAddress(string address) =>
        address != null && (address == PrefabAddress || address == DummyAddress);
}

/// <summary>Optional per-car steps on top of what the framework does for every car (main thread, while building).</summary>
public sealed class CarHooks
{
    /// <summary>After the car's model went onto a copy of the donor (<c>template</c>: the networked template, otherwise
    /// the garage model). Return null, or why the car can't be used (it then stays off).</summary>
    public Func<GameObject, bool, string> AfterBody;
    /// <summary>After the networked template is complete (model, tuning, engine sound, CarInfo). Null or why not.</summary>
    public Func<GameObject, string> AfterTemplate;
}
