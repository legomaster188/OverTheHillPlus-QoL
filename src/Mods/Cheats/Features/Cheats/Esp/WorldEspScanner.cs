using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using OverTheHillPlus.CheatsPlus;
using OverTheHillPlus.UI;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace OverTheHillPlus.Features.Cheats.Esp;

/// <summary>World ESP categories. The order is the order of the menu switches.</summary>
internal enum WorldEspCat
{
    Cabin, Merchant, Portal, Scenic, Trail, Recovery, Crate, OtherPoi,
    Meteorite, MeteoriteSpawn, Animal, Npc,
}

/// <summary>Discovery / progress state of a target as the local player's save sees it.</summary>
internal enum WorldEspState { None, Undiscovered, Discovered, Reached, Completed, Incoming, Active }

/// <summary>One thing the world ESP can label. Static things keep <see cref="Pos"/>; moving things read <see cref="Live"/>.</summary>
internal sealed class WorldEspTarget
{
    public WorldEspCat Cat;
    public string Name;
    public string Extra;          // short detail for the second label line (size, rarity, destination...)
    public WorldEspState State;
    public Color Tint;            // overrides the category colour when a != 0 (animal rarity)
    public Vector3 Pos;
    public Transform Live;
    public bool Dead;

    // per-frame scratch (draw pass)
    public float Dist;
    public Vector3 View;          // viewport point (x,y in 0..1, z = depth)
    public string InfoCache;
    public int InfoDistKey = -1;
}

/// <summary>
/// Finds world objects for the ESP. Work is staggered: one group is rescanned every 0.5 s, so every group is
/// refreshed every 2 s and no single frame pays for all the FindObjectsByType calls.
/// Everything here is a local read; nothing is written to the game.
/// </summary>
internal static class WorldEspScanner
{
    private const float PhaseInterval = 0.5f;
    private const int Groups = 4; // 0 POIs, 1 meteorites, 2 animals, 3 NPCs + active trail checkpoint

    private static readonly List<WorldEspTarget>[] ByGroup =
        { new List<WorldEspTarget>(), new List<WorldEspTarget>(), new List<WorldEspTarget>(), new List<WorldEspTarget>() };

    /// <summary>Names are resolved once per object (localization lookups are not free).</summary>
    private static readonly Dictionary<IntPtr, string> NameCache = new();
    private static readonly Dictionary<IntPtr, (string name, int rarity, uint itemId)> AnimalCache = new();
    private static readonly Dictionary<IntPtr, uint> DataIdCache = new();
    private static readonly HashSet<string> LoggedErrors = new();

    private static float _nextPhase;
    private static int _phase;
    private static string _sceneKey = "";

    public static int Version { get; private set; }
    public static float LastScanMs { get; private set; }

    public static IEnumerable<WorldEspTarget> All
    {
        get
        {
            for (int g = 0; g < Groups; g++)
                foreach (var t in ByGroup[g]) yield return t;
        }
    }

    public static int Count(WorldEspCat cat)
    {
        int n = 0;
        for (int g = 0; g < Groups; g++)
            foreach (var t in ByGroup[g]) if (t.Cat == cat && !t.Dead) n++;
        return n;
    }

    public static void ForceRefresh()
    {
        _nextPhase = 0f;
        _phase = 0;
        for (int g = 0; g < Groups; g++) Scan(g);
    }

    public static void Clear()
    {
        for (int g = 0; g < Groups; g++) ByGroup[g].Clear();
        Version++;
    }

    /// <summary>Call every frame while the ESP is wanted; does at most one group of work per 0.5 s.</summary>
    public static void Tick()
    {
        if (Time.unscaledTime < _nextPhase) return;
        _nextPhase = Time.unscaledTime + PhaseInterval;
        CheckScene();
        Scan(_phase);
        _phase = (_phase + 1) % Groups;
    }

    private static void CheckScene()
    {
        string key;
        try
        {
            var s = SceneManager.GetActiveScene();
            key = s.name + "#" + SceneManager.sceneCount;
        }
        catch (Exception ex) { DebugLog.Error("cheats", ex, "WorldEspScanner.CheckScene"); key = ""; }
        if (key == _sceneKey) return;
        DebugLog.Verbose("cheats", () => $"world ESP: scene changed to {key}, name caches cleared ({NameCache.Count} names, {AnimalCache.Count} animals)");
        _sceneKey = key;
        // Pointers can be reused by new objects after a scene change, so forget cached names.
        NameCache.Clear();
        AnimalCache.Clear();
        DataIdCache.Clear();
    }

