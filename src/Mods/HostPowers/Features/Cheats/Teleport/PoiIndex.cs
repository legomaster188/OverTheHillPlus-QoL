using System;
using System.Collections.Generic;
using System.Linq;
using Il2CppInterop.Runtime;
using UnityEngine;
using Object = UnityEngine.Object;

namespace OverTheHillPlus.Features.Cheats.Teleport;

/// <summary>
/// A cached list of the map's points of interest (cabins, merchants, portals, scenic points, trails, crates...)
/// read from SceneReferences.PointsOfInterest (or a scene search). Rebuilt on demand, never every frame.
/// </summary>
internal static class PoiIndex
{
    public sealed class Poi
    {
        public string Name;
        public string Category;
        public Vector3 Position;
        public float Yaw;
        public string Landing; // where exactly we land: "spawn point", "trail start" or "marker"
        public string Search;
    }

    private static readonly List<Poi> Items = new();
    private static string _builtFor;
    private static float _builtAt = -100f;

    public static IReadOnlyList<Poi> All => Items;
    public static IEnumerable<string> Categories => Items.Select(p => p.Category).Distinct().OrderBy(c => c);

    /// <summary>Rebuilds the list when the map changed or it's older than <paramref name="maxAge"/> seconds.</summary>
    public static void EnsureFresh(float maxAge = 30f)
    {
        if (Features.Public.MenuUx.Indexing) return; // menu search indexing runs every draw function: no scans then
        string area = TeleportCore.AreaKey;
        if (_builtFor == area && Time.unscaledTime - _builtAt < maxAge && Items.Count > 0) return;
        if (_builtFor == area && Items.Count == 0 && Time.unscaledTime - _builtAt < 3f) return; // nothing there; retry slowly
        Rebuild();
    }

    public static void Rebuild()
    {
        _builtFor = TeleportCore.AreaKey;
        _builtAt = Time.unscaledTime;
        Items.Clear();
        try
        {
            var seen = new HashSet<IntPtr>();
            Il2CppSystem.Collections.Generic.List<PointOfInterest> list = null;
            try { list = SceneReferences.Instance != null ? SceneReferences.Instance.PointsOfInterest : null; } catch (Exception ex) { DebugLog.Error("hostpowers", ex, "PoiIndex.Rebuild"); }
            if (list != null)
                for (int i = 0; i < list.Count; i++)
                    Add(list[i], seen);
            // Also anything not registered in SceneReferences (spawned crates, mission objects).
            foreach (var o in Object.FindObjectsOfType(Il2CppType.Of<PointOfInterest>()))
                Add(o.TryCast<PointOfInterest>(), seen);

            AddLobbyStart();
        }
        catch (Exception e) { DebugLog.Error("hostpowers", e, "PoiIndex.Rebuild"); TeleportCore.LogOnce("poi", "POI scan: " + e.Message); }
        Items.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        DebugLog.Verbose("hostpowers", () => $"points of interest indexed for '{_builtFor}': {Items.Count}");
    }

