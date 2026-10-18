using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace OverTheHillPlus.Features.Cheats.Convoy;

/// <summary>
/// What an AI car can "see": ground height and slope ahead, water, obstacles. All queries reuse one hit buffer
/// (no per-call allocation), ignore triggers, and skip the asking car's own body. Callers throttle them (a car scans
/// at most a few times a second), see <see cref="ConvoyDriver"/>.
/// </summary>
internal static class ConvoySense
{
    private const int Mask = ~(1 << 2); // everything but "Ignore Raycast"
    private static readonly Il2CppStructArray<RaycastHit> Hits = new(48);
    private static bool _waterBroken;

    /// <summary>Number of physics queries made this second (shown in the debug card).</summary>
    public static int QueriesPerSecond { get; private set; }
    private static int _queries;
    private static float _queriesAt;

    private static void Count()
    {
        _queries++;
        float now = Time.unscaledTime;
        if (now - _queriesAt >= 1f)
        {
            QueriesPerSecond = _queries;
            _queries = 0;
            _queriesAt = now;
        }
    }

    // ------------------------------------------------------------------ ground

    /// <summary>
    /// The highest walkable surface at x,z that is at most <paramref name="above"/> over <paramref name="refY"/>
    /// (so tree canopies and bridges above are skipped), ignoring moving bodies (cars, loose props). Terrain fallback.
    /// </summary>
    public static bool Ground(Vector3 at, float refY, float above, out float y, out Vector3 normal)
    {
        y = 0f;
        normal = Vector3.up;
        float best = float.NegativeInfinity;
        Vector3 bestN = Vector3.up;
        int n = 0;
        try
        {
            Count();
            n = Physics.RaycastNonAlloc(new Vector3(at.x, refY + above + 20f, at.z), Vector3.down, Hits, 60f + above, Mask,
                QueryTriggerInteraction.Ignore);
        }
        catch (Exception e) { DebugLog.Error("convoy", e, "ConvoySense.Ground"); ConvoyAi.LogOnce("groundray", "ground raycast: " + e.Message); }
        for (int i = 0; i < n; i++)
        {
            var h = Hits[i];
            var rb = h.rigidbody;
            if (rb != null && !rb.isKinematic) continue;
            if (h.normal.y < 0.3f) continue;
            float hy = h.point.y;
            if (hy <= refY + above && hy > best)
            {
                best = hy;
                bestN = h.normal;
            }
        }
        if (float.IsNegativeInfinity(best))
        {
            if (!TerrainHeight(at.x, at.z, out best)) return false;
            bestN = Vector3.up;
        }
        y = best;
        normal = bestN;
        return true;
    }

    /// <summary>Water surface height at a point (rivers and lakes from the game's water registry).</summary>
    public static bool WaterSurface(Vector3 p, out float surface)
    {
        surface = 0f;
        if (_waterBroken) return false;
        try
        {
            return WaterBodyRegistry.TryGetWaterSurface(p, out surface) && float.IsFinite(surface);
        }
        catch (Exception e)
        {
            DebugLog.Error("convoy", e, "ConvoySense.WaterSurface");
            _waterBroken = true;
            ConvoyAi.LogOnce("water", "water check unavailable: " + e.Message);
            return false;
        }
    }

    /// <summary>How deep the water is over the ground at a point (0 when dry).</summary>
    public static float WaterDepthOverGround(Vector3 p, float groundY)
    {
        if (!WaterSurface(new Vector3(p.x, groundY, p.z), out float s)) return 0f;
        return Mathf.Max(0f, s - groundY);
    }

    /// <summary>Water depth under a car (its ground is probed from just above the car).</summary>
    public static float WaterDepthAt(Vector3 carPos, float lift)
    {
        if (!WaterSurface(carPos, out float s)) return 0f;
        if (!Ground(carPos, carPos.y + lift, 1.5f, out float gy, out _)) return 0f;
        return Mathf.Max(0f, s - gy);
    }

    // ------------------------------------------------------------------ obstacles

    internal enum HitKind { None, Static, Dynamic }