    private static void Scan(int group)
    {
        float t0 = Time.realtimeSinceStartup;
        var list = ByGroup[group];
        list.Clear();
        try
        {
            switch (group)
            {
                case 0: ScanPois(list); break;
                case 1: ScanMeteorites(list); break;
                case 2: ScanAnimals(list); break;
                case 3: ScanNpcs(list); ScanActiveTrail(list); break;
            }
        }
        catch (Exception e) { DebugLog.Error("cheats", e, "WorldEspScanner.Scan"); LogOnce("scan" + group, e); }
        LastScanMs = (Time.realtimeSinceStartup - t0) * 1000f;
        if (DebugLog.IsVerbose && LastScanMs > 8f) DebugLog.Verbose("cheats", $"world ESP: scan of group {group} found {list.Count} target(s) in {LastScanMs:0.0} ms");
        Version++;
    }

    // ---------------------------------------------------------------- helpers

    internal static void LogOnce(string key, Exception e)
    {
        if (LoggedErrors.Add(key)) CheatsPlugin.Logger.LogWarning($"WorldEsp {key}: {e.Message}");
    }

    private static Il2CppReferenceArray<Object> Find<T>(bool includeInactive) where T : Object =>
        Object.FindObjectsByType(Il2CppType.Of<T>(),
            includeInactive ? FindObjectsInactive.Include : FindObjectsInactive.Exclude, FindObjectsSortMode.None);

    private static bool Want(WorldEspCat c) => WorldEsp.CategoryOn(c);

    internal static string Clean(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        s = s.Replace("(Clone)", "").Replace('_', ' ').Trim();
        return s.Length > 48 ? s.Substring(0, 48) : s;
    }

    private static string Localized(UnityEngine.Localization.LocalizedString ls)
    {
        if (ls == null) return null;
        try
        {
            if (ls.IsEmpty) return null;
            var s = ls.GetLocalizedString();
            return string.IsNullOrWhiteSpace(s) ? null : s;
        }
        catch (Exception ex) { DebugLog.Error("cheats", ex, "WorldEspScanner.Localized"); return null; }
    }

    private static PlayerInventory Inventory
    {
        get
        {
            try { return PlayerInventoryManager.Instance?.Inventory; }
            catch (Exception ex) { DebugLog.Error("cheats", ex, "WorldEspScanner.Inventory"); return null; }
        }
    }

    private static VirtualItem TryVirtual(PlayerInventory inv, uint id)
    {
        if (inv == null || id == 0) return null;
        try
        {
            VirtualItem vi = null;
            return inv.TryGetVirtualItem(id, out vi) ? vi : null;
        }
        catch (Exception ex) { DebugLog.Error("cheats", ex, "WorldEspScanner.TryVirtual"); return null; }
    }

    // ---------------------------------------------------------------- points of interest

    private static void ScanPois(List<WorldEspTarget> list)
    {
        bool any = Want(WorldEspCat.Cabin) || Want(WorldEspCat.Merchant) || Want(WorldEspCat.Portal) || Want(WorldEspCat.Scenic)
                   || Want(WorldEspCat.Trail) || Want(WorldEspCat.Recovery) || Want(WorldEspCat.Crate) || Want(WorldEspCat.OtherPoi);
        if (!any) return;

        bool deep = WorldEsp.DeepScan.Value;
        var inv = Inventory;
        foreach (var o in Find<PointOfInterest>(deep))
        {
            PointOfInterest poi;
            try { poi = o?.TryCast<PointOfInterest>(); } catch (Exception ex) { DebugLog.Error("cheats", ex, "WorldEspScanner.ScanPois"); continue; }
            if (poi == null) continue;
            try { AddPoi(list, poi, inv, deep); }
            catch (Exception e) { DebugLog.Error("cheats", e, "WorldEspScanner.ScanPois"); LogOnce("poi", e); }
        }
    }

