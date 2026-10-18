using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime;
using UnityEngine;
using UnityEngine.AddressableAssets;

namespace OverTheHillPlus.UnlockAll;

/// <summary>
/// The Maps section: the game's regions (RegionsList) with their areas. Areas can be unlocked for any region;
/// cabins, fog and places only for the loaded map, because they live in the loaded scene.
///
/// Findings (Cpp2IL of the updated game):
/// - CheatsController.UnlockAllAreas loads the Addressable "Data/Game/Regions/RegionsList" and, for every area of
///   every region, calls PlayerInventory.AddVirtualItem(new AreaItem(id, true, -1, -1, -1)). AddVirtualItem refuses
///   an id the profile already holds, even a locked one; here a locked AreaItem gets IsUnlocked set instead. Areas
///   are virtual items with no DLC field (AreaInfo is not an inventory item), so the DLC guard has nothing to check.
/// - The loaded map is SceneReferences.Instance.AreaInfo. UnlockAllCabins = Cabin.Cheat_UnlockCabin() on every
///   SceneReferences.Instance.GetComponentsInChildren&lt;Cabin&gt;(), then OverlandSaver.SaveGame. Cheat_UnlockCabin
///   runs the cabin's first visit for a cabin not reached yet: that also reveals the fog around it and discovers the
///   places nearby (Cabin.RevealFogOfWar -> RevealMapInRadius -> MarkAllRevealedPOIsAsDiscovered).
/// - Fog: MapManager.Instance.FogOfWarManager.ClearFullFogOfWar() clears it and saves the fog file itself.
/// - Places: FogOfWarManager.MarkAllRevealedPOIsAsDiscovered(types, position, radius) discovers every not yet
///   discovered SceneReferences point of interest within the radius (types -1 = all, as Cabin passes it).
/// - Unknown place icons: CheatsController.RevealUnknownPoiIcons sets the static
///   PointOfInterestMapIcon.RevealUnknownPoiIcons and refreshes every icon (UpdateIconAndVisibility). Not saved.
/// </summary>
internal static class MapUnlocks
{
    private const string RegionsListKey = "Data/Game/Regions/RegionsList";

    private static List<UnlockPlan.Region> _regions;
    private static float _nextRead;

    /// <summary>The game's regions with their areas (empty until the list can be read; retried every few seconds).</summary>
    internal static IReadOnlyList<UnlockPlan.Region> Regions
    {
        get
        {
            if (_regions != null) return _regions;
            if (Time.unscaledTime < _nextRead) return Array.Empty<UnlockPlan.Region>();
            _nextRead = Time.unscaledTime + 5f;
            var list = Read();
            if (list.Count == 0) return list;
            UnlockAllPlugin.Logger.LogInfo($"[UnlockAll] {list.Count} regions on the map list.");
            return _regions = list;
        }
    }

    private static List<UnlockPlan.Region> Read()
    {
        var raw = new List<UnlockPlan.Region>();
        try
        {
            var regionsList = LoadRegionsList();
            var regions = regionsList?.Regions;
            if (regions == null) return raw;
            foreach (var region in regions)
            {
                if (region == null) continue;
                var areas = new List<UnlockPlan.Area>();
                var infos = region.Areas;
                if (infos != null)
                    foreach (var area in infos)
                        if (area != null) areas.Add(new UnlockPlan.Area(area.ItemId, Localized(area.Name, area.name)));
                raw.Add(new UnlockPlan.Region(Localized(region.Name, region.name), areas));
            }
        }
        catch (Exception e)
        {
            DebugLog.Error("unlockall", e, "MapUnlocks.Read");
            UnlockAllPlugin.Logger.LogWarning("[UnlockAll] map list unreadable: " + e.GetBaseException().Message);
        }
        return UnlockPlan.Regions(raw);
    }

    /// <summary>The list the game already has in memory, else loaded from its Addressable like UnlockAllAreas does.</summary>
    private static RegionsList LoadRegionsList()
    {
        try
        {
            foreach (var o in Resources.FindObjectsOfTypeAll(Il2CppType.Of<RegionsList>()))
            {
                var r = o?.TryCast<RegionsList>();
                if (r?.Regions != null && r.Regions.Count > 0) return r;
            }
        }
        catch (Exception ex) { DebugLog.Error("unlockall", ex, "MapUnlocks.LoadRegionsList"); }
        try
        {
            var key = new Il2CppSystem.Object(IL2CPP.ManagedStringToIl2Cpp(RegionsListKey));
            return Addressables.LoadAssetAsync<RegionsList>(key).WaitForCompletion();
        }
        catch (Exception e)
        {
            DebugLog.Error("unlockall", e, "MapUnlocks.LoadRegionsList");
            UnlockAllPlugin.Logger.LogWarning("[UnlockAll] loading the map list failed: " + e.GetBaseException().Message);
            return null;
        }
    }

