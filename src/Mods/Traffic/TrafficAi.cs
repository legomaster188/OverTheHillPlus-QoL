using System;
using System.Collections.Generic;
using Fusion;
using UnityEngine;
using VehiclePhysics;
using NVec = System.Numerics.Vector3;

namespace OverTheHillPlus.Traffic;

/// <summary>
/// AI Traffic's manager (host or solo only): keeps a few driverless cars on the roads around the players, drives
/// them, and takes them away again.
/// - About once a second <see cref="TrafficPlanner"/> decides: cars far from every player, stuck, flipped or standing
///   in a player's way go; new ones appear on a road out of sight range (a safe spot from core's SafeSpot), at most
///   two per round and one per half second, so spawning never hitches.
/// - Every frame each car follows its road (<see cref="RoadNet"/>) on the right-hand side, slowing for bends, slopes,
///   obstacles and other cars, with much more room for players (it stops rather than push into one).
/// - Every physics step <see cref="TrafficDriver.Apply"/> writes its pedals and steering (prefix on
///   VPVehicleController.DoUpdateBlocks, which has no parameters besides the instance).
/// The host simulates every car, so players see the traffic like other players' cars; vanilla guests too. Spawning
/// marks <see cref="AdvantageFlag"/>. The cars are taken away when the mod's switch goes off, when the host leaves
/// the session (quit prefixes) and when the session changes. No NetworkString writes.
/// </summary>
internal static class TrafficAi
{
    public static readonly List<TrafficAgent> Agents = new();
    private static readonly Dictionary<IntPtr, TrafficAgent> ByCar = new();
    private static readonly List<CarBody> Bodies = new();
    private static readonly List<(int id, Vector3 pos, float speed)> Players = new();
    private static readonly List<NVec> PlayerPos = new();
    private static readonly HashSet<string> Logged = new();
    private static readonly System.Random Rng = new();

    private struct PendingSpawn
    {
        public ItemCatalog.Item Model;
        public Vector3 Pos;
        public float Yaw;
        public RoadNet.Cursor Route;
        public int Version;
    }

    private static readonly Queue<PendingSpawn> Pending = new();
    private static float _nextSpawnAt, _nextPlan;
    private static IntPtr _runnerPtr;

    public static string LastEvent = "";
    public static int SpawnedTotal { get; private set; }

    internal static void LogOnce(string key, string message)
    {
        if (Logged.Add(key)) TrafficPlugin.Logger?.LogWarning("[Traffic] " + message + " (logged once)");
    }

    private static void Event(string text)
    {
        LastEvent = text;
        DebugLog.Info(TrafficPlugin.ModId, text);
        TrafficPlugin.Logger?.LogInfo("[Traffic] " + text);
    }

    /// <summary>Null when traffic can run here, else why not.</summary>
    public static string BlockReason()
    {
        if (GameState.Runner == null) return "Load into a world first.";
        if (!GameState.IsAuthority) return "Host or solo only: the host's game drives the traffic for everyone.";
        try { if (CheatsManager.Instance == null) return "The game's spawn manager isn't loaded in this session."; }
        catch (Exception ex) { DebugLog.Error("traffic", ex, "TrafficAi.BlockReason"); return "The game's spawn manager isn't available."; }
        return null;
    }

    internal static bool IsTrafficCar(IntPtr carPtr) => ByCar.ContainsKey(carPtr);

    // ---------------------------------------------------------------- physics step (from the patch)

    public static void PhysicsStep(VPVehicleController v)
    {
        if (ByCar.Count == 0 || v is null) return;
        if (!ByCar.TryGetValue(v.Pointer, out var a)) return;
        if (!GameState.IsAuthority) return;
        TrafficDriver.Apply(a, v);
    }

    // ---------------------------------------------------------------- per frame

