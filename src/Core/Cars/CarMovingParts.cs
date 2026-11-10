using System;
using System.Collections.Generic;
using UnityEngine;
using Object = UnityEngine.Object;

namespace OverTheHillPlus.Cars;

/// <summary>
/// A vehicle's moving parts (<see cref="MeshBlob.MovingPrefix"/>: a snowmobile's skis, A-arms, handlebars and track lugs, a
/// boat's outboard), each on its own object under <see cref="HolderName"/> on the car's root, at its pivot (the model's
/// <c>pivot_&lt;name&gt;</c> anchor, else the middle of the part), drawn with the car's material. Made on the networked
/// template and the garage model before either wakes (from the car mod's <see cref="CarHooks.AfterModel"/>), so every
/// spawned copy has them and the game's property blocks (livery, dirt, snow, damage, lights) reach them like the body.
/// The car's own mod moves them each frame; on the garage model they stay at rest.
/// </summary>
internal static class CarMovingParts
{
    public const string HolderName = "OTHP_Moving";

    /// <summary>Puts every moving part of the model on <paramref name="root"/>. Returns how many; 0 for a model without any.</summary>
    public static int Build(GameObject root, CarModelPlace place, string carName)
    {
        if (root == null || place?.Blob == null) return 0;
        var parts = place.Blob.MovingParts();
        if (parts.Count == 0) return 0;
        var rt = root.transform;
        var old = rt.Find(HolderName);
        if (old != null) Object.DestroyImmediate(old.gameObject);
        var holder = new GameObject(HolderName);
        holder.transform.SetParent(rt, false);
        int layer = BodyLayer(root);
        holder.layer = layer;
        int made = 0;
        foreach (var p in parts)
        {
            var pivot = PivotOf(place.Blob, p);
            var mesh = CarAssets.BuildMesh(carName + "_" + p.Name, new List<MeshPart> { p },
                Matrix4x4.Translate(new Vector3(-pivot.X, -pivot.Y, -pivot.Z)));
            if (mesh == null) continue;
            var go = new GameObject(p.Name) { layer = layer };
            go.transform.SetParent(holder.transform, false);
            go.transform.localPosition = place.ToRoot(pivot);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var r = go.AddComponent<MeshRenderer>();
            if (place.Material != null) r.sharedMaterial = place.Material;
            made++;
        }
        return made;
    }

    /// <summary>The part's pivot in model space: its <c>pivot_</c> anchor, else the middle of its bounds.</summary>
    public static MeshVec3 PivotOf(MeshBlob blob, MeshPart p)
    {
        var a = blob?.Anchor(MeshBlob.PivotOf(p.Name));
        if (a != null) return a.Position;
        p.Bounds(out var lo, out var hi);
        return new MeshVec3((lo.X + hi.X) * 0.5f, (lo.Y + hi.Y) * 0.5f, (lo.Z + hi.Z) * 0.5f);
    }

    /// <summary>A moving part's object on a car (or null).</summary>
    public static Transform Find(Transform carRoot, string partName)
    {
        if (carRoot == null) return null;
        var h = carRoot.Find(HolderName);
        return h != null ? h.Find(partName) : null;
    }

    /// <summary>
    /// Copies of a moving part (a track lug: one modelled, dozens drawn), named "&lt;part&gt;#&lt;k&gt;" (k from 1), made
    /// next to it on the template and the garage model. Returns the part and its copies in order (k = 0 is the part).
    /// </summary>
    public static List<Transform> Copies(Transform carRoot, string partName, int count)
    {
        var list = new List<Transform>();
        var src = Find(carRoot, partName);
        if (src == null || count < 1) return list;
        list.Add(src);
        for (int k = 1; k < count; k++)
        {
            string name = partName + "#" + k.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var have = src.parent.Find(name);
            if (have == null)
            {
                var go = Object.Instantiate(src.gameObject, src.parent, false);
                go.name = name;
                have = go.transform;
            }
            list.Add(have);
        }
        return list;
    }

    /// <summary>The layer the car's body renderers are on (moving parts share it: the game's cameras and probes treat
    /// them as the body).</summary>
    private static int BodyLayer(GameObject root)
    {
        try
        {
            var parts = root.transform.Find("Parts");
            if (parts != null)
                foreach (var r in parts.GetComponentsInChildren<MeshRenderer>(true))
                    if (r != null && CarBody.BodyLodAt(r.name) >= 0) return r.gameObject.layer;
        }
        catch (Exception ex) { DebugLog.Error("core", ex, "CarMovingParts.BodyLayer"); }
        return root.layer;
    }
}
