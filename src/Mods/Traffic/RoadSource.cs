using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime;
using MicroVersePath = JBooth.MicroVerseCore.SplinePath;
using UnityEngine;
using UnityEngine.Splines;
using NVec = System.Numerics.Vector3;
using Object = UnityEngine.Object;

namespace OverTheHillPlus.Traffic;

/// <summary>
/// Where AI Traffic's roads come from, best first (setting Roads = Auto):
/// 1. The map's road splines. The game has no road or AI-path data of its own (Cpp2IL of the updated game: no road
///    classes in Assembly-CSharp; its only splines are SplineFollower / SplineRaycastTerrain props). Its terrain is
///    built with MicroVerse, whose roads are JBooth.MicroVerseCore.SplinePath stamps holding a Unity
///    SplineContainer (width, splat layer, tree clearing). When those stamps are still in the loaded scene, each
///    spline is sampled every 5 m with SplineContainer.EvaluatePosition (world space). Splines that are mostly under
///    water (riverbeds, which MicroVerse carves the same way) are left out, as are plain SplineContainers unless
///    their object is named like a road or path.
/// 2. Paths the players drove this session (<see cref="LearnedTrails"/>): a driven path is known to be drivable.
/// 3. Straight lines between nearby points of interest (cabins, merchants, scenic points), the last resort: open
///    country, so the cars get stuck more often (and are then replaced).
/// </summary>
internal static class RoadSource
{
    internal enum Kind { None, MapRoads, DrivenPaths, BetweenPlaces }

    public static readonly RoadNet Net = new();
    public static readonly LearnedTrails Learned = new();
    public static Kind Current { get; private set; }
    /// <summary>Changes whenever <see cref="Net"/> is rebuilt (road cursors from before are then stale).</summary>
    public static int NetVersion { get; private set; }

    private static readonly List<NVec[]> MapRoads = new();
    private static readonly List<NVec[]> PlaceLinks = new();
    private static bool _scanned;
    private static float _scanAt, _nextSample, _nextRebuild;
    private static int _builtVersion = -1;
    private static Kind _builtKind;
    private static TrafficConfig.RoadChoice _builtChoice;

    public static NVec N(Vector3 v) => new(v.x, v.y, v.z);
    public static Vector3 U(NVec v) => new(v.X, v.Y, v.Z);

    /// <summary>A new world: forget the old roads; the map is scanned a few seconds after it loaded.</summary>
    public static void Reset()
    {
        DebugLog.Verbose(TrafficPlugin.ModId, "new world: roads forgotten, the map is scanned again in 5 s");
        Net.Clear();
        Learned.Clear();
        MapRoads.Clear();
        PlaceLinks.Clear();
        _scanned = false;
        _scanAt = Time.unscaledTime + 5f;
        _builtVersion = -1;
        NetVersion++;
        Current = Kind.None;
    }

    public static string Describe()
    {
        float km = Net.TotalLength / 1000f;
        return Current switch
        {
            Kind.MapRoads => $"the map's roads ({Net.Roads.Count}, {km:0.0} km)",
            Kind.DrivenPaths => $"paths players drove ({Net.Roads.Count}, {km:0.0} km)",
            Kind.BetweenPlaces => $"straight lines between places ({Net.Roads.Count}, {km:0.0} km)",
            _ => _scanned ? "no roads yet: drive around a bit" : "looking for roads",
        };
    }

    /// <summary>Per frame (host): records where players drive, scans the map once, rebuilds the network when needed.</summary>
    public static void Tick(IReadOnlyList<(int id, Vector3 pos, float speed)> players)
    {
        if (Time.unscaledTime >= _nextSample)
        {
            _nextSample = Time.unscaledTime + 0.5f;
            foreach (var p in players) Learned.Sample(p.id, N(p.pos), p.speed);
        }
        if (!_scanned && Time.unscaledTime >= _scanAt)
        {
            _scanned = true;
            ScanMap();
        }
        if (Time.unscaledTime < _nextRebuild) return;
        _nextRebuild = Time.unscaledTime + 10f;
        Rebuild();
    }