    public static void Tick()
    {
        if (TrafficConfig.Enabled == null) return;
        var runner = GameState.Runner;
        IntPtr rp = IntPtr.Zero;
        try { rp = runner != null ? runner.Pointer : IntPtr.Zero; } catch (Exception ex) { DebugLog.Error("traffic", ex, "TrafficAi.Tick"); }
        if (rp != _runnerPtr)
        {
            _runnerPtr = rp;
            Forget("session changed");
            RoadSource.Reset();
        }
        if (runner == null || !GameState.IsAuthority)
        {
            if (Agents.Count > 0 || Pending.Count > 0) Forget("not hosting");
            return;
        }

        float dt = Mathf.Min(Time.deltaTime, 0.1f);
        GatherPlayers();
        try { RoadSource.Tick(Players); }
        catch (Exception e) { DebugLog.Error("traffic", e, "TrafficAi.Tick"); LogOnce("roads", "roads: " + e.Message); }

        if (!TrafficConfig.Enabled.Value || BlockReason() != null)
        {
            if (Agents.Count > 0 || Pending.Count > 0) Dismiss(TrafficConfig.Enabled.Value ? BlockReason() : "switched off");
            return;
        }

        for (int i = Agents.Count - 1; i >= 0; i--)
        {
            if (Agents[i].Alive) continue;
            Remove(Agents[i], despawn: false);
        }
        BuildBodies();
        SpawnPending();

        foreach (var a in Agents)
        {
            try
            {
                a.Refresh(dt, NearestPlayer(a.Pos));
                a.KeepEngineRunning();
                Drive(a, dt);
            }
            catch (Exception e)
            {
                DebugLog.Error("traffic", e, "TrafficAi.Tick");
                LogOnce("drive", "driving an AI car: " + e);
                a.Cmd = DriveCmd.Hold();
            }
        }

        if (Time.unscaledTime >= _nextPlan)
        {
            _nextPlan = Time.unscaledTime + 1f;
            try { Plan(); }
            catch (Exception e) { DebugLog.Error("traffic", e, "TrafficAi.Tick"); LogOnce("plan", "traffic plan: " + e); }
        }
    }

    private static void GatherPlayers()
    {
        Players.Clear();
        PlayerPos.Clear();
        foreach (var fp in GameState.FusionPlayers)
        {
            try
            {
                if (fp == null || fp.Object == null) continue;
                var p = fp.Object.InputAuthority;
                if (!p.IsRealPlayer) continue; // driverless cars (ours, the convoy's) are not players
                var car = fp.vehicleBase;
                if (car == null || IsTrafficCar(car.Pointer)) continue;
                var pos = car.transform.position;
                float speed = 0f;
                try { speed = car.speed; } catch (Exception ex) { DebugLog.Error("traffic", ex, "TrafficAi.GatherPlayers"); }
                Players.Add((p.PlayerId, pos, speed));
                PlayerPos.Add(RoadSource.N(pos));
            }
            catch (Exception ex) { DebugLog.Error("traffic", ex, "TrafficAi.GatherPlayers"); }
        }
    }

    private static float NearestPlayer(Vector3 p) => TrafficPlanner.NearestPlayer(RoadSource.N(p), PlayerPos);

    private static void BuildBodies()
    {
        Bodies.Clear();
        foreach (var fp in GameState.FusionPlayers)
        {
            try
            {
                if (fp == null) continue;
                var car = fp.vehicleBase;
                if (car == null || IsTrafficCar(car.Pointer)) continue;
                var t = car.transform;
                var f = t.forward;
                f.y = 0f;
                var rb = car.cachedRigidbody;
                float speed = 0f;
                try { speed = car.speed; } catch (Exception ex) { DebugLog.Error("traffic", ex, "TrafficAi.BuildBodies"); }
                Bodies.Add(new CarBody
                {
                    Pos = t.position, Fwd = f.sqrMagnitude > 1e-4f ? f.normalized : Vector3.forward, Speed = speed,
                    IsPlayer = true, Rb = rb != null ? rb.Pointer : IntPtr.Zero,
                });
            }
            catch (Exception ex) { DebugLog.Error("traffic", ex, "TrafficAi.BuildBodies"); }
        }
        foreach (var a in Agents)
            Bodies.Add(new CarBody { Pos = a.Pos, Fwd = a.Fwd, Speed = a.Speed, IsPlayer = false, Rb = a.RbPtr });
    }

