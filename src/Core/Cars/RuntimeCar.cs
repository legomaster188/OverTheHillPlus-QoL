using System;
using System.Collections.Generic;
using System.Reflection;
using OverTheHillPlus.Loader;
#if MELON
using Il2CppFunCore.Managers;
using Il2CppFusion;
#else
using FunCore.Managers;
using Fusion;
#endif
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
    public readonly ModLog Logger;
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
    /// <summary>The garage model's own layout: its wheels stand at another height than the template's (each stock garage
    /// model is its own prefab) and its drop comes from its tyre meshes, so it can't be worked out from <see cref="Layout"/>.</summary>
    public CarLayout GarageLayout { get; private set; }

    public string PrefabAddress { get; private set; }
    public string DummyAddress { get; private set; }

    private GameObject _holder;
    private NetworkObjectGuid _guid;
    private float _nextTry;
    private bool _loggedCacheClash;
    private readonly HashSet<string> _logged = new();

    private static readonly Il2CppStringArray NoTags = new(0);

    /// <summary>This car's beat within a period (0 to 1): the 25-odd cars' once-a-second checks each get a frame of their
    /// own, rather than all landing on the same one.</summary>
    public float Beat { get; }

    /// <summary>The next time this car does something it does every <paramref name="period"/> seconds.</summary>
    public float NextBeat(float period) => OverTheHillPlus.Core.Shared.Beats.Next(Time.unscaledTime, period, Beat);

    private static int _cars;

    public RuntimeCar(CarDefinition def, CarHooks hooks, ModLog log, Assembly assets, OverTheHillPlus.Config.ConfigFile cfg)
    {
        Beat = _cars++ % 32 / 32f;
        _nextTry = Beat;
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
        Liveries = new CarLiveryParts(this);
    }

    /// <summary>The car's bundled liveries as the game's livery parts (empty when the definition has none).</summary>
    public CarLiveryParts Liveries { get; }

    public string Id => Def.Id;

    /// <summary>
    /// Added to a model position to get car-root space on the car (<paramref name="garage"/> false) or on its garage model
    /// (the place of every part fitted by the model's coordinates, as <see cref="CarLayout.Offset"/> is for the slots). The
    /// two differ by more than the wheel drop: a stock garage model's wheels don't stand at its prefab's height, by up to
    /// 8 cm on the Polyana. Zero before the build.
    /// </summary>
    public Vector3 ModelOffset(bool garage)
    {
        if (garage && GarageLayout != null) return GarageLayout.Offset;
        var lay = Layout;
        if (lay == null) return Vector3.zero;
        var o = lay.Offset;
        if (garage) o.y -= lay.WheelDrop;
        return o;
    }

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
        bool second = Time.unscaledTime >= _nextTry;
        if (second)
        {
            _nextTry = NextBeat(1f);
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
        if (second) Save.Grant();
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

    // The build runs as steps over several frames (core's registry asks again each frame while it says Working, and
    // nothing after it registers in between): the assets it needs load in the background first, and the heavy main-
    // thread parts (garage data, each texture, the template, the garage model) get a frame each. All of it in one frame
    // froze the game at start (25 s for the first car, 3.7 s for the Hilo). When a session starts, core asks with
    // NetPrefabRegistry.Urgent set and every step finishes in that call.
    private StagedBuild _build;
    private int _buildIndex = -1;
    private ResourcesManager _rm;
    private NetworkPrefabTable _table;
    private NetworkPrefabId _donorId;
    private float _loadsSince;
    private UnityEngine.ResourceManagement.AsyncOperations.AsyncOperationHandle<CarInfo> _infoLoad;
    private UnityEngine.ResourceManagement.AsyncOperations.AsyncOperationHandle<GameObject> _dummyLoad;
    private bool _infoLoading, _dummyLoading;
    /// <summary>Seconds the background loads may take before the build goes on without them (it then loads what is
    /// still missing itself, at once).</summary>
    private const float LoadLimit = 60f;

    private static double ClockMs() => System.Diagnostics.Stopwatch.GetTimestamp() * 1000.0 / System.Diagnostics.Stopwatch.Frequency;

    private NetPrefabBuild Build(int index)
    {
        if (FailedReason != null) return NetPrefabBuild.Fail(FailedReason);
        string modFailed = ModManager.FailedReason(Id);
        if (modFailed != null) return NetPrefabBuild.Fail(modFailed); // the plugin's start failed after declaring
        if (_build == null || _buildIndex != index)
        {
            if (_build != null) Abandon(); // asked for another index: start over (the template carries its index)
            _build = NewBuild(index);
            _buildIndex = index;
        }
        BuildProgress p;
        try { p = _build.Advance(NetPrefabRegistry.Urgent, NetPrefabRegistry.BudgetLeft); }
        catch (Exception e)
        {
            Abandon();
            DebugLog.Error(Id, e, "build");
            Fail("could not build the car: " + e.Message);
            Error("build failed: " + e);
            return NetPrefabBuild.Fail(e.Message);
        }
        switch (p)
        {
            case BuildProgress.Working:
                return NetPrefabBuild.Working(_build.LastRan);
            case BuildProgress.Later:
                return NetPrefabBuild.Later(Waiting = _build.Text);
            case BuildProgress.Failed:
            {
                string why = _build.Text;
                Abandon();
                Fail(why);
                return NetPrefabBuild.Fail(why);
            }
        }
        CrashTrail.Mark(Id + ": built");
        string steps = _build.Summary();
        DebugLog.Info(Id, $"build steps over {_build.Frames} frame(s): {steps}");
        DebugLog.Verbose(Id, () => $"built: template {Template?.name}, garage model {Dummy?.name}, garage base {Catalog.BaseName}");
        _build = null;
        return NetPrefabBuild.Done(Template.GetComponent<NetworkObject>());
    }

    private StagedBuild NewBuild(int index) => new StagedBuild(ClockMs)
        .Step("asset system", _ => StepAssetSystem(index))
        .Step("start loads", _ => StepStartLoads())
        .Step("loads", StepLoads)
        .Step("garage data", _ => StepGarageData())
        .Step("albedo", u => Tex(u, "albedo_base", () => Assets.Albedo))
        .Step("ao", u => Tex(u, "ao", () => Assets.Ao))
        .Step("lights", u => Tex(u, "lights", () => Assets.Lights))
        .Step("mask", u => Tex(u, "mask", () => Assets.Mask))
        .Step("template", _ => StepTemplate(index))
        .Step("garage model", _ => StepDummy());

    /// <summary>Makes a texture in a frame of its own; one still being decoded in the background is asked for next frame.</summary>
    private StepResult Tex(bool urgent, string kind, Func<Texture2D> make)
    {
        if (!urgent && !Assets.TextureReady(kind)) return StepResult.Wait;
        make();
        return StepResult.Next;
    }

    private StepResult StepAssetSystem(int index)
    {
        CrashTrail.Mark(Id + ": asset system");
        ResourcesManager rm;
        try { rm = ResourcesManager.Instance; } catch (Exception ex) { DebugLog.Error("core", ex, "RuntimeCar.Build"); rm = null; }
        if (rm == null || !rm.AddressablesInitialized()) return StepResult.Later("the game's asset system");
        NetworkPrefabTable table;
        try { table = NetworkProjectConfig.Global?.PrefabTable; } catch (Exception ex) { DebugLog.Error("core", ex, "RuntimeCar.Build"); table = null; }
        if (table == null) return StepResult.Later("Fusion's project config");
        Waiting = null;
        _rm = rm;
        _table = table;
        _guid = new NetworkObjectGuid(Def.FusionGuid);
        PrefabAddress = GameConstants.ADDRESSABLES_PATHS.GetCarPrefabAddress(Def.CarName);
        DummyAddress = GameConstants.ADDRESSABLES_PATHS.GetCarDummyPrefabAddress(Def.CarName);
        DebugLog.Verbose(Id, () => $"build: prefab index {index}, donor {Config.Donor.Value}, address {PrefabAddress}");
        return StepResult.Next;
    }

    /// <summary>Starts the background loads of what the build needs from the game: the donor's network prefab (through
    /// Fusion's own async load), the garage base car's data (unless the item catalog has it) and its garage model.</summary>
    private StepResult StepStartLoads()
    {
        CrashTrail.Mark(Id + ": start loads");
        string donor = Config.Donor.Value;
        _donorId = _table.GetId(donor);
        if (!_donorId.IsValid) return StepResult.Fail($"the donor prefab {donor} is not in Fusion's table");
        _table.Load(_donorId, false);
        _loadsSince = Time.realtimeSinceStartup;
        string infoKey = Catalog.HasBaseInCatalog() ? null : CarCatalog.FirstKnownKey(CarCatalog.BaseKeys(Config.GarageBaseName));
        if (infoKey != null)
            try
            {
                _infoLoad = UnityEngine.AddressableAssets.Addressables.LoadAssetAsync<CarInfo>((Il2CppSystem.String)infoKey);
                _infoLoading = true;
            }
            catch (Exception ex) { DebugLog.Error(Id, ex, "RuntimeCar.StepStartLoads info"); _infoLoading = false; }
        string dummyKey = CarCatalog.FirstKnownKey(CarCatalog.DummyKeys(Config.GarageBaseName));
        if (dummyKey != null)
            try
            {
                _dummyLoad = UnityEngine.AddressableAssets.Addressables.LoadAssetAsync<GameObject>((Il2CppSystem.String)dummyKey);
                _dummyLoading = true;
            }
            catch (Exception ex) { DebugLog.Error(Id, ex, "RuntimeCar.StepStartLoads dummy"); _dummyLoading = false; }
        return StepResult.Next;
    }

    /// <summary>Waits for the background loads (urgent: finishes them now). The handles are kept: the game loads the
    /// same assets again by key, which then finds them loaded, as the build's old one-frame loads left them.</summary>
    private StepResult StepLoads(bool urgent)
    {
        bool donor;
        try { donor = _table.Load(_donorId, urgent) != null; } catch (Exception ex) { DebugLog.Error(Id, ex, "RuntimeCar.StepLoads donor"); donor = true; }
        if (_infoLoading) _infoLoading = !Finished(() => _infoLoad.IsDone, urgent, () => _infoLoad.WaitForCompletion());
        if (_dummyLoading) _dummyLoading = !Finished(() => _dummyLoad.IsDone, urgent, () => _dummyLoad.WaitForCompletion());
        bool info = !_infoLoading, dummy = !_dummyLoading;
        if (donor && info && dummy) return StepResult.Next;
        if (Time.realtimeSinceStartup - _loadsSince > LoadLimit)
        {
            Warn($"background loads still running after {LoadLimit:0} s (donor {(donor ? "in" : "loading")}, garage data {(info ? "in" : "loading")}, " +
                 $"garage model {(dummy ? "in" : "loading")}): the build loads the rest itself.");
            return StepResult.Next;
        }
        return StepResult.Wait;
    }

    /// <summary>The load is over (done, failed, or finished now because <paramref name="urgent"/>); a handle that can't
    /// be asked counts as over (the build then loads by itself what it lacks).</summary>
    private bool Finished(Func<bool> isDone, bool urgent, Action complete)
    {
        try
        {
            if (isDone()) return true;
            if (!urgent) return false;
            complete();
        }
        catch (Exception ex) { DebugLog.Error(Id, ex, "RuntimeCar.Finished"); }
        return true;
    }

    private StepResult StepGarageData()
    {
        CrashTrail.Mark(Id + ": garage data");
        string why = Catalog.Prepare(_rm, NoTags);
        if (why == CarCatalog.NotYet) return StepResult.Later("the game's car list");
        return why == null ? StepResult.Next : StepResult.Fail(why);
    }

    private StepResult StepTemplate(int index)
    {
        if (_holder == null)
        {
            _holder = new GameObject("OTH_" + Def.CarName + "Templates");
            _holder.SetActive(false); // nothing below it ever wakes up (no Awake, no OnEnable)
            _holder.hideFlags = HideFlags.HideAndDontSave;
            Object.DontDestroyOnLoad(_holder);
        }
        CrashTrail.Mark(Id + ": build template");
        string why = BuildTemplate(_table, index);
        return why == null ? StepResult.Next : StepResult.Fail(why);
    }

    private StepResult StepDummy()
    {
        CrashTrail.Mark(Id + ": build garage model");
        string why = BuildDummy(_rm);
        return why == null ? StepResult.Next : StepResult.Fail(why);
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
        DropUnplaceableParts(tpl);
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
        string why = Body.Apply(d, template: false, out var garageLayout);
        if (why != null) return "garage model: " + why;
        GarageLayout = garageLayout;
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

    /// <summary>
    /// Leaves out of the car's garage the cosmetics whose every model CarBody hid on the template (a slot without an
    /// anchor, the car's hidden slots and items): they would sell nothing visible (<see cref="CarPartObjects.Unplaceable"/>).
    /// Before HideDroppedParts, which then switches their objects off on the template and the garage model alike.
    /// </summary>
    private void DropUnplaceableParts(GameObject root)
    {
        try
        {
            var hiddenNodes = Body.HiddenNodes;
            if (hiddenNodes.Count == 0) return;
            var parts = new List<uint[]>();
            var hidden = new List<bool>();
            foreach (var c in root.GetComponentsInChildren<VehicleUpgradeObject>(true))
            {
                if (c == null) continue;
                uint id = 0;
                try { var p = c.VehiclePartReference; if (p != null) id = p.ItemId; }
                catch (Exception ex) { DebugLog.Error(Id, ex, "RuntimeCar.DropUnplaceableParts"); }
                bool under = false;
                for (var t = c.transform; t != null && t != root.transform && !under; t = t.parent)
                    under = hiddenNodes.Contains(t.Pointer);
                parts.Add(new[] { id });
                hidden.Add(under);
            }
            Catalog.DropUnplaceable(CarPartObjects.Unplaceable(parts, hidden));
        }
        catch (Exception e)
        {
            DebugLog.Error(Id, e, "RuntimeCar.DropUnplaceableParts");
            Warn("could not check which cosmetics have a place on the car: " + e.Message);
        }
    }

    /// <summary>The item ids of the car's own part list.</summary>
    public HashSet<uint> PartIds() => PartIds(Catalog.Info);

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
        GarageLayout = null;
        // the build's own background loads (nothing registered holds what they brought)
        try { if (_infoLoad.IsValid()) _infoLoad.Release(); } catch (Exception ex) { DebugLog.Error("core", ex, "RuntimeCar.Abandon"); }
        try { if (_dummyLoad.IsValid()) _dummyLoad.Release(); } catch (Exception ex) { DebugLog.Error("core", ex, "RuntimeCar.Abandon"); }
        _infoLoad = default;
        _dummyLoad = default;
        _infoLoading = _dummyLoading = false;
        _build = null;
        _buildIndex = -1;
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
    /// <summary>Like <see cref="AfterBody"/> (called right after it), with where the model went on the copy
    /// (<see cref="CarModelPlace"/>): what a vehicle with moving parts needs to put them on their own objects.</summary>
    public Func<GameObject, bool, CarModelPlace, string> AfterModel;
    /// <summary>After the networked template is complete (model, tuning, engine sound, CarInfo). Null or why not.</summary>
    public Func<GameObject, string> AfterTemplate;
    /// <summary>Why you can't switch into the vehicle where you are now (shown next to its "Drive" button), or null: a boat
    /// is taken out at the water's edge, not on a mountain road. Checked on top of the framework's own rules.</summary>
    public Func<string> DriveBlocker;
}

/// <summary>Where a car's model went on a copy of its donor (the networked template or the garage model), for
/// <see cref="CarHooks.AfterModel"/>.</summary>
public sealed class CarModelPlace
{
    /// <summary>Added to every model position to get car-root space (the layout offset; on the garage model it includes
    /// the drop onto the floor).</summary>
    public Vector3 Offset;
    /// <summary>The model (moving parts, anchors).</summary>
    public MeshBlob Blob;
    /// <summary>The car's material (the donor's car shader with the car's textures): moving parts drawn with it take the
    /// livery, paint, dirt, snow and lights like the body, as long as their renderers exist before the copy wakes.</summary>
    public Material Material;
    /// <summary>The model's tyre radius and the donor's, metres.</summary>
    public float TyreRadius, DonorTyreRadius;
    /// <summary>The networked template (true) or the garage model (false).</summary>
    public bool Template;

    /// <summary>A model point in car-root space.</summary>
    public Vector3 ToRoot(MeshVec3 p) => new Vector3(p.X, p.Y, p.Z) + Offset;
}
