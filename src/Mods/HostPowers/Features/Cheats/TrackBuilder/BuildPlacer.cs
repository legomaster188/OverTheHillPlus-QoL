using System;
using System.Collections.Generic;
using Fusion;
using OverTheHillPlus.HostPowers;
using UnityEngine;

namespace OverTheHillPlus.Features.Cheats.TrackBuilder;

/// <summary>
/// Places planned pieces through the game's own networked calls (so every player sees them), a few per frame, and
/// keeps them in undoable groups.
///
/// Planks: MapNetworkManager.RPC_PlacePortablePlank(player, pos, rot, isFirst). From the ISIL, the host keeps a
/// managed (not networked, unbounded) map player -> NetworkObject[2]: isFirst = true despawns that player's old planks
/// (and any plank touching them) and spawns into the empty slot; isFirst = false fills the second slot, and with no
/// free slot nothing is spawned. The game's placement state passes isFirst = (planksPositioned == 0), so a player owns
/// at most two planks. The builder does the same per owner, but gives each pair of planks its own owner key: a player
/// ref far above any real player (index 6000+). Those keys exist only in that host-side map (the RPC body only uses
/// the ref as a dictionary key), and the builder removes them again on undo / clear.
///
/// Winch anchor: RPC_PlacePortableWinchAnchor(player, pos), one per player in a networked 7-entry table, moved if it
/// already exists. Only your own ref is used (fake keys would take seats real players need, and the table guards purge
/// keys of absent players). Undo uses RPC_DestroyPlayerOwnedObjects(you), which also removes your map pin.
///
/// Cars: CheatsManager.SpawnCar (see BuildCars), despawned with Runner.Despawn.
/// </summary>
internal static class BuildPlacer
{
    private const int FakeOwnerBase = 6000;

    internal sealed class Placed
    {
        public PieceKind Kind;
        public NetworkObject Obj;
    }

    internal sealed class Group
    {
        public string Label;
        public readonly List<Placed> Items = new();
        public readonly List<int> PlankOwners = new(); // PlayerRef.RawEncoded of the fake owners used
        public int PairOwner;                           // owner of an unfinished pair (0 = none)
        public int Failed;
    }

    public static readonly List<Group> Groups = new();
    private static readonly Queue<Piece> Pending = new();
    private static Group _building;
    private static int _nextFake;
    private static IntPtr _runnerPtr;
    private static float _nextCarAt, _nextSessionCheck;
    private static int _consecutiveFails;
    private static readonly HashSet<string> Logged = new();

    public static bool Busy => _building != null;
    public static int PendingCount => Pending.Count;

    // ---- permissions ----

    /// <summary>Null when building is allowed here, otherwise the reason.</summary>
    public static string BlockReason(BuildPattern pattern)
    {
        if (GameState.Runner == null) return "Load into a world first.";
        if (GameState.IsPublicGuest || !GameState.IsAuthority) return "Host only: the host's game spawns these objects for everyone.";
        if (GameState.LocalCar == null) return "No car: get in your car first.";
        if (BuildPlanner.UsesCars(pattern))
        {
            try { if (CheatsManager.Instance == null) return "The game's cheat manager isn't loaded in this session."; }
            catch (Exception ex) { DebugLog.Error("hostpowers", ex, "BuildPlacer.BlockReason"); return "The game's cheat manager isn't available."; }
        }
        else
        {
            try { if (MapNetworkManager.Instance == null) return "No map loaded (planks and anchors need the map manager)."; }
            catch (Exception ex) { DebugLog.Error("hostpowers", ex, "BuildPlacer.BlockReason"); return "The map manager isn't available."; }
        }
        return null;
    }

    // ---- counts ----

    private static bool Alive(NetworkObject o)
    {
        try { return o != null && o.IsValid; } catch (Exception ex) { DebugLog.Error("hostpowers", ex, "BuildPlacer.Alive"); return false; }
    }

    public static int Live(PieceKind kind)
    {
        int n = 0;
        foreach (var g in Groups)
            foreach (var p in g.Items)
                if (p.Kind == kind && Alive(p.Obj)) n++;
        return n;
    }

    public static bool AnchorPlaced
    {
        get
        {
            foreach (var g in Groups) foreach (var p in g.Items) if (p.Kind == PieceKind.Anchor) return true;
            return false;
        }
    }