    // ---------------------------------------------------------------- driving

    private static Vector3 RightOf(Vector3 h) => new(h.z, 0f, -h.x);

    private static void Drive(TrafficAgent a, float dt)
    {
        var net = RoadSource.Net;
        if (!a.EngineRunning)
        {
            var hold = DriveCmd.Hold();
            hold.Steer = a.SmoothedSteer;
            a.Cmd = hold;
            a.TargetSpeed = 0f;
            a.Activity = "starting the engine";
            return;
        }
        if (a.RouteVersion != RoadSource.NetVersion || !net.Valid(a.Route))
        {
            if (!net.Nearest(RoadSource.N(a.Pos), out var c, out float d) || d > 25f)
            {
                a.Cmd = DriveCmd.Hold();
                a.TargetSpeed = 0f;
                a.DeadEnd = true;
                a.Activity = "no road here";
                return;
            }
            var h = RoadSource.U(net.Heading(c));
            c.Dir = Vector3.Dot(h, a.Fwd) >= 0f ? 1 : -1;
            a.Route = c;
            a.RouteVersion = RoadSource.NetVersion;
            a.DeadEnd = false;
        }

        if (!a.DeadEnd && !net.Advance(ref a.Route, RoadSource.N(a.Pos)))
        {
            if (net.TryJoin(a.Route, 15f, Rng, out var next)) a.Route = next;
            else a.DeadEnd = true;
        }
        if (a.DeadEnd)
        {
            var hold = DriveCmd.Hold();
            hold.Steer = a.SmoothedSteer;
            a.Cmd = hold;
            a.TargetSpeed = 0f;
            a.Activity = "dead end (will be replaced)";
            return;
        }

        var cmd = a.Cmd;
        float v = Mathf.Abs(a.Speed);
        float lane = Mathf.Clamp(TrafficConfig.LaneOffset.Value, 0f, 4f);
        float look = Mathf.Clamp(4.5f + 0.55f * v, 6f, 18f);
        var ahead = net.Ahead(a.Route, look);
        var p = RoadSource.U(net.Point(ahead)) + RightOf(RoadSource.U(net.Heading(ahead))) * lane;
        cmd.Steer = TrafficDriver.SmoothSteer(a, TrafficDriver.Pursuit(a, p), dt);

        float maxSpeed = Mathf.Clamp(TrafficConfig.MaxSpeed.Value, 15f, 90f) / 3.6f;
        float span = Mathf.Max(15f, v * 2.6f);
        float kappa = net.TurnAngle(a.Route, span) * Mathf.Deg2Rad / span;
        float vCurve = kappa < 1e-3f ? 99f : Mathf.Sqrt(2.6f / kappa);
        float vSteer = 4f + 14f * (1f - Mathf.Abs(cmd.Steer));
        float vSlope = a.Pitch < -10f ? Mathf.Max(3f, 9f - (-a.Pitch - 10f) * 0.35f) : 99f;
        float offRoad = RoadNet.Flat(RoadSource.N(a.Pos), net.Point(a.Route));
        float vBack = offRoad > 6f ? Mathf.Max(3f, 12f - offRoad) : 99f; // pushed off the road: slow until back on it
        TrafficDriver.ObstacleScan(a, p);
        float carCap = TrafficDriver.CarCap(a, Bodies, 4f, 10f);

        float target = Mathf.Min(Mathf.Min(maxSpeed, vCurve), Mathf.Min(vSteer, vSlope));
        target = Mathf.Min(target, Mathf.Min(vBack, Mathf.Min(a.ObstacleCap, carCap)));
        TrafficDriver.SpeedControl(a, ref cmd, Mathf.Max(0f, target), dt);
        a.Cmd = cmd;
        a.Activity = target >= 0.5f ? "driving"
            : carCap < 0.5f ? "waiting for a car ahead"
            : "stopped for something on the road";
    }

