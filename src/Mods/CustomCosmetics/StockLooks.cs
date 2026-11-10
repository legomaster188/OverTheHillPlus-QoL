using System;
using System.Collections.Generic;
using UnityEngine;
using Object = UnityEngine.Object;

namespace OverTheHillPlus.CustomCosmetics;

/// <summary>
/// The game's parts drawn with our shape (<see cref="PartManifest.Replaces"/>): the game's objects stay where they are and
/// keep being switched by the car's bitset, but their meshes draw nothing; our object is shown exactly while any of them is
/// (checked every frame, like <see cref="WheelRims"/>: it catches mounting, the garage preview and a car joining).
/// </summary>
internal static class StockLooks
{
    private sealed class Look
    {
        public readonly List<GameObject> ShownBy = new();
        public GameObject Ours;
    }

    private static readonly List<Look> Looks = new();
    private static readonly Dictionary<IntPtr, Transform> Finished = new();
    private static Mesh _empty;

    public static int Count => Looks.Count;

    /// <summary>Has this controller instance (its cosmetics root) got its looks already?</summary>
    public static bool Done(Transform cosmetics)
    {
        if (cosmetics == null) return true;
        if (!Finished.TryGetValue(cosmetics.Pointer, out var t)) return false;
        if (t != null) return true;
        Finished.Remove(cosmetics.Pointer);
        return false;
    }

    public static void MarkDone(Transform cosmetics)
    {
        if (cosmetics != null) Finished[cosmetics.Pointer] = cosmetics;
    }

    private static Mesh Empty()
    {
        if (_empty != null) return _empty;
        _empty = new Mesh { name = "cc_no_mesh" };
        _empty.hideFlags = HideFlags.DontUnloadUnusedAsset;
        Object.DontDestroyOnLoad(_empty);
        return _empty;
    }

    /// <summary>Registers our object for the game objects that show the part, and empties their meshes. -> meshes emptied.</summary>
    public static int Add(Transform cosmetics, List<GameObject> shownBy, GameObject ours)
    {
        var look = new Look { Ours = ours };
        int n = 0;
        var empty = Empty();
        foreach (var go in shownBy)
        {
            if (go == null) continue;
            look.ShownBy.Add(go);
            foreach (var mf in go.GetComponentsInChildren<MeshFilter>(true))
            {
                if (mf == null || mf.gameObject == ours) continue;
                mf.sharedMesh = empty;
                n++;
            }
            foreach (var sk in go.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (sk == null) continue;
                sk.sharedMesh = empty;
                n++;
            }
        }
        Looks.Add(look);
        Apply(look);
        return n;
    }

    private static bool Apply(Look look)
    {
        bool alive = false, on = false;
        foreach (var go in look.ShownBy)
        {
            if (go == null) continue;
            alive = true;
            if (go.activeInHierarchy) { on = true; break; }
        }
        if (!alive || look.Ours == null) return false;
        if (look.Ours.activeSelf != on) look.Ours.SetActive(on);
        return true;
    }

    public static void Tick()
    {
        if (Looks.Count == 0) return;
        for (int i = Looks.Count - 1; i >= 0; i--)
        {
            if (Apply(Looks[i])) continue;
            if (Looks[i].Ours != null) Object.Destroy(Looks[i].Ours);
            Looks.RemoveAt(i);
        }
    }
}
