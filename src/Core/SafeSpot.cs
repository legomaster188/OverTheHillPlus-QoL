using System;
using System.Collections.Generic;
using UnityEngine;
using VehiclePhysics;

namespace OverTheHillPlus;

/// <summary>
/// Finds a safe place to drop a car near a wanted spot: solid walkable ground (ground raycast, terrain fallback),
/// not in a river or lake (the game's WaterBodyRegistry), not too steep across the car's footprint, nothing solid
/// in the car's box or right above it, and not on top of another car or a spot already handed out. If the wanted
/// spot fails, rings of offsets around it are tried (closest first) up to a search radius.
/// Only reads the world: moving the car is up to the caller. In core because more than one mod places cars; the
/// search limits come from <see cref="SettingsSource"/> (a mod with settings for them sets it) or the defaults.
/// </summary>
internal static class SafeSpot
{
    internal struct Result
    {
        public bool Ok;
        /// <summary>The ground point (no clearance added).</summary>
        public Vector3 Ground;
        public float Yaw;
        /// <summary>How far from the wanted spot it ended up (m).</summary>
        public float Offset;
        public int Tries;
        /// <summary>Why the last candidate failed (when not Ok).</summary>
        public string Why;
    }

    /// <summary>What to ignore and avoid during one search (or one batch of searches).</summary>
    internal sealed class Context
    {
        /// <summary>Rigidbodies (by pointer) whose colliders never block: the car being placed, cars already moved.</summary>
        public readonly HashSet<IntPtr> IgnoreBodies = new();
        /// <summary>Points another car stands on or was given: candidates closer than the separation are refused.</summary>
        public readonly List<Vector3> Occupied = new();

        public void Ignore(VehicleBase car)
        {
            try
            {
                var rb = car != null ? car.cachedRigidbody : null;
                if (rb != null) IgnoreBodies.Add(rb.Pointer);
            }
            catch (Exception ex) { DebugLog.Error("core", ex, "Context.Ignore"); }
        }
    }

    // A generous car footprint (the game's cars are about 2 x 4.5 m).
    private const float HalfWidth = 1.2f, HalfLength = 2.5f;
    private const float BoxBottom = 0.55f, BoxHeight = 1.5f;
    private const float Headroom = 3.5f;
    private const float MaxStep = 1.3f;       // ground jump between the centre and a corner = edge of a ledge
    private const int MaxTries = 64;

    private static readonly HashSet<string> LoggedOnce = new();
    private static bool _waterBroken;

    /// <summary>
    /// Searches around <paramref name="wanted"/> (a point near the ground, e.g. a car position or a map pin) for a spot
    /// for a car facing <paramref name="yaw"/>.
    /// </summary>
    public static Result Find(Vector3 wanted, float yaw, Context ctx)
    {
        var res = new Result { Yaw = yaw, Why = "no ground found" };
        if (!IsFinite(wanted)) { res.Why = "invalid position"; return res; }
        var cfg = Settings.Current;
        float step = Mathf.Max(2f, cfg.RingStep);
        float radius = Mathf.Max(0f, cfg.SearchRadius);
        var rot = Quaternion.Euler(0f, yaw, 0f);

        int tries = 0;
        foreach (var offset in Candidates(step, radius, rot))
        {
            if (tries >= MaxTries) break;
            tries++;
            var p = wanted + offset;
            if (Check(p, wanted.y, yaw, ctx, cfg, out var ground, out string why))
            {
                res.Ok = true;
                res.Ground = ground;
                res.Offset = new Vector2(offset.x, offset.z).magnitude;
                res.Tries = tries;
                res.Why = null;
                return res;
            }
            res.Why = why;
        }
        res.Tries = tries;
        return res;
    }