    // ---- starting a build ----

    public static string Start(BuildPattern pattern, List<Piece> pieces)
    {
        var block = BlockReason(pattern);
        if (block != null) return "Blocked: " + block;
        if (Busy) return "Still placing the last build: wait or Undo.";
        if (pieces == null || pieces.Count == 0) return "Nothing to place.";

        int planks = 0, cars = 0;
        foreach (var p in pieces)
        {
            if (p.Kind == PieceKind.Plank) planks++;
            else if (p.Kind == PieceKind.Car) cars++;
        }
        int maxPlanks = Mathf.RoundToInt(TrackBuilderFeature.MaxPlanks.Value);
        int maxCars = Mathf.RoundToInt(TrackBuilderFeature.MaxCars.Value);
        if (planks > 0 && Live(PieceKind.Plank) + planks > maxPlanks)
            return $"Blocked: that's {planks} more planks, over the limit of {maxPlanks} (Undo / Clear, or raise the limit).";
        if (cars > 0 && Live(PieceKind.Car) + cars > maxCars)
            return $"Blocked: that's {cars} more cars, over the limit of {maxCars} (Undo / Clear, or raise the limit).";

        CheckSession();
        AdvantageFlag.MarkUsed();
        _runnerPtr = RunnerPtr();
        _building = new Group { Label = BuildPlanner.Name(pattern) + Summary(planks, cars) };
        Groups.Add(_building);
        foreach (var p in pieces) Pending.Enqueue(p);
        _consecutiveFails = 0;
        _nextCarAt = 0f;
        HostPowersPlugin.Logger.LogInfo($"[Build] {_building.Label}: placing {pieces.Count} piece(s)");
        return $"Building {_building.Label}…";
    }

    private static string Summary(int planks, int cars) =>
        planks > 0 && cars > 0 ? $" ({planks} planks, {cars} cars)" : planks > 1 ? $" ({planks} planks)" : cars > 1 ? $" ({cars} cars)" : "";

    // ---- per frame ----

    public static void Tick()
    {
        if (Time.unscaledTime >= _nextSessionCheck)
        {
            _nextSessionCheck = Time.unscaledTime + 1f;
            CheckSession();
        }
        if (_building == null) return;
        if (Pending.Count == 0) { Finish(); return; }
        if (!GameState.IsAuthority || GameState.Runner == null)
        {
            Report("Build stopped: you are no longer the host.");
            Pending.Clear();
            Finish();
            return;
        }

        int budget = 2; // planks per frame; cars are spaced out in time (each one is a full physics car)
        while (budget > 0 && Pending.Count > 0)
        {
            var p = Pending.Peek();
            if (p.Kind == PieceKind.Car)
            {
                if (Time.unscaledTime < _nextCarAt) return;
                _nextCarAt = Time.unscaledTime + 0.2f;
                budget = 0;
            }
            else budget--;
            Pending.Dequeue();
            bool ok = PlaceOne(_building, p);
            _consecutiveFails = ok ? 0 : _consecutiveFails + 1;
            if (_consecutiveFails >= 3)
            {
                Report($"Build stopped: the game refused {_consecutiveFails} pieces in a row (see the log).");
                Pending.Clear();
                break;
            }
        }
        if (Pending.Count == 0) Finish();
    }

    private static void Finish()
    {
        var g = _building;
        _building = null;
        if (g == null) return;
        if (g.Items.Count == 0) Groups.Remove(g);
        string msg = g.Failed > 0
            ? $"{g.Label}: placed {g.Items.Count}, {g.Failed} failed (see the log)."
            : $"Placed {g.Label}.";
        Report(msg);
    }

    private static void Report(string s)
    {
        Menu.SetStatus(s);
        HostPowersPlugin.Logger.LogInfo("[Build] " + s);
    }

    private static void LogOnce(string key, string msg)
    {
        if (Logged.Add(key)) HostPowersPlugin.Logger.LogWarning("[Build] " + msg);
    }

