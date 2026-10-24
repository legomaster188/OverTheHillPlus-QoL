using System;
using System.Collections.Generic;
using UnityEngine;
using Object = UnityEngine.Object;

namespace OverTheHillPlus.Cars;

/// <summary>
/// The visible extra rear axles of a tandem (see <see cref="CarTandem"/>). For each rim and tyre mesh object of a rear
/// physical wheel (the donor's own, already given the model's rear meshes by <see cref="CarBody"/>) every extra axle gets
/// a follower: a plain object with the same mesh, material and layer, under <c>Axles/WheelMeshes</c>. Each frame it takes
/// the physical wheel's world rotation and its position moved along the car by the axle's offset, so it turns, bounces
/// and steers exactly as the physical wheel does. The physical wheel's own mesh is emptied (the followers draw every
/// axle), but its renderer keeps its place in the donor's LOD group, so a follower copies its <c>enabled</c> flag and the
/// wheel is culled and shown with the rest of the car.
/// <para>No component of ours is added to the car (the framework injects none), and nothing in a follower reacts to
/// physics: it is only drawn. Followers are made on the template and on the garage model, so every spawned copy has them;
/// <see cref="Tick"/> finds the live cars once a second and moves their followers every frame.</para>
/// </summary>
internal static class CarTandemWheels
{
    private sealed class Follower
    {
        public Transform Src, Self, Root;
        public MeshRenderer SrcRenderer, SelfRenderer;
        public float Dz;
    }

    private static readonly List<Follower> Live = new();
    private static readonly HashSet<IntPtr> Known = new();
    private static float _nextScan;

    /// <summary>
    /// Gives the rear wheel mesh objects <paramref name="drawn"/> of one physical rear wheel a follower per extra axle and
    /// empties their meshes. <paramref name="wheelMeshes"/> is <c>Axles/WheelMeshes</c>, <paramref name="rt"/> the car root.
    /// Returns how many followers were made.
    /// </summary>
    public static int Add(Transform rt, Transform wheelMeshes, IReadOnlyList<MeshFilter> drawn, float[] axleOffsets)
    {
        int made = 0;
        for (int a = 0; a < axleOffsets.Length; a++)
        {
            foreach (var mf in drawn)
            {
                if (mf == null || mf.sharedMesh == null) continue;
                var src = mf.transform;
                var srcRenderer = mf.GetComponent<MeshRenderer>();
                var go = new GameObject(CarTandem.FollowerName(a, PathBelow(wheelMeshes, src)));
                go.layer = mf.gameObject.layer;
                var t = go.transform;
                t.SetParent(wheelMeshes, false);
                t.localScale = Vector3.one;
                var f = go.AddComponent<MeshFilter>();
                f.sharedMesh = mf.sharedMesh;
                var r = go.AddComponent<MeshRenderer>();
                if (srcRenderer != null)
                {
                    r.sharedMaterials = srcRenderer.sharedMaterials;
                    r.shadowCastingMode = srcRenderer.shadowCastingMode;
                    r.receiveShadows = srcRenderer.receiveShadows;
                    r.enabled = srcRenderer.enabled;
                }
                // match the physical mesh object's size as the world sees it (its parents may be scaled)
                var ls = src.lossyScale;
                t.localScale = new Vector3(Safe(ls.x / Safe(wheelMeshes.lossyScale.x)), Safe(ls.y / Safe(wheelMeshes.lossyScale.y)),
                    Safe(ls.z / Safe(wheelMeshes.lossyScale.z)));
                Move(rt, src, t, axleOffsets[a]);
                made++;
            }
        }
        // the followers draw every axle: the physical wheel's meshes stay in the hierarchy (LOD group, enabled flags) but empty
        foreach (var mf in drawn)
            if (mf != null) mf.sharedMesh = null;
        return made;
    }

    private static float Safe(float v) => Mathf.Abs(v) < 1e-4f ? 1f : v;

    private static string PathBelow(Transform root, Transform t)
    {
        var parts = new List<string>();
        for (var c = t; c != null && c != root; c = c.parent) parts.Add(c.name);
        parts.Reverse();
        return string.Join("/", parts);
    }

    /// <summary>The follower's pose: the physical wheel's rotation, its position moved <paramref name="dz"/> metres along the car.</summary>
    private static void Move(Transform rt, Transform src, Transform self, float dz)
    {
        self.SetPositionAndRotation(src.position + rt.TransformVector(new Vector3(0f, 0f, dz)), src.rotation);
    }

    // ---------------------------------------------------------------- per frame

    /// <summary>Called every frame by the framework's shared tick; costs nothing while no tandem car exists.</summary>
    public static void Tick()
    {
        float now = Time.unscaledTime;
        if (now >= _nextScan)
        {
            _nextScan = now + 1f;
            try { Scan(); }
            catch (Exception e) { DebugLog.Error("core", e, "CarTandemWheels.Scan"); }
        }
        for (int i = Live.Count - 1; i >= 0; i--)
        {
            var f = Live[i];
            if (f.Self == null || f.Src == null || f.Root == null)
            {
                Live.RemoveAt(i);
                continue;
            }
            try
            {
                Move(f.Root, f.Src, f.Self, f.Dz);
                if (f.SrcRenderer != null && f.SelfRenderer != null)
                    f.SelfRenderer.enabled = f.SrcRenderer.enabled && f.Src.gameObject.activeInHierarchy;
            }
            catch (Exception e) { DebugLog.Error("core", e, "CarTandemWheels.Tick"); Live.RemoveAt(i); }
        }
    }

    /// <summary>Finds the followers of every live car not seen yet (their offsets are in the model, read once per car).</summary>
    private static void Scan()
    {
        var seen = new HashSet<IntPtr>();
        foreach (var fp in GameState.FusionPlayers)
        {
            if (fp == null) continue;
            var root = fp.transform;
            var wm = root.Find("Axles/WheelMeshes");
            if (wm == null) continue;
            seen.Add(root.Pointer);
            if (!Known.Add(root.Pointer)) continue;
            foreach (var car in ModdedCars.All)
            {
                if (!car.Catalog.IsThis(fp.GetComponent<VehicleCustomizationController>())) continue;
                var blob = car.Assets.Blob;
                var offsets = CarTandem.AxleOffsets(blob);
                if (offsets.Length == 0) break;
                for (int i = 0; i < wm.childCount; i++)
                {
                    var c = wm.GetChild(i);
                    if (!CarTandem.TryParse(c.name, out int axle, out string path) || axle >= offsets.Length) continue;
                    var src = wm.Find(path);
                    if (src == null) continue;
                    Live.Add(new Follower
                    {
                        Src = src, Self = c, Root = root, Dz = offsets[axle],
                        SrcRenderer = src.GetComponent<MeshRenderer>(), SelfRenderer = c.GetComponent<MeshRenderer>(),
                    });
                }
                break;
            }
        }
        Known.RemoveWhere(p => !seen.Contains(p));
    }
}
