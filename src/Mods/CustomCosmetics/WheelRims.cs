using System;
using System.Collections.Generic;
using UnityEngine;
using Object = UnityEngine.Object;

namespace OverTheHillPlus.CustomCosmetics;

/// <summary>
/// The stock rims under a custom set of wheels. While any wheels part of a car instance is shown (the game switches its
/// objects on by the car's bitset, in the garage and for every player's car), that instance's own rim meshes draw an empty
/// mesh; when none is shown they get back the mesh they had. The renderers are left alone (the game's LOD and culling
/// switch them), and the tyres are never touched, so they keep their tread. Checked every frame: cheap (one flag per
/// part until one is found on), and it catches every way the game shows a part (mounting, preview, a car joining).
/// </summary>
internal static class WheelRims
{
    private sealed class Car
    {
        public Transform WheelMeshes;
        public readonly List<MeshFilter> Rims = new();
        public readonly List<Mesh> Saved = new();
        public readonly List<GameObject> Ours = new();
        public bool Hidden;
    }

    private static readonly Dictionary<IntPtr, Car> Cars = new();
    private static readonly List<IntPtr> Gone = new();
    private static Mesh _empty;

    public static int Count => Cars.Count;

    /// <summary>Registers one wheels part's objects on a car instance and the rims they replace (idempotent per rim).</summary>
    public static void Add(Transform wheelMeshes, List<MeshFilter> rims, List<GameObject> ours)
    {
        if (wheelMeshes == null) return;
        if (!Cars.TryGetValue(wheelMeshes.Pointer, out var car) || car.WheelMeshes == null)
            Cars[wheelMeshes.Pointer] = car = new Car { WheelMeshes = wheelMeshes };
        foreach (var r in rims)
        {
            if (r == null) continue;
            bool known = false;
            foreach (var k in car.Rims)
                if (k != null && k.Pointer == r.Pointer) { known = true; break; }
            if (known) continue;
            car.Rims.Add(r);
            car.Saved.Add(null);
        }
        car.Ours.AddRange(ours);
    }

    private static Mesh Empty()
    {
        if (_empty != null) return _empty;
        _empty = new Mesh { name = "cc_no_rim" };
        _empty.hideFlags = HideFlags.DontUnloadUnusedAsset;
        Object.DontDestroyOnLoad(_empty);
        return _empty;
    }

    public static void Tick()
    {
        if (Cars.Count == 0) return;
        Gone.Clear();
        foreach (var kv in Cars)
        {
            var car = kv.Value;
            if (car.WheelMeshes == null) { Gone.Add(kv.Key); continue; }
            bool on = false;
            foreach (var go in car.Ours)
                if (go != null && go.activeSelf) { on = true; break; }
            if (on == car.Hidden) continue;
            Apply(car, on);
        }
        foreach (var k in Gone) Cars.Remove(k);
    }

    private static void Apply(Car car, bool hide)
    {
        try
        {
            var empty = Empty();
            for (int i = 0; i < car.Rims.Count; i++)
            {
                var mf = car.Rims[i];
                if (mf == null) continue;
                if (hide)
                {
                    // What it draws now (the donor's rim, or the modded car's), kept for when the wheels come off.
                    var now = mf.sharedMesh;
                    if (now != null && now.Pointer != empty.Pointer) car.Saved[i] = now;
                    mf.sharedMesh = empty;
                }
                else if (car.Saved[i] != null) mf.sharedMesh = car.Saved[i];
            }
            car.Hidden = hide;
            DebugLog.Info(CustomCosmeticsPlugin.ModId, $"{car.WheelMeshes.root.name}: stock rims {(hide ? "hidden under custom wheels" : "back")} ({car.Rims.Count} rim meshes)");
        }
        catch (Exception e)
        {
            car.Hidden = hide; // don't retry every frame
            DebugLog.Error(CustomCosmeticsPlugin.ModId, e, "WheelRims.Apply");
        }
    }
}

/// <summary>
/// Lower detail for the parts far away: every part object that has a LOD1 draws it beyond
/// <see cref="PartLodRule.Lod1Distance"/> from the camera, swapped twice a second (no LODGroup: its LOD structs don't
/// cross into the game safely).
/// </summary>
internal static class PartLods
{
    private sealed class Entry
    {
        public GameObject Go;
        public MeshFilter Mf;
        public Mesh Full, Low;
        public bool LowNow;
    }

    private static readonly List<Entry> All = new();
    private static readonly List<Vector3> Eyes = new();
    private static float _next;

    public static void Add(GameObject go, MeshFilter mf, Mesh full, Mesh low)
    {
        if (go == null || mf == null || full == null || low == null) return;
        All.Add(new Entry { Go = go, Mf = mf, Full = full, Low = low });
    }

    /// <summary>The object behind <paramref name="mf"/> draws <paramref name="to"/> at full detail from now on instead of
    /// <paramref name="from"/> (a twinkling part without its bulbs).</summary>
    public static void SwapFull(MeshFilter mf, Mesh from, Mesh to)
    {
        if (mf == null || from == null || to == null) return;
        foreach (var e in All)
            if (e.Mf != null && e.Mf.Pointer == mf.Pointer && e.Full != null && e.Full.Pointer == from.Pointer) e.Full = to;
    }

    public static void Tick()
    {
        if (All.Count == 0 || Time.unscaledTime < _next) return;
        _next = Time.unscaledTime + 0.5f;
        try
        {
            // Every camera that draws, not only Camera.main: the garage and photo mode draw through other cameras while the
            // main one waits far away, which left the parts on their LOD1 right in front of the garage camera.
            Eyes.Clear();
            foreach (var cam in Camera.allCameras)
                if (cam != null && cam.isActiveAndEnabled) Eyes.Add(cam.transform.position);
            if (Eyes.Count == 0) return;
            for (int i = All.Count - 1; i >= 0; i--)
            {
                var e = All[i];
                if (e.Go == null || e.Mf == null) { All.RemoveAt(i); continue; }
                if (!e.Go.activeInHierarchy) continue;
                var at = e.Go.transform.position;
                float near = float.MaxValue;
                foreach (var eye in Eyes) near = Mathf.Min(near, (at - eye).sqrMagnitude);
                bool low = PartLodRule.UseLod1(near, e.LowNow);
                if (low == e.LowNow) continue;
                e.Mf.sharedMesh = low ? e.Low : e.Full;
                e.LowNow = low;
            }
        }
        catch (Exception e) { DebugLog.Error(CustomCosmeticsPlugin.ModId, e, "PartLods.Tick"); }
    }
}
