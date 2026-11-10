using System;
using System.Collections.Generic;
using UnityEngine;
#if MELON
using Il2CppVehiclePhysics;
#else
using VehiclePhysics;
#endif

namespace OverTheHillPlus.Cars;

/// <summary>
/// The start-up proof that every modded car is whole, so a game start says what used to need someone driving each car.
/// Once all the network prefabs are registered it reads each car's networked template and garage model, one object at a
/// time, and writes a <c>[CarCheck]</c> line per car and a summary to the log: wheels and each wheel's collider radius
/// against the tyre drawn on it, tyres and rims, cosmetic slots, renderers with no material, body LODs and LOD groups,
/// lamps, colliders, and every garage part's models against the body (<see cref="CarStructure"/> judges).
/// <para>It only reads objects that already exist: no spawn, no scene change, no save, no screenshot, no input, nothing is
/// set on a car. Once per session, about a fifth of a second between objects, wrapped so that nothing it finds or trips
/// over can reach the game. Off by default: [Debug] CarStructureCheck in oth.core.cfg switches it on (it costs about 0.4 s of
/// the main thread per car, so it's for checking the cars, not for playing). Never reads LOD structs (GetLODs):
/// they don't cross into IL2CPP safely.</para>
/// </summary>
internal static class CarStructureCheck
{
    private const string Tag = "[CarCheck] ";
    private const float Every = 0.2f;
    private static readonly string[] Corners = { "FL", "FR", "RL", "RR" };
    private static readonly string[] Lamps =
    {
        "LeftHeadlight", "RightHeadlight", "LeftPositionBrakelight", "RightPositionBrakelight",
        "LeftBrakingBrakelight", "RightBrakingBrakelight", "LeftReverselight", "RightReverselight",
    };

    private static bool _done, _started;
    private static float _next;
    private static int _car;
    private static bool _garageTurn;
    private static CarStructureResult _template;
    private static int _checked;
    private static readonly List<string> Problems = new();
    private static readonly List<string> NotBuilt = new();

    /// <summary>Called every frame by the cars' shared tick; a flag test once it has run.</summary>
    public static void Tick()
    {
        if (_done) return;
        float now = Time.unscaledTime;
        if (now < _next) return;
        _next = now + Every;
        try
        {
            if (!Logging.DebugConfig.Get(Logging.DebugConfig.CarStructureCheck, false)) { _done = true; return; }
            if (!_started)
            {
                if (!AllBuilt()) return;
                _started = true;
            }
            Step();
        }
        catch (Exception e)
        {
            _done = true;
            DebugLog.Error("core", e, "CarStructureCheck.Tick");
            Core.CorePlugin.Logger?.LogWarning(Tag + "stopped: " + e.Message);
        }
    }

    private static bool AllBuilt()
    {
        if (Core.Shared.NetPrefabRegistry.AnyPending) return false;
        foreach (var c in ModdedCars.All)
            if (!c.NetReady && c.FailedReason == null) return false;
        return true;
    }

    /// <summary>One object: the car's template, then its garage model, then the next car.</summary>
    private static void Step()
    {
        var cars = ModdedCars.All;
        if (_car >= cars.Count)
        {
            _done = true;
            string text = Tag + CarStructure.Summary(_checked, Problems, NotBuilt);
            if (Problems.Count > 0 || NotBuilt.Count > 0) Core.CorePlugin.Logger?.LogWarning(text);
            else Core.CorePlugin.Logger?.LogInfo(text);
            DebugLog.Info("core", text);
            return;
        }
        var car = cars[_car];
        if (car.FailedReason != null || car.Template == null || car.Dummy == null)
        {
            NotBuilt.Add(car.Def.CarName);
            _car++;
            return;
        }
        CrashTrail.Mark("car check: " + car.Id);
        if (!_garageTurn)
        {
            _template = One(car, template: true);
            _garageTurn = true;
            return;
        }
        var garage = One(car, template: false);
        string line = CarStructure.Line(car.Def.CarName, _template, garage);
        _checked++;
        if (_template.Ok && garage.Ok)
        {
            Core.CorePlugin.Logger?.LogInfo(Tag + line);
            DebugLog.Info("core", Tag + line);
        }
        else
        {
            Problems.Add(car.Def.CarName);
            Core.CorePlugin.Logger?.LogWarning(Tag + line);
            DebugLog.Error("core", Tag + line);
        }
        _template = null;
        _garageTurn = false;
        _car++;
    }