    private static void AddPoi(List<WorldEspTarget> list, PointOfInterest poi, PlayerInventory inv, bool deep)
    {
        var info = poi.Info;
        if (!deep && info != null)
        {
            try { if ((int)info.VisibilityCondition == 3 /* Never */) return; } catch (Exception ex) { DebugLog.Error("cheats", ex, "WorldEspScanner.AddPoi"); }
        }

        WorldEspCat cat;
        string extra = null;
        ItemRecovery recovery = null;
        if (poi.TryCast<Cabin>() != null) cat = WorldEspCat.Cabin;
        else if (poi.TryCast<Merchant>() != null) cat = WorldEspCat.Merchant;
        else if (poi.TryCast<Portal>() != null) { cat = WorldEspCat.Portal; extra = PortalExtra(info); }
        else if (poi.TryCast<ScenicPoint>() != null) cat = WorldEspCat.Scenic;
        else if (poi.TryCast<ChallengeTrail>() != null) cat = WorldEspCat.Trail;
        else if ((recovery = poi.TryCast<ItemRecovery>()) != null) cat = WorldEspCat.Recovery;
        else if (poi.TryCast<Crate>() != null) cat = WorldEspCat.Crate;
        else
        {
            cat = WorldEspCat.OtherPoi;
            try { if (info != null) extra = info.Type.ToString(); } catch (Exception ex) { DebugLog.Error("cheats", ex, "WorldEspScanner.AddPoi"); }
        }
        if (!Want(cat)) return;

        string name = PoiName(poi, info);
        var state = PoiState(poi, inv);

        list.Add(new WorldEspTarget
        {
            Cat = cat, Name = name, Extra = extra, State = state,
            Pos = poi.transform.position,
        });

        if (recovery != null) AddRecoveryParts(list, recovery, name);
    }

    private static string PoiName(PointOfInterest poi, PointOfInterestInfo info)
    {
        var key = poi.Pointer;
        if (NameCache.TryGetValue(key, out var cached)) return cached;
        string name = null;
        if (info != null)
        {
            name = Localized(info.Name);
            if (name == null) { try { name = Clean(info.name); } catch (Exception ex) { DebugLog.Error("cheats", ex, "WorldEspScanner.PoiName"); } }
        }
        if (string.IsNullOrEmpty(name)) { try { name = Clean(poi.gameObject.name); } catch (Exception ex) { DebugLog.Error("cheats", ex, "WorldEspScanner.PoiName"); name = "?"; } }
        NameCache[key] = name;
        return name;
    }

    private static string PortalExtra(PointOfInterestInfo info)
    {
        try
        {
            var pi = info?.TryCast<PortalInfo>();
            if (pi == null) return null;
            string dir = pi.isEntry ? "entry" : "exit";
            string dest = Localized(pi.DestinationArea?.Name);
            return dest == null ? dir : $"{dir} to {dest}";
        }
        catch (Exception ex) { DebugLog.Error("cheats", ex, "WorldEspScanner.PortalExtra"); return null; }
    }

    private static WorldEspState PoiState(PointOfInterest poi, PlayerInventory inv)
    {
        try
        {
            if (!poi.IsInitialized) return WorldEspState.None;
            // DataId can walk the scene's root objects (per-area state), so remember it per object.
            if (!DataIdCache.TryGetValue(poi.Pointer, out uint id))
            {
                id = poi.DataId;
                if (id != 0) DataIdCache[poi.Pointer] = id;
            }
            var data = TryVirtual(inv, id)?.TryCast<PointOfInterestData>();
            if (data == null) return WorldEspState.Undiscovered;
            if (data.Completed) return WorldEspState.Completed;
            return (int)data.CurrentState switch
            {
                0 => WorldEspState.Undiscovered, // Hidden
                1 => WorldEspState.Discovered,   // Visible
                2 => WorldEspState.Reached,
                _ => WorldEspState.None,
            };
        }
        catch (Exception ex) { DebugLog.Error("cheats", ex, "WorldEspScanner.PoiState"); return WorldEspState.None; }
    }

    private static void AddRecoveryParts(List<WorldEspTarget> list, ItemRecovery rec, string name)
    {
        try
        {
            var spawned = rec.SpawnedObject;
            if (spawned != null)
            {
                var tr = spawned.transform;
                list.Add(new WorldEspTarget
                {
                    Cat = WorldEspCat.Recovery, Name = "Recovery: " + name, Extra = "vehicle to recover",
                    State = WorldEspState.Active, Pos = tr.position, Live = tr,
                });
            }
        }
        catch (Exception e) { DebugLog.Error("cheats", e, "WorldEspScanner.AddRecoveryParts"); LogOnce("recovery vehicle", e); }

        try
        {
            int status = (int)rec.localStatus; // Inactive, Started, Abandoning, Abandoned, Completing, Completed
            var end = rec.endPoint;
            if (end != null && (status == 1 || status == 2 || status == 4))
            {
                list.Add(new WorldEspTarget
                {
                    Cat = WorldEspCat.Recovery, Name = "Drop-off: " + name, Extra = "recovery destination",
                    State = WorldEspState.Active, Pos = end.transform.position,
                });
            }
        }
        catch (Exception e) { DebugLog.Error("cheats", e, "WorldEspScanner.AddRecoveryParts"); LogOnce("recovery endpoint", e); }
    }