    /// <summary>The wanted spot itself, then rings around it (8+ points each), nearest ring first.</summary>
    private static IEnumerable<Vector3> Candidates(float step, float radius, Quaternion rot)
    {
        yield return Vector3.zero;
        for (float r = step; r <= radius + 0.01f; r += step)
        {
            int n = Mathf.Clamp(Mathf.RoundToInt(2f * Mathf.PI * r / step), 8, 16);
            for (int i = 0; i < n; i++)
            {
                // Start beside the wanted spot (left/right before ahead/behind), alternating sides.
                int k = (i % 2 == 0 ? i / 2 : n - 1 - i / 2);
                float a = (90f + 360f * k / n) * Mathf.Deg2Rad;
                yield return rot * new Vector3(Mathf.Cos(a) * r, 0f, Mathf.Sin(a) * r);
            }
        }
    }

    private static bool Check(Vector3 p, float refY, float yaw, Context ctx, Settings cfg, out Vector3 ground, out string why)
    {
        ground = default;
        why = null;

        // Cheap first: other cars and spots already handed out.
        float sep = cfg.Separation;
        for (int i = 0; i < ctx.Occupied.Count; i++)
        {
            var o = ctx.Occupied[i];
            float dx = o.x - p.x, dz = o.z - p.z;
            if (dx * dx + dz * dz < sep * sep) { why = "too close to another car"; return false; }
        }

        var grounds = new List<IntPtr>(5);
        if (!Probe(p.x, p.z, refY, 3f, ctx, out float y, out var normal, out bool covered, grounds))
        {
            why = "no ground found (area not loaded?)";
            return false;
        }
        if (covered) { why = "something above the spot (overhang, rock or building)"; return false; }
        ground = new Vector3(p.x, y, p.z);

        float maxSlope = cfg.MaxSlope;
        if (Vector3.Angle(normal, Vector3.up) > maxSlope) { why = "too steep"; return false; }

        if (cfg.AvoidWater && InWater(ground, cfg.MaxWaterDepth)) { why = "water"; return false; }

        // Footprint: ground under the four sides of the car, so a ledge or a ditch is caught too.
        var rot = Quaternion.Euler(0f, yaw, 0f);
        var fwd = rot * Vector3.forward;
        var right = rot * Vector3.right;
        if (!Side(ground + fwd * HalfLength, y, ctx, grounds, out float yF)
            || !Side(ground - fwd * HalfLength, y, ctx, grounds, out float yB)
            || !Side(ground + right * HalfWidth, y, ctx, grounds, out float yR)
            || !Side(ground - right * HalfWidth, y, ctx, grounds, out float yL))
        {
            why = "uneven ground (ledge or drop)";
            return false;
        }
        float pitch = Mathf.Atan2(Mathf.Abs(yF - yB), 2f * HalfLength) * Mathf.Rad2Deg;
        float roll = Mathf.Atan2(Mathf.Abs(yR - yL), 2f * HalfWidth) * Mathf.Rad2Deg;
        if (pitch > maxSlope || roll > maxSlope) { why = "too steep"; return false; }
        if (cfg.AvoidWater && (InWater(new Vector3(p.x, yF, p.z) + fwd * HalfLength, cfg.MaxWaterDepth)
                               || InWater(new Vector3(p.x, yB, p.z) - fwd * HalfLength, cfg.MaxWaterDepth)))
        {
            why = "water";
            return false;
        }

        if (Blocked(ground, normal, rot, ctx, grounds)) { why = "blocked (tree, rock, building or car)"; return false; }
        return true;
    }

    private static bool Side(Vector3 at, float centerY, Context ctx, List<IntPtr> grounds, out float y)
    {
        if (!Probe(at.x, at.z, centerY, MaxStep, ctx, out y, out _, out _, grounds)) return false;
        return Mathf.Abs(y - centerY) <= MaxStep;
    }

