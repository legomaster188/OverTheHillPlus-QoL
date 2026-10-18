using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using VehiclePhysics;
using Object = UnityEngine.Object;

namespace OverTheHillPlus.Ghosts;

/// <summary>
/// The ghost: a new GameObject holding copies of your car's visible meshes (each copy is a MeshFilter + MeshRenderer
/// only: no colliders, rigidbody, scripts or network parts; skinned parts are baked once into a still mesh, destroyed
/// with the ghost), drawn with one see-through material. Wheels get their own pivots
/// so steering, spin and suspension replay. Built from the car you're in, so a ghost recorded in another car borrows
/// this car's look.
/// </summary>
internal sealed class GhostCar
{
    private static readonly Color Tint = new(0.55f, 0.85f, 1f, 1f);

    private static Material _material;
    private static bool _materialTried, _materialSolid;
    private static float _materialOpacity = -1f;

    private GameObject _root;
    private Pivot[] _wheels = Array.Empty<Pivot>();
    private readonly List<Mesh> _baked = new();

    /// <summary>One wheel's pivots in the ghost: where they sit (car space) at rest.</summary>
    private sealed class Pivot
    {
        public Transform Wheel, Hub;
        public float WheelX, WheelZ, HubX, HubZ, HubDy;
    }

    public bool Alive => _root != null;

    /// <summary>Builds a ghost from <paramref name="car"/>, or returns null (with one log line) when it can't.</summary>
    public static GhostCar Build(VPVehicleController car)
    {
        if (car == null) return null;
        var g = new GhostCar();
        try
        {
            g.BuildFrom(car);
            return g;
        }
        catch (Exception e)
        {
            DebugLog.Error("ghosts", e, "GhostCar.Build");
            GhostsPlugin.Logger.LogWarning("Couldn't build the ghost car: " + e.Message);
            g.Destroy();
            return null;
        }
    }