    private static void Add(PointOfInterest poi, HashSet<IntPtr> seen)
    {
        if (poi == null || !seen.Add(poi.Pointer)) return;
        try
        {
            var t = poi.transform;
            var entry = new Poi { Position = t.position, Yaw = t.eulerAngles.y, Landing = "marker" };
            entry.Category = CategoryOf(poi);
            entry.Name = NameOf(poi);

            // Cabins, portals and merchants have spawn points: land exactly where the game would put a car.
            var spawnable = poi.TryCast<SpawnablePointOfInterest>();
            if (spawnable != null)
            {
                try
                {
                    var sps = spawnable.spawnPoints;
                    if (sps != null && sps.Count > 0)
                    {
                        SpawnPoint pick = null;
                        for (int i = 0; i < sps.Count && pick == null; i++)
                        {
                            var sp = sps[i];
                            if (sp == null) continue;
                            bool occupied = false;
                            try { occupied = sp.IsOccupied; } catch (Exception ex) { DebugLog.Error("hostpowers", ex, "PoiIndex.Add"); }
                            if (!occupied) pick = sp;
                        }
                        pick ??= sps[0];
                        if (pick != null)
                        {
                            entry.Position = pick.transform.position;
                            entry.Yaw = pick.transform.eulerAngles.y;
                            entry.Landing = "spawn point";
                        }
                    }
                }
                catch (Exception ex) { DebugLog.Error("hostpowers", ex, "PoiIndex.Add"); }
            }

            // Trails: land at the start line.
            var trail = poi.TryCast<ChallengeTrail>();
            if (trail != null)
            {
                try
                {
                    var start = trail.GetStartingWaypoint();
                    if (start != null)
                    {
                        entry.Position = start.position;
                        entry.Yaw = start.eulerAngles.y;
                        entry.Landing = "trail start";
                    }
                }
                catch (Exception ex) { DebugLog.Error("hostpowers", ex, "PoiIndex.Add"); }
            }

            if (!float.IsFinite(entry.Position.x) || !float.IsFinite(entry.Position.y) || !float.IsFinite(entry.Position.z)) return;
            entry.Search = (entry.Name + " " + entry.Category).ToLowerInvariant();
            Items.Add(entry);
        }
        catch (Exception e) { DebugLog.Error("hostpowers", e, "PoiIndex.Add"); TeleportCore.LogOnce("poi1", "POI entry: " + e.Message); }
    }

    private static void AddLobbyStart()
    {
        try
        {
            var lobby = SceneReferences.Instance != null ? SceneReferences.Instance.Lobby : null;
            var sps = lobby != null ? lobby.spawnPoints : null;
            if (sps == null || sps.Count == 0 || sps[0] == null) return;
            var t = sps[0].transform;
            Items.Add(new Poi
            {
                Name = "Start (lobby spawn)", Category = "Start", Position = t.position, Yaw = t.eulerAngles.y,
                Landing = "spawn point", Search = "start lobby spawn",
            });
        }
        catch (Exception ex) { DebugLog.Error("hostpowers", ex, "PoiIndex.AddLobbyStart"); }
    }

    private static string CategoryOf(PointOfInterest poi)
    {
        string cls = null;
        try { cls = poi.GetIl2CppType().Name; } catch (Exception ex) { DebugLog.Error("hostpowers", ex, "PoiIndex.CategoryOf"); }
        switch (cls)
        {
            case "ScenicPoint": return "Scenic";
            case "ChallengeTrail": return "Trail";
            case "ItemRecovery": return "Recovery";
            case "Cabin": case "Merchant": case "Portal": case "Crate": return cls;
        }
        try
        {
            var info = poi.Info;
            if (info != null) return info.Type.ToString();
        }
        catch (Exception ex) { DebugLog.Error("hostpowers", ex, "PoiIndex.CategoryOf"); }
        return string.IsNullOrEmpty(cls) || cls == "PointOfInterest" ? "Other" : cls;
    }

    private static string NameOf(PointOfInterest poi)
    {
        string name = null;
        try
        {
            var info = poi.Info;
            if (info != null && info.Name != null && !info.Name.IsEmpty) name = info.Name.GetLocalizedString();
        }
        catch (Exception ex) { DebugLog.Error("hostpowers", ex, "PoiIndex.NameOf"); }
        if (string.IsNullOrWhiteSpace(name)) { try { name = poi.GetDataName(); } catch (Exception ex) { DebugLog.Error("hostpowers", ex, "PoiIndex.NameOf"); } }
        if (string.IsNullOrWhiteSpace(name)) { try { name = poi.gameObject.name; } catch (Exception ex) { DebugLog.Error("hostpowers", ex, "PoiIndex.NameOf"); } }
        if (string.IsNullOrWhiteSpace(name)) name = "Unnamed";
        name = name.Replace('<', '‹').Replace('>', '›').Trim();
        return name.Length > 48 ? name.Substring(0, 48) : name;
    }
}
