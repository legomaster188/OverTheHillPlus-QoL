using System;
using System.Collections.Generic;
using UnityEngine;
using VehiclePhysics;
using Object = UnityEngine.Object;

namespace OverTheHillPlus.Cars;

/// <summary>Where a modded car sits on a donor skeleton, worked out once from the donor's wheels and the model's anchors.</summary>
internal sealed class CarLayout
{
    /// <summary>Added to every model position (car root space) so the model's wheel centres meet the donor's axle line
    /// (on the garage model lowered by <see cref="VisualDrop"/>).</summary>
    public Vector3 Offset;
    /// <summary>Wheel centres in car root space, FL FR RL RR (x and z from the model, y the donor's minus <see cref="VisualDrop"/>).</summary>
    public readonly Vector3[] Wheels = new Vector3[4];
    /// <summary>The donor's wheel centres (FL FR RL RR) before the move.</summary>
    public readonly Vector3[] DonorWheels = new Vector3[4];
    /// <summary>The body's bounds in car root space (after <see cref="Offset"/>).</summary>
    public Bounds Body;
    /// <summary>The donor body's bounds in car root space.</summary>
    public Bounds DonorBody;
    /// <summary>The model's tyre radius (metres).</summary>
    public float TyreRadius;
    /// <summary>The donor's tyre radius (its wheel colliders', else its tyre meshes').</summary>
    public float DonorTyreRadius;
    /// <summary>The difference of the tyre radii (<see cref="CarWheelFit.Drop"/>): how much lower than the donor the live
    /// car settles on its wheel colliders (they take the model's radius and are not moved).</summary>
    public float WheelDrop;
    /// <summary>How far this object's wheels and body were lowered: <see cref="WheelDrop"/> on the garage model (no
    /// physics settles it), 0 on the networked template (physics does).</summary>
    public float VisualDrop;

    public float FrontZ => (Wheels[0].z + Wheels[1].z) * 0.5f;
    public float RearZ => (Wheels[2].z + Wheels[3].z) * 0.5f;
    public float DonorFrontZ => (DonorWheels[0].z + DonorWheels[1].z) * 0.5f;
    public float DonorRearZ => (DonorWheels[2].z + DonorWheels[3].z) * 0.5f;
}

/// <summary>
/// Puts a modded car's model on a copy of its donor (the networked template or the garage dummy): body meshes on every
/// LOD, rims and tyres, the material with the car's AO / lights / mask textures (same shader, so the game's property
/// blocks still drive dirt, wet, snow, damage, lights and liveries), wheels moved to the model's wheelbase, track and
/// tyre size, the donor's cosmetic slots moved onto the model, the shadow cube resized and, on the template, wheel
/// colliders, lights, winch points and the body collider moved to the model. Only renderers, meshes, transforms and
/// plain colliders change: never a NetworkBehaviour.
/// <para>The game's cosmetic slots stay (Parts/&lt;Car&gt;_CosmeticParts): each slot, and the few items placed inside
/// one, moves to its anchor on the model (<see cref="CarCosmetics"/>, plus the car's per-anchor tweak); the rollcage,
/// the car's hidden slots and any slot whose anchor the model lacks are hidden.</para>
/// <para>Tyre size (<see cref="CarWheelFit"/>): on the template the wheel colliders keep their place and get the model's
/// tyre radius (CarTuning), and the body is placed so the model's wheel centres meet the donor's: VPP settles the live
/// car lower by the difference of the radii, onto its own tyres, with the centre of mass, winch points and lights where
/// they always were relative to the body (the Vera's behaviour). The garage model has no physics: its wheels and body are
/// lowered by that difference so it stands on the floor.</para>
/// <para>Snow, mud, dust, wet, damage and lights keep working because the donor's own MeshRenderers stay (only their
/// meshes change) and all of them get one material made from the donor's car material (same shader): the car's
/// PropertyBlockSetter collects renderers by that shader in Awake, on each spawned copy, and drives those effects
/// (and the livery texture) through per-material property blocks; the shader weights them with _Mask_Texture B.
/// The meshes carry UV0, normals and tangents like the game's.</para>
/// </summary>
internal sealed class CarBody
{
    private static readonly string[] Corners = { "FL", "FR", "RL", "RR" };
    private const string LiveryProperty = "_Livery_Texture";

    private readonly RuntimeCar _car;
    private Material _material;
    private readonly Dictionary<string, Mesh> _meshCache = new();
    /// <summary>The donor tyre radius the template used (the garage model's fallback).</summary>
    private float _donorRadius;
    private bool _lodLogged;
    /// <summary>The cosmetic slots and items the last <see cref="Apply"/> hid (their transforms).</summary>
    public readonly HashSet<IntPtr> HiddenNodes = new();

    public CarBody(RuntimeCar car) { _car = car; }

    /// <summary>The car's material (made once, from the donor's car material).</summary>
    public Material Material => _material;

