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

        // ---- wheels: move each corner to the model's track, wheelbase and tyre height, then rims and tyres
        var rim = new List<MeshPart> { blob.Pick(MeshPartKind.Wheel, MeshBlob.WheelName) };
        var tyre = new List<MeshPart> { tyrePart };
        var rimAndTyre = new List<MeshPart> { rim[0], tyrePart };
        for (int i = 0; i < 4; i++)
        {
            MoveTo(rt, corner[i], lay.Wheels[i]);
            var filters = corner[i].GetComponentsInChildren<MeshFilter>(true);
            bool hasTyre = false;
            foreach (var mf in filters)
                if (mf != null && IsTyre(mf.name)) hasTyre = true;
            foreach (var mf in filters)
            {
                if (mf == null) continue;
                bool isTyre = IsTyre(mf.name), isRim = !isTyre && mf.name.IndexOf("wheel", StringComparison.OrdinalIgnoreCase) >= 0;
                // Anything else on the wheel (the snow-trax stamp) is left as the donor has it.
                if (!isTyre && !isRim) continue;
                var frame = WheelFrame(mf.transform, corner[i]);
                var toLocal = mf.transform.worldToLocalMatrix * frame.localToWorldMatrix;
                var list = isTyre ? tyre : hasTyre ? rim : rimAndTyre;
                mf.sharedMesh = CachedMesh(n + (isTyre ? "_tyre" : hasTyre ? "_rim" : "_wheel"), list, toLocal);
                var r = mf.GetComponent<MeshRenderer>();
                if (r != null) r.sharedMaterials = new[] { _material };
            }
        }

        // ---- the game's cosmetics: slots moved to the car's anchors (the rollcage and unanchored slots hidden)
        if (cosmetics != null) PlaceCosmetics(rt, cosmetics, lay, blob, template);

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
            MoveLights(rt, lay, blob);
            MoveWinchPoints(rt, lay);
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
    private void PlaceCosmetics(Transform rt, Transform cosmetics, CarLayout lay, MeshBlob blob, bool log)
    {
        var def = _car.Def;
        var table = def.CosmeticTable;
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
                    hidden.Add(path == CarCosmetics.Rollcage ? path + " (the donor's shape)" : path);
                    continue;
                }
                foreach (var a in CarCosmetics.For(table, path))
                {
                    var anchor = blob.Anchor(a.Anchor);
                    if (anchor == null) continue; // an optional item anchor this model lacks: the item stays with its slot
                    var t = cosmetics.Find(a.Target);
                    if (t == null || t.parent == null) continue; // this donor has no such item
                    var want = CarAssets.V(anchor.Position) + CarAssets.V(def.TweakOf(a.Anchor)) + lay.Offset;
                    t.localPosition = t.parent.InverseTransformPoint(rt.TransformPoint(want));
                    // read back in car-root space: the next run's log shows where each slot really ended up
                    var got = rt.InverseTransformPoint(t.position);
                    where.Add($"{a.Target} {F(got)} (anchor {a.Anchor} wants {F(want)}{((got - want).magnitude > 0.01f ? ", OFF" : "")})");
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

    /// <summary>The donor's body collider becomes the model's convex hull (or a box of the body when the model has no
    /// collider part); any further body collider is switched off.</summary>
    private string SetBodyCollider(Transform rt, CarLayout lay, MeshBlob blob)
    {
        var holder = rt.Find("Collider");
        if (holder == null) return "the donor has no Collider node";
        var cols = holder.GetComponentsInChildren<MeshCollider>(true);
        if (cols.Length == 0) return "the donor has no body MeshCollider";
        var main = cols[0];
        var toLocal = main.transform.worldToLocalMatrix * rt.localToWorldMatrix * Matrix4x4.Translate(lay.Offset);
        var hull = blob.Pick(MeshPartKind.Collider, MeshBlob.ColliderName);
        string n = _car.Def.CarName;
        var mesh = hull != null
            ? CachedMesh(n + "_collider", new List<MeshPart> { hull }, toLocal)
            : BoxMesh(n + "_collider_box", new Bounds(lay.Body.center - lay.Offset, lay.Body.size), toLocal);
        if (mesh == null) return "the car's collider has no triangles";
        main.sharedMesh = mesh;
        main.convex = true;
        for (int i = 1; i < cols.Length; i++) cols[i].enabled = false;
        return null;
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