    /// <summary>
    /// First thing in the way along a horizontal direction, from just outside the car's bumper at about bonnet height.
    /// Ground and gentle slopes (surface normal mostly up) don't count.
    /// </summary>
    public static HitKind Obstacle(ConvoyAgent a, Vector3 dir, float dist, float lateral, out float hitDist, out IntPtr hitBody)
    {
        hitDist = dist;
        hitBody = IntPtr.Zero;
        float startZ = Vector3.Dot(dir, a.Fwd) >= 0f ? a.FrontZ : -a.RearZ;
        var origin = a.Pos + dir * (startZ * 0.9f) + a.Right * lateral;
        origin.y = Mathf.Max(a.GroundY + 0.8f, a.Pos.y - 0.2f);
        int n = 0;
        try
        {
            Count();
            n = Physics.RaycastNonAlloc(origin, dir, Hits, dist, Mask, QueryTriggerInteraction.Ignore);
        }
        catch (Exception e) { DebugLog.Error("convoy", e, "ConvoySense.Obstacle"); ConvoyAi.LogOnce("obstacleray", "obstacle raycast: " + e.Message); }
        var kind = HitKind.None;
        for (int i = 0; i < n; i++)
        {
            var h = Hits[i];
            if (h.distance <= 0.01f || h.distance >= hitDist) continue;
            var rb = h.rigidbody;
            if (rb != null && rb.Pointer == a.RbPtr) continue;
            if (h.normal.y > 0.72f) continue; // drivable slope, not a wall
            hitDist = h.distance;
            kind = rb != null && !rb.isKinematic ? HitKind.Dynamic : HitKind.Static;
            hitBody = rb != null ? rb.Pointer : IntPtr.Zero;
        }
        return kind;
    }

    /// <summary>
    /// Nothing solid between the car and a target car (a rope over a rock or through a tree would just snag):
    /// a ray at about winch height, ignoring both cars and the ground itself.
    /// </summary>
    public static bool ClearLine(ConvoyAgent a, Vector3 target, IntPtr targetRb)
    {
        var from = a.Pos;
        from.y = Mathf.Max(a.GroundY + 0.8f, a.Pos.y - 0.2f);
        var to = target + Vector3.up * 0.6f;
        var d = to - from;
        float len = d.magnitude;
        if (len < 0.5f) return true;
        int n = 0;
        try
        {
            Count();
            n = Physics.RaycastNonAlloc(from, d / len, Hits, len, Mask, QueryTriggerInteraction.Ignore);
        }
        catch (Exception e) { DebugLog.Error("convoy", e, "ConvoySense.ClearLine"); ConvoyAi.LogOnce("lineray", "line raycast: " + e.Message); return true; }
        for (int i = 0; i < n; i++)
        {
            var h = Hits[i];
            if (h.distance <= 0.01f || h.distance > len - 1.5f) continue;
            var rb = h.rigidbody;
            if (rb != null && (rb.Pointer == a.RbPtr || rb.Pointer == targetRb || !rb.isKinematic)) continue;
            if (h.normal.y > 0.8f) continue; // a bump in the ground under the rope
            return false;
        }
        return true;
    }

    // ------------------------------------------------------------------ terrain ahead

    internal struct TerrainProbe
    {
        public bool Ground;      // ground found at the far point
        public float Climb;      // degrees, + uphill
        public float Drop;       // metres the ground falls away between the samples (cliff check)
        public float Water;      // water depth at the far point
    }

    /// <summary>Ground profile along a direction: samples at <paramref name="near"/> and <paramref name="far"/> metres.</summary>
    public static TerrainProbe Profile(ConvoyAgent a, Vector3 dir, float near, float far)
    {
        var res = new TerrainProbe { Ground = true };
        float baseY = a.GroundY;
        var p1 = a.Pos + dir * near;
        var p2 = a.Pos + dir * far;
        if (!Ground(p1, baseY, 3f, out float y1, out _)) { res.Ground = false; return res; }
        if (!Ground(p2, y1, 3f + (far - near) * 0.7f, out float y2, out _)) { res.Ground = false; return res; }
        float rise = y2 - baseY;
        res.Climb = Mathf.Atan2(rise, far) * Mathf.Rad2Deg;
        res.Drop = Mathf.Max(0f, Mathf.Max(baseY - y1, y1 - y2) - near * 0.6f);
        res.Water = WaterDepthOverGround(p2, y2);
        float w1 = WaterDepthOverGround(p1, y1);
        if (w1 > res.Water) res.Water = w1;
        return res;
    }