    private static void Rebuild()
    {
        var choice = TrafficConfig.Roads.Value;
        Kind kind = choice switch
        {
            TrafficConfig.RoadChoice.MapRoads => Kind.MapRoads,
            TrafficConfig.RoadChoice.DrivenPaths => Kind.DrivenPaths,
            TrafficConfig.RoadChoice.BetweenPlaces => Kind.BetweenPlaces,
            _ => MapRoads.Count > 0 ? Kind.MapRoads : Learned.Points >= 60 ? Kind.DrivenPaths : PlaceLinks.Count > 0 ? Kind.BetweenPlaces : Kind.DrivenPaths,
        };
        bool learnedChanged = kind == Kind.DrivenPaths && Learned.Version != _builtVersion;
        if (kind == _builtKind && choice == _builtChoice && !learnedChanged && Current != Kind.None) return;
        _builtKind = kind;
        _builtChoice = choice;
        NetVersion++;
        Net.Clear();
        switch (kind)
        {
            case Kind.MapRoads:
                foreach (var r in MapRoads) Net.Add(r, 40f, "map");
                break;
            case Kind.DrivenPaths:
                foreach (var r in Learned.All()) Net.Add(r, 60f, "driven");
                _builtVersion = Learned.Version;
                break;
            case Kind.BetweenPlaces:
                foreach (var r in PlaceLinks) Net.Add(r, 40f, "places");
                break;
        }
        Current = Net.Roads.Count > 0 ? kind : Kind.None;
        DebugLog.Info(TrafficPlugin.ModId, $"road network rebuilt from {kind} ({choice}): {Net.Roads.Count} road(s), {Net.TotalLength / 1000f:0.0} km; map roads {MapRoads.Count}, driven points {Learned.Points}, place links {PlaceLinks.Count}");
    }

    // ---------------------------------------------------------------- the map's splines

    private static void ScanMap()
    {
        MapRoads.Clear();
        PlaceLinks.Clear();
        var seen = new HashSet<IntPtr>();
        int stamps = 0, named = 0, wet = 0;
        try
        {
            foreach (var o in Object.FindObjectsOfType(Il2CppType.Of<MicroVersePath>()))
            {
                var sp = o?.TryCast<MicroVersePath>();
                if (sp == null) continue;
                SplineContainer c = null;
                try { c = sp.spline; } catch (Exception ex) { DebugLog.Error("traffic", ex, "RoadSource.ScanMap"); }
                if (c == null || !seen.Add(c.Pointer)) continue;
                stamps++;
                wet += AddContainer(c);
            }
        }
        catch (Exception e) { DebugLog.Error("traffic", e, "RoadSource.ScanMap"); TrafficAi.LogOnce("splinepath", "map road stamps: " + e.Message); }
        try
        {
            foreach (var o in Object.FindObjectsOfType(Il2CppType.Of<SplineContainer>()))
            {
                var c = o?.TryCast<SplineContainer>();
                if (c == null || seen.Contains(c.Pointer) || !RoadLikeName(c.transform)) continue;
                seen.Add(c.Pointer);
                named++;
                wet += AddContainer(c);
            }
        }
        catch (Exception e) { DebugLog.Error("traffic", e, "RoadSource.ScanMap"); TrafficAi.LogOnce("splines", "map splines: " + e.Message); }
        try { BuildPlaceLinks(); }
        catch (Exception e) { DebugLog.Error("traffic", e, "RoadSource.ScanMap"); TrafficAi.LogOnce("places", "places: " + e.Message); }
        DebugLog.Info(TrafficPlugin.ModId, $"map scan: {stamps} road stamp(s), {named} named spline(s), {MapRoads.Count} road(s) kept, {wet} under water left out, {PlaceLinks.Count} place link(s)");
        TrafficPlugin.Logger?.LogInfo($"[Traffic] map scan: {stamps} road stamp(s), {named} named spline(s), {MapRoads.Count} road(s) kept " +
                                      $"({wet} under water left out), {PlaceLinks.Count} link(s) between places");
    }

