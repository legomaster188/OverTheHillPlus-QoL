using System;
using System.Collections.Generic;
using Fusion;
using Fusion.Addons.InterestManagement;
using Il2CppInterop.Runtime;
using UnityEngine;
using Object = UnityEngine.Object;

namespace OverTheHillPlus.Features.Public.NetPerf;

/// <summary>
/// Live host-side tweaks (interest distance, always-replicated cars) and the per-player diagnostics.
/// Everything is throttled (scene scans every 1.5 s) and only writes while this game is the host, since interest is
/// computed on the host (PlayerInterestManager.UpdatePlayerInterests only runs on the server).
/// </summary>
internal static class NetPerfRuntime
{
    private const float ScanInterval = 1.5f;
    private const float StatsInterval = 2f;

    private sealed class Scaled<T>
    {
        public T Target;
        public float Original;
    }

    private static readonly Dictionary<IntPtr, Scaled<PlayerCameraPositioning>> Cones = new();
    private static readonly Dictionary<IntPtr, Scaled<InterestSphere>> Spheres = new();
    /// <summary>(car object, player) pairs we marked always-interested, so they can be cleared again.</summary>
    private static readonly Dictionary<(IntPtr obj, int player), (NetworkObject obj, PlayerRef player)> Pinned = new();

    private static float _nextScan, _nextStats;
    private static readonly HashSet<string> LoggedOnce = new();

    // ---- read by the menu ----
    internal static bool Hosting { get; private set; }
    internal static int ConeCount => Cones.Count;
    internal static int SphereCount => Spheres.Count;
    internal static int PinnedCount => Pinned.Count;
    internal static float SampleConeOriginal = -1f, SampleConeNow = -1f;
    internal static string CarInterestMode = "?";
    internal static int ViewsFound, ConesSeen;

    internal sealed class PlayerRow
    {
        public string Name;
        public bool IsYou;
        public double RttMs = -1;
        public string Connection = "";
        public int Objects = -1;
        public int Cells = -1;
    }

    internal static readonly List<PlayerRow> Rows = new();
    internal static string SessionLine = "", InterestLine = "", StatsLine = "";

    internal static bool IsHosting(NetworkRunner r)
    {
        if (r == null) return false;
        try { return r.IsRunning && r.IsServer && !r.IsSinglePlayer; } catch { return false; }
    }

    private static void LogOnce(string key, Exception e)
    {
        if (LoggedOnce.Add(key)) Plugin.Logger.LogWarning($"[NetPerf] {key}: {e.Message}");
    }

    public static void Tick()
    {
        if (Time.unscaledTime < _nextScan) return;
        _nextScan = Time.unscaledTime + ScanInterval;

        var r = GameState.Runner;
        Hosting = IsHosting(r);
        if (!Hosting)
        {
            // Left the session or joined someone else's: the objects are gone or not ours to change. Forget them.
            if (Cones.Count > 0 || Spheres.Count > 0 || Pinned.Count > 0) Forget();
            return;
        }

        try { ApplyInterestScale(); } catch (Exception e) { LogOnce("interest scale", e); }
        try { ApplyKeepCarsVisible(r); } catch (Exception e) { LogOnce("keep cars visible", e); }
    }

    private static void Forget()
    {
        Cones.Clear();
        Spheres.Clear();
        Pinned.Clear();
        SampleConeOriginal = SampleConeNow = -1f;
        CarInterestMode = "?";
    }

    // ---- interest distance ----