    // ------------------------------------------------------------------ choosing a heading off the trail

    private static readonly float[] Offsets = { 0f, -20f, 20f, -40f, 40f, -60f, 60f, -85f, 85f };

    /// <summary>
    /// Picks the best direction to drive towards a goal: straight at it unless something is in the way, the ground is
    /// too steep or falls away, or there is deep water; then the smallest detour that is clear. Returns the cost of the
    /// pick (high = everything is bad, the caller should consider backing up).
    /// </summary>
    public static Vector3 ChooseHeading(ConvoyAgent a, Vector3 goalDir, float range, float maxClimb, bool avoidWater,
        Vector3 preferred, out float bestCost, out string why)
    {
        bestCost = float.MaxValue;
        why = null;
        var best = goalDir;
        for (int i = 0; i < Offsets.Length; i++)
        {
            var dir = Quaternion.Euler(0f, Offsets[i], 0f) * goalDir;
            float cost = Mathf.Abs(Offsets[i]) / 90f * 1.6f;
            string reason = null;

            var kind = Obstacle(a, dir, range, 0f, out float hd, out _);
            if (kind != HitKind.None)
            {
                cost += (1f - hd / range) * 4f + (hd < 5f ? 6f : 0f);
                reason = kind == HitKind.Dynamic ? "car or object ahead" : "obstacle ahead";
            }
            var prof = Profile(a, dir, 5f, 11f);
            if (!prof.Ground) { cost += 9f; reason = "no ground ahead"; }
            else
            {
                if (prof.Climb > maxClimb) { cost += 3f + (prof.Climb - maxClimb) * 0.4f; reason = "too steep"; }
                if (prof.Climb < -maxClimb - 6f) { cost += 2f + (-prof.Climb - maxClimb) * 0.3f; reason = "steep drop"; }
                if (prof.Drop > 2.5f) { cost += 8f; reason = "ledge"; }
                if (avoidWater && prof.Water > 0.6f) { cost += 4f + prof.Water * 6f; reason = "deep water"; }
            }
            // Keep the previous choice unless something clearly better turns up (no zig-zag).
            if (preferred.sqrMagnitude > 0.5f && Vector3.Angle(dir, preferred) < 12f) cost -= 0.35f;
            if (cost < bestCost)
            {
                bestCost = cost;
                best = dir;
                why = reason;
            }
        }
        return best;
    }

    // ------------------------------------------------------------------ terrain fallback

    private static Terrain[] _terrains = Array.Empty<Terrain>();
    private static float _terrainsAt = -100f;

    private static bool TerrainHeight(float x, float z, out float h)
    {
        h = 0f;
        try
        {
            if (Time.unscaledTime - _terrainsAt > 2f)
            {
                _terrainsAt = Time.unscaledTime;
                var list = new List<Terrain>();
                foreach (var t in Terrain.activeTerrains)
                    if (t != null && t.terrainData != null) list.Add(t);
                _terrains = list.ToArray();
            }
            foreach (var t in _terrains)
            {
                if (t == null) continue;
                var o = t.GetPosition();
                var s = t.terrainData.size;
                if (x < o.x || z < o.z || x > o.x + s.x || z > o.z + s.z) continue;
                h = t.SampleHeight(new Vector3(x, 0f, z)) + o.y;
                return float.IsFinite(h);
            }
        }
        catch (Exception e) { DebugLog.Error("convoy", e, "ConvoySense.TerrainHeight"); ConvoyAi.LogOnce("terrain", "terrain height: " + e.Message); }
        return false;
    }
}