    /// <summary>Skins <paramref name="root"/>. Returns null on success, otherwise why it could not.</summary>
    public string Apply(GameObject root, bool template, out CarLayout layout)
    {
        layout = null;
        var blob = _car.Assets.Blob;
        var def = _car.Def;
        var rt = root.transform;

        // ---- find the donor's pieces
        var parts = rt.Find("Parts");
        if (parts == null) return "the donor has no Parts node";
        var bodies = new List<MeshFilter>();
        Transform cosmetics = null;
        for (int i = 0; i < parts.childCount; i++)
        {
            var c = parts.GetChild(i);
            if (c.name.EndsWith("_CosmeticParts", StringComparison.Ordinal)) { cosmetics = c; continue; }
            foreach (var mf in c.GetComponentsInChildren<MeshFilter>(true))
                if (mf != null && mf.name.IndexOf("_body_LOD", StringComparison.OrdinalIgnoreCase) >= 0) bodies.Add(mf);
        }
        if (bodies.Count == 0) return "the donor has no body meshes (Parts/*/*_body_LOD*)";

        var wheelMeshes = rt.Find("Axles/WheelMeshes");
        if (wheelMeshes == null) return "the donor has no Axles/WheelMeshes";
        var corner = new Transform[4];
        for (int i = 0; i < 4; i++)
        {
            corner[i] = wheelMeshes.Find(Corners[i]);
            if (corner[i] == null) return $"the donor has no wheel {Corners[i]}";
        }

        // ---- material: the donor's car material, with the car's textures
        if (_material == null)
        {
            var src = CarMaterial(bodies);
            if (src == null) return "the donor's body has no car material (" + LiveryProperty + ")";
            var m = new Material(src) { name = def.CarName + "_Car" };
            m.SetTexture(LiveryProperty, _car.Assets.Albedo);
            m.SetTexture("_AO_Texture", _car.Assets.Ao);
            m.SetTexture("_Lights_Texture", _car.Assets.Lights);
            m.SetTexture("_Mask_Texture", _car.Assets.Mask);
            m.hideFlags = HideFlags.DontUnloadUnusedAsset;
            Object.DontDestroyOnLoad(m);
            _material = m;
        }

        // ---- layout: line the model's wheel anchors up with the donor's axles, at the model's tyre size
        var lay = new CarLayout();
        var model = new Vector3[4];
        for (int i = 0; i < 4; i++)
        {
            lay.DonorWheels[i] = rt.InverseTransformPoint(corner[i].position);
            model[i] = CarAssets.V(blob.Anchor(MeshBlob.WheelAnchors[i]).Position);
        }
        var tyrePart = blob.Pick(MeshPartKind.Tyre, MeshBlob.TyreName);
        var tyreBounds = CarAssets.RootBounds(new[] { tyrePart });
        lay.TyreRadius = Mathf.Max(tyreBounds.size.y, tyreBounds.size.z) * 0.5f;
        // The template's physics runs on its wheel colliders' radius; the garage model has none, so its own tyre meshes
        // say where its tyres meet the floor (the template's radius when it has none either).
        string radiusFrom = template ? "its wheel colliders" : "its tyre meshes";
        float donorR = template ? ColliderRadius(rt) : MeshTyreRadius(rt, corner);
        if (donorR <= 0f)
        {
            donorR = template ? MeshTyreRadius(rt, corner) : _donorRadius;
            radiusFrom = template ? "its tyre meshes" : "the template's";
        }
        if (template) _donorRadius = donorR;
        lay.DonorTyreRadius = donorR;
        lay.WheelDrop = CarWheelFit.Drop(donorR, lay.TyreRadius);
        lay.VisualDrop = template ? 0f : lay.WheelDrop;

        float dy = Avg(lay.DonorWheels, v => v.y) - lay.VisualDrop - Avg(model, v => v.y);
        float donorMid = (lay.DonorFrontZ + lay.DonorRearZ) * 0.5f;
        float modelMid = ((model[0].z + model[1].z) * 0.5f + (model[2].z + model[3].z) * 0.5f) * 0.5f;
        lay.Offset = new Vector3(0f, dy, donorMid - modelMid);
        for (int i = 0; i < 4; i++)
            lay.Wheels[i] = new Vector3(model[i].x, lay.DonorWheels[i].y - lay.VisualDrop, model[i].z + lay.Offset.z);
        lay.DonorBody = BoundsInRoot(rt, bodies);
        bool same = Mathf.Abs(lay.WheelDrop) < 0.0005f;
        string dir = lay.WheelDrop > 0 ? "lower" : "higher";
        _car.Info(template
            ? $"wheels (car): donor tyre radius {donorR:0.000} m ({radiusFrom}), model {lay.TyreRadius:0.000} m: " +
              (CarWheelFit.Applies(donorR, lay.TyreRadius)
                  ? same ? "the same ride height." : $"the wheel colliders take the model's radius; the car settles {Mathf.Abs(lay.WheelDrop):0.000} m {dir} than the donor."
                  : "the donor's wheel radius stays (the model's tyre is out of range).")
            : $"wheels (garage model): donor tyre radius {donorR:0.000} m ({radiusFrom}), model {lay.TyreRadius:0.000} m: " +
              (same ? "nothing to move." : $"wheels and body {Mathf.Abs(lay.WheelDrop):0.000} m {dir}, as the car settles."));

        var bodyParts = blob.ShellParts(0);
        var rootBody = CarAssets.RootBounds(bodyParts);
        lay.Body = new Bounds(rootBody.center + lay.Offset, rootBody.size);

        // ---- body on every LOD: the model's body_lodN on the donor's LOD N when it has one, else the full body
        var toRoot = rt.localToWorldMatrix * Matrix4x4.Translate(lay.Offset);
        string n = def.CarName;
        var lodTris = new List<(int lod, int triangles)>();
        bool released = false;
        foreach (var mf in bodies)
        {
            int lod = BodyLod(mf.name);
            bool own = lod >= 1 && blob.Part(MeshBlob.BodyLodName(lod)) != null;
            var shell = own ? blob.ShellParts(lod) : bodyParts;
            // Body meshes are only drawn, so their CPU copy is released once uploaded, unless the donor's own body mesh is
            // readable (then the game may read or change it, and ours stays readable too). Tyres stay readable.
            bool donorReadable = true;
            try { donorReadable = mf.sharedMesh != null && mf.sharedMesh.isReadable; } catch (Exception ex) { DebugLog.Error("core", ex, "CarBody.Apply"); }
            released |= !donorReadable;
            mf.sharedMesh = own
                ? CachedMesh(n + "_body_lod" + lod, shell, mf.transform.worldToLocalMatrix * toRoot, release: !donorReadable)
                : CachedMesh(n + "_body", shell, mf.transform.worldToLocalMatrix * toRoot, release: !donorReadable);
            var r = mf.GetComponent<MeshRenderer>();
            if (r != null) r.sharedMaterials = new[] { _material };
            int tris = 0;
            foreach (var p in shell) tris += (p.Indices?.Length ?? 0) / 3;
            lodTris.Add((lod, tris));
        }
        if (template && !_lodLogged)
        {
            // Once: a donor with two body filters on one LOD would draw the whole body twice.
            _lodLogged = true;
            string report = CarLodReport.Summary(lodTris, out bool doubled) +
                            (released ? " (CPU copies released)" : " (kept readable: the donor's body is)");
            if (doubled) _car.LogOnce("lod-doubled", report + ": more than one body renderer on a LOD (the body is drawn more than once).");
            else _car.Info(report + ".");
        }

        // ---- wheels: move each corner to the model's track, wheelbase and tyre height, then rims and tyres. A model with
        // its own rear wheel (a dual pair) draws it on the rear corners; one with tandem anchors gets the extra axles as
        // followers of the physical rear wheels (CarTandemWheels).
        var rim = new List<MeshPart> { blob.Pick(MeshPartKind.Wheel, MeshBlob.WheelName) };
        var tyre = new List<MeshPart> { tyrePart };
        var rimAndTyre = new List<MeshPart> { rim[0], tyrePart };
        var rimR = new List<MeshPart> { CarTandem.RearRim(blob) };
        var tyreR = new List<MeshPart> { CarTandem.RearTyre(blob) };
        var rimAndTyreR = new List<MeshPart> { rimR[0], tyreR[0] };
        bool ownRear = blob.Part(MeshBlob.RearWheelName) != null || blob.Part(MeshBlob.RearTyreName) != null;
        var tandem = CarTandem.AxleOffsets(blob);
        for (int i = 0; i < 4; i++)
        {
            MoveTo(rt, corner[i], lay.Wheels[i]);
            bool rear = i >= 2;
            var filters = corner[i].GetComponentsInChildren<MeshFilter>(true);
            bool hasTyre = false;
            foreach (var mf in filters)
                if (mf != null && IsTyre(mf.name)) hasTyre = true;
            var drawn = new List<MeshFilter>();
            foreach (var mf in filters)
            {
                if (mf == null) continue;
                bool isTyre = IsTyre(mf.name), isRim = !isTyre && mf.name.IndexOf("wheel", StringComparison.OrdinalIgnoreCase) >= 0;
                // Anything else on the wheel (the snow-trax stamp) is left as the donor has it.
                if (!isTyre && !isRim) continue;
                var frame = WheelFrame(mf.transform, corner[i]);
                var toLocal = mf.transform.worldToLocalMatrix * frame.localToWorldMatrix;
                var list = rear ? (isTyre ? tyreR : hasTyre ? rimR : rimAndTyreR) : (isTyre ? tyre : hasTyre ? rim : rimAndTyre);
                mf.sharedMesh = CachedMesh(n + (rear && ownRear ? "_r" : "") + (isTyre ? "_tyre" : hasTyre ? "_rim" : "_wheel"), list, toLocal);
                var r = mf.GetComponent<MeshRenderer>();
                if (r != null) r.sharedMaterials = new[] { _material };
                drawn.Add(mf);
            }
            if (rear && tandem.Length > 0)
            {
                int made = CarTandemWheels.Add(rt, wheelMeshes, drawn, tandem);
                if (template && i == 2)
                    _car.Info($"tandem: {tandem.Length} extra axle(s), {made} follower object(s) on the left rear wheel, offsets " +
                              string.Join(", ", Array.ConvertAll(tandem, a => a.ToString("0.00"))) + " m.");
            }
        }

        // ---- the game's cosmetics: slots moved to the car's anchors (the rollcage and unanchored slots hidden)
        var moves = cosmetics != null ? PlaceCosmetics(rt, cosmetics, lay, blob, template) : null;
        // ---- the snorkel upgrade's intake probes follow the snorkel (the networked car only: the garage model has none)
        if (template) PlaceWaterProbes(rt, lay, moves);

        // ---- donor-only visuals: the rigid-axle meshes
        var axles = rt.Find("Axles");
        if (axles != null)
            foreach (var smr in axles.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                if (smr != null) smr.enabled = false;

        // ---- shadow cube
        var shadow = rt.Find("Effects/ShadowCube") ?? rt.Find("ShadowCube");
        if (shadow != null && lay.DonorBody.size.x > 0.1f && lay.DonorBody.size.z > 0.1f)
        {
            var s = shadow.localScale;
            shadow.localScale = new Vector3(s.x * lay.Body.size.x / lay.DonorBody.size.x, s.y, s.z * lay.Body.size.z / lay.DonorBody.size.z);
            var p = rt.InverseTransformPoint(shadow.position);
            shadow.position = rt.TransformPoint(new Vector3(lay.Body.center.x, p.y, p.z + (lay.Body.center.z - lay.DonorBody.center.z)));
        }

        if (template)
        {
            // The wheel colliders follow the model's track and wheelbase and keep their height: CarTuning gives them the
            // model's tyre radius and VPP settles the car onto its own tyres.
            var wheelColliders = rt.Find("Axles/WheelColliders");
            for (int i = 0; i < 4 && wheelColliders != null; i++)
            {
                var wc = wheelColliders.Find("Wheel" + Corners[i]);
                if (wc == null) continue;
                var p = rt.InverseTransformPoint(wc.position);
                MoveTo(rt, wc, new Vector3(lay.Wheels[i].x, p.y, lay.Wheels[i].z));
            }
            FitBumpStops(rt, wheelColliders, lay, blob, tandem);
            MoveLights(rt, lay, blob);
            MoveWinchPoints(rt, lay);
            PlaceDonorObjects(rt, lay, blob, moves);
            string why = SetBodyCollider(rt, lay, blob);
            if (why != null) return why;
        }
        if (_car.Hooks.AfterBody != null)
        {
            string why = _car.Hooks.AfterBody(root, template);
            if (why != null) return why;
        }
        layout = lay;
        return null;
    }

    /// <summary>The donor's wheel collider radius (the four averaged; 0 when it has none).</summary>
    private static float ColliderRadius(Transform rt)
    {
        float sum = 0f;
        int n = 0;
        foreach (var wc in rt.GetComponentsInChildren<VPFunselektor3DRaycastWheelCollider>(true))
        {
            if (wc == null || wc.radius <= 0f) continue;
            sum += wc.radius;
            n++;
        }
        return n == 0 ? 0f : sum / n;
    }

    /// <summary>The donor's tyre radius from its tyre meshes (car root space; 0 when none is found).</summary>
    private static float MeshTyreRadius(Transform rt, Transform[] corners)
    {
        float sum = 0f;
        int n = 0;
        foreach (var c in corners)
            foreach (var mf in c.GetComponentsInChildren<MeshFilter>(true))
            {
                if (mf == null || mf.sharedMesh == null || !IsTyre(mf.name)) continue;
                var b = BoundsInRoot(rt, new List<MeshFilter> { mf });
                sum += Mathf.Max(b.size.y, b.size.z) * 0.5f;
                n++;
                break;
            }
        return n == 0 ? 0f : sum / n;
    }

    /// <summary>The donor body's LOD from its name (Parts/*/*_body_LOD2 → 2; 0 when it has no number).</summary>
    private static int BodyLod(string name)
    {
        int at = name.LastIndexOf("_body_LOD", StringComparison.OrdinalIgnoreCase);
        if (at < 0) return 0;
        int lod = 0, i = at + "_body_LOD".Length;
        for (; i < name.Length && name[i] >= '0' && name[i] <= '9'; i++) lod = lod * 10 + (name[i] - '0');
        return lod;
    }

    /// <summary>
    /// Moves each cosmetic slot (and the items placed inside one) to its anchor on the car, keeping its rotation and
    /// scale. Anchors are points in car-root space before the layout offset (plus the car's tweak); each target gets
    /// the point in its parent's space. A slot without all its anchors, the rollcage, the car's hidden slots and any
    /// slot not in the table are hidden.
    /// </summary>
    /// <summary>An offset item's local position before its offset (so placing the same object again never adds it twice).</summary>
    private static readonly Dictionary<IntPtr, Vector3> ItemBase = new();

    private Dictionary<string, MeshVec3> PlaceCosmetics(Transform rt, Transform cosmetics, CarLayout lay, MeshBlob blob, bool log)
    {
        var def = _car.Def;
        var table = def.CosmeticTable;
        var moves = new Dictionary<string, MeshVec3>(StringComparer.Ordinal);
        HiddenNodes.Clear();
        int placed = 0;
        var hidden = new List<string>();
        var where = new List<string>();
        for (int g = 0; g < cosmetics.childCount; g++)
        {
            var group = cosmetics.GetChild(g);
            for (int k = 0; k < group.childCount; k++)
            {
                var slot = group.GetChild(k);
                string path = group.name + "/" + slot.name;
                if (!CarCosmetics.Shown(table, path, a => blob.Anchor(a) != null, def.HiddenSlots))
                {
                    slot.gameObject.SetActive(false);
                    HiddenNodes.Add(slot.Pointer);
                    hidden.Add(path == CarCosmetics.Rollcage ? path + " (the donor's shape)" : path);
                    continue;
                }
                var slotScale = new MeshVec3(1f, 1f, 1f);
                foreach (var a in CarCosmetics.For(table, path))
                {
                    var anchor = blob.Anchor(a.Anchor);
                    if (anchor == null) continue; // an optional item anchor this model lacks: the item stays with its slot
                    var t = cosmetics.Find(a.Target);
                    if (t == null || t.parent == null) continue; // this donor has no such item
                    var before = rt.InverseTransformPoint(t.position);
                    var want = CarAssets.V(anchor.Position) + CarAssets.V(def.TweakOf(a.Anchor)) + lay.Offset;
                    t.localPosition = t.parent.InverseTransformPoint(rt.TransformPoint(want));
                    // a slimmer or shorter body: the model's scale anchor resizes the target in its own axes; an item
                    // with its own anchor keeps its own size inside a scaled slot (a plate stays a plate's size)
                    var scale = blob.Anchor(CarCosmetics.ScaleAnchorOf(a.Anchor));
                    var own = scale != null ? CarCosmetics.ScaleOf(scale.Position) : new MeshVec3(1f, 1f, 1f);
                    var ls = t.localScale;
                    var fit = CarCosmetics.TargetScale(new MeshVec3(ls.x, ls.y, ls.z), a.IsSlot ? new MeshVec3(1f, 1f, 1f) : slotScale, own);
                    t.localScale = CarAssets.V(fit);
                    if (a.IsSlot) slotScale = own;
                    string sc = scale != null ? $", scale {F(CarAssets.V(own))}" : "";
                    // read back in car-root space: the next run's log shows where each slot really ended up
                    var got = rt.InverseTransformPoint(t.position);
                    var d = got - before;
                    moves[a.Target] = new MeshVec3(d.x, d.y, d.z);
                    where.Add($"{a.Target} {F(got)} (anchor {a.Anchor} wants {F(want)}{sc}{((got - want).magnitude > 0.01f ? ", OFF" : "")})");
                }
                // single items moved on their own (the car's item offsets), from where the prefab has them in the slot
                if (def.ItemOffsets != null)
                    foreach (var kv in def.ItemOffsets)
                    {
                        if (CosmeticItemAnchor.SlotOf(kv.Key) != path) continue;
                        var t = cosmetics.Find(kv.Key);
                        if (t == null || t.parent == null) continue;
                        if (!ItemBase.TryGetValue(t.Pointer, out var at)) ItemBase[t.Pointer] = at = t.localPosition;
                        t.localPosition = at + t.parent.InverseTransformVector(rt.TransformVector(CarAssets.V(kv.Value)));
                        where.Add($"{kv.Key} {F(rt.InverseTransformPoint(t.position))} (item offset {F(CarAssets.V(kv.Value))})");
                    }
                // single items this car can't carry (shaped for the donor's body)
                if (def.HiddenItems != null)
                    foreach (var h in def.HiddenItems)
                    {
                        if (CosmeticItemAnchor.SlotOf(h) != path) continue;
                        var t = cosmetics.Find(h);
                        if (t == null) continue;
                        t.gameObject.SetActive(false);
                        HiddenNodes.Add(t.Pointer);
                        hidden.Add(h);
                    }
                placed++;
            }
        }
        if (log)
            _car.Info($"cosmetics: {placed} slot(s) placed on the {def.CarName}" +
                      (hidden.Count > 0 ? "; hidden: " + string.Join(", ", hidden) : "") + ".");
        // once per build (the car and its garage model), in car-root space with the layout offset (car-root y is up)
        _car.Info($"cosmetics ({(log ? "car" : "garage model")}), layout offset {F(lay.Offset)}, body {F(lay.Body.min)}..{F(lay.Body.max)}: " +
                  string.Join("; ", where));
        return moves;
    }

    /// <summary>
    /// The water probes (the donor's WaterDetector children) moved with what they sit on (<see cref="CarUpgradeFit"/>):
    /// the stock engine intake with the hood, the snorkel intakes with the snorkel levels, the roof probes with the body's
    /// top. Without this the snorkel upgrade flooded at the donor's intake heights, not at the modded snorkel's top.
    /// </summary>
    private void PlaceWaterProbes(Transform rt, CarLayout lay, Dictionary<string, MeshVec3> moves)
    {
        var node = rt.Find(CarUpgradeFit.WaterDetectorNode);
        if (node == null) return;
        var donorBody = new BodySpan(lay.DonorBody.min.z, lay.DonorBody.max.z, lay.DonorBody.max.y);
        var body = new BodySpan(lay.Body.min.z, lay.Body.max.z, lay.Body.max.y);
        var moved = new List<string>();
        for (int i = 0; i < node.childCount; i++)
        {
            var p = node.GetChild(i);
            var at = rt.InverseTransformPoint(p.position);
            var want = CarUpgradeFit.Place(p.name, new MeshVec3(at.x, at.y, at.z), moves, donorBody, body);
            if (want == null) continue;
            var w = CarAssets.V(want.Value);
            p.position = rt.TransformPoint(w);
            moved.Add($"{p.name} {F(at)} -> {F(w)}");
        }
        if (moved.Count > 0) _car.Info("water probes: " + string.Join("; ", moved) + ".");
    }

    private static string F(Vector3 v) => $"({v.x:F2}, {v.y:F2}, {v.z:F2})";

    private static bool IsTyre(string n) => n.IndexOf("tyre", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                            n.IndexOf("tire", StringComparison.OrdinalIgnoreCase) >= 0;

    /// <summary>The wheel's own frame: the nearest LODGroup above the mesh (it carries the right wheels' 180° yaw),
    /// else the corner node.</summary>
    private static Transform WheelFrame(Transform mesh, Transform corner)
    {
        for (var t = mesh.parent; t != null && t != corner.parent; t = t.parent)
            if (t.GetComponent<LODGroup>() != null) return t;
        return corner;
    }

    private static Material CarMaterial(List<MeshFilter> bodies)
    {
        foreach (var mf in bodies)
        {
            var r = mf.GetComponent<MeshRenderer>();
            if (r == null) continue;
            foreach (var m in r.sharedMaterials)
                if (m != null && m.HasProperty(LiveryProperty)) return m;
        }
        return null;
    }

    /// <summary>A built mesh, cached by name and placement. <paramref name="release"/>: only drawn (never read back,
    /// never a collider), so once it is uploaded its CPU copy is freed (a modded car's LOD0 body is hundreds of thousands
    /// of triangles; Mesh.bounds still works).</summary>
    private Mesh CachedMesh(string name, List<MeshPart> parts, Matrix4x4 toLocal, bool release = false)
    {
        // the release flag is part of the key: a readable donor filter must never get a released (non-readable) mesh
        string key = name + "|" + toLocal.ToString("F4") + (release ? "|released" : "");
        if (_meshCache.TryGetValue(key, out var m) && m != null) return m;
        m = CarAssets.BuildMesh(name, parts, toLocal);
        if (release && m != null)
        {
            try
            {
                CrashTrail.Mark(_car.Id + ": upload " + name);
                m.UploadMeshData(true);
            }
            catch (Exception e) { DebugLog.Error("core", e, "CarBody.CachedMesh"); _car.LogOnce("upload", "could not release a body mesh's CPU copy: " + e.Message); }
        }
        _meshCache[key] = m;
        return m;
    }

    private static float Avg(Vector3[] v, Func<Vector3, float> f) => (f(v[0]) + f(v[1]) + f(v[2]) + f(v[3])) * 0.25f;

    private static void MoveTo(Transform root, Transform t, Vector3 rootPos) => t.position = root.TransformPoint(rootPos);

    private static Bounds BoundsInRoot(Transform root, List<MeshFilter> filters)
    {
        bool any = false;
        var b = new Bounds();
        foreach (var mf in filters)
        {
            var mesh = mf.sharedMesh;
            if (mesh == null) continue;
            var lb = mesh.bounds;
            for (int i = 0; i < 8; i++)
            {
                var c = lb.center + Vector3.Scale(lb.extents, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
                var p = root.InverseTransformPoint(mf.transform.TransformPoint(c));
                if (!any) { b = new Bounds(p, Vector3.zero); any = true; }
                else b.Encapsulate(p);
            }
        }
        return b;
    }

    private static void MoveLights(Transform rt, CarLayout lay, MeshBlob blob)
    {
        var lights = rt.Find("Lights");
        if (lights == null) return;
        void Put(string light, string anchor, float dx = 0f)
        {
            var t = lights.Find(light);
            var a = blob.Anchor(anchor);
            if (t == null || a == null) return;
            MoveTo(rt, t, CarAssets.V(a.Position) + lay.Offset + new Vector3(dx, 0f, 0f));
        }
        Put("LeftHeadlight", "headlight_L");
        Put("RightHeadlight", "headlight_R");
        Put("LeftPositionBrakelight", "tail_L");
        Put("RightPositionBrakelight", "tail_R");
        Put("LeftBrakingBrakelight", "tail_L");
        Put("RightBrakingBrakelight", "tail_R");
        Put("LeftReverselight", "reverse", -0.25f);
        Put("RightReverselight", "reverse", 0.25f);
        var bounce = lights.Find("FakeBounceLight");
        if (bounce != null)
        {
            var p = rt.InverseTransformPoint(bounce.position);
            MoveTo(rt, bounce, new Vector3(p.x, p.y, p.z + (lay.Body.max.z - lay.DonorBody.max.z)));
        }
    }

    private static void MoveWinchPoints(Transform rt, CarLayout lay)
    {
        var pts = rt.Find("Winch/WinchAttachPoints");
        if (pts == null) return;
        void Z(string n, float z)
        {
            var t = pts.Find(n);
            if (t == null) return;
            var p = rt.InverseTransformPoint(t.position);
            MoveTo(rt, t, new Vector3(p.x, p.y, z));
        }
        void X(string n, float x)
        {
            var t = pts.Find(n);
            if (t == null) return;
            var p = rt.InverseTransformPoint(t.position);
            MoveTo(rt, t, new Vector3(x, p.y, lay.Body.center.z));
        }
        Z("Front", lay.Body.max.z);
        Z("Rear", lay.Body.min.z);
        X("Left", lay.Body.min.x);
        X("Right", lay.Body.max.x);
    }

    /// <summary>
    /// The donor's body collider becomes the model's convex hull (or a box of the body when the model has no collider
    /// part), its bottom lifted a little above the body's lowest point the way the game's own cars are
    /// (<see cref="CarPhysicsFit.ColliderLift"/>: a sill or bumper skims a rock instead of hooking it); any further donor
    /// body collider is switched off; the car's <see cref="CarDefinition.BodyBoxes"/> are added as box colliders.
    /// </summary>
    private string SetBodyCollider(Transform rt, CarLayout lay, MeshBlob blob)
    {
        var holder = rt.Find("Collider");
        if (holder == null) return "the donor has no Collider node";
        var cols = holder.GetComponentsInChildren<MeshCollider>(true);
        if (cols.Length == 0) return "the donor has no body MeshCollider";
        // the donor's own lift: its body colliders' bottom over its visible body's bottom
        float donorColMin = float.MaxValue;
        foreach (var c in cols)
        {
            if (c == null || c.sharedMesh == null) continue;
            var b = MeshBoundsInRoot(rt, c.transform, c.sharedMesh.bounds);
            donorColMin = Mathf.Min(donorColMin, b.min.y);
        }
        float donorLift = donorColMin < float.MaxValue ? donorColMin - lay.DonorBody.min.y : CarPhysicsFit.MinColliderLift;
        float lift = CarPhysicsFit.ColliderLift(donorLift, lay.DonorTyreRadius, lay.TyreRadius);
        float bodyMin = lay.Body.min.y - lay.Offset.y; // model space: the ground is y = 0 under the tyres

        var main = cols[0];
        var toLocal = main.transform.worldToLocalMatrix * rt.localToWorldMatrix * Matrix4x4.Translate(lay.Offset);
        var hull = blob.Pick(MeshPartKind.Collider, MeshBlob.ColliderName);
        string n = _car.Def.CarName;
        Mesh mesh;
        float colMin, colMax, floor;
        if (hull != null)
        {
            CarPhysicsFit.HeightRange(hull, out colMin, out colMax);
            floor = CarPhysicsFit.ColliderFloor(colMin, colMax, bodyMin, lift);
            mesh = CachedMesh(n + "_collider_" + floor.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture),
                new List<MeshPart> { CarPhysicsFit.Floored(hull, floor) }, toLocal);
        }
        else
        {
            var b = new Bounds(lay.Body.center - lay.Offset, lay.Body.size);
            colMin = b.min.y; colMax = b.max.y;
            floor = CarPhysicsFit.ColliderFloor(colMin, colMax, bodyMin, lift);
            b.SetMinMax(new Vector3(b.min.x, floor, b.min.z), b.max);
            mesh = BoxMesh(n + "_collider_box", b, toLocal);
        }
        if (mesh == null) return "the car's collider has no triangles";
        main.sharedMesh = mesh;
        main.convex = true;
        for (int i = 1; i < cols.Length; i++) cols[i].enabled = false;

        // the body's extra boxes (a tractor's frame behind the cab), on the main collider's layer
        var boxes = _car.Def.BodyBoxes ?? Array.Empty<CarBodyBox>();
        for (int i = 0; i < boxes.Length; i++)
        {
            var bx = boxes[i];
            var go = new GameObject("OTHP_body_box_" + i.ToString(System.Globalization.CultureInfo.InvariantCulture)) { layer = main.gameObject.layer };
            var t = go.transform;
            t.SetParent(holder, false);
            t.SetPositionAndRotation(rt.TransformPoint(CarAssets.V(bx.Center) + lay.Offset), rt.rotation);
            var box = go.AddComponent<BoxCollider>();
            var ls = t.lossyScale;
            box.size = new Vector3(bx.Size.X / Safe(ls.x), bx.Size.Y / Safe(ls.y), bx.Size.Z / Safe(ls.z));
            box.center = Vector3.zero;
        }
        _car.Info($"body collider: {(hull != null ? "the model's collider part" : "a box of the body")} from {colMin:0.00} to {colMax:0.00} m " +
                  $"over the ground, bottom lifted to {floor:0.00} m (body bottom {bodyMin:0.00} m, lift {lift:0.00} m: the donor's {donorLift:0.00} m " +
                  $"scaled to the tyre); {cols.Length - 1} other donor body collider(s) off; {boxes.Length} body box(es).");
        return null;
    }

    private static float Safe(float v) => Mathf.Abs(v) < 1e-4f ? 1f : v;

    /// <summary>A mesh bounds box (in <paramref name="t"/>'s space) as a box in car-root space.</summary>
    private static Bounds MeshBoundsInRoot(Transform root, Transform t, Bounds lb)
    {
        var b = new Bounds();
        for (int i = 0; i < 8; i++)
        {
            var c = lb.center + Vector3.Scale(lb.extents, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
            var p = root.InverseTransformPoint(t.TransformPoint(c));
            if (i == 0) b = new Bounds(p, Vector3.zero);
            else b.Encapsulate(p);
        }
        return b;
    }

    /// <summary>
    /// Each wheel's bump stop collider (the donor's own tyre mesh, 0.38 m over the hub, moving with it: a solid fender
    /// behind the raycast suspension) resized to the model's tyre, so it stands as high over the ground as on a stock car
    /// (a smaller tyre left the donor's big one 0.15 to 0.2 m lower, catching rocks a stock car clears). The rear wheels of
    /// a tandem get one per extra axle, moved along the car with it.
    /// </summary>
    private void FitBumpStops(Transform rt, Transform wheelColliders, CarLayout lay, MeshBlob blob, float[] tandem)
    {
        if (wheelColliders == null) return;
        var front = CarAssets.RootBounds(new[] { blob.Pick(MeshPartKind.Tyre, MeshBlob.TyreName) });
        var rear = CarAssets.RootBounds(new[] { CarTandem.RearTyre(blob) });
        var report = new List<string>();
        for (int i = 0; i < 4; i++)
        {
            var wc = wheelColliders.Find("Wheel" + Corners[i]);
            var bs = wc != null ? wc.Find("WheelHub/BumpStopCollider") : null;
            var mc = bs != null ? bs.GetComponent<MeshCollider>() : null;
            if (mc == null || mc.sharedMesh == null) continue;
            var donor = MeshBoundsInRoot(rt, bs, mc.sharedMesh.bounds);
            float donorWidth = donor.size.x;
            float modelWidth = (i < 2 ? front : rear).size.x;
            var s = CarPhysicsFit.BumpStopColliderScale(lay.DonorTyreRadius, lay.TyreRadius, donorWidth, modelWidth);
            var ls = bs.localScale;
            bs.localScale = new Vector3(ls.x * s.X, ls.y * s.Y, ls.z * s.Z);
            report.Add($"{Corners[i]} x{s.X:0.00}/{s.Y:0.00}");
            if (i < 2 || tandem == null) continue;
            for (int a = 0; a < tandem.Length; a++)
            {
                var copy = Object.Instantiate(bs.gameObject, bs.parent, false);
                copy.name = "OTHP_bumpstop_tandem_" + a.ToString(System.Globalization.CultureInfo.InvariantCulture);
                var ct = copy.transform;
                ct.localPosition = bs.localPosition + bs.parent.InverseTransformVector(rt.TransformVector(new Vector3(0f, 0f, tandem[a])));
                ct.localRotation = bs.localRotation;
                ct.localScale = bs.localScale;
            }
            if (tandem.Length > 0) report.Add($"{Corners[i]} +{tandem.Length} tandem");
        }
        if (report.Count > 0) _car.Info("bump stop colliders sized to the model's tyres (width/radius): " + string.Join(", ", report) + ".");
    }

    /// <summary>The donor's lens flares, smoke, dust and wheel sounds moved onto the model or switched off
    /// (<see cref="CarDonorObjects"/>); any other light or flare outside the cosmetic slots is switched off.</summary>
    private void PlaceDonorObjects(Transform rt, CarLayout lay, MeshBlob blob, Dictionary<string, MeshVec3> moves)
    {
        var rules = CarDonorObjects.For(_car.Def.DonorObjects);
        var done = new List<string>();
        foreach (var r in rules)
        {
            var t = rt.Find(r.Path);
            if (t == null) continue;
            var at = rt.InverseTransformPoint(t.position);
            Vector3? want = null;
            string why;
            switch (r.Follow)
            {
                case DonorFollow.Anchor:
                case DonorFollow.Hood:
                {
                    string a = CarDonorObjects.PickAnchor(r, n => blob.Anchor(n) != null, at.x);
                    if (a != null) { want = CarAssets.V(blob.Anchor(a).Position) + lay.Offset; why = a; break; }
                    if (r.Follow == DonorFollow.Anchor) { why = "off (no " + string.Join("/", r.Anchors) + ")"; break; }
                    if (moves != null && moves.TryGetValue(CarUpgradeFit.Hood, out var d)) { want = at + CarAssets.V(d); why = "with the hood"; break; }
                    want = at + new Vector3(0f, lay.Body.max.y - lay.DonorBody.max.y, lay.FrontZ - lay.DonorFrontZ);
                    why = "with the front axle";
                    break;
                }
                case DonorFollow.Wheel:
                {
                    int w = Mathf.Clamp(r.Wheel, 0, 3);
                    var d = lay.Wheels[w] - lay.DonorWheels[w];
                    // the ground under a smaller tyre is higher on the body (the live car settles by the radius difference)
                    want = at + new Vector3(d.x, lay.WheelDrop - lay.VisualDrop, d.z);
                    why = "wheel " + Corners[w];
                    break;
                }
                default:
                    why = "off";
                    break;
            }
            if (want.HasValue) MoveTo(rt, t, want.Value);
            else SwitchOff(t);
            done.Add($"{r.Path} {F(at)} -> {(want.HasValue ? F(want.Value) : "-")} ({why})");
        }
        // anything else that shines: off (the donor's lamps are not where the model's are)
        var off = new List<string>();
        foreach (var l in rt.GetComponentsInChildren<Light>(true))
        {
            string path = PathBelow(rt, l.transform);
            if (CarDonorObjects.Placed(path, rules)) continue;
            l.intensity = 0f;
            l.enabled = false;
            off.Add(path);
        }
        foreach (var f in rt.GetComponentsInChildren<UnityEngine.Rendering.LensFlareComponentSRP>(true))
        {
            string path = PathBelow(rt, f.transform);
            if (CarDonorObjects.Placed(path, rules) || off.Contains(path)) continue;
            f.intensity = 0f;
            f.enabled = false;
            off.Add(path);
        }
        _car.Info("donor objects: " + string.Join("; ", done) + (off.Count > 0 ? "; other lights and flares off: " + string.Join(", ", off) : "") + ".");
    }

    /// <summary>Switches a donor object off for good: its lights and flares disabled (the game toggles the light objects
    /// themselves on and off), anything else inactive.</summary>
    private static void SwitchOff(Transform t)
    {
        bool shines = false;
        foreach (var l in t.GetComponentsInChildren<Light>(true)) { l.intensity = 0f; l.enabled = false; shines = true; }
        foreach (var f in t.GetComponentsInChildren<UnityEngine.Rendering.LensFlareComponentSRP>(true)) { f.intensity = 0f; f.enabled = false; shines = true; }
        if (!shines) t.gameObject.SetActive(false);
    }

    private static string PathBelow(Transform root, Transform t)
    {
        var parts = new List<string>();
        for (var c = t; c != null && c != root; c = c.parent) parts.Add(c.name);
        parts.Reverse();
        return string.Join("/", parts);
    }

    private static Mesh BoxMesh(string name, Bounds b, Matrix4x4 toLocal)
    {
        var verts = new Vector3[8];
        for (int i = 0; i < 8; i++)
            verts[i] = toLocal.MultiplyPoint3x4(b.center + Vector3.Scale(b.extents, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1)));
        int[] tris =
        {
            0, 2, 1, 1, 2, 3, 4, 5, 6, 5, 7, 6, 0, 1, 4, 1, 5, 4,
            2, 6, 3, 3, 6, 7, 0, 4, 2, 2, 4, 6, 1, 3, 5, 3, 7, 5,
        };
        var m = new Mesh { name = name, vertices = verts, triangles = tris };
        m.RecalculateBounds();
        m.RecalculateNormals();
        m.hideFlags = HideFlags.DontUnloadUnusedAsset;
        Object.DontDestroyOnLoad(m);
        return m;
    }
}