    private void BuildFrom(VPVehicleController car)
    {
        var src = car.transform;
        _root = new GameObject("OTH Ghost Car");
        var root = _root.transform;
        root.SetPositionAndRotation(src.position, src.rotation);
        root.localScale = src.lossyScale;
        var inv = Quaternion.Inverse(src.rotation);

        // Wheels first: their pivots are where their meshes go.
        var visuals = GhostRig.Wheels(car);
        var steer = new float[visuals.Length];
        GhostRig.Steering(car, steer);
        _wheels = new Pivot[visuals.Length];
        var spinning = new List<(Transform from, Transform to, Quaternion undo, Vector3 at)>();
        for (int i = 0; i < visuals.Length; i++)
        {
            var v = visuals[i];
            if (v.Wheel == null || v.Wheel == src) continue;
            var p = new Pivot();
            var wl = src.InverseTransformPoint(v.Wheel.position);
            p.WheelX = wl.x; p.WheelZ = wl.z;
            p.Wheel = NewChild(root, "Wheel " + i, wl, Quaternion.Euler(0f, steer[i], 0f));
            // Meshes keep their pose relative to an un-steered, un-spun pivot.
            var undo = Quaternion.Inverse(Quaternion.Euler(0f, steer[i], 0f));
            spinning.Add((v.Wheel, p.Wheel, undo, wl));
            if (v.Hub != null && v.Hub != src && v.Hub != v.Wheel && !v.Hub.IsChildOf(v.Wheel))
            {
                var hl = src.InverseTransformPoint(v.Hub.position);
                p.HubX = hl.x; p.HubZ = hl.z; p.HubDy = hl.y - wl.y;
                p.Hub = NewChild(root, "Hub " + i, hl, Quaternion.Euler(0f, steer[i], 0f));
                spinning.Add((v.Hub, p.Hub, undo, hl));
            }
            _wheels[i] = p;
        }

        var skip = new HashSet<IntPtr>();
        try { if (car.resimulationRenderer != null) skip.Add(car.resimulationRenderer.Pointer); } catch (Exception ex) { DebugLog.Error("ghosts", ex, "GhostCar.BuildFrom"); }
        try { if (car.forwardRenderer != null) skip.Add(car.forwardRenderer.Pointer); } catch (Exception ex) { DebugLog.Error("ghosts", ex, "GhostCar.BuildFrom"); }

        // Plain meshes as they are; skinned parts (e.g. the driver) baked once to a still mesh in their current pose.
        // Of a mesh with levels of detail only the lowest is copied (GhostLod): the ghost has no LODGroup.
        var parts = new List<(Renderer r, Mesh mesh, bool baked)>();
        var plain = new List<(Renderer r, Mesh mesh)>();
        var names = new List<string>();
        foreach (var r in src.GetComponentsInChildren<MeshRenderer>(false))
        {
            if (!Wanted(r, skip)) continue;
            var mf = r.GetComponent<MeshFilter>();
            var mesh = mf == null ? null : mf.sharedMesh;
            if (mesh == null) continue;
            plain.Add((r, mesh));
            names.Add(r.transform.name ?? "");
        }
        var keep = GhostLod.KeepLowestDetail(names);
        for (int i = 0; i < plain.Count; i++)
            if (keep[i]) parts.Add((plain[i].r, plain[i].mesh, false));
        var skinned = new List<SkinnedMeshRenderer>();
        var skinnedNames = new List<string>();
        foreach (var r in src.GetComponentsInChildren<SkinnedMeshRenderer>(false))
        {
            if (!Wanted(r, skip) || r.sharedMesh == null) continue;
            skinned.Add(r);
            skinnedNames.Add(r.transform.name ?? "");
        }
        var keepSkinned = GhostLod.KeepLowestDetail(skinnedNames);
        for (int i = 0; i < skinned.Count; i++)
        {
            if (!keepSkinned[i]) continue;
            var r = skinned[i];
            try
            {
                var mesh = new Mesh { name = "OTH Ghost baked" };
                r.BakeMesh(mesh, true); // vertices in the renderer's space, its scale included
                _baked.Add(mesh);
                parts.Add((r, mesh, true));
            }
            catch (Exception e) { DebugLog.Error("ghosts", e, "GhostCar.BuildFrom"); GhostsPlugin.Logger.LogDebug($"Ghost: couldn't bake {r.name}: {e.Message}"); }
        }

        var material = Material(GhostsConfig.Opacity?.Value ?? 0.35f);
        int copied = 0;
        foreach (var (r, mesh, baked) in parts)
        {
            var t = r.transform;

            // Wheel (spins) before hub (only steers): the wheel is usually under the hub.
            Transform parent = root;
            Vector3 lp = src.InverseTransformPoint(t.position);
            Quaternion lr = inv * t.rotation;
            foreach (var (from, to, undo, at) in spinning)
            {
                if (!t.IsChildOf(from)) continue;
                parent = to;
                lp = undo * (lp - at);
                lr = undo * lr;
                break;
            }
            var s = src.lossyScale;
            var ts = t.lossyScale;
            var scale = baked
                ? new Vector3(SafeDiv(1f, s.x), SafeDiv(1f, s.y), SafeDiv(1f, s.z)) // the bake already holds the scale
                : new Vector3(SafeDiv(ts.x, s.x), SafeDiv(ts.y, s.y), SafeDiv(ts.z, s.z));

            var go = new GameObject(t.name);
            var ct = go.transform;
            ct.SetParent(parent, false);
            ct.localPosition = lp;
            ct.localRotation = lr;
            ct.localScale = scale;
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var cr = go.AddComponent<MeshRenderer>();
            cr.shadowCastingMode = ShadowCastingMode.Off;
            cr.receiveShadows = false;
            if (material != null)
            {
                var src2 = r.sharedMaterials;
                int n = Math.Max(1, Math.Max(mesh.subMeshCount, src2 == null ? 0 : src2.Length));
                var mats = new Material[n];
                for (int k = 0; k < n; k++) mats[k] = material;
                cr.sharedMaterials = mats;
            }
            else cr.sharedMaterials = r.sharedMaterials;
            copied++;
        }
        if (copied == 0) throw new InvalidOperationException("the car has no meshes to copy");
    }

    private const int UiLayer = 5;

    private static bool Wanted(Renderer r, HashSet<IntPtr> skip)
    {
        if (r == null || !r.enabled || skip.Contains(r.Pointer)) return false;
        if (r.shadowCastingMode == ShadowCastingMode.ShadowsOnly) return false;
        var t = r.transform;
        return t.gameObject.layer != UiLayer && !IsText(r); // name tags and other labels
    }

    private static bool IsText(Renderer r)
    {
        try
        {
            string shader = r.sharedMaterial?.shader?.name ?? "";
            return shader.IndexOf("TextMeshPro", StringComparison.OrdinalIgnoreCase) >= 0
                || shader.IndexOf("TMP", StringComparison.Ordinal) >= 0
                || shader.IndexOf("Font", StringComparison.OrdinalIgnoreCase) >= 0;
        }
        catch (Exception ex) { DebugLog.Error("ghosts", ex, "GhostCar.IsText"); return false; }
    }

    private static float SafeDiv(float a, float b) => Mathf.Abs(b) < 1e-6f ? a : a / b;

    private static Transform NewChild(Transform parent, string name, Vector3 localPos, Quaternion localRot)
    {
        var t = new GameObject(name).transform;
        t.SetParent(parent, false);
        t.localPosition = localPos;
        t.localRotation = localRot;
        return t;
    }