    // ---------------------------------------------------------------- planning, spawning, despawning

    private static void Plan()
    {
        var net = RoadSource.Net;
        var s = TrafficConfig.PlannerSettings();
        var cars = new List<TrafficPlanner.Car>(Agents.Count + Pending.Count);
        foreach (var a in Agents)
            cars.Add(new TrafficPlanner.Car { Id = a.Id, Pos = RoadSource.N(a.Pos), StuckFor = a.StuckFor, BlockingFor = a.BlockingFor });
        // Cars still waiting to spawn count as there (negative ids: never despawned by the plan).
        int pid = -1;
        foreach (var ps in Pending) cars.Add(new TrafficPlanner.Car { Id = pid--, Pos = RoadSource.N(ps.Pos) });

        var places = net.Roads.Count > 0 ? net.RandomPlaces(Rng, 40) : new List<RoadNet.Cursor>();
        var spots = new List<NVec>(places.Count);
        foreach (var c in places) spots.Add(net.Point(c));

        var plan = TrafficPlanner.Make(PlayerPos, cars, spots, s, Math.Max(0, 2 - Pending.Count));
        if (plan.Despawn.Count > 0 || plan.SpawnAt.Count > 0)
            DebugLog.Verbose(TrafficPlugin.ModId, () => $"plan: {PlayerPos.Count} player(s), {cars.Count} car(s), {spots.Count} candidate spot(s) -> {plan.Despawn.Count} to remove, {plan.SpawnAt.Count} to spawn");
        foreach (var (id, why) in plan.Despawn)
        {
            if (id < 0) continue;
            var a = Agents.Find(x => x.Id == id);
            if (a == null) continue;
            Remove(a, despawn: true);
            if (why != TrafficPlanner.Why.TooFar) Event($"Took away an AI car ({Reason(why)}).");
        }
        if (plan.SpawnAt.Count == 0) return;

        var models = TrafficCars.All;
        if (models.Count == 0) return;
        var ctx = new SafeSpot.Context();
        foreach (var b in Bodies) ctx.Occupied.Add(b.Pos);
        foreach (var ps in Pending) ctx.Occupied.Add(ps.Pos);
        float lane = Mathf.Clamp(TrafficConfig.LaneOffset.Value, 0f, 4f);
        foreach (int i in plan.SpawnAt)
        {
            var c = places[i];
            var h = RoadSource.U(net.Heading(c));
            var want = RoadSource.U(net.Point(c)) + RightOf(h) * lane;
            float yaw = Quaternion.LookRotation(h, Vector3.up).eulerAngles.y;
            var res = SafeSpot.Find(want, yaw, ctx);
            if (!res.Ok || res.Offset > 6f) continue; // off the road: try another place next round
            ctx.Occupied.Add(res.Ground);
            Pending.Enqueue(new PendingSpawn
            {
                Model = models[Rng.Next(models.Count)], Pos = res.Ground, Yaw = yaw, Route = c, Version = RoadSource.NetVersion,
            });
        }
    }

    private static string Reason(TrafficPlanner.Why why) => why switch
    {
        TrafficPlanner.Why.Stuck => "stuck or flipped",
        TrafficPlanner.Why.Blocking => "it was in a player's way",
        TrafficPlanner.Why.TooMany => "fewer wanted",
        _ => "far from everyone",
    };