    private static void ScanActiveTrail(List<WorldEspTarget> list)
    {
        if (!Want(WorldEspCat.Trail)) return;
        try
        {
            var trail = ChallengeTrail.ActiveTrail;
            if (trail == null) return;
            var wps = trail.waypoints;
            if (wps == null || wps.Count == 0) return;
            int ix = trail.currentActiveWaypointIx;
            if (ix < 0 || ix >= wps.Count) return;
            var wp = wps[ix];
            if (wp == null) return;
            list.Add(new WorldEspTarget
            {
                Cat = WorldEspCat.Trail, Name = "Trail checkpoint", Extra = $"{ix + 1} / {wps.Count}",
                State = WorldEspState.Active, Pos = wp.position,
            });
        }
        catch (Exception e) { DebugLog.Error("cheats", e, "WorldEspScanner.ScanActiveTrail"); LogOnce("trail", e); }
    }

    // ---------------------------------------------------------------- meteorites

    private static readonly string[] MeteoriteStatusNames =
        { "uninitialised", "countdown", "falling", "on ground", "extracting", "pulling", "extracted", "crater", "gone" };
    private static readonly string[] MeteoriteSizeNames =
        { "very small", "small", "medium", "large", "very large", "huge", "very huge" };

    private static void ScanMeteorites(List<WorldEspTarget> list)
    {
        if (Want(WorldEspCat.Meteorite))
        {
            foreach (var o in Find<Meteorite>(false))
            {
                try
                {
                    var m = o?.TryCast<Meteorite>();
                    if (m == null) continue;
                    int status = -1, size = -1;
                    string detail = null;
                    try
                    {
                        // Networked properties are only valid once the object is spawned.
                        var no = m.Object;
                        if (no != null && no.IsValid)
                        {
                            status = (int)m.Status;
                            size = (int)m.MeteoriteSize;
                        }
                    }
                    catch (Exception ex) { DebugLog.Error("cheats", ex, "WorldEspScanner.ScanMeteorites"); }
                    if (status == 8 || status == 0) continue; // deactivated / not set up yet
                    try
                    {
                        var cfg = m.meteoriteConfig;
                        if (cfg != null)
                            detail = $"{cfg.PebbleValue} pebbles, {cfg.RequiredNumberOfPlayersToExtract} player(s)";
                    }
                    catch (Exception ex) { DebugLog.Error("cheats", ex, "WorldEspScanner.ScanMeteorites"); }

                    string sizeName = size >= 0 && size < MeteoriteSizeNames.Length ? MeteoriteSizeNames[size] : null;
                    string statusName = status >= 0 && status < MeteoriteStatusNames.Length ? MeteoriteStatusNames[status] : null;
                    var state = status switch
                    {
                        1 or 2 => WorldEspState.Incoming,
                        4 or 5 => WorldEspState.Active,
                        6 or 7 => WorldEspState.Completed,
                        _ => WorldEspState.None,
                    };
                    string extra = string.Join(", ", new[] { sizeName, statusName, detail }.WhereNotNull());
                    var tr = m.transform;
                    list.Add(new WorldEspTarget
                    {
                        Cat = WorldEspCat.Meteorite, Name = "Meteorite", Extra = extra, State = state,
                        Pos = tr.position, Live = tr,
                    });
                }
                catch (Exception e) { DebugLog.Error("cheats", e, "WorldEspScanner.ScanMeteorites"); LogOnce("meteorite", e); }
            }
        }

        if (Want(WorldEspCat.MeteoriteSpawn))
        {
            foreach (var o in Find<MeteoriteSpawnPoint>(false))
            {
                try
                {
                    var sp = o?.TryCast<MeteoriteSpawnPoint>();
                    if (sp == null) continue;
                    list.Add(new WorldEspTarget
                    {
                        Cat = WorldEspCat.MeteoriteSpawn, Name = "Meteorite spawn", Extra = "#" + sp.MeteoriteIndex,
                        Pos = sp.transform.position,
                    });
                }
                catch (Exception e) { DebugLog.Error("cheats", e, "WorldEspScanner.ScanMeteorites"); LogOnce("meteorite spawn", e); }
            }
        }
    }

    // ---------------------------------------------------------------- animals and NPCs