    private static bool RoadLikeName(Transform t)
    {
        for (int depth = 0; t != null && depth < 3; depth++, t = t.parent)
        {
            string n = null;
            try { n = t.name; } catch (Exception ex) { DebugLog.Error("traffic", ex, "RoadSource.RoadLikeName"); }
            if (string.IsNullOrEmpty(n)) continue;
            n = n.ToLowerInvariant();
            if (n.Contains("river") || n.Contains("water") || n.Contains("stream") || n.Contains("rail")) return false;
            if (n.Contains("road") || n.Contains("path") || n.Contains("track") || n.Contains("street")) return true;
        }
        return false;
    }

    /// <summary>Samples every spline of a container; returns how many were left out as riverbeds.</summary>
    private static int AddContainer(SplineContainer c)
    {
        int wet = 0;
        int count = 0;
        try { count = c.m_Splines?.Length ?? 0; } catch (Exception ex) { DebugLog.Error("traffic", ex, "RoadSource.AddContainer"); }
        if (count == 0) { try { if (c.Spline != null) count = 1; } catch (Exception ex) { DebugLog.Error("traffic", ex, "RoadSource.AddContainer"); } }
        for (int i = 0; i < Math.Min(count, 256); i++)
        {
            try
            {
                float len = c.CalculateLength(i);
                if (!(len >= 40f) || !float.IsFinite(len)) continue;
                int n = Mathf.Clamp(Mathf.CeilToInt(len / RoadNet.Spacing) + 1, 2, 4000);
                var pts = new NVec[n];
                for (int k = 0; k < n; k++)
                {
                    var p = c.EvaluatePosition(i, k / (float)(n - 1));
                    pts[k] = new NVec(p.x, p.y, p.z);
                }
                if (MostlyWet(pts)) { wet++; continue; }
                MapRoads.Add(pts);
            }
            catch (Exception e) { DebugLog.Error("traffic", e, "RoadSource.AddContainer"); TrafficAi.LogOnce("spline-sample", "sampling a spline: " + e.Message); }
        }
        return wet;
    }

    private static bool _waterBroken;

    private static bool MostlyWet(NVec[] pts)
    {
        if (_waterBroken) return false;
        int wet = 0, checkedN = 0;
        for (int k = 0; k < pts.Length; k += Math.Max(1, pts.Length / 24))
        {
            checkedN++;
            try
            {
                float surface = 0f;
                if (WaterBodyRegistry.TryGetWaterSurface(U(pts[k]), out surface) && float.IsFinite(surface) && surface > pts[k].Y + 0.3f) wet++;
            }
            catch (Exception e)
            {
                DebugLog.Error("traffic", e, "RoadSource.MostlyWet");
                _waterBroken = true;
                TrafficAi.LogOnce("water", "water check unavailable: " + e.Message);
                return false;
            }
        }
        return checkedN > 0 && wet * 3 > checkedN;
    }

    // ---------------------------------------------------------------- between places

    private static void BuildPlaceLinks()
    {
        var places = new List<Vector3>();
        Il2CppSystem.Collections.Generic.List<PointOfInterest> list = null;
        try { list = SceneReferences.Instance != null ? SceneReferences.Instance.PointsOfInterest : null; } catch (Exception ex) { DebugLog.Error("traffic", ex, "RoadSource.BuildPlaceLinks"); }
        if (list != null)
            for (int i = 0; i < list.Count; i++)
            {
                try
                {
                    var poi = list[i];
                    if (poi != null) places.Add(poi.transform.position);
                }
                catch (Exception ex) { DebugLog.Error("traffic", ex, "RoadSource.BuildPlaceLinks"); }
            }
        // Each place to its two nearest neighbours within 450 m: a sparse web of short hops.
        var done = new HashSet<(int, int)>();
        for (int a = 0; a < places.Count; a++)
        {
            var near = new List<(float d, int b)>();
            for (int b = 0; b < places.Count; b++)
            {
                if (a == b) continue;
                float d = RoadNet.Flat(N(places[a]), N(places[b]));
                if (d > 30f && d < 450f) near.Add((d, b));
            }
            near.Sort((x, y) => x.d.CompareTo(y.d));
            for (int k = 0; k < Math.Min(2, near.Count); k++)
            {
                int b = near[k].b;
                var key = a < b ? (a, b) : (b, a);
                if (!done.Add(key)) continue;
                PlaceLinks.Add(new[] { N(places[a]), N(places[b]) });
            }
        }
    }
}