    /// <summary>
    /// The highest walkable surface at most <paramref name="above"/> over <paramref name="refY"/> (lifted to the
    /// terrain if the point is under it), skipping triggers, moving bodies and ignored cars. <paramref name="covered"/>:
    /// a solid surface sits less than <see cref="Headroom"/> above the found ground (inside a rock, under an overhang).
    /// </summary>
    private static bool Probe(float x, float z, float refY, float above, Context ctx, out float y, out Vector3 normal,
        out bool covered, List<IntPtr> grounds)
    {
        y = 0f;
        normal = Vector3.up;
        covered = false;
        bool hasTerrain = TryTerrainHeight(x, z, out float terrainY);
        if (hasTerrain && terrainY > refY) refY = terrainY;

        float best = float.NegativeInfinity;
        Vector3 bestNormal = Vector3.up;
        IntPtr bestCollider = IntPtr.Zero;
        float lowestAbove = float.PositiveInfinity; // lowest solid hit above the chosen ground (checked after)
        Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<RaycastHit> hits = null;
        try
        {
            hits = Physics.RaycastAll(new Vector3(x, refY + 40f, z), Vector3.down, 160f, ~0, QueryTriggerInteraction.Ignore);
        }
        catch (Exception e) { DebugLog.Error("core", e, "SafeSpot.Probe"); LogOnce("ray", "ground raycast: " + e.Message); }

        if (hits != null)
        {
            for (int i = 0; i < hits.Length; i++)
            {
                var h = hits[i];
                var rb = h.rigidbody;
                if (rb != null && (ctx.IgnoreBodies.Contains(rb.Pointer) || !rb.isKinematic)) continue; // cars, loose props
                float hy = h.point.y;
                if (h.normal.y < 0.35f) continue; // walls and cliff faces are not ground
                if (hy <= refY + above && hy > best)
                {
                    best = hy;
                    bestNormal = h.normal;
                    try { bestCollider = h.collider != null ? h.collider.Pointer : IntPtr.Zero; } catch (Exception ex) { DebugLog.Error("core", ex, "SafeSpot.Probe"); bestCollider = IntPtr.Zero; }
                }
            }
            if (!float.IsNegativeInfinity(best))
            {
                for (int i = 0; i < hits.Length; i++)
                {
                    var h = hits[i];
                    float hy = h.point.y;
                    if (hy <= best + 0.05f) continue;
                    var rb = h.rigidbody;
                    if (rb != null && (ctx.IgnoreBodies.Contains(rb.Pointer) || !rb.isKinematic)) continue;
                    if (hy < lowestAbove) lowestAbove = hy;
                }
            }
        }

        if (float.IsNegativeInfinity(best))
        {
            if (!hasTerrain) return false;
            best = terrainY;
            bestNormal = TerrainNormal(x, z);
        }
        y = best;
        normal = bestNormal.sqrMagnitude > 0.01f ? bestNormal.normalized : Vector3.up;
        covered = lowestAbove - best < Headroom;
        if (bestCollider != IntPtr.Zero) grounds?.Add(bestCollider);
        return true;
    }

    /// <summary>Anything solid (not the ground it stands on, the terrain or an ignored car) inside the car's box.</summary>
    private static bool Blocked(Vector3 ground, Vector3 normal, Quaternion yawRot, Context ctx, List<IntPtr> grounds)
    {
        try
        {
            var tilt = Quaternion.FromToRotation(Vector3.up, normal) * yawRot;
            var center = ground + normal * (BoxBottom + BoxHeight * 0.5f);
            var cols = Physics.OverlapBox(center, new Vector3(HalfWidth, BoxHeight * 0.5f, HalfLength), tilt, ~0, QueryTriggerInteraction.Ignore);
            if (cols == null) return false;
            for (int i = 0; i < cols.Length; i++)
            {
                var c = cols[i];
                if (c == null) continue;
                if (grounds.Contains(c.Pointer)) continue;
                var rb = c.attachedRigidbody;
                if (rb != null && ctx.IgnoreBodies.Contains(rb.Pointer)) continue;
                if (c.TryCast<TerrainCollider>() != null) continue;
                return true;
            }
        }
        catch (Exception e) { DebugLog.Error("core", e, "SafeSpot.Blocked"); LogOnce("overlap", "overlap check: " + e.Message); }
        return false;
    }