    private static readonly string[] RarityNames = { "common", "uncommon", "rare", "very rare" };
    internal static readonly Color[] RarityColors =
    {
        Theme.Hex(0x9BE36B), Theme.Hex(0x4FA8F7), Theme.Hex(0xB07CF7), Theme.Hex(0xF2B33D),
    };

    private static void ScanAnimals(List<WorldEspTarget> list)
    {
        if (!Want(WorldEspCat.Animal)) return;
        var inv = Inventory;
        bool onlyNew = WorldEsp.AnimalsOnlyNew.Value;
        foreach (var o in Find<AnimalAI_Simple>(false))
        {
            try
            {
                var a = o?.TryCast<AnimalAI_Simple>();
                if (a == null) continue;
                var (name, rarity, itemId) = AnimalInfoFor(a);
                bool photographed = itemId != 0 && TryVirtual(inv, itemId) != null;
                if (onlyNew && photographed) continue;
                string extra = rarity >= 0 && rarity < RarityNames.Length ? RarityNames[rarity] : null;
                if (itemId != 0) extra = (extra == null ? "" : extra + ", ") + (photographed ? "photographed" : "new");
                var tr = a.transform;
                list.Add(new WorldEspTarget
                {
                    Cat = WorldEspCat.Animal, Name = name, Extra = extra,
                    State = itemId == 0 ? WorldEspState.None : photographed ? WorldEspState.Completed : WorldEspState.Undiscovered,
                    Tint = rarity >= 0 && rarity < RarityColors.Length ? RarityColors[rarity] : default,
                    Pos = tr.position, Live = tr,
                });
            }
            catch (Exception e) { DebugLog.Error("cheats", e, "WorldEspScanner.ScanAnimals"); LogOnce("animal", e); }
        }
    }

    private static (string name, int rarity, uint itemId) AnimalInfoFor(AnimalAI_Simple a)
    {
        var key = a.Pointer;
        if (AnimalCache.TryGetValue(key, out var c)) return c;
        string name = null;
        int rarity = -1;
        uint itemId = 0;
        try
        {
            var type = Il2CppType.Of<Animal>();
            var comp = a.GetComponent(type) ?? a.GetComponentInParent(type) ?? a.GetComponentInChildren(type);
            var info = comp?.TryCast<Animal>()?.AnimalInfo;
            if (info != null)
            {
                name = Localized(info.Name);
                try { rarity = (int)info.Rarity; } catch (Exception ex) { DebugLog.Error("cheats", ex, "WorldEspScanner.static"); }
                try { itemId = info.ItemId; } catch (Exception ex) { DebugLog.Error("cheats", ex, "WorldEspScanner.static"); }
                if (name == null) { try { name = Clean(info.name); } catch (Exception ex) { DebugLog.Error("cheats", ex, "WorldEspScanner.static"); } }
            }
        }
        catch (Exception e) { DebugLog.Error("cheats", e, "WorldEspScanner.static"); LogOnce("animal info", e); }
        if (string.IsNullOrEmpty(name)) { try { name = Clean(a.gameObject.name); } catch (Exception ex) { DebugLog.Error("cheats", ex, "WorldEspScanner.static"); name = "Animal"; } }
        c = (name, rarity, itemId);
        AnimalCache[key] = c;
        return c;
    }

    private static void ScanNpcs(List<WorldEspTarget> list)
    {
        if (!Want(WorldEspCat.Npc)) return;
        foreach (var o in Find<NPC>(false))
        {
            try
            {
                var n = o?.TryCast<NPC>();
                if (n == null) continue;
                string name = null, sub = null;
                try { name = n.Name; sub = n.NameSubtitle; } catch (Exception ex) { DebugLog.Error("cheats", ex, "WorldEspScanner.ScanNpcs"); }
                if (string.IsNullOrWhiteSpace(name)) name = Clean(n.gameObject.name);
                var tr = n.transform;
                list.Add(new WorldEspTarget
                {
                    Cat = WorldEspCat.Npc, Name = name, Extra = string.IsNullOrWhiteSpace(sub) ? null : sub,
                    Pos = tr.position, Live = tr,
                });
            }
            catch (Exception e) { DebugLog.Error("cheats", e, "WorldEspScanner.ScanNpcs"); LogOnce("npc", e); }
        }
    }

    private static IEnumerable<string> WhereNotNull(this IEnumerable<string> items)
    {
        foreach (var s in items) if (!string.IsNullOrEmpty(s)) yield return s;
    }
}
