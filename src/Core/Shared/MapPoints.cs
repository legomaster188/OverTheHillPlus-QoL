using System;
using System.Collections.Generic;
using OverTheHillPlus.Api;
using UnityEngine;

namespace OverTheHillPlus.Shared;

/// <summary>
/// The named spots on the loaded map that a mod can use as the ends of a trip: the game's points of interest (read from
/// SceneReferences.PointsOfInterest, rebuilt when the map changes) and the places a map mod published to <see cref="WorldPlaces"/>
/// (shops, camps, lodges, towns, peaks, lookouts). Same-named points get a number. Read only. Main thread only.
/// </summary>
internal static class MapPoints
{
    public struct Point
    {
        public string Name;
        public Vector3 Pos;
        /// <summary>The game's scenic points and published scenic places / peaks.</summary>
        public bool Scenic;
    }

    private static readonly List<Point> Points = new();
    private static IntPtr _builtFor;
    private static int _placesVersion = -1;
    private static float _builtAt = -100f;

    private static readonly string[] PlaceKinds = { "scenic", "tower_peak", "lodge", "town", "camp", "poi", "dock", "boat_ramp", "mine", "quarry", "ranch" };

    /// <summary>Every point on this map (a shared list: do not change it).</summary>
    public static IReadOnlyList<Point> All()
    {
        IntPtr scene = IntPtr.Zero;
        try { scene = SceneReferences.Instance != null ? SceneReferences.Instance.Pointer : IntPtr.Zero; }
        catch (Exception e) { DebugLog.Error("core", e, "MapPoints.All"); }
        int version = WorldPlaces.Version;
        if (scene == IntPtr.Zero && version == _placesVersion && Points.Count == 0) return Points;
        if (scene == _builtFor && version == _placesVersion && (Points.Count > 0 || Time.unscaledTime - _builtAt < 10f)) return Points;
        Rebuild(scene, version);
        return Points;
    }

    private static void Rebuild(IntPtr scene, int version)
    {
        _builtFor = scene;
        _placesVersion = version;
        _builtAt = Time.unscaledTime;
        Points.Clear();
        var names = new HashSet<string>(StringComparer.Ordinal);
        if (scene != IntPtr.Zero)
        {
            try
            {
                var list = SceneReferences.Instance.PointsOfInterest;
                for (int i = 0; list != null && i < list.Count; i++)
                {
                    var poi = list[i];
                    if (poi == null) continue;
                    try
                    {
                        var p = poi.transform.position;
                        if (!float.IsFinite(p.x) || !float.IsFinite(p.y) || !float.IsFinite(p.z)) continue;
                        bool scenic = poi.TryCast<ScenicPoint>() != null;
                        Add(names, NameOf(poi, scenic), p, scenic);
                    }
                    catch (Exception e) { DebugLog.Error("core", e, "MapPoints.Rebuild poi"); }
                }
            }
            catch (Exception e) { DebugLog.Error("core", e, "MapPoints.Rebuild"); }
        }
        foreach (string kind in PlaceKinds)
        {
            foreach (var pl in WorldPlaces.OfKind(kind))
            {
                var p = new Vector3(pl.X, pl.Y, pl.Z);
                if (!float.IsFinite(p.x) || !float.IsFinite(p.y) || !float.IsFinite(p.z) || DuplicateOf(p)) continue;
                Add(names, string.IsNullOrWhiteSpace(pl.Name) ? kind : pl.Name, p, kind == "scenic" || kind == "tower_peak");
            }
        }
    }

    private static bool DuplicateOf(Vector3 p)
    {
        for (int i = 0; i < Points.Count; i++)
        {
            float dx = Points[i].Pos.x - p.x, dz = Points[i].Pos.z - p.z;
            if (dx * dx + dz * dz < 100f) return true;
        }
        return false;
    }

    private static void Add(HashSet<string> names, string name, Vector3 pos, bool scenic)
    {
        name = name.Replace('\n', ' ').Replace('\r', ' ').Trim();
        if (name.Length > 32) name = name.Substring(0, 32);
        string unique = name;
        for (int n = 2; !names.Add(unique) && n < 200; n++) unique = name + " " + n;
        Points.Add(new Point { Name = unique, Pos = pos, Scenic = scenic });
    }

    private static string NameOf(PointOfInterest poi, bool scenic)
    {
        string name = null;
        try
        {
            var info = poi.Info;
            if (info != null && info.Name != null && !info.Name.IsEmpty) name = info.Name.GetLocalizedString();
        }
        catch (Exception e) { DebugLog.Error("core", e, "MapPoints.NameOf"); }
        if (string.IsNullOrWhiteSpace(name)) { try { name = poi.GetDataName(); } catch (Exception e) { DebugLog.Error("core", e, "MapPoints.NameOf"); } }
        if (string.IsNullOrWhiteSpace(name)) { try { name = poi.gameObject.name; } catch (Exception e) { DebugLog.Error("core", e, "MapPoints.NameOf"); } }
        if (string.IsNullOrWhiteSpace(name)) name = scenic ? "Scenic point" : "Point";
        return name;
    }
}
