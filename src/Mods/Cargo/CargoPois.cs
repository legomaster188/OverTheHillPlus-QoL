using System;
using System.Collections.Generic;
using UnityEngine;

namespace OverTheHillPlus.Cargo;

/// <summary>
/// The loaded map's points of interest (cabins, merchants, scenic points, trails, portals...) as job points, from the
/// game's own list (SceneReferences.PointsOfInterest, filled by SceneReferences.ScanPointsOfInterest). Names are the
/// game's localized POI names. Host only; rebuilt when the map changes, never per frame.
/// </summary>
internal static class CargoPois
{
    private static readonly List<CargoPoint> Points = new();
    private static IntPtr _builtFor;
    private static float _builtAt = -100f;

    public static IList<CargoPoint> Get()
    {
        IntPtr scene = IntPtr.Zero;
        try { scene = SceneReferences.Instance != null ? SceneReferences.Instance.Pointer : IntPtr.Zero; } catch (Exception ex) { DebugLog.Error("cargo", ex, "CargoPois.Get"); }
        if (scene == IntPtr.Zero) { Points.Clear(); _builtFor = IntPtr.Zero; return Points; }
        if (scene == _builtFor && (Points.Count > 0 || Time.unscaledTime - _builtAt < 10f)) return Points;
        Rebuild(scene);
        return Points;
    }

    private static void Rebuild(IntPtr scene)
    {
        _builtFor = scene;
        _builtAt = Time.unscaledTime;
        Points.Clear();
        try
        {
            var list = SceneReferences.Instance.PointsOfInterest;
            if (list == null) return;
            var names = new HashSet<string>();
            for (int i = 0; i < list.Count; i++)
            {
                var poi = list[i];
                if (poi == null) continue;
                try
                {
                    var pos = poi.transform.position;
                    if (!float.IsFinite(pos.x) || !float.IsFinite(pos.y) || !float.IsFinite(pos.z)) continue;
                    string name = NameOf(poi);
                    // Same-named points (several "Scenic point"s) get a number so the board can tell them apart.
                    string unique = name;
                    for (int n = 2; !names.Add(unique) && n < 100; n++) unique = name + " " + n;
                    Points.Add(new CargoPoint { Name = unique, Pos = new Vec3(pos.x, pos.y, pos.z) });
                }
                catch (Exception ex) { DebugLog.Error("cargo", ex, "CargoPois.Rebuild"); }
            }
            CargoPlugin.Logger.LogInfo($"[Cargo] {Points.Count} job points on this map");
            DebugLog.Info(CargoPlugin.ModId, $"{Points.Count} job points built from {list.Count} points of interest ({list.Count - Points.Count} skipped)");
        }
        catch (Exception e) { DebugLog.Error("cargo", e, "CargoPois.Rebuild"); CargoGame.LogOnce("pois", "[Cargo] could not read the map's points of interest: " + e.Message); }
    }

    private static string NameOf(PointOfInterest poi)
    {
        string name = null;
        try
        {
            var info = poi.Info;
            if (info != null && info.Name != null && !info.Name.IsEmpty) name = info.Name.GetLocalizedString();
        }
        catch (Exception ex) { DebugLog.Error("cargo", ex, "CargoPois.NameOf"); }
        if (string.IsNullOrWhiteSpace(name)) { try { name = poi.GetDataName(); } catch (Exception ex) { DebugLog.Error("cargo", ex, "CargoPois.NameOf"); } }
        if (string.IsNullOrWhiteSpace(name)) { try { name = poi.gameObject.name; } catch (Exception ex) { DebugLog.Error("cargo", ex, "CargoPois.NameOf"); } }
        if (string.IsNullOrWhiteSpace(name)) name = "Point";
        name = Multiplayer.HostNotice.Escape(name).Replace('\n', ' ').Replace('\r', ' ').Trim();
        return name.Length > 32 ? name.Substring(0, 32) : name;
    }
}
