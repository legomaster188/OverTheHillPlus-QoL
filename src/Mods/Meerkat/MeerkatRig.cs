using System;
using System.Collections.Generic;
using OverTheHillPlus.Cars;
using UnityEngine;
#if MELON
using Il2CppVehiclePhysics;
#else
using VehiclePhysics;
#endif

namespace OverTheHillPlus.Meerkat;

/// <summary>
/// The Meerkat on the networked template and the garage model (core's <see cref="CarHooks"/>): its optional and moving
/// parts on their own objects (core's <see cref="CarMovingParts"/>: the pods, the tops, the rack, the kit), the other tyre
/// meshes made next to each wheel's own (so a build can swap them in), rear-wheel drive on the template and its suspension
/// travel read for the lift stages.
/// </summary>
internal static class MeerkatRig
{
    /// <summary>How far the pods turn up on their hinges when the headlamps are on, in degrees, front first (the model:
    /// meerkat_addons.POD_OPEN; each pod's pivot is its hinge along the lid's top rear edge).</summary>
    public const float PodOpenDegrees = 50f;

    private static MeshBlob _blob;

    /// <summary>Per tyre and rim mesh core drew on a wheel: the meshes of every tread and size, made in that wheel's frame
    /// (index <see cref="Slot"/>; a rim only differs on the 14 in size), and the reverse: which wheel mesh each of them
    /// stands for.</summary>
    internal static readonly Dictionary<IntPtr, Mesh[]> Variants = new();
    internal static readonly Dictionary<IntPtr, IntPtr> BaseOf = new();

    private const int Sizes = 3;

    public static int Slot(TyreKind t, TyreSize s) => (int)t * Sizes + (int)s;

    public static string Prepare(MeshBlob blob)
    {
        if (blob == null) return "no model";
        var missing = new List<string>();
        foreach (var n in new[] { MeerkatBuild.PodL, MeerkatBuild.PodR })
            if (blob.Part(n) == null) missing.Add(n);
        foreach (var n in MeerkatBuild.OptionalParts)
            if (blob.Part(n) == null) missing.Add(n);
        foreach (var n in new[] { MeerkatTyres.StreetPart, MeerkatTyres.MudPart })
            if (blob.Part(n) == null) missing.Add(n);
        if (missing.Count > 0) return "its model has no " + string.Join(", ", missing);
        _blob = blob;
        return null;
    }

    public static string AfterModel(GameObject root, bool template, CarModelPlace place)
    {
        int made = CarMovingParts.Build(root, place, MeerkatDefinition.CarName);
        if (made == 0) return "the Meerkat's model has no moving parts";
        try { MakeTyreVariants(root, place); }
        catch (Exception e)
        {
            DebugLog.Error(MeerkatPlugin.ModId, e, "MeerkatRig.MakeTyreVariants");
            MeerkatPlugin.LogOnce("tyres", "the other tyres could not be made: " + e.Message);
        }
        // as built (the safari build, the pods down) until the first frame shows the car's own build
        MeerkatLive.ApplyParts(root.transform, MeerkatBuild.Safari.PartsShown());
        if (template)
            MeerkatPlugin.Logger?.LogInfo($"[Meerkat] rig: {made} moving and optional parts, {Variants.Count} tyre mesh(es) with their other treads.");
        return null;
    }

    /// <summary>The template is complete (tuned): rear-wheel drive, and the travel the lift stages are worked out from.</summary>
    public static string AfterTemplate(GameObject template)
    {
        try { RearWheelDrive(template); }
        catch (Exception e)
        {
            DebugLog.Error(MeerkatPlugin.ModId, e, "MeerkatRig.RearWheelDrive");
            MeerkatPlugin.LogOnce("rwd", "rear-wheel drive could not be set (it drives all four): " + e.Message);
        }
        try
        {
            float sum = 0f;
            int n = 0;
            foreach (var wc in template.GetComponentsInChildren<VPFunselektor3DRaycastWheelCollider>(true))
            {
                if (wc == null || !(wc.suspensionTravel > 0f)) continue;
                sum += wc.suspensionTravel;
                n++;
            }
            if (n > 0) MeerkatCareer.TravelKnown(sum / n);
        }
        catch (Exception e) { DebugLog.Error(MeerkatPlugin.ModId, e, "MeerkatRig.travel"); }
        return null;
    }

    /// <summary>One driven axle, the rear one (the axle whose wheels sit furthest back): VPP's driveline is set up from these
    /// settings when the car wakes, on every peer the same.</summary>
    private static void RearWheelDrive(GameObject template)
    {
        var vp = template.GetComponent<VPVehicleController>();
        if (vp == null) return;
        var axles = vp.axles;
        if (axles == null || axles.Length < 2) return;
        var rt = template.transform;
        int rear = -1;
        float best = float.MaxValue;
        for (int i = 0; i < axles.Length; i++)
        {
            var a = axles[i];
            var w = a?.leftWheel;
            if (w == null) continue;
            float z = rt.InverseTransformPoint(w.transform.position).z;
            if (z < best) { best = z; rear = i; }
        }
        if (rear < 0) return;
        var d = vp.driveline;
        if (d == null) return;
        d.drivenAxles = Driveline.DrivenAxles.SingleAxle;
        d.firstDrivenAxle = rear;
        MeerkatPlugin.Logger?.LogInfo($"[Meerkat] rear-wheel drive: axle {rear} of {axles.Length} driven.");
    }