    private static void SpawnPending()
    {
        if (Pending.Count == 0 || Time.unscaledTime < _nextSpawnAt) return;
        _nextSpawnAt = Time.unscaledTime + 0.5f;
        var ps = Pending.Dequeue();
        // A player may have driven up since it was planned: never pop a car in next to someone.
        if (NearestPlayer(ps.Pos) < TrafficConfig.PlannerSettings().SpawnMin * 0.6f) return;
        var obj = TrafficCars.Spawn(ps.Model, ps.Pos + Vector3.up * 1.1f, ps.Yaw, out string err);
        if (obj == null)
        {
            LogOnce("spawn-" + err, $"couldn't spawn {ps.Model?.Name}: {err}");
            return;
        }
        var a = new TrafficAgent();
        if (!a.Bind(obj, ps.Model.Name))
        {
            try { GameState.Runner?.Despawn(obj); } catch (Exception ex) { DebugLog.Error("traffic", ex, "TrafficAi.SpawnPending"); }
            LogOnce("bind-" + ps.Model.Name, $"spawned {ps.Model.Name} has no drivable car on it; removed");
            return;
        }
        a.Route = ps.Route;
        a.RouteVersion = ps.Version;
        Agents.Add(a);
        ByCar[a.CarPtr] = a;
        SpawnedTotal++;
        DebugLog.Verbose(TrafficPlugin.ModId, () => $"spawned {ps.Model.Name} at {ps.Pos.x:0},{ps.Pos.z:0}: {Agents.Count} AI car(s) out, {Pending.Count} waiting, {SpawnedTotal} spawned in all");
        AdvantageFlag.MarkUsed();
        a.KeepEngineRunning();
    }

    private static void Remove(TrafficAgent a, bool despawn)
    {
        if (despawn)
        {
            try
            {
                var runner = GameState.Runner;
                if (runner != null && a.Obj != null && a.Obj.IsValid && a.Obj.HasStateAuthority) runner.Despawn(a.Obj);
            }
            catch (Exception e) { DebugLog.Error("traffic", e, "TrafficAi.Remove"); LogOnce("despawn", "despawning an AI car: " + e.Message); }
        }
        Agents.Remove(a);
        ByCar.Remove(a.CarPtr);
        DebugLog.Verbose(TrafficPlugin.ModId, () => $"AI car removed ({(despawn ? "despawned" : "already gone")}): {Agents.Count} left");
    }

    /// <summary>Takes every AI car away.</summary>
    public static string Dismiss(string why = null)
    {
        Pending.Clear();
        int n = Agents.Count;
        for (int i = Agents.Count - 1; i >= 0; i--) Remove(Agents[i], despawn: true);
        Agents.Clear();
        ByCar.Clear();
        string msg = n == 0 ? "No AI cars." : $"Took away {n} AI car(s)" + (why != null ? $" ({why})." : ".");
        if (n > 0) Event(msg);
        return msg;
    }

    /// <summary>The session ended or changed: its cars went with it, forget them (nothing to despawn).</summary>
    private static void Forget(string why)
    {
        if (Agents.Count == 0 && Pending.Count == 0) return;
        DebugLog.Info(TrafficPlugin.ModId, $"{why}: forgot {Agents.Count} AI car(s) and {Pending.Count} waiting spawn(s)");
        Pending.Clear();
        Agents.Clear();
        ByCar.Clear();
        TrafficPlugin.Logger?.LogInfo("[Traffic] " + why + ": forgot the AI cars.");
    }

    /// <summary>Quitting to the menu or the desktop: take the cars away while the session still exists.</summary>
    public static void OnLeavingSession()
    {
        if (Agents.Count == 0 && Pending.Count == 0) return;
        try
        {
            if (GameState.IsAuthority) Dismiss("leaving the session");
            else Forget("left the session");
        }
        catch (Exception e)
        {
            DebugLog.Error("traffic", e, "TrafficAi.OnLeavingSession");
            LogOnce("leave", "cleaning up when leaving: " + e.Message);
            Pending.Clear();
            Agents.Clear();
            ByCar.Clear();
        }
    }

    public static int PendingCount => Pending.Count;
}