    private static bool PlaceOne(Group g, Piece p)
    {
        try
        {
            switch (p.Kind)
            {
                case PieceKind.Plank: return PlacePlank(g, p);
                case PieceKind.Anchor: return PlaceAnchor(g, p);
                default:
                    var obj = BuildCars.Spawn(p.Car, p.Pos, p.Yaw, out var err);
                    if (obj == null)
                    {
                        g.Failed++;
                        LogOnce("car:" + err, "Car spawn failed: " + err);
                        return false;
                    }
                    g.Items.Add(new Placed { Kind = PieceKind.Car, Obj = obj });
                    return true;
            }
        }
        catch (Exception e)
        {
            DebugLog.Error("hostpowers", e, "BuildPlacer.PlaceOne");
            g.Failed++;
            LogOnce("place:" + p.Kind + e.GetType().Name, $"Placing a {p.Kind} failed: {e}");
            return false;
        }
    }

    // ---- planks ----

    private static bool PlacePlank(Group g, Piece p)
    {
        var mnm = MapNetworkManager.Instance;
        if (mnm == null) { g.Failed++; return false; }

        bool first = g.PairOwner == 0;
        if (first)
        {
            g.PairOwner = NewOwner(mnm);
            g.PlankOwners.Add(g.PairOwner);
        }
        var owner = PlayerRef.FromEncoded(g.PairOwner);
        var before = Slots(mnm, owner);
        mnm.RPC_PlacePortablePlank(owner, p.Pos, p.Rot, first);
        var spawned = NewIn(Slots(mnm, owner), before);

        // The game's placement state: first plank isFirst = true, second false, then the pair is complete.
        g.PairOwner = first ? g.PairOwner : 0;
        if (spawned == null)
        {
            g.PairOwner = 0; // don't build on a pair the game didn't accept
            g.Failed++;
            LogOnce("plank-none", "RPC_PlacePortablePlank spawned nothing (not running on the host?)");
            return false;
        }
        g.Items.Add(new Placed { Kind = PieceKind.Plank, Obj = spawned });
        if (!PlankShape.Measured) PlankShape.MeasureFrom(spawned.gameObject);
        return true;
    }

    private static int NewOwner(MapNetworkManager mnm)
    {
        for (int tries = 0; tries < 1000; tries++)
        {
            var r = PlayerRef.FromIndex(FakeOwnerBase + _nextFake++);
            bool taken = false;
            try { taken = mnm.portablePlanks != null && mnm.portablePlanks.ContainsKey(r); } catch (Exception ex) { DebugLog.Error("hostpowers", ex, "BuildPlacer.NewOwner"); }
            if (!taken) return r.RawEncoded;
        }
        return PlayerRef.FromIndex(FakeOwnerBase + _nextFake++).RawEncoded;
    }

    private static NetworkObject[] Slots(MapNetworkManager mnm, PlayerRef owner)
    {
        try
        {
            var map = mnm.portablePlanks;
            if (map == null || !map.ContainsKey(owner)) return Array.Empty<NetworkObject>();
            var arr = map[owner];
            if (arr == null) return Array.Empty<NetworkObject>();
            var copy = new NetworkObject[arr.Length];
            for (int i = 0; i < arr.Length; i++) copy[i] = arr[i];
            return copy;
        }
        catch (Exception ex2) { DebugLog.Error("hostpowers", ex2, "BuildPlacer.Slots"); return Array.Empty<NetworkObject>(); }
    }

    private static NetworkObject NewIn(NetworkObject[] now, NetworkObject[] before)
    {
        foreach (var o in now)
        {
            if (!Alive(o)) continue;
            bool old = false;
            foreach (var b in before) if (b != null && b.Pointer == o.Pointer) { old = true; break; }
            if (!old) return o;
        }
        return null;
    }

    // ---- winch anchor ----

    private static bool PlaceAnchor(Group g, Piece p)
    {
        var mnm = MapNetworkManager.Instance;
        var runner = GameState.Runner;
        if (mnm == null || runner == null) { g.Failed++; return false; }
        mnm.RPC_PlacePortableWinchAnchor(runner.LocalPlayer, p.Pos);
        // Moving an existing anchor or spawning a new one: either way it's ours to remove; the object is looked up
        // through the game's table at undo time (an anchor that moved keeps its object).
        g.Items.Add(new Placed { Kind = PieceKind.Anchor, Obj = null });
        return true;
    }