    // ---------------------------------------------------------------- tyres

    private static Transform WheelFrame(Transform mesh, Transform corner)
    {
        for (var t = mesh.parent; t != null && t != corner.parent; t = t.parent)
            if (t.GetComponent<LODGroup>() != null) return t;
        return corner;
    }

    private static bool IsTyre(string n) => n.IndexOf("tyre", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                             n.IndexOf("tire", StringComparison.OrdinalIgnoreCase) >= 0;

    private static bool IsRim(string n) => !IsTyre(n) && n.IndexOf("wheel", StringComparison.OrdinalIgnoreCase) >= 0;

    private static bool HasTyre(Transform corner)
    {
        foreach (var mf in corner.GetComponentsInChildren<MeshFilter>(true))
            if (mf != null && IsTyre(mf.name)) return true;
        return false;
    }

    private static MeshPart Resized(MeshPart p, string tag, float[] positions)
    {
        return new MeshPart
        {
            Name = p.Name + tag, Kind = p.Kind, Position = p.Position, Rotation = p.Rotation, Scale = p.Scale,
            Positions = positions, Normals = p.Normals, Uv0 = p.Uv0, Indices = p.Indices,
        };
    }

    private static MeshPart Stretched(MeshPart p) =>
        Resized(p, "_big", MeerkatTyres.Stretch(p.Positions, MeerkatBuild.ModelTyreRadius, MeerkatBuild.BigTyreRadius));

    private static MeshPart StockTyre(MeshPart p) =>
        Resized(p, "_14", MeerkatTyres.TyreToStock(p.Positions, MeerkatBuild.ModelTyreRadius, MeerkatBuild.StockTyreRadius));

    private static void MakeTyreVariants(GameObject root, CarModelPlace place)
    {
        var blob = place.Blob ?? _blob;
        var wm = root.transform.Find("Axles/WheelMeshes");
        if (wm == null || blob == null) return;
        var parts = new MeshPart[3];
        parts[(int)TyreKind.AllTerrain] = blob.Pick(MeshPartKind.Tyre, MeshBlob.TyreName);
        parts[(int)TyreKind.Street] = blob.Part(MeerkatTyres.StreetPart);
        parts[(int)TyreKind.Mud] = blob.Part(MeerkatTyres.MudPart);
        var big = new MeshPart[3];
        var small = new MeshPart[3];
        for (int k = 0; k < 3; k++)
        {
            big[k] = parts[k] != null ? Stretched(parts[k]) : null;
            small[k] = parts[k] != null ? StockTyre(parts[k]) : null;
        }
        var rim = blob.Pick(MeshPartKind.Wheel, MeshBlob.WheelName);
        var smallRim = rim != null ? Resized(rim, "_14", MeerkatTyres.RimToStock(rim.Positions)) : null;
        foreach (var mf in wm.GetComponentsInChildren<MeshFilter>(true))
        {
            if (mf == null || mf.sharedMesh == null) continue;
            bool tyre = IsTyre(mf.name);
            if (!tyre && !IsRim(mf.name)) continue;
            var orig = mf.sharedMesh;
            if (Variants.ContainsKey(orig.Pointer)) continue;
            Transform corner = mf.transform;
            while (corner.parent != null && corner.parent != wm) corner = corner.parent;
            var frame = WheelFrame(mf.transform, corner);
            var toLocal = mf.transform.worldToLocalMatrix * frame.localToWorldMatrix;
            var set = new Mesh[3 * Sizes];
            if (tyre)
                for (int k = 0; k < 3; k++)
                {
                    var t = (TyreKind)k;
                    set[Slot(t, TyreSize.Model)] = k == (int)TyreKind.AllTerrain ? orig
                        : parts[k] != null ? CarAssets.BuildMesh(MeerkatDefinition.CarName + "_tyre_" + k, new List<MeshPart> { parts[k] }, toLocal) : orig;
                    set[Slot(t, TyreSize.Big)] = big[k] != null
                        ? CarAssets.BuildMesh(MeerkatDefinition.CarName + "_tyre_" + k + "_big", new List<MeshPart> { big[k] }, toLocal) : set[Slot(t, TyreSize.Model)];
                    set[Slot(t, TyreSize.Small)] = small[k] != null
                        ? CarAssets.BuildMesh(MeerkatDefinition.CarName + "_tyre_" + k + "_14", new List<MeshPart> { small[k] }, toLocal) : set[Slot(t, TyreSize.Model)];
                }
            else
            {
                // A donor that draws rim and tyre as one mesh (the Brick doesn't) keeps it: the 14 in rim alone has no tyre.
                var r14 = smallRim != null && HasTyre(corner)
                    ? CarAssets.BuildMesh(MeerkatDefinition.CarName + "_rim_14", new List<MeshPart> { smallRim }, toLocal) : orig;
                for (int k = 0; k < 3; k++)
                {
                    var t = (TyreKind)k;
                    set[Slot(t, TyreSize.Model)] = set[Slot(t, TyreSize.Big)] = orig;
                    set[Slot(t, TyreSize.Small)] = r14;
                }
            }
            Variants[orig.Pointer] = set;
            foreach (var m in set)
                if (m != null) BaseOf[m.Pointer] = orig.Pointer;
        }
    }
}
