using System;
using System.Collections.Generic;
using UnityEngine;
using VehiclePhysics;

namespace OverTheHillPlus;

/// <summary>
/// Puts a car back on its wheels a little above the ground under it, the way the game's own (developer console)
/// reset-player RPC does it (decoded with Cpp2IL): detach the winches, then VehicleBase.Reposition to the car's spot
/// raised a bit, keeping only its heading. The height comes from a ground raycast (skipping the car itself, moving
/// bodies and overhangs more than 2 m above it), so a car that sank into the terrain comes back out on top.
/// In core because more than one mod resets cars (Driving QoL's unstuck, and others).
/// </summary>
internal static class CarReset
{
    private const float Lift = 1.2f;
    private static readonly HashSet<string> LoggedOnce = new();

    /// <summary>The game's reset, with a ground raycast. Caller checks authority.</summary>
    internal static bool Reset(VehicleBase car, out string why)
    {
        why = null;
        try
        {
            if (car == null) { why = "no car"; return false; }
            var t = car.cachedTransform != null ? car.cachedTransform : car.transform;
            if (t == null) { why = "no car"; return false; }
            Vector3 here = t.position;
            if (!IsFinite(here)) { why = "the car's position is invalid"; return false; }

            Vector3 fwd = Vector3.ProjectOnPlane(t.forward, Vector3.up);
            if (fwd.sqrMagnitude < 0.01f) fwd = Vector3.ProjectOnPlane(-t.up, Vector3.up); // nose straight up or down
            if (fwd.sqrMagnitude < 0.01f) fwd = Vector3.forward;
            var rot = Quaternion.LookRotation(fwd.normalized, Vector3.up);

            Vector3 target = GroundUnder(here, car, out bool found);
            target = found ? target + Vector3.up * Lift : here + Vector3.up * 1.5f;

            // Same order as the game: let go of winch anchors first, or the rope yanks the car back.
            try
            {
                var winch = car.GetComponentInChildren<VehicleWinchSystem>();
                if (winch != null) winch.DetachAllWinches();
            }
            catch (Exception e) { DebugLog.Error("core", e, "CarReset.Reset"); LogOnce("winch", "[Car reset] detach winches: " + e.Message); }

            car.Reposition(target, rot);

            try
            {
                var rb = car.cachedRigidbody;
                if (rb != null && !rb.isKinematic)
                {
                    rb.linearVelocity = Vector3.zero;
                    rb.angularVelocity = Vector3.zero;
                }
            }
            catch (Exception e) { DebugLog.Error("core", e, "CarReset.Reset"); LogOnce("stop", "[Car reset] stop car: " + e.Message); }
            return true;
        }
        catch (Exception e)
        {
            DebugLog.Error("core", e, "CarReset.Reset");
            why = e.Message;
            LogOnce("reset", "[Car reset] reset failed: " + e);
            return false;
        }
    }

    /// <summary>
    /// The highest walkable surface at most 2 m above <paramref name="p"/> (so a roof, bridge or tree canopy above the
    /// car is skipped), ignoring triggers, moving bodies and the car itself; a point under the terrain counts from
    /// the terrain surface. <paramref name="found"/> is false when nothing was hit and no terrain covers the spot.
    /// </summary>
    private static Vector3 GroundUnder(Vector3 p, VehicleBase car, out bool found)
    {
        found = false;
        float refY = p.y;
        bool hasTerrain = TryTerrainHeight(p, out float terrainY);
        if (hasTerrain && terrainY > refY) refY = terrainY;

        float best = float.NegativeInfinity;
        try
        {
            Rigidbody own = null;
            Transform root = null;
            try { own = car.cachedRigidbody; root = car.transform; } catch (Exception ex) { DebugLog.Error("core", ex, "CarReset.GroundUnder"); }
            var hits = Physics.RaycastAll(new Vector3(p.x, refY + 30f, p.z), Vector3.down, 120f, ~0, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < hits.Length; i++)
            {
                var h = hits[i];
                if (h.normal.y < 0.35f) continue; // walls, cliffs
                var rb = h.rigidbody;
                if (rb != null && ((own != null && rb.Pointer == own.Pointer) || !rb.isKinematic)) continue;
                var col = h.collider;
                if (root != null && col != null && col.transform.IsChildOf(root)) continue;
                float y = h.point.y;
                if (y <= refY + 2f && y > best) best = y;
            }
        }
        catch (Exception e) { DebugLog.Error("core", e, "CarReset.GroundUnder"); LogOnce("ray", "[Car reset] ground raycast: " + e.Message); }

        if (float.IsNegativeInfinity(best))
        {
            if (!hasTerrain) return p;
            best = terrainY;
        }
        found = true;
        return new Vector3(p.x, best, p.z);
    }

    private static bool TryTerrainHeight(Vector3 p, out float height)
    {
        height = 0f;
        try
        {
            foreach (var t in Terrain.activeTerrains)
            {
                if (t == null || t.terrainData == null) continue;
                var o = t.GetPosition();
                var size = t.terrainData.size;
                if (p.x < o.x || p.z < o.z || p.x > o.x + size.x || p.z > o.z + size.z) continue;
                height = t.SampleHeight(p) + o.y;
                return true;
            }
        }
        catch (Exception e) { DebugLog.Error("core", e, "CarReset.TryTerrainHeight"); LogOnce("terrain", "[Car reset] terrain height: " + e.Message); }
        return false;
    }

    private static bool IsFinite(Vector3 v) => float.IsFinite(v.x) && float.IsFinite(v.y) && float.IsFinite(v.z);

    private static void LogOnce(string id, string message)
    {
        if (LoggedOnce.Add(id)) Core.CorePlugin.Logger?.LogWarning(message);
    }
}