    private static void ApplyInterestScale()
    {
        float scale = Mathf.Clamp(NetPerfFeature.InterestScale.Value, 0.4f, 2f);
        bool active = Mathf.Abs(scale - 1f) > 0.01f;
        if (!active && Cones.Count == 0 && Spheres.Count == 0) return; // off and nothing to restore: no scene scans

        // View cones: PlayerCameraPositioning writes cone.Length = cameraDistance every tick on the host, so scaling
        // cameraDistance is the supported way to change it (the field is used for nothing else).
        var seen = new HashSet<IntPtr>();
        foreach (var o in Object.FindObjectsOfType(Il2CppType.Of<PlayerCameraPositioning>()))
        {
            var c = o?.TryCast<PlayerCameraPositioning>();
            if (c == null) continue;
            seen.Add(c.Pointer);
            if (!Cones.TryGetValue(c.Pointer, out var e))
            {
                if (!active) continue;
                e = new Scaled<PlayerCameraPositioning> { Target = c, Original = c.cameraDistance };
                Cones[c.Pointer] = e;
            }
            float want = active ? e.Original * scale : e.Original;
            if (Mathf.Abs(c.cameraDistance - want) > 0.01f) c.cameraDistance = want;
            SampleConeOriginal = e.Original;
            SampleConeNow = want;
        }
        ConesSeen = seen.Count;
        Prune(Cones, seen);

        // Extra sphere shapes on the players' interest views (if the prefab has any): scale their radius too.
        var seenSpheres = new HashSet<IntPtr>();
        int views = 0;
        foreach (var o in Object.FindObjectsOfType(Il2CppType.Of<PlayerInterestView>()))
        {
            var v = o?.TryCast<PlayerInterestView>();
            if (v == null) continue;
            views++;
            var shapes = v.Shapes;
            if (shapes == null) continue;
            for (int i = 0; i < shapes.Count; i++)
            {
                var s = shapes[i]?.TryCast<InterestSphere>();
                if (s == null) continue;
                seenSpheres.Add(s.Pointer);
                if (!Spheres.TryGetValue(s.Pointer, out var e))
                {
                    if (!active) continue;
                    e = new Scaled<InterestSphere> { Target = s, Original = s.Radius };
                    Spheres[s.Pointer] = e;
                }
                float want = active ? e.Original * scale : e.Original;
                if (Mathf.Abs(s.Radius - want) > 0.01f)
                {
                    s.Radius = want;
                    try { s.InvalidateCache(); } catch { }
                }
            }
        }
        ViewsFound = views;
        Prune(Spheres, seenSpheres);

        // Back at 1.0: everything has been written back above, so stop tracking.
        if (!active)
        {
            Cones.Clear();
            Spheres.Clear();
        }
    }

    private static void Prune<T>(Dictionary<IntPtr, T> map, HashSet<IntPtr> alive)
    {
        if (map.Count == 0) return;
        List<IntPtr> dead = null;
        foreach (var k in map.Keys)
            if (!alive.Contains(k)) (dead ??= new List<IntPtr>()).Add(k);
        if (dead != null) foreach (var k in dead) map.Remove(k);
    }

    // ---- always-replicated cars ----

    private static void ApplyKeepCarsVisible(NetworkRunner r)
    {
        bool on = NetPerfFeature.KeepCarsVisible.Value;
        if (!on && Pinned.Count == 0) return;

        var cars = new List<(NetworkObject obj, PlayerRef owner)>();
        foreach (var o in GameState.FusionPlayers)
        {
            var fp = o?.TryCast<FusionPlayer>();
            var no = fp?.Object;
            if (no == null) continue;
            try
            {
                if (!no.IsValid) continue;
                var owner = no.InputAuthority;
                if (owner.IsRealPlayer) cars.Add((no, owner));
            }
            catch { }
        }

        if (cars.Count > 0)
        {
            try { CarInterestMode = cars[0].obj.ObjectInterest.ToString(); } catch { CarInterestMode = "?"; }
        }
        // Cars that already replicate to everyone need no pins.
        if (on && CarInterestMode == "Global" && Pinned.Count == 0) return;

        if (!on)
        {
            foreach (var kv in Pinned)
            {
                try
                {
                    var obj = kv.Value.obj;
                    if (obj != null && obj.IsValid) r.SetPlayerAlwaysInterested(kv.Value.player, obj, false);
                }
                catch { }
            }
            Pinned.Clear();
            return;
        }

        int local = int.MinValue;
        try { local = r.LocalPlayer.RawEncoded; } catch { }
        var alive = new HashSet<(IntPtr, int)>();
        foreach (var car in cars)
        {
            foreach (var viewer in cars)
            {
                if (viewer.owner.RawEncoded == local) continue; // the host has every object anyway
                if (viewer.owner.RawEncoded == car.owner.RawEncoded) continue; // own car is always replicated to its owner
                var key = (car.obj.Pointer, viewer.owner.RawEncoded);
                alive.Add(key);
                if (Pinned.ContainsKey(key)) continue;
                try { r.SetPlayerAlwaysInterested(viewer.owner, car.obj, true); } catch (Exception e) { LogOnce("always interested", e); }
                Pinned[key] = (car.obj, viewer.owner);
            }
        }
        // Players who left / cars that despawned: Fusion drops those itself, just forget them.
        if (Pinned.Count > alive.Count)
        {
            var dead = new List<(IntPtr, int)>();
            foreach (var k in Pinned.Keys) if (!alive.Contains(k)) dead.Add(k);
            foreach (var k in dead) Pinned.Remove(k);
        }
    }

    // ---- diagnostics (called from the menu, throttled) ----