    /// <summary>One object's verdict; a read that fails is a problem of that object, never the end of the check.</summary>
    private static CarStructureResult One(RuntimeCar car, bool template)
    {
        try { return CarStructure.Judge(Read(car, template)); }
        catch (Exception e)
        {
            DebugLog.Error("core", e, "CarStructureCheck.Read");
            var r = new CarStructureResult();
            r.Problems.Add("could not be read: " + e.Message);
            return r;
        }
    }

    private static CarStructureFacts Read(RuntimeCar car, bool template)
    {
        var root = template ? car.Template : car.Dummy;
        var lay = template ? car.Layout : car.GarageLayout;
        if (lay == null) throw new InvalidOperationException("no layout");
        var rt = root.transform;
        var toRoot = rt.worldToLocalMatrix;
        var f = new CarStructureFacts
        {
            Template = template,
            Kind = car.Def.Kind,
            BodyMin = M(lay.Body.min),
            BodyMax = M(lay.Body.max),
            Ground = lay.Offset.y,
        };
        ReadWheels(rt, toRoot, template, f);
        ReadRenderers(root, f);
        var parts = rt.Find("Parts");
        Transform cosmetics = null;
        if (parts != null)
            for (int i = 0; i < parts.childCount; i++)
            {
                var c = parts.GetChild(i);
                if (c.name.EndsWith("_CosmeticParts", StringComparison.Ordinal)) cosmetics = c;
                else ReadBody(c, f);
            }
        f.LodGroups = root.GetComponentsInChildren<LODGroup>(true).Length;
        if (cosmetics != null) ReadItems(car, cosmetics, toRoot, f);
        if (template)
        {
            ReadLamps(rt, f);
            ReadColliders(rt, car.Def, f);
        }
        return f;
    }

    private static void ReadWheels(Transform rt, Matrix4x4 toRoot, bool template, CarStructureFacts f)
    {
        var meshes = rt.Find("Axles/WheelMeshes");
        var colliders = rt.Find("Axles/WheelColliders");
        if (meshes == null) return;
        foreach (string corner in Corners)
        {
            var node = meshes.Find(corner);
            if (node == null) continue;
            var w = new CarWheelFacts { Corner = corner };
            Drawn(node, toRoot, w);
            // a tandem's axles are drawn by followers of this wheel: its own meshes are empty
            for (int i = 0; i < meshes.childCount; i++)
            {
                var c = meshes.GetChild(i);
                if (CarTandem.TryParse(c.name, out _, out string path) && path.StartsWith(corner + "/", StringComparison.Ordinal)) Drawn(c, toRoot, w);
            }
            if (template && colliders != null)
            {
                var wc = colliders.Find("Wheel" + corner);
                if (wc != null)
                {
                    var col = wc.GetComponentInChildren<VPFunselektor3DRaycastWheelCollider>(true);
                    if (col != null) w.ColliderRadius = col.radius;
                    var stop = wc.Find("WheelHub/BumpStopCollider");
                    var mc = stop != null ? stop.GetComponent<MeshCollider>() : null;
                    w.BumpStop = mc != null && mc.sharedMesh != null;
                }
            }
            f.Wheels.Add(w);
        }
    }

