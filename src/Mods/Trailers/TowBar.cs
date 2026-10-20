using System;
using System.Collections.Generic;
using Fusion;
using UnityEngine;

namespace OverTheHillPlus.Trailers;

/// <summary>
/// Finds a car's mounted tow bar (the game's cosmetic item <c>TowBar</c> under the rear bumper slot, active only while
/// mounted) and the hitch ball on it. Read from the live object, so it follows wherever the car framework placed the
/// slot on a modded car, and a car that dismounts the bar loses its hitch at once.
/// </summary>
internal static class TowBar
{
    private static readonly Dictionary<uint, (float at, bool mounted)> Seen = new();

    /// <summary>True when the car has its tow bar mounted; <paramref name="ball"/> is the hitch ball in the car's space.</summary>
    public static bool Locate(NetworkObject car, out Vector3 ball)
    {
        ball = default;
        if (car == null) return false;
        try
        {
            Transform bar = null;
            foreach (var t in car.GetComponentsInChildren<Transform>(true))
            {
                if (t == null || t.name != TowHitch.PartName) continue;
                if (t.gameObject.activeInHierarchy) { bar = t; break; }
            }
            if (bar == null) return false;
            var ct = car.transform;
            MeshFilter best = null;
            foreach (var mf in bar.GetComponentsInChildren<MeshFilter>(false))
            {
                if (mf == null || mf.sharedMesh == null) continue;
                if (best == null || mf.name.EndsWith("LOD0", StringComparison.Ordinal)) best = mf;
            }
            if (best == null)
            {
                ball = ct.InverseTransformPoint(bar.position);
                return true;
            }
            var b = best.sharedMesh.bounds;
            Vector3 lo = new(float.MaxValue, float.MaxValue, float.MaxValue), hi = -lo;
            for (int i = 0; i < 8; i++)
            {
                var corner = b.center + new Vector3((i & 1) == 0 ? -b.extents.x : b.extents.x,
                    (i & 2) == 0 ? -b.extents.y : b.extents.y, (i & 4) == 0 ? -b.extents.z : b.extents.z);
                var p = ct.InverseTransformPoint(best.transform.TransformPoint(corner));
                lo = Vector3.Min(lo, p);
                hi = Vector3.Max(hi, p);
            }
            var v = TowHitch.BallFromBounds(lo.x, lo.y, lo.z, hi.x, hi.y, hi.z);
            ball = new Vector3(v.x, v.y, v.z);
            return true;
        }
        catch (Exception ex) { DebugLog.Error("trailers", ex, "TowBar.Locate"); return false; }
    }

    /// <summary>For the menu, which asks every frame: <see cref="Locate"/>'s answer, at most twice a second per car.</summary>
    public static bool Mounted(NetworkObject car)
    {
        if (car == null) return false;
        uint id;
        try { id = car.Id.Raw; } catch (Exception ex) { DebugLog.Error("trailers", ex, "TowBar.Mounted"); return false; }
        if (Seen.TryGetValue(id, out var s) && Time.unscaledTime - s.at < 0.5f) return s.mounted;
        bool m = Locate(car, out _);
        Seen[id] = (Time.unscaledTime, m);
        return m;
    }
}