    internal static void RefreshStats(bool force = false)
    {
        if (Features.Public.MenuUx.Indexing) return; // menu search indexing runs every draw function: no scans then
        if (!force && Time.unscaledTime < _nextStats) return;
        _nextStats = Time.unscaledTime + StatsInterval;

        Rows.Clear();
        SessionLine = InterestLine = StatsLine = "";
        var r = GameState.Runner;
        if (r == null) return;

        bool hosting = IsHosting(r);
        try
        {
            var cfg = r.Config;
            var sim = cfg?.Simulation;
            if (sim != null)
            {
                SessionLine = hosting ? NetPerfConfig.Describe(sim.TickRateSelection) : $"tick {r.TickRate} Hz (host's settings)";
                bool aoi = false;
                try { aoi = sim.AreaOfInterestEnabled; } catch { }
                string cell = "";
                try { cell = $", cell {sim.InterestManagementConfig?.AreaOfInterestCellSize} m"; } catch { }
                InterestLine = aoi ? $"area of interest on{cell}" : "area of interest off (everything goes to everyone)";
            }
        }
        catch (Exception e) { LogOnce("session readout", e); }

        if (!hosting) return;

        try { ReadFusionStats(r); } catch (Exception e) { LogOnce("fusion stats", e); }

        var cells = new Dictionary<int, int>();
        try
        {
            foreach (var o in Object.FindObjectsOfType(Il2CppType.Of<PlayerInterestManager>()))
            {
                var m = o?.TryCast<PlayerInterestManager>();
                if (m == null) continue;
                try { cells[m.Player.RawEncoded] = m.InterestCellCount; } catch { }
            }
        }
        catch (Exception e) { LogOnce("interest managers", e); }

        var players = new List<PlayerRef>();
        try
        {
            foreach (var o in GameState.FusionPlayers)
            {
                var fp = o?.TryCast<FusionPlayer>();
                if (fp?.Object == null) continue;
                try { var p = fp.Object.InputAuthority; if (p.IsRealPlayer) players.Add(p); } catch { }
                if (CarInterestMode == "?") { try { CarInterestMode = fp.Object.ObjectInterest.ToString(); } catch { } }
            }
        }
        catch (Exception e) { LogOnce("players", e); }

        // View cone lengths for the menu (the live tweak only scans while it is in use).
        if (Cones.Count == 0)
        {
            try
            {
                int n = 0;
                foreach (var o in Object.FindObjectsOfType(Il2CppType.Of<PlayerCameraPositioning>()))
                {
                    var c = o?.TryCast<PlayerCameraPositioning>();
                    if (c == null) continue;
                    n++;
                    SampleConeOriginal = SampleConeNow = c.cameraDistance;
                }
                ConesSeen = n;
            }
            catch (Exception e) { LogOnce("view cones", e); }
        }
        players.Sort((a, b) => a.RawEncoded.CompareTo(b.RawEncoded));

        PlayerRef local = default;
        try { local = r.LocalPlayer; } catch { }
        OverlandSessionPlayers sp = null;
        try { sp = OverlandSessionPlayers.Instance; } catch { }

        foreach (var p in players)
        {
            var row = new PlayerRow { IsYou = p.RawEncoded == local.RawEncoded };
            try { row.Name = sp?.GetPlayerName(p); } catch { }
            if (string.IsNullOrEmpty(row.Name)) row.Name = "Player " + p.PlayerId;
            if (!row.IsYou)
            {
                try { row.RttMs = r.GetPlayerRtt(p) * 1000.0; } catch { }
                try
                {
                    var ct = r.GetPlayerConnectionType(p);
                    row.Connection = ct == ConnectionType.Direct ? "direct" : ct == ConnectionType.Relayed ? "relayed" : "";
                }
                catch { }
                try { row.Objects = r.GetObjectsInAreaOfInterestForPlayer(p)?.Count ?? -1; } catch { }
            }
            if (cells.TryGetValue(p.RawEncoded, out int c)) row.Cells = c;
            Rows.Add(row);
        }
    }

    private static void ReadFusionStats(NetworkRunner r)
    {
        if (!r.TryGetFusionStatistics(out var mgr) || mgr == null) return;
        var stats = mgr.SimulationSnapshot?.Stats;
        if (stats == null) return;
        float Get(Fusion.Statistics.FusionStatType t) => stats.TryGetValue(t, out float v) ? v : float.NaN;
        float outBw = Get(Fusion.Statistics.FusionStatType.OutBandwidth);
        float inBw = Get(Fusion.Statistics.FusionStatType.InBandwidth);
        float outObj = Get(Fusion.Statistics.FusionStatType.OutObjectUpdates);
        var parts = new List<string>();
        if (!float.IsNaN(outBw)) parts.Add($"out {outBw:0}");
        if (!float.IsNaN(inBw)) parts.Add($"in {inBw:0}");
        if (!float.IsNaN(outObj)) parts.Add($"object updates out {outObj:0}");
        if (parts.Count > 0) StatsLine = "Fusion stats: " + string.Join(", ", parts);
    }
}