    private static string Localized(UnityEngine.Localization.LocalizedString s, string fallback)
    {
        try
        {
            string v = s?.GetLocalizedString();
            if (!string.IsNullOrWhiteSpace(v)) return v.Trim();
        }
        catch (Exception ex) { DebugLog.Error("unlockall", ex, "MapUnlocks.Localized"); }
        return string.IsNullOrEmpty(fallback) ? "?" : fallback;
    }

    // ---------------------------------------------------------------- the loaded map

    /// <summary>Area id of the loaded map, or null outside a world.</summary>
    internal static uint? LoadedAreaId
    {
        get
        {
            try
            {
                var area = SceneReferences.Instance?.AreaInfo;
                return area == null ? null : area.ItemId;
            }
            catch (Exception ex) { DebugLog.Error("unlockall", ex, "MapUnlocks.LoadedAreaId"); return null; }
        }
    }

    /// <summary>Index into <see cref="Regions"/> of the loaded map's region, or -1.</summary>
    internal static int LoadedRegion => UnlockPlan.RegionOf(Regions, LoadedAreaId);

    private static FogOfWarManager Fog
    {
        get
        {
            try { return MapManager.Instance?.FogOfWarManager; }
            catch (Exception ex) { DebugLog.Error("unlockall", ex, "MapUnlocks.Fog"); return null; }
        }
    }

    // ---------------------------------------------------------------- areas

    internal static int ApplyAreas(IEnumerable<uint> ids, Unlocker.Tally tally)
    {
        var inv = PlayerInventoryManager.Instance?.Inventory;
        if (inv == null) return 0;
        var idList = new List<uint>(ids);
        var known = new Dictionary<uint, bool>();
        foreach (uint id in idList)
        {
            var area = Area(inv, id);
            if (area != null) known[id] = area.IsUnlocked;
            else if (HasVirtual(inv, id)) known[id] = true; // something else holds the id: leave it
        }
        int done = 0;
        foreach (var (id, step) in UnlockPlan.ForAreas(idList, known))
        {
            try
            {
                if (step == UnlockPlan.AreaStep.Create)
                {
                    if (inv.AddVirtualItem(new AreaItem(id, true, -1, -1, -1))) done++;
                }
                else
                {
                    var area = Area(inv, id);
                    if (area != null)
                    {
                        area.IsUnlocked = true;
                        done++;
                    }
                }
            }
            catch (Exception e)
            {
                DebugLog.Error("unlockall", e, "MapUnlocks.ApplyAreas");
                if (!tally.Failed.Contains("areas")) tally.Failed.Add("areas");
                UnlockAllPlugin.Logger.LogWarning($"[UnlockAll] area {id} failed: {e.GetBaseException().Message}");
            }
        }
        return done;
    }

    private static AreaItem Area(PlayerInventory inv, uint id)
    {
        try
        {
            VirtualItem v = null;
            return inv.TryGetVirtualItem(id, out v) ? v?.TryCast<AreaItem>() : null;
        }
        catch (Exception ex) { DebugLog.Error("unlockall", ex, "MapUnlocks.Area"); return null; }
    }

    private static bool HasVirtual(PlayerInventory inv, uint id)
    {
        try
        {
            VirtualItem v = null;
            return inv.TryGetVirtualItem(id, out v);
        }
        catch (Exception ex) { DebugLog.Error("unlockall", ex, "MapUnlocks.HasVirtual"); return false; }
    }

    internal static string UnlockAreas(UnlockPlan.Region region) =>
        Unlocker.Guarded($"{region.Name} areas", t => t.Added += ApplyAreas(UnlockPlan.AreaIds(region), t));

    // ---------------------------------------------------------------- loaded map only

    /// <summary>Runs every cabin's unlock on the loaded map; returns how many cabins were run.</summary>
    internal static int ApplyCabins(Unlocker.Tally tally)
    {
        var scene = SceneReferences.Instance;
        if (scene == null) return 0;
        int n = 0;
        var cabins = scene.GetComponentsInChildren<Cabin>();
        if (cabins == null) return 0;
        foreach (var cabin in cabins)
        {
            if (cabin == null) continue;
            try
            {
                cabin.Cheat_UnlockCabin();
                n++;
            }
            catch (Exception e)
            {
                DebugLog.Error("unlockall", e, "MapUnlocks.ApplyCabins");
                if (!tally.Failed.Contains("cabins")) tally.Failed.Add("cabins");
                UnlockAllPlugin.Logger.LogWarning("[UnlockAll] a cabin failed: " + e.GetBaseException().Message);
            }
        }
        return n;
    }