    // ---- undo / clear ----

    public static string UndoLast()
    {
        if (_building != null)
        {
            Pending.Clear();
            var g = _building;
            _building = null;
            Groups.Remove(g);
            int n = Remove(g);
            return $"Stopped and removed {g.Label} ({n} piece(s)).";
        }
        if (Groups.Count == 0) return "Nothing to undo.";
        if (!GameState.IsAuthority || GameState.Runner == null) return "Blocked: host only.";
        var last = Groups[Groups.Count - 1];
        Groups.RemoveAt(Groups.Count - 1);
        int removed = Remove(last);
        return $"Removed {last.Label} ({removed} piece(s)).";
    }

    public static string ClearAll()
    {
        if (Groups.Count == 0 && _building == null) return "Nothing placed.";
        if (!GameState.IsAuthority || GameState.Runner == null) return "Blocked: host only.";
        Pending.Clear();
        _building = null;
        int n = 0;
        // Newest first, each taken off the list before removing it (so the anchor check sees only what's left).
        while (Groups.Count > 0)
        {
            var g = Groups[Groups.Count - 1];
            Groups.RemoveAt(Groups.Count - 1);
            n += Remove(g);
        }
        return $"Cleared everything the builder placed ({n} piece(s)).";
    }

    private static int Remove(Group g)
    {
        var runner = GameState.Runner;
        int n = 0;
        bool anchor = false;
        foreach (var p in g.Items)
        {
            if (p.Kind == PieceKind.Anchor) { anchor = true; continue; }
            if (!Alive(p.Obj) || runner == null) continue;
            try
            {
                if (!p.Obj.HasStateAuthority) continue;
                runner.Despawn(p.Obj);
                n++;
            }
            catch (Exception e) { DebugLog.Error("hostpowers", e, "BuildPlacer.Remove"); LogOnce("despawn" + e.GetType().Name, "Despawn failed: " + e.Message); }
        }
        g.Items.Clear();

        var mnm = SafeMap();
        if (mnm != null)
        {
            foreach (int raw in g.PlankOwners)
            {
                try
                {
                    var key = PlayerRef.FromEncoded(raw);
                    if (mnm.portablePlanks != null && mnm.portablePlanks.ContainsKey(key)) mnm.portablePlanks.Remove(key);
                }
                catch (Exception ex) { DebugLog.Error("hostpowers", ex, "BuildPlacer.Remove"); }
            }
            if (anchor && runner != null && !StillUsedAnchor(g))
            {
                try
                {
                    mnm.RPC_DestroyPlayerOwnedObjects(runner.LocalPlayer);
                    n++;
                }
                catch (Exception e) { DebugLog.Error("hostpowers", e, "BuildPlacer.Remove"); LogOnce("anchor-undo", "Removing the winch anchor failed: " + e.Message); }
            }
        }
        g.PlankOwners.Clear();
        return n;
    }

    /// <summary>Another remaining group placed the anchor more recently (it was moved there): keep it.</summary>
    private static bool StillUsedAnchor(Group removing)
    {
        foreach (var g in Groups)
            if (g != removing)
                foreach (var p in g.Items)
                    if (p.Kind == PieceKind.Anchor) return true;
        return false;
    }

    private static MapNetworkManager SafeMap()
    {
        try { return MapNetworkManager.Instance; } catch (Exception ex) { DebugLog.Error("hostpowers", ex, "BuildPlacer.SafeMap"); return null; }
    }

    private static IntPtr RunnerPtr()
    {
        try { return GameState.Runner != null ? GameState.Runner.Pointer : IntPtr.Zero; } catch (Exception ex) { DebugLog.Error("hostpowers", ex, "BuildPlacer.RunnerPtr"); return IntPtr.Zero; }
    }

    /// <summary>A new or ended session: whatever was placed went with the old one, so forget it.</summary>
    private static void CheckSession()
    {
        if (Groups.Count == 0 && _building == null) return;
        var now = RunnerPtr();
        if (now == _runnerPtr) return;
        Pending.Clear();
        _building = null;
        Groups.Clear();
        _runnerPtr = now;
        HostPowersPlugin.Logger.LogInfo("[Build] Session changed: forgot the placed objects of the previous one.");
    }
}