    /// <summary>The tyre and rim drawn below <paramref name="node"/>: the largest tyre's radius and lowest point.</summary>
    private static void Drawn(Transform node, Matrix4x4 toRoot, CarWheelFacts w)
    {
        foreach (var mf in node.GetComponentsInChildren<MeshFilter>(true))
        {
            if (mf == null || mf.sharedMesh == null) continue;
            string name = mf.name;
            if (name.EndsWith(CarBody.TraxStamp, StringComparison.Ordinal)) continue;
            if (CarBody.IsTyre(name))
            {
                Box(toRoot * mf.transform.localToWorldMatrix, mf.sharedMesh.bounds, out var lo, out var hi);
                float r = Math.Max(hi.y - lo.y, hi.z - lo.z) * 0.5f;
                if (r > w.TyreRadius)
                {
                    w.TyreRadius = r;
                    w.Bottom = lo.y;
                }
            }
            else if (name.IndexOf("wheel", StringComparison.OrdinalIgnoreCase) >= 0) w.Rim = true;
        }
    }

    private static void ReadRenderers(GameObject root, CarStructureFacts f)
    {
        foreach (var mf in root.GetComponentsInChildren<MeshFilter>(true))
        {
            if (mf == null || mf.sharedMesh == null) continue;
            var r = mf.GetComponent<MeshRenderer>();
            if (r != null) Materials(r, mf.name, f);
        }
        foreach (var s in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            if (s != null && s.sharedMesh != null) Materials(s, s.name, f);
    }

    private static void Materials(Renderer r, string name, CarStructureFacts f)
    {
        // every stock car has a frame indicator with no material; it is never drawn
        if (!r.enabled || name.IndexOf("FrameIndicator", StringComparison.Ordinal) >= 0) return;
        f.Renderers++;
        var mats = r.sharedMaterials;
        bool none = mats == null || mats.Length == 0;
        if (!none)
            for (int i = 0; i < mats.Length; i++)
                if (mats[i] == null) none = true;
        if (none) f.NoMaterial.Add(name);
    }

    private static void ReadBody(Transform node, CarStructureFacts f)
    {
        var lods = new HashSet<int>();
        foreach (var mf in node.GetComponentsInChildren<MeshFilter>(true))
        {
            if (mf == null || CarBody.BodyLodAt(mf.name) < 0) continue;
            if (mf.sharedMesh == null) f.BodyNoMesh++;
            else lods.Add(CarBody.BodyLod(mf.name));
        }
        if (lods.Count == 0) return;
        f.BodyLods += lods.Count;
        var group = node.GetComponent<LODGroup>();
        if (group == null) group = node.GetComponentInChildren<LODGroup>(true);
        if (group == null) return;
        f.LodGroupCount = group.lodCount;
        f.LodGroupOn = group.enabled;
    }

    /// <summary>The slots switched on, and the parts on the car's list with models in them (their boxes in car-root space).</summary>
    private static void ReadItems(RuntimeCar car, Transform cosmetics, Matrix4x4 toRoot, CarStructureFacts f)
    {
        var offered = car.PartIds();
        var filled = new HashSet<IntPtr>();
        for (int g = 0; g < cosmetics.childCount; g++)
        {
            var group = cosmetics.GetChild(g);
            for (int k = 0; k < group.childCount; k++)
                if (group.GetChild(k).gameObject.activeSelf) f.SlotsShown++;
        }
        var hiddenItems = car.Def.HiddenItems ?? Array.Empty<string>();
        foreach (var c in cosmetics.GetComponentsInChildren<VehicleUpgradeObject>(true))
        {
            if (c == null) continue;
            var p = c.VehiclePartReference;
            if (p == null || !offered.Contains(p.ItemId)) continue;
            string path = PathBelow(cosmetics, c.transform, out var slot);
            if (slot == null || !slot.gameObject.activeSelf) continue;
            bool hidden = false;
            foreach (var h in hiddenItems) if (CarCosmetics.IsUnder(path, h)) hidden = true;
            if (hidden) continue;
            filled.Add(slot.Pointer);
            var item = new CarItemFacts { Path = path };
            if (ItemBox(c.transform, toRoot, item)) f.Items.Add(item);
            else if (c.GetComponentInChildren<Renderer>(true) != null) f.ItemsNoMesh++;
        }
        f.SlotsFilled = filled.Count;
    }

    /// <summary>The path below the cosmetic root, and the slot on it (the node under its group; null for a node higher up).</summary>
    private static string PathBelow(Transform cosmetics, Transform t, out Transform slot)
    {
        var nodes = new List<Transform>();
        for (var c = t; c != null && c != cosmetics; c = c.parent) nodes.Add(c);
        nodes.Reverse();
        slot = nodes.Count >= 2 ? nodes[1] : null;
        var names = new List<string>(nodes.Count);
        foreach (var n in nodes) names.Add(n.name);
        return string.Join("/", names);
    }

    private static bool ItemBox(Transform item, Matrix4x4 toRoot, CarItemFacts o)
    {
        bool any = false;
        var lo = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
        var hi = new Vector3(float.MinValue, float.MinValue, float.MinValue);
        void Add(Transform t, Mesh m)
        {
            Box(toRoot * t.localToWorldMatrix, m.bounds, out var a, out var b);
            lo = Vector3.Min(lo, a);
            hi = Vector3.Max(hi, b);
            any = true;
        }
        foreach (var mf in item.GetComponentsInChildren<MeshFilter>(true))
            if (mf != null && mf.sharedMesh != null) Add(mf.transform, mf.sharedMesh);
        foreach (var s in item.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            if (s != null && s.sharedMesh != null) Add(s.transform, s.sharedMesh);
        if (!any) return false;
        o.Min = M(lo);
        o.Max = M(hi);
        return true;
    }

    private static void ReadLamps(Transform rt, CarStructureFacts f)
    {
        var lights = rt.Find("Lights");
        f.LightsWanted = Lamps.Length;
        foreach (string name in Lamps)
        {
            var t = lights != null ? lights.Find(name) : null;
            var l = t != null ? t.GetComponentInChildren<Light>(true) : null;
            if (l == null || !l.enabled) { f.LightsMissing.Add(name); continue; }
            var p = M(rt.InverseTransformPoint(t.position));
            if (CarStructure.Gap(p, p, f.BodyMin, f.BodyMax) > CarStructure.FarFromBody) { f.LightsMissing.Add(name + " off the body"); continue; }
            f.Lights++;
        }
    }

    private static void ReadColliders(Transform rt, CarDefinition def, CarStructureFacts f)
    {
        f.BodyBoxesWanted = def.BodyBoxes?.Length ?? 0;
        var holder = rt.Find("Collider");
        if (holder == null) return;
        foreach (var c in holder.GetComponentsInChildren<MeshCollider>(true))
        {
            if (c == null || !c.enabled) continue;
            if (c.sharedMesh != null) f.BodyColliders++;
            else f.BodyCollidersNoMesh++;
        }
        foreach (var b in holder.GetComponentsInChildren<BoxCollider>(true))
            if (b != null && b.name.StartsWith(CarBodyBox.NodePrefix, StringComparison.Ordinal)) f.BodyBoxes++;
    }

    /// <summary>A mesh bounds box seen through <paramref name="m"/> (its eight corners).</summary>
    private static void Box(Matrix4x4 m, Bounds b, out Vector3 lo, out Vector3 hi)
    {
        lo = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
        hi = new Vector3(float.MinValue, float.MinValue, float.MinValue);
        var c = b.center;
        var e = b.extents;
        for (int i = 0; i < 8; i++)
        {
            var p = m.MultiplyPoint3x4(c + new Vector3((i & 1) == 0 ? -e.x : e.x, (i & 2) == 0 ? -e.y : e.y, (i & 4) == 0 ? -e.z : e.z));
            lo = Vector3.Min(lo, p);
            hi = Vector3.Max(hi, p);
        }
    }

    private static MeshVec3 M(Vector3 v) => new(v.x, v.y, v.z);
}