    internal static bool ApplyFog(Unlocker.Tally tally)
    {
        var fog = Fog;
        if (fog?.FogOfWarTexture == null) return false;
        try
        {
            fog.ClearFullFogOfWar();
            return true;
        }
        catch (Exception e)
        {
            DebugLog.Error("unlockall", e, "MapUnlocks.ApplyFog");
            tally.Failed.Add("fog");
            UnlockAllPlugin.Logger.LogWarning("[UnlockAll] clearing the fog failed: " + e.GetBaseException().Message);
            return false;
        }
    }

    internal static bool ApplyPlaces(Unlocker.Tally tally)
    {
        if (SceneReferences.Instance == null) return false;
        try
        {
            // Every type (-1, what Cabin passes) within a radius far larger than any map.
            FogOfWarManager.MarkAllRevealedPOIsAsDiscovered((PointOfInterestType)(-1), Vector2.zero, 1e7f);
            return true;
        }
        catch (Exception e)
        {
            DebugLog.Error("unlockall", e, "MapUnlocks.ApplyPlaces");
            tally.Failed.Add("places");
            UnlockAllPlugin.Logger.LogWarning("[UnlockAll] discovering places failed: " + e.GetBaseException().Message);
            return false;
        }
    }

    private const string NoMap = "Load into this map first: cabins, fog and places only change on the loaded map.";

    internal static string UnlockCabins() => Unlocker.Guarded("Cabins", t =>
    {
        if (SceneReferences.Instance == null) { t.Message = NoMap; return; }
        int n = ApplyCabins(t);
        t.Added += n;
        t.Message = n == 0 ? "No cabins found on this map." : $"Cabins on this map unlocked ({n} checked).";
    });

    internal static string RevealFog() => Unlocker.Guarded("Fog", t =>
    {
        t.Message = ApplyFog(t) ? "Whole map revealed." : NoMap;
    }, save: false); // ClearFullFogOfWar saves the fog file itself

    internal static string DiscoverPlaces() => Unlocker.Guarded("Places", t =>
    {
        t.Message = ApplyPlaces(t) ? "Every place on this map discovered." : NoMap;
    });

    /// <summary>Unknown place icons shown on the map (this session, like the game's RevealUnknownPoiIcons).</summary>
    internal static bool UnknownIconsShown
    {
        get
        {
            try { return PointOfInterestMapIcon.RevealUnknownPoiIcons; }
            catch (Exception ex) { DebugLog.Error("unlockall", ex, "MapUnlocks.UnknownIconsShown"); return false; }
        }
    }

    internal static string ShowUnknownIcons(bool on)
    {
        string blocked = Unlocker.Blocker;
        if (blocked != null) return blocked;
        AdvantageFlag.MarkUsed();
        try
        {
            PointOfInterestMapIcon.RevealUnknownPoiIcons = on;
            foreach (var o in UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<PointOfInterestMapIcon>()))
            {
                try { o?.TryCast<PointOfInterestMapIcon>()?.UpdateIconAndVisibility(); }
                catch (Exception ex) { DebugLog.Error("unlockall", ex, "MapUnlocks.ShowUnknownIcons"); }
            }
            return on ? "Unknown places now show on the map (this session)." : "Unknown places hidden again.";
        }
        catch (Exception e)
        {
            DebugLog.Error("unlockall", e, "MapUnlocks.ShowUnknownIcons");
            UnlockAllPlugin.Logger.LogWarning("[UnlockAll] place icons failed: " + e.GetBaseException().Message);
            return "Showing the place icons failed (see the log).";
        }
    }

    /// <summary>"Unlock everything on this map": its areas, and on the loaded map also cabins, fog and places.</summary>
    internal static string Everything(UnlockPlan.Region region, bool loaded) => Unlocker.Guarded($"{region.Name}", t =>
    {
        int areas = ApplyAreas(UnlockPlan.AreaIds(region), t);
        t.Added += areas;
        var parts = new List<string> { areas > 0 ? $"{areas} area{(areas == 1 ? "" : "s")}" : "areas (already open)" };
        if (loaded && SceneReferences.Instance != null)
        {
            int cabins = ApplyCabins(t);
            t.Added += cabins;
            parts.Add($"cabins ({cabins} checked)");
            if (ApplyFog(t)) parts.Add("fog");
            if (ApplyPlaces(t)) parts.Add("places");
        }
        t.Message = $"{region.Name}: unlocked {string.Join(", ", parts)}." +
                    (loaded ? "" : " Cabins, fog and places need this map loaded.");
    });
}