    /// <summary>Moves the ghost to <paramref name="f"/>.</summary>
    public void Apply(GhostFrame f, int wheelCount)
    {
        if (_root == null) return;
        var root = _root.transform;
        root.SetPositionAndRotation(new Vector3(f.Px, f.Py, f.Pz), new Quaternion(f.Qx, f.Qy, f.Qz, f.Qw));
        int n = Math.Min(Math.Min(wheelCount, _wheels.Length), f.Wheels.Length);
        for (int i = 0; i < n; i++)
        {
            var p = _wheels[i];
            if (p == null) continue;
            var w = f.Wheels[i];
            var yaw = Quaternion.Euler(0f, w.Steer, 0f);
            if (p.Wheel != null)
            {
                p.Wheel.localPosition = new Vector3(p.WheelX, w.Suspension, p.WheelZ);
                p.Wheel.localRotation = yaw * Quaternion.Euler(w.Spin % 360f, 0f, 0f);
            }
            if (p.Hub != null)
            {
                p.Hub.localPosition = new Vector3(p.HubX, w.Suspension + p.HubDy, p.HubZ);
                p.Hub.localRotation = yaw;
            }
        }
        if (Math.Abs((GhostsConfig.Opacity?.Value ?? 0.35f) - _materialOpacity) > 0.001f) SetOpacity(GhostsConfig.Opacity.Value);
    }

    public void Destroy()
    {
        if (_root != null) Object.Destroy(_root);
        _root = null;
        foreach (var m in _baked)
            if (m != null) Object.Destroy(m);
        _baked.Clear();
        _wheels = Array.Empty<Pivot>();
    }

    // ---------------------------------------------------------------- the see-through material

    // The game ships these (URP): the first that loads is used. With none, the ghost is drawn with the car's own
    // materials (solid) and one log line says so.
    private static readonly string[] Shaders =
    {
        "Universal Render Pipeline/Unlit",
        "Universal Render Pipeline/Particles/Unlit",
        "Sprites/Default",
        "UI/Default",
    };

    private static Material Material(float opacity)
    {
        if (_material != null) return _material;
        if (_materialTried && _materialSolid) return null;
        _materialTried = true;
        foreach (string name in Shaders)
        {
            try
            {
                var shader = Shader.Find(name);
                if (shader == null || !shader.isSupported) continue;
                var m = new Material(shader) { name = "OTH Ghost", hideFlags = HideFlags.DontUnloadUnusedAsset };
                MakeTransparent(m);
                _material = m;
                SetOpacity(opacity);
                GhostsPlugin.Logger.LogInfo($"Ghost material uses \"{name}\".");
                return m;
            }
            catch (Exception e) { DebugLog.Error("ghosts", e, "GhostCar.Material"); GhostsPlugin.Logger.LogDebug($"Ghost shader {name}: {e.Message}"); }
        }
        _materialSolid = true;
        GhostsPlugin.Logger.LogWarning("No see-through shader found: ghosts are drawn with the car's own (solid) materials.");
        return null;
    }

    private static void MakeTransparent(Material m)
    {
        // URP's own property names; shaders without them ignore the calls.
        SetFloat(m, "_Surface", 1f);       // transparent
        SetFloat(m, "_Blend", 0f);         // alpha
        SetFloat(m, "_SrcBlend", (float)BlendMode.SrcAlpha);
        SetFloat(m, "_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
        SetFloat(m, "_SrcBlendAlpha", (float)BlendMode.One);
        SetFloat(m, "_DstBlendAlpha", (float)BlendMode.OneMinusSrcAlpha);
        SetFloat(m, "_ZWrite", 0f);
        SetFloat(m, "_Cull", (float)CullMode.Back);
        m.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
        m.DisableKeyword("_ALPHATEST_ON");
        m.SetOverrideTag("RenderType", "Transparent");
        m.renderQueue = (int)RenderQueue.Transparent;
    }

    private static void SetFloat(Material m, string name, float v)
    {
        if (m.HasProperty(name)) m.SetFloat(name, v);
    }

    private static void SetOpacity(float opacity)
    {
        _materialOpacity = opacity;
        if (_material == null) return;
        var c = new Color(Tint.r, Tint.g, Tint.b, Mathf.Clamp(opacity, 0.05f, 0.9f));
        if (_material.HasProperty("_BaseColor")) _material.SetColor("_BaseColor", c);
        if (_material.HasProperty("_Color")) _material.SetColor("_Color", c);
        if (_material.HasProperty("_TintColor")) _material.SetColor("_TintColor", c);
    }
}