    /// <summary>A river or lake surface more than <paramref name="maxDepth"/> above the ground point.</summary>
    private static bool InWater(Vector3 ground, float maxDepth)
    {
        if (_waterBroken) return false;
        try
        {
            float surface = 0f;
            if (WaterBodyRegistry.TryGetWaterSurface(ground, out surface) && float.IsFinite(surface))
                return surface > ground.y + maxDepth;
        }
        catch (Exception e)
        {
            DebugLog.Error("core", e, "SafeSpot.InWater");
            _waterBroken = true; // keep placing cars, just without the water check
            LogOnce("water", "water check unavailable: " + e.Message);
        }
        return false;
    }

    // ---- terrain (cached per frame: Terrain.activeTerrains allocates) ----

    private static Terrain[] _terrains = Array.Empty<Terrain>();
    private static int _terrainsFrame = -1;

    private static Terrain[] Terrains()
    {
        if (_terrainsFrame == Time.frameCount) return _terrains;
        _terrainsFrame = Time.frameCount;
        try
        {
            var list = new List<Terrain>();
            foreach (var t in Terrain.activeTerrains)
                if (t != null && t.terrainData != null) list.Add(t);
            _terrains = list.ToArray();
        }
        catch (Exception e)
        {
            DebugLog.Error("core", e, "SafeSpot.Terrains");
            LogOnce("terrains", "terrain list: " + e.Message);
            _terrains = Array.Empty<Terrain>();
        }
        return _terrains;
    }

    private static Terrain TerrainAt(float x, float z, out Vector3 origin, out Vector3 size)
    {
        origin = size = default;
        foreach (var t in Terrains())
        {
            try
            {
                if (t == null) continue;
                var o = t.GetPosition();
                var s = t.terrainData.size;
                if (x < o.x || z < o.z || x > o.x + s.x || z > o.z + s.z) continue;
                origin = o;
                size = s;
                return t;
            }
            catch (Exception ex) { DebugLog.Error("core", ex, "SafeSpot.TerrainAt"); }
        }
        return null;
    }

    private static bool TryTerrainHeight(float x, float z, out float height)
    {
        height = 0f;
        try
        {
            var t = TerrainAt(x, z, out var o, out _);
            if (t == null) return false;
            height = t.SampleHeight(new Vector3(x, 0f, z)) + o.y;
            return float.IsFinite(height);
        }
        catch (Exception e) { DebugLog.Error("core", e, "SafeSpot.TryTerrainHeight"); LogOnce("terrain", "terrain height: " + e.Message); }
        return false;
    }

    private static Vector3 TerrainNormal(float x, float z)
    {
        try
        {
            var t = TerrainAt(x, z, out var o, out var s);
            if (t != null && s.x > 0f && s.z > 0f)
                return t.terrainData.GetInterpolatedNormal((x - o.x) / s.x, (z - o.z) / s.z);
        }
        catch (Exception e) { DebugLog.Error("core", e, "SafeSpot.TerrainNormal"); LogOnce("tnormal", "terrain normal: " + e.Message); }
        return Vector3.up;
    }

    internal static bool IsFinite(Vector3 v) => float.IsFinite(v.x) && float.IsFinite(v.y) && float.IsFinite(v.z);

    internal static void LogOnce(string key, string message)
    {
        if (LoggedOnce.Add(key)) Core.CorePlugin.Logger?.LogWarning("[Players] " + message + " (logged once)");
    }

    // ---- settings snapshot ----

    /// <summary>The search limits to use; null = <see cref="Settings.Default"/>.</summary>
    internal static Func<Settings> SettingsSource;

    internal struct Settings
    {
        public float MaxSlope, Separation, SearchRadius, RingStep, MaxWaterDepth;
        public bool AvoidWater;

        public static Settings Default => new()
        {
            MaxSlope = 28f,
            Separation = 5.5f,
            SearchRadius = 30f,
            RingStep = 4f,
            AvoidWater = true,
            MaxWaterDepth = 0.35f,
        };

        public static Settings Current
        {
            get
            {
                try { return SettingsSource != null ? SettingsSource() : Default; }
                catch (Exception ex) { DebugLog.Error("core", ex, "Settings.Current"); return Default; }
            }
        }
    }
}
