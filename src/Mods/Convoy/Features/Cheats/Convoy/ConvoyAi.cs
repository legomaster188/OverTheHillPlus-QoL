using System;
using System.Collections.Generic;
using Fusion;
using OverTheHillPlus.Convoy;
using UnityEngine;
using VehiclePhysics;

namespace OverTheHillPlus.Features.Cheats.Convoy;

/*
 * CONVOY AI — design
 * ==================
 * A host/solo feature (MODS > Convoy AI) that spawns driverless cars and drives them as a convoy behind a leader
 * (you, or any player), with teamwork when someone gets stuck.
 *
 * Game facts it is built on (decompiled with Cpp2IL, see the comments in each file for details):
 * - Cars: CheatsManager.SpawnCar("Car_<Model>") network-spawns a real car (FusionPlayer + VPP VehicleBase) with no
 *   input authority. In Fusion host mode the host simulates every car, so the host's physics step of that car is
 *   the only one that matters and every player sees it move like another player's car.
 * - Driving: FunselektorFusionVPStandardInput.ApplyVehicleInput turns the driver's network input into VPP data bus
 *   values (Channel.Input: Steer/Throttle/Brake/Handbrake scaled to 10000, ManualGear = the current gear, ±1 to
 *   change direction once stopped). A car without a driver gets zeros. The AI writes its own values in a prefix of
 *   VPVehicleController.DoUpdateBlocks (the hook the driving assists already use), right before VPP's blocks read
 *   them. The engine is started with the game's CarIgnition.StartEngine (VPP start key), low range through the
 *   input block's networked GearboxIsLowTransferCase flag, diff locks through Channel.Settings.
 * - The game has no AI driver of its own (only animal AI, VPP's random/clone inputs).
 * - Winch: VehicleWinchSystem.AttachToPlayerCarAnchorPoint / AttachToMovableObjectAnchorPoint (what the attach RPCs
 *   run on the state authority), RopeLength to reel in, DetachOwnWinch to let go (see ConvoyWinch).
 * - Meteorites are extracted by enough taut ropes pulling the same way; players near it get the pebbles.
 * - Repair = CarDamageSystem.HealDamage(healer, maxDamage, reason), exactly what a repair kit does on the host.
 * - There is no fuel or tyre damage in the game; deep water floods the engine (NetworkedVehicleStatus).
 *
 * Structure:
 * - ConvoyAi (this file): the manager and "brain": spawning, the shared blackboard (incidents, holds, the meteorite
 *   task), helper assignment, and each car's top-level behaviour every frame.
 * - ConvoyAgent: one AI car (components, telemetry, mode). ConvoyMembers: the player cars.
 * - ConvoyTrail: the leader's breadcrumb trail, the road the convoy follows.
 * - ConvoyDriver: steering (pure pursuit), speed control, gear/direction, three-point turns, writing the data bus.
 * - ConvoySense: raycasts for ground, slope, water and obstacles (throttled, no allocation).
 * - ConvoyRecovery: stuck detection (players and AI) and the AI's own recovery ladder.
 * - ConvoyJobs: tow, repair, flip help, meteorite pulling. ConvoyWinch: the winch calls.
 * - ConvoyOverlay: debug overlay + status line. ConvoyFeature: settings, menu, hotkeys. ConvoyPatches: hooks.
 *
 * Behaviour per car (modes): Starting -> Follow (trail, gap scaled with speed) / Wait (convoy holds) / Regroup
 * (back to the trail) / Recover (own ladder: back off + new line, rock with low range + diff locks, ask for a tow,
 * last resort reposition) / Help (on a job) / BeingTowed (gentle throttle towards the helper) / Park.
 *
 * Teamwork: every half second the brain looks at all convoy members (players near the convoy and the AI cars):
 * stuck, flipped, in deep water or badly damaged makes an incident; the convoy waits; the best-placed free AI car
 * (shortest way along the trail, no other car in between, has a winch / repair kits) gets the job, drives there, does
 * it, and rejoins. Players far behind make the convoy wait too. A stopped leader next to a meteorite makes a team of
 * AI cars pull it out.
 *
 * Policy: host or solo only (GameState.IsAuthority), never as a public guest. Spawning, repositioning, winching and
 * repairing mark cheats as used. The cars are despawned on Dismiss, when leaving the session and on quit. At most
 * MaxCars cars. No NetworkString writes; no patches on per-frame Unity methods with struct parameters.
 */
internal static class ConvoyAi
{
    public static readonly List<ConvoyAgent> Agents = new();
    private static readonly Dictionary<IntPtr, ConvoyAgent> ByCar = new();
    public static readonly ConvoyTrail Trail = new();
    public static readonly List<Incident> Incidents = new();
    public static readonly List<CarBody> Bodies = new();
    public static readonly List<string> Events = new();

    public static PlayerMember Leader { get; private set; }
    /// <summary>Raw PlayerRef of the chosen leader; int.MinValue = always the local player.</summary>
    public static int LeaderRaw = int.MinValue;
    public static bool Holding;          // "hold" ordered by the user
    public static bool Parked;
    public static string HoldReason;     // why the convoy is waiting right now (null = driving)
    public static FetchTask Fetch;
    public static float LeaderS;
    public static string LastEvent = "";
    public static float LastEventAt = -100f;

    public static IntPtr LocalCarPtr { get; private set; }
    private static VehicleBase _localCar;

    private struct PendingSpawn
    {
        public ItemCatalog.Item Model;
        public Vector3 Pos;
        public float Yaw;
    }

    private static readonly Queue<PendingSpawn> Pending = new();
    private static float _nextSpawnAt;
    private static int _nameCounter;
    private static IntPtr _runnerPtr;
    private static float _nextBrain, _leaderStillSince;
    private static readonly HashSet<string> Logged = new();
    private static readonly HashSet<IntPtr> DoneMeteorites = new();

    public static int PendingCount => Pending.Count;
    public static bool Active => Agents.Count > 0 || Pending.Count > 0;

    // ------------------------------------------------------------------ logging / events

    internal static void LogOnce(string key, string message)
    {
        if (Logged.Add(key)) ConvoyPlugin.Logger.LogWarning("[Convoy] " + message + " (logged once)");
    }

    internal static void Event(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        LastEvent = text;
        LastEventAt = Time.unscaledTime;
        Events.Add($"{DateTime.Now:HH:mm:ss}  {text}");
        if (Events.Count > 12) Events.RemoveAt(0);
        DebugLog.Info(ConvoyPlugin.ModId, text);
        ConvoyPlugin.Logger.LogInfo("[Convoy] " + text);
    }

    // ------------------------------------------------------------------ policy

    /// <summary>Null when the convoy can run here, else why not.</summary>
    public static string BlockReason()
    {
        if (GameState.Runner == null) return "Load into a world first.";
        if (GameState.IsPublicGuest || !GameState.IsAuthority)
            return "Host or solo only: the host's game drives the AI cars for everyone.";
        try { if (CheatsManager.Instance == null) return "The game's cheat manager isn't loaded in this session."; }
        catch (Exception ex) { DebugLog.Error("convoy", ex, "ConvoyAi.BlockReason"); return "The game's cheat manager isn't available."; }
        return null;
    }

    internal static bool IsAgentCar(IntPtr carPtr) => ByCar.ContainsKey(carPtr);

    internal static ConvoyAgent AgentByRb(IntPtr rb)
    {
        for (int i = 0; i < Agents.Count; i++) if (Agents[i].RbPtr == rb) return Agents[i];
        return null;
    }

    // ------------------------------------------------------------------ physics step (called from the patch)

    public static void PhysicsStep(VPVehicleController v)
    {
        if (ByCar.Count == 0 || v is null) return;
        if (!ByCar.TryGetValue(v.Pointer, out var a)) return;
        if (!GameState.IsAuthority) return;
        ConvoyDriver.Apply(a, v);
    }

    // ------------------------------------------------------------------ spawning

    /// <summary>Spawns <paramref name="count"/> AI cars in a line behind the leader (or behind the last AI car).</summary>
    public static string Spawn(int count)
    {
        var block = BlockReason();
        if (block != null) return "Blocked: " + block;
        UpdateLocalCar();
        ConvoyMembers.Tick(0f, IsAgentCar);
        ResolveLeader();
        if (Leader == null) return "Failed: no leader car (get in your car first).";
        int max = Mathf.RoundToInt(ConvoyFeature.MaxCars.Value);
        int room = max - Agents.Count - Pending.Count;
        if (room <= 0) return $"Limit reached ({max} AI cars). Raise it in the settings or dismiss some.";
        count = Mathf.Clamp(count, 1, room);
        var models = ConvoyModels.Pick(count, Leader);
        if (models.Count == 0) return "Failed: no car models available (load into a world first).";

        // Line them up behind the last car of the convoy, on safe ground, facing the leader's way.
        Vector3 basePos = Leader.Pos, back = -Leader.Fwd;
        float yaw = Quaternion.LookRotation(Leader.Fwd, Vector3.up).eulerAngles.y;
        float start = 13f;
        ConvoyAgent last = Tail();
        if (last != null)
        {
            basePos = last.Pos;
            back = -last.Fwd;
            yaw = Quaternion.LookRotation(last.Fwd, Vector3.up).eulerAngles.y;
            start = 10f;
        }
        var ctx = new SafeSpot.Context();
        foreach (var b in Bodies) ctx.Occupied.Add(b.Pos);
        foreach (var p in ConvoyMembers.Players) ctx.Occupied.Add(p.Pos);
        foreach (var a in Agents) ctx.Occupied.Add(a.Pos);
        var spots = new List<Vector3>();
        float gap = Mathf.Max(9f, ConvoyFeature.Spacing.Value * 0.8f);
        float along = start;
        for (int i = 0; i < count; i++)
        {
            SafeSpot.Result res = default;
            for (int tryN = 0; tryN < 3 && !res.Ok; tryN++)
            {
                res = SafeSpot.Find(basePos + back * along, yaw, ctx);
                if (!res.Ok) along += 6f;
            }
            if (!res.Ok) break;
            ctx.Occupied.Add(res.Ground);
            spots.Add(res.Ground);
            along += gap;
        }
        if (spots.Count == 0) return "Failed: no safe ground behind the leader (move somewhere flatter).";

        if (Agents.Count == 0 && Pending.Count == 0)
        {
            // A fresh trail: from the last spawn spot up to the leader, so the cars have a road from the start.
            var seed = new List<Vector3>();
            for (int i = spots.Count - 1; i >= 0; i--) seed.Add(spots[i]);
            seed.Add(Leader.Pos);
            Trail.Seed(seed, 6f);
            Holding = false;
            Parked = false;
        }
        AdvantageFlag.MarkUsed();
        for (int i = 0; i < spots.Count; i++)
            Pending.Enqueue(new PendingSpawn { Model = models[i % models.Count], Pos = spots[i], Yaw = yaw });
        _nextSpawnAt = 0f;
        string partial = spots.Count < count ? $" (only {spots.Count} safe spot(s) found)" : "";
        DebugLog.Info(ConvoyPlugin.ModId, $"spawning {spots.Count} of {count} asked AI car(s) behind {Leader.Name}{partial}; {Agents.Count} out, {Pending.Count} queued (limit {max})");
        return $"Spawning {spots.Count} AI car(s) behind {Leader.Name}{partial}…";
    }

    private static void SpawnPending()
    {
        if (Pending.Count == 0 || Time.unscaledTime < _nextSpawnAt) return;
        _nextSpawnAt = Time.unscaledTime + 0.4f; // one per 0.4 s: no hitch from spawning a whole convoy in one frame
        var ps = Pending.Dequeue();
        if (BlockReason() != null) { Pending.Clear(); return; }
        var obj = ConvoyCars.Spawn(ps.Model, ps.Pos + Vector3.up * 1.1f, ps.Yaw, out string err);
        if (obj == null)
        {
            Event($"Couldn't spawn {ps.Model?.Name}: {err}");
            return;
        }
        var a = new ConvoyAgent();
        if (!a.Bind(obj, ps.Model.Name))
        {
            Event($"Spawned {ps.Model.Name} has no drivable car on it; removed.");
            try { GameState.Runner?.Despawn(obj); } catch (Exception ex) { DebugLog.Error("convoy", ex, "ConvoyAi.SpawnPending"); }
            return;
        }
        _nameCounter++;
        a.Name = $"AI {_nameCounter}";
        a.RepairsLeft = Mathf.RoundToInt(ConvoyFeature.RepairKits.Value);
        a.LaneOffset = ConvoyFeature.LaneOffset.Value * ((_nameCounter % 2 == 0) ? 1f : -1f);
        a.SetMode(AgentMode.Starting, "starting the engine");
        Agents.Add(a);
        ByCar[a.CarPtr] = a;
        a.KeepEngineRunning();
        ConvoyPlugin.Logger.LogInfo($"[Convoy] Spawned {a.Name} ({a.Model}) wheelbase {a.Wheelbase:0.00} m, steer {a.MaxSteerDeg:0}°, winch {(ConvoyWinch.HasWinch(a) ? "yes" : "no")}");
    }

    /// <summary>Despawns every AI car (detaching ropes first).</summary>
    public static string Dismiss(string why = null)
    {
        Pending.Clear();
        if (Agents.Count == 0) { ResetState(); return "No AI cars to dismiss."; }
        foreach (var inc in Incidents) inc.Job?.Abort("convoy dismissed");
        Fetch?.Abort("convoy dismissed");
        foreach (var a in Agents) { a.Job?.Abort("convoy dismissed"); ConvoyWinch.Detach(a); }
        var runner = GameState.Runner;
        int n = 0;
        foreach (var a in Agents)
        {
            try
            {
                if (runner != null && a.Obj != null && a.Obj.IsValid && a.Obj.HasStateAuthority)
                {
                    runner.Despawn(a.Obj);
                    n++;
                }
            }
            catch (Exception e) { DebugLog.Error("convoy", e, "ConvoyAi.Dismiss"); LogOnce("despawn", "despawning an AI car: " + e.Message); }
        }
        ResetState();
        string msg = $"Dismissed {n} AI car(s)" + (why != null ? $" ({why})." : ".");
        Event(msg);
        return msg;
    }

    /// <summary>Removes one AI car (the last one in the line).</summary>
    public static string DismissOne()
    {
        var a = Tail();
        if (a == null) return "No AI cars.";
        Remove(a, despawn: true);
        return $"Dismissed {a.Name}.";
    }

    private static void Remove(ConvoyAgent a, bool despawn)
    {
        a.Job?.Abort("removed");
        a.Job = null;
        foreach (var inc in Incidents)
        {
            if (inc.Agent == a || inc.Helper == a) { inc.Job?.Abort("car removed"); inc.Closed = true; }
        }
        foreach (var b in Agents) if (b.TowedBy == a) b.TowedBy = null;
        if (despawn)
        {
            ConvoyWinch.Detach(a);
            try
            {
                var runner = GameState.Runner;
                if (runner != null && a.Obj != null && a.Obj.IsValid && a.Obj.HasStateAuthority) runner.Despawn(a.Obj);
            }
            catch (Exception e) { DebugLog.Error("convoy", e, "ConvoyAi.Remove"); LogOnce("despawn1", "despawning an AI car: " + e.Message); }
        }
        Agents.Remove(a);
        ByCar.Remove(a.CarPtr);
        if (Fetch != null && Fetch.Team.Contains(a)) Fetch.Abort($"{a.Name} left");
    }

    private static void ResetState()
    {
        Agents.Clear();
        ByCar.Clear();
        Incidents.Clear();
        Trail.Clear();
        Fetch = null;
        Holding = Parked = false;
        HoldReason = null;
        DoneMeteorites.Clear();
        _nameCounter = 0;
    }

    /// <summary>A new or ended session: the cars went with it, forget them (nothing to despawn).</summary>
    private static void Forget(string why)
    {
        if (Agents.Count == 0 && Pending.Count == 0) return;
        Pending.Clear();
        ResetState();
        ConvoyMembers.Clear();
        ConvoyPlugin.Logger.LogInfo("[Convoy] " + why + ": forgot the AI cars.");
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
        catch (Exception e) { DebugLog.Error("convoy", e, "ConvoyAi.OnLeavingSession"); LogOnce("leave", "cleaning up when leaving: " + e.Message); ResetState(); }
    }

    // ------------------------------------------------------------------ per frame

    public static void Tick()
    {
        UpdateLocalCar();
        if (!Active)
        {
            if (Incidents.Count > 0) Incidents.Clear();
            return;
        }
        var runner = GameState.Runner;
        IntPtr rp = IntPtr.Zero;
        try { rp = runner != null ? runner.Pointer : IntPtr.Zero; } catch (Exception ex) { DebugLog.Error("convoy", ex, "ConvoyAi.Tick"); }
        if (_runnerPtr == IntPtr.Zero) _runnerPtr = rp;
        if (rp != _runnerPtr)
        {
            _runnerPtr = rp;
            Forget("session changed");
            return;
        }
        if (runner == null || !GameState.IsAuthority) return;

        float dt = Mathf.Min(Time.deltaTime, 0.1f);
        SpawnPending();
        for (int i = Agents.Count - 1; i >= 0; i--)
        {
            if (!Agents[i].Alive)
            {
                Event($"{Agents[i].Name} is gone.");
                Remove(Agents[i], despawn: false);
            }
        }

        ConvoyMembers.Tick(dt, IsAgentCar);
        ResolveLeader();
        if (Leader != null)
        {
            if (Trail.Record(Leader.Pos, Leader.Speed))
                Event($"{Leader.Name} jumped to a new place: the convoy regroups there.");
            LeaderS = Trail.EndS + ConvoyTrail.Flat(Leader.Pos - Trail.PointAt(Trail.EndS)).magnitude;
        }
        BuildBodies();

        foreach (var a in Agents)
        {
            try
            {
                a.Refresh();
                UpdateProgress(a);
                bool trying = a.Mode is AgentMode.Follow or AgentMode.Regroup or AgentMode.Recover or AgentMode.Help
                              && a.TargetSpeed != 0f && Mathf.Abs(a.TargetSpeed) > 0.4f;
                float effort = trying ? a.Cmd.Throttle : 0f;
                a.Stuck.Update(dt, a.Pos, a.Speed, effort, a.UpDot, a.WaterDepth, a.Flooded, ConvoyFeature.StuckSeconds.Value);
            }
            catch (Exception e) { DebugLog.Error("convoy", e, "ConvoyAi.Tick"); LogOnce("refresh", "reading an AI car: " + e); }
        }

        if (Time.unscaledTime >= _nextBrain)
        {
            _nextBrain = Time.unscaledTime + 0.5f;
            try { Brain(); }
            catch (Exception e) { DebugLog.Error("convoy", e, "ConvoyAi.Tick"); LogOnce("brain", "convoy brain: " + e); }
        }

        foreach (var a in Agents)
        {
            try { Behave(a, dt); }
            catch (Exception e)
            {
                DebugLog.Error("convoy", e, "ConvoyAi.Tick");
                LogOnce("behave", "driving an AI car: " + e);
                a.Cmd = DriveCmd.Hold();
            }
        }

        if (Trail.Usable && Agents.Count > 0)
        {
            float minS = float.MaxValue;
            foreach (var a in Agents) if (a.S < minS) minS = a.S;
            Trail.PruneBefore(minS - 80f);
        }
    }

    private static void UpdateLocalCar()
    {
        var lc = GameState.LocalCar;
        if (lc != null)
        {
            _localCar = lc;
            LocalCarPtr = lc.Pointer;
            return;
        }
        try
        {
            if (_localCar == null || !_localCar.isActiveAndEnabled) { _localCar = null; LocalCarPtr = IntPtr.Zero; }
        }
        catch (Exception ex) { DebugLog.Error("convoy", ex, "ConvoyAi.UpdateLocalCar"); _localCar = null; LocalCarPtr = IntPtr.Zero; }
    }

    private static void ResolveLeader()
    {
        PlayerMember found = null;
        if (LeaderRaw != int.MinValue) found = ConvoyMembers.Find(LeaderRaw);
        if (found == null)
        {
            if (LeaderRaw != int.MinValue && Leader != null && Leader.Raw == LeaderRaw)
                Event($"{Leader.Name} left: you lead the convoy now.");
            if (found == null && LeaderRaw != int.MinValue && ConvoyMembers.Players.Count > 0) LeaderRaw = int.MinValue;
            foreach (var p in ConvoyMembers.Players) if (p.IsLocal) { found = p; break; }
        }
        if (found != Leader && found != null && Leader != null && Agents.Count > 0)
        {
            // New leader: the old trail leads somewhere else.
            Trail.Clear();
            foreach (var a in Agents) if (a.Job == null) a.SetMode(AgentMode.Regroup, "regrouping on the new leader");
        }
        Leader = found;
    }

    private static void BuildBodies()
    {
        Bodies.Clear();
        foreach (var p in ConvoyMembers.Players)
            Bodies.Add(new CarBody { Pos = p.Pos, Fwd = p.Fwd, Speed = p.Speed, IsPlayer = true, Rb = p.RbPtr });
        foreach (var a in Agents)
            Bodies.Add(new CarBody { Pos = a.Pos, Fwd = a.Fwd, Speed = a.Speed, IsPlayer = false, Rb = a.RbPtr });
    }

    private static void UpdateProgress(ConvoyAgent a)
    {
        if (!Trail.Usable) { a.OnTrail = false; a.TrailDist = float.MaxValue; return; }
        float s, d;
        bool far = a.TrailDist > 15f;
        if (a.TrailGen != Trail.Generation || (far && Time.frameCount % 30 == a.Id % 30))
        {
            Trail.ProjectGlobal(a.Pos, out s, out d);
            a.TrailGen = Trail.Generation;
        }
        else Trail.Project(a.Pos, a.S, 12f, 30f, out s, out d);
        a.S = s;
        a.TrailDist = d;
        a.OnTrail = d < (a.OnTrail ? 11f : 6f);
    }

    /// <summary>The AI car furthest back along the trail (null if none).</summary>
    public static ConvoyAgent Tail()
    {
        ConvoyAgent best = null;
        foreach (var a in Agents) if (best == null || a.S < best.S) best = a;
        return best;
    }

    /// <summary>The convoy car right ahead of an AI car on the trail (leader at the front).</summary>
    private static void Front(ConvoyAgent a, out float frontS, out float frontSpeed, out string frontName)
    {
        frontS = LeaderS;
        frontSpeed = Leader != null ? Mathf.Abs(Leader.Speed) : 0f;
        frontName = Leader?.Name ?? "leader";
        foreach (var b in Agents)
        {
            if (b == a || !b.OnTrail) continue;
            if (b.Mode is not (AgentMode.Follow or AgentMode.Wait or AgentMode.Starting or AgentMode.Recover or AgentMode.BeingTowed)) continue;
            if (b.S > a.S && b.S < frontS)
            {
                frontS = b.S;
                frontSpeed = Mathf.Abs(b.Speed);
                frontName = b.Name;
            }
        }
    }

    // ------------------------------------------------------------------ behaviour of one car

    private static void Behave(ConvoyAgent a, float dt)
    {
        a.HasDebugTarget = false;
        if (!a.EngineRunning) a.KeepEngineRunning();

        if (a.Mode == AgentMode.Starting)
        {
            a.Cmd = DriveCmd.Hold();
            if (a.EngineRunning || a.InMode > 15f) a.SetMode(AgentMode.Regroup, "joining the convoy");
            else return;
        }

        if (a.Job != null)
        {
            if (a.Stuck.IsStuck && a.Job.AllowsSelfRecovery && a.Stuck.StuckFor > 1f)
            {
                // The helper got itself stuck: hand the job back and sort itself out first.
                var inc = a.Job.Incident;
                a.Job.Abort($"{a.Name} got stuck itself");
                if (inc != null) { inc.Helper = null; inc.Job = null; inc.NextAssignAt = Time.time + 2f; }
                a.Job = null;
                Event($"{a.Name} got stuck on the way; someone else will help.");
            }
            else
            {
                a.Job.Update(dt);
                if (a.Job.Done) EndJob(a);
                return;
            }
        }

        if (a.TowedBy != null) { BeingTowed(a, dt); return; }

        if (a.Stuck.IsStuck && !Parked) { Recover(a, dt); return; }
        if (a.Mode == AgentMode.Recover)
        {
            Event($"{a.Name} is free again.");
            a.Recovery.Level = 0;
            a.SetMode(AgentMode.Regroup, "back on track");
        }

        if (Parked)
        {
            a.SetMode(AgentMode.Park, "parked");
            a.Cmd = DriveCmd.Hold();
            return;
        }

        if (a.MakeWayUntil > Time.time && MakeWay(a, dt)) return;

        if (HoldReason != null && a.OnTrail)
        {
            Wait(a, dt);
            return;
        }

        if (!Trail.Usable || !a.OnTrail || a.Mode == AgentMode.Regroup && a.TrailDist > 4f)
        {
            Regroup(a, dt);
            return;
        }
        Follow(a, dt);
    }

    private static void Follow(ConvoyAgent a, float dt)
    {
        Front(a, out float frontS, out float frontSpeed, out string frontName);
        float cap = ConvoyDriver.CarCap(a, 1, Bodies, IntPtr.Zero, 3.5f, 6f);
        ConvoyDriver.FollowTrail(a, Trail, frontS, frontSpeed, cap, dt);
        float gap = frontS - a.S;
        a.SetMode(AgentMode.Follow, gap > 60f ? $"catching up with {frontName} ({gap:0} m)" : $"following {frontName}");
    }

    private static void Wait(ConvoyAgent a, float dt)
    {
        // Close up to formation distance slowly, then stand still with the handbrake on.
        Front(a, out float frontS, out float frontSpeed, out _);
        float gap = frontS - a.S;
        float want = ConvoyFeature.Spacing.Value;
        if (gap > want * 1.6f && a.TrailDist < 6f)
        {
            float cap = Mathf.Min(3f, ConvoyDriver.CarCap(a, 1, Bodies, IntPtr.Zero, 3.5f, 6f));
            ConvoyDriver.FollowTrail(a, Trail, frontS, 0f, cap, dt);
        }
        else
        {
            var hold = DriveCmd.Hold(Mathf.Abs(a.Speed) < 0.5f);
            hold.Steer = a.SmoothedSteer;
            a.Cmd = hold;
            a.TargetSpeed = 0f;
        }
        a.SetMode(AgentMode.Wait, HoldReason);
    }

    private static void Regroup(ConvoyAgent a, float dt)
    {
        if (Leader == null) { a.Cmd = DriveCmd.Hold(); a.SetMode(AgentMode.Wait, "no leader"); return; }
        if (!Trail.Usable)
        {
            // No trail yet (new leader / teleport): drive to the leader and start from there.
            float dl = a.DistanceTo(Leader.Pos);
            if (ConvoyFeature.CatchUp.Value && dl > ConvoyFeature.CatchUpDistance.Value) { CatchUp(a); return; }
            var goal = Leader.Pos - Leader.Fwd * (ConvoyFeature.Spacing.Value + 4f);
            ConvoyDriver.DriveTo(a, goal, 5f, 13f, IntPtr.Zero, true, dt);
            a.SetMode(AgentMode.Regroup, $"driving to {Leader.Name}");
            return;
        }
        if (a.OnTrail && a.TrailDist < 4f)
        {
            a.SetMode(AgentMode.Follow, "back in line");
            Follow(a, dt);
            return;
        }
        if (ConvoyFeature.CatchUp.Value && a.TrailDist > ConvoyFeature.CatchUpDistance.Value) { CatchUp(a); return; }
        // Rejoin a bit ahead of the closest trail point (never past the car in front of that spot).
        float target = Mathf.Min(a.S + 7f, LeaderS - ConvoyFeature.Spacing.Value);
        var p = Trail.PointAt(Mathf.Max(Trail.StartS, target));
        ConvoyDriver.DriveTo(a, p, 2.5f, 11f, IntPtr.Zero, true, dt);
        a.SetMode(AgentMode.Regroup, $"rejoining the trail ({a.TrailDist:0} m away)");
    }

    /// <summary>Far away (left behind, leader teleported): put the car behind the convoy's last car (host power).</summary>
    private static void CatchUp(ConvoyAgent a)
    {
        if (Time.time < a.NextCatchUp) { a.Cmd = DriveCmd.Hold(); return; }
        a.NextCatchUp = Time.time + 8f;
        Vector3 pos;
        float yaw;
        var tail = TailExcept(a);
        if (Trail.Usable)
        {
            float s = tail != null ? tail.S - ConvoyFeature.Spacing.Value : LeaderS - ConvoyFeature.Spacing.Value * (1 + Agents.IndexOf(a));
            s = Mathf.Max(Trail.StartS, s);
            pos = Trail.PointAt(s, out var f, out _);
            yaw = Quaternion.LookRotation(f, Vector3.up).eulerAngles.y;
        }
        else
        {
            pos = Leader.Pos - Leader.Fwd * (ConvoyFeature.Spacing.Value + 6f);
            yaw = Quaternion.LookRotation(Leader.Fwd, Vector3.up).eulerAngles.y;
        }
        if (Relocate(a, pos, yaw, "caught up with the convoy")) a.SetMode(AgentMode.Regroup, "caught up");
    }

    private static ConvoyAgent TailExcept(ConvoyAgent ex)
    {
        ConvoyAgent best = null;
        foreach (var a in Agents)
            if (a != ex && a.OnTrail && (best == null || a.S < best.S)) best = a;
        return best;
    }

    private static void Recover(ConvoyAgent a, float dt)
    {
        var r = a.Recovery;
        var kind = a.Stuck.Kind;
        if (a.Mode != AgentMode.Recover)
        {
            r.Start(a.SmoothedSteer);
            a.SetMode(AgentMode.Recover, "stuck");
            Event($"{a.Name} is {StuckMonitor.Describe(kind)}.");
        }
        var cmd = DriveCmd.Hold(false);

        if (kind == StuckKind.Flipped)
        {
            a.Cmd = DriveCmd.Hold();
            if (ConvoyFeature.AllowAiReposition.Value)
            {
                if (a.Stuck.StuckFor > 2f && Time.time >= a.NextCatchUp)
                {
                    a.NextCatchUp = Time.time + 4f;
                    if (FlipUpright(a)) Event($"{a.Name} was put back on its wheels.");
                }
                a.Activity = "on its roof: flipping back";
            }
            else a.Activity = "on its roof: waiting for help";
            return;
        }

        bool water = kind == StuckKind.Water;
        switch (r.Level)
        {
            case 0: // back off along the way it came, then try a slightly different line
                cmd.LowRange = true;
                cmd.DiffLock = true;
                if (r.Step == 0)
                {
                    cmd.Steer = ConvoyDriver.SmoothSteer(a, water ? 0f : r.Side * 0.5f, dt);
                    cmd.Dir = -1;
                    cmd.Throttle = 0.65f;
                    if (r.InStep > (water ? 4f : 2.3f)) r.NextStep();
                }
                else if (r.Step == 1)
                {
                    cmd.Steer = ConvoyDriver.SmoothSteer(a, -r.Side * 0.55f, dt);
                    cmd.Dir = 1;
                    cmd.Throttle = 0.7f;
                    if (r.InStep > 3f)
                    {
                        // Try a line a metre or two to the side for the next stretch.
                        a.Nav.Nudge = -r.Side * 1.8f;
                        a.Nav.NudgeUntil = Time.time + 12f;
                        r.NextLevel();
                    }
                }
                a.Activity = $"{StuckMonitor.Describe(kind)}: {r.Describe()}";
                break;

            case 1: // rock it: forward/back with everything locked
                cmd.LowRange = true;
                cmd.DiffLock = true;
                cmd.Dir = r.Step % 2 == 0 ? (water ? -1 : 1) : (water ? 1 : -1);
                cmd.Throttle = 1f;
                cmd.Steer = ConvoyDriver.SmoothSteer(a, r.Step % 4 < 2 ? r.Side * 0.3f : -r.Side * 0.3f, dt);
                if (r.InStep > 1.4f) r.NextStep();
                if (r.Step >= 8) r.NextLevel();
                a.Activity = $"{StuckMonitor.Describe(kind)}: {r.Describe()}";
                break;

            case 2: // ask for a tow; keep trying gently now and then
                r.TowRequested = true;
                bool helperComing = HelperFor(a) != null;
                if (!helperComing && (!ConvoyFeature.TowAi.Value || !AnyFreeHelper(a, needWinch: true)) && r.InLevel > 10f)
                {
                    r.NextLevel();
                    break;
                }
                if (r.InLevel > ConvoyFeature.TowWaitSeconds.Value && !helperComing) { r.NextLevel(); break; }
                cmd = DriveCmd.Hold(false);
                if (!helperComing && r.InLevel % 7f > 5.5f)
                {
                    cmd.Dir = (int)(r.InLevel / 7f) % 2 == 0 ? 1 : -1;
                    cmd.Throttle = 0.6f;
                    cmd.Brake = 0f;
                    cmd.Handbrake = 0f;
                    cmd.LowRange = cmd.DiffLock = true;
                }
                a.Activity = helperComing ? $"waiting for {HelperFor(a).Name} to tow it" : $"{StuckMonitor.Describe(kind)}: {r.Describe()}";
                break;

            default: // last resort
                if (ConvoyFeature.AllowAiReposition.Value)
                {
                    if (Time.time >= a.NextCatchUp)
                    {
                        a.NextCatchUp = Time.time + 6f;
                        if (RescueReposition(a))
                        {
                            r.Rescues++;
                            Event($"{a.Name} couldn't get free and was moved back onto the trail.");
                        }
                    }
                    a.Activity = "being put back on safe ground";
                }
                else
                {
                    // No teleports allowed: keep asking for help.
                    r.Level = 2;
                    r.LevelStarted = Time.time;
                    a.Activity = "stuck: waiting for help";
                }
                cmd = DriveCmd.Hold();
                break;
        }
        if (cmd.Dir != 0 && cmd.Throttle > 0f) a.TargetSpeed = cmd.Dir * 2f;
        else a.TargetSpeed = 0f;
        a.Cmd = cmd;
    }

    private static void BeingTowed(ConvoyAgent a, float dt)
    {
        var h = a.TowedBy;
        if (h == null || !h.Alive || h.Job is not TowJob tj || tj.Done || tj.Incident?.Agent != a)
        {
            a.TowedBy = null;
            return;
        }
        a.SetMode(AgentMode.BeingTowed, $"being towed by {h.Name}");
        var cmd = DriveCmd.Hold(false);
        if (tj.Phase is "pull" or "reel")
        {
            var l = a.Local(h.Pos);
            int dir = l.z >= 0f ? 1 : -1;
            cmd.Dir = dir;
            cmd.Steer = ConvoyDriver.SmoothSteer(a, ConvoyDriver.Pursuit(a, h.Pos, dir < 0), dt);
            cmd.Throttle = tj.Phase == "pull" ? 0.45f : 0.2f;
            cmd.Brake = 0f;
            cmd.Handbrake = 0f;
            cmd.LowRange = cmd.DiffLock = true;
            a.TargetSpeed = dir * 1.5f;
        }
        else
        {
            cmd = DriveCmd.Hold(true);
            a.TargetSpeed = 0f;
        }
        a.Cmd = cmd;
    }

    /// <summary>Pull over to let a helper past: a few metres to the side, then wait there.</summary>
    private static bool MakeWay(ConvoyAgent a, float dt)
    {
        bool there = ConvoyDriver.DriveTo(a, a.MakeWaySpot, 2f, 4f, IntPtr.Zero, true, dt);
        a.SetMode(AgentMode.Wait, there ? $"making way for {a.MakeWayFor?.Name}" : $"pulling over for {a.MakeWayFor?.Name}");
        return true;
    }

    private static void EndJob(ConvoyAgent a)
    {
        var job = a.Job;
        a.Job = null;
        a.BusyUntil = Time.time + 3f;
        if (job == null) return;
        if (job.Incident != null)
        {
            var inc = job.Incident;
            inc.Job = null;
            inc.Helper = null;
            if (job.Failed)
            {
                inc.Attempts++;
                inc.Note = job.Result ?? "";
                inc.NextAssignAt = Time.time + 4f;
                if (inc.Manual || inc.Attempts >= 3)
                {
                    inc.Closed = true;
                    HandleUnsolvable(inc);
                }
            }
            else
            {
                inc.Closed = inc.Closed || inc.Manual || inc.Kind is IncidentKind.Damaged or IncidentKind.Flipped;
            }
        }
        if (!string.IsNullOrEmpty(job.Result) && job is not FetchJob) Event($"{a.Name}: {job.Result}");
        a.SetMode(AgentMode.Regroup, "rejoining after the job");
    }

    /// <summary>Help failed a few times: for an AI car, reposition it; for a player, a hint (or a reposition if allowed).</summary>
    private static void HandleUnsolvable(Incident inc)
    {
        if (inc.Agent != null)
        {
            inc.Agent.Recovery.Level = 3;
            return;
        }
        if (inc.Player != null && inc.Kind != IncidentKind.Damaged)
        {
            if (ConvoyFeature.AllowPlayerReposition.Value && inc.Car != null)
            {
                if (CarReset.Reset(inc.Car, out string why))
                {
                    AdvantageFlag.MarkUsed();
                    Event($"Couldn't tow {inc.Name} free: put the car back on its wheels instead.");
                }
                else Event($"Couldn't help {inc.Name}: {why}");
            }
            else Event($"Couldn't tow {inc.Name} free. Try the Unstuck button (or allow repositioning players).");
        }
    }

    // ------------------------------------------------------------------ the brain (2x per second)

    private static void Brain()
    {
        float now = Time.time;
        // Who counts as part of the convoy: the leader, and players near the leader or any AI car.
        float join = ConvoyFeature.JoinRadius.Value;
        foreach (var p in ConvoyMembers.Players)
        {
            if (p == Leader) { p.LastNear = now; continue; }
            if (Leader != null && ConvoyTrail.Flat(p.Pos - Leader.Pos).magnitude < join) { p.LastNear = now; continue; }
            foreach (var a in Agents)
                if (ConvoyTrail.Flat(p.Pos - a.Pos).magnitude < join) { p.LastNear = now; break; }
        }

        DetectIncidents();
        CloseIncidents();
        AssignHelpers();
        FetchBrain();
        ComputeHold();
        MakeWayChecks();
        LaggardChecks();
    }

    private static IncidentKind KindOf(StuckKind k) => k switch
    {
        StuckKind.Flipped => IncidentKind.Flipped,
        StuckKind.Water => IncidentKind.Water,
        _ => IncidentKind.Stuck,
    };

    private static bool StuckFamily(IncidentKind k) => k != IncidentKind.Damaged;

    private static Incident Ensure(IncidentKind kind, PlayerMember p, ConvoyAgent a, bool manual = false)
    {
        foreach (var inc in Incidents)
        {
            if (inc.Closed || !inc.SameTarget(p, a)) continue;
            if (StuckFamily(inc.Kind) == StuckFamily(kind))
            {
                if (inc.Kind != kind && inc.Job == null) inc.Kind = kind;
                inc.Manual |= manual;
                return inc;
            }
        }
        var n = new Incident { Kind = kind, Player = p, Agent = a, Manual = manual };
        Incidents.Add(n);
        Event(n.Describe() + (manual ? " (asked for help)" : "") + ".");
        return n;
    }

    private static void DetectIncidents()
    {
        bool helpPlayers = ConvoyFeature.HelpPlayers.Value;
        bool waitForStuck = ConvoyFeature.WaitForStuck.Value;
        float repairAt = ConvoyFeature.RepairAt.Value;
        foreach (var p in ConvoyMembers.Players)
        {
            if (!p.InConvoy) continue;
            if ((helpPlayers || waitForStuck) && p.Stuck.IsStuck) Ensure(KindOf(p.Stuck.Kind), p, null);
            if (helpPlayers && ConvoyFeature.RepairPlayers.Value && p.Damage01 >= repairAt) Ensure(IncidentKind.Damaged, p, null);
        }
        foreach (var a in Agents)
        {
            if (a.Mode == AgentMode.Recover && a.Recovery.TowRequested && ConvoyFeature.TowAi.Value && a.Stuck.Kind != StuckKind.Flipped)
                Ensure(KindOf(a.Stuck.Kind), null, a);
            if (a.Stuck.Kind == StuckKind.Flipped && !ConvoyFeature.AllowAiReposition.Value)
                Ensure(IncidentKind.Flipped, null, a);
            if (ConvoyFeature.RepairAi.Value && a.Damage01 >= repairAt && a.Job == null) Ensure(IncidentKind.Damaged, null, a);
        }
    }

    private static void CloseIncidents()
    {
        float now = Time.time;
        foreach (var inc in Incidents)
        {
            if (inc.Closed) continue;
            string why = null;
            if (!inc.TargetAlive) why = "gone";
            else if (inc.Player != null && !inc.Player.InConvoy && !inc.Manual) why = "left the convoy";
            else if (inc.Kind == IncidentKind.Damaged && inc.Damage01 < 0.12f) why = "repaired";
            else if (StuckFamily(inc.Kind) && !inc.Manual && !inc.Stuck.IsStuck && (inc.Job == null || !inc.Job.Busy))
                why = "free again";
            else if (inc.Manual && inc.Job == null && now - inc.Since > 120f) why = "timed out";
            if (why == null) continue;
            inc.Closed = true;
            inc.Note = why;
            if (inc.Job != null && !inc.Job.Done) inc.Job.Abort("not needed any more (" + why + ")");
        }
        Incidents.RemoveAll(i => i.Closed && (i.Job == null || i.Job.Done));
    }

    private static void AssignHelpers()
    {
        float now = Time.time;
        foreach (var inc in Incidents)
        {
            if (inc.Closed || inc.Helper != null || now < inc.NextAssignAt) continue;
            if (inc.IsPlayer && !ConvoyFeature.HelpPlayers.Value) continue;
            if (inc.Agent != null && StuckFamily(inc.Kind) && inc.Kind != IncidentKind.Flipped && inc.Agent.Recovery.Level < 2 && !inc.Manual)
                continue; // let it try on its own first
            bool flip = inc.Kind == IncidentKind.Flipped;
            if (flip && inc.IsPlayer && !ConvoyFeature.FlipPlayers.Value) { inc.Note = "on its roof (flip help is off)"; continue; }
            bool repair = inc.Kind == IncidentKind.Damaged;
            var best = BestHelper(inc, needWinch: !flip && !repair, needKit: repair);
            if (best == null)
            {
                inc.Note = repair ? "no AI car with repair kits free" : "no free AI car with a winch";
                inc.NextAssignAt = now + 3f;
                continue;
            }
            ConvoyJob job = inc.Kind switch
            {
                IncidentKind.Damaged => new RepairJob(best, inc),
                IncidentKind.Flipped => new FlipJob(best, inc),
                _ => new TowJob(best, inc),
            };
            inc.Helper = best;
            inc.Job = job;
            inc.LastHelperAt = now;
            inc.Note = $"{best.Name} is on the way";
            best.Job = job;
            best.MakeWayUntil = 0f;
            best.SetMode(AgentMode.Help, job.Title);
            Event($"{best.Name} goes to help {inc.Name} ({job.Title}).");
        }
    }

    private static bool AnyFreeHelper(ConvoyAgent except, bool needWinch)
    {
        foreach (var a in Agents)
            if (a != except && FreeForJob(a) && (!needWinch || ConvoyWinch.HasWinch(a))) return true;
        return false;
    }

    private static bool FreeForJob(ConvoyAgent a)
    {
        if (!a.Alive || a.Job != null || a.TowedBy != null || !a.EngineRunning) return false;
        if (a.Mode is AgentMode.Recover or AgentMode.Starting or AgentMode.BeingTowed) return false;
        if (a.Stuck.IsStuck || a.Damage01 > 0.92f || Time.time < a.BusyUntil) return false;
        return true;
    }

    private static ConvoyAgent HelperFor(ConvoyAgent target)
    {
        foreach (var inc in Incidents)
            if (!inc.Closed && inc.Agent == target && inc.Helper != null) return inc.Helper;
        return null;
    }

    private static ConvoyAgent BestHelper(Incident inc, bool needWinch, bool needKit)
    {
        ConvoyAgent best = null;
        float bestCost = float.MaxValue;
        float targetS = 0f, targetTrailDist = float.MaxValue;
        if (Trail.Usable) Trail.ProjectGlobal(inc.Pos, out targetS, out targetTrailDist);
        foreach (var a in Agents)
        {
            if (a == inc.Agent || !FreeForJob(a)) continue;
            if (needWinch && !ConvoyWinch.HasWinch(a)) continue;
            if (needKit && a.RepairsLeft <= 0) continue;
            float cost;
            if (a.OnTrail && targetTrailDist < 12f)
            {
                float ds = targetS - a.S;
                int between = 0;
                foreach (var b in Agents)
                {
                    if (b == a || b == inc.Agent || !b.OnTrail) continue;
                    if ((b.S > Mathf.Min(a.S, targetS) + 2f) && (b.S < Mathf.Max(a.S, targetS) - 2f)) between++;
                }
                cost = Mathf.Abs(ds) + (ds < 0f ? 20f : 0f) + between * 25f;
            }
            else cost = a.DistanceTo(inc.Pos) * 1.3f;
            cost += a.Damage01 * 30f;
            if (cost < bestCost) { bestCost = cost; best = a; }
        }
        if (best != null && bestCost > ConvoyFeature.HelpRange.Value) return null;
        return best;
    }

    private static void ComputeHold()
    {
        HoldReason = null;
        if (Holding) { HoldReason = "holding (your order)"; return; }
        if (Fetch != null && !Fetch.Done) { HoldReason = "waiting while the meteorite is pulled out"; return; }
        if (ConvoyFeature.WaitForStuck.Value)
        {
            foreach (var inc in Incidents)
            {
                if (inc.Closed) continue;
                if (inc.Kind == IncidentKind.Damaged && inc.Job == null) continue; // repairs happen on the move
                HoldReason = "waiting: " + inc.Describe() + (inc.Helper != null ? $", {inc.Helper.Name} is helping" : "");
                return;
            }
        }
        if (ConvoyFeature.WaitForBehind.Value)
        {
            foreach (var p in ConvoyMembers.Players)
            {
                if (p == Leader || !p.InConvoy) continue;
                float nearest = Leader != null ? ConvoyTrail.Flat(p.Pos - Leader.Pos).magnitude : float.MaxValue;
                foreach (var a in Agents) nearest = Mathf.Min(nearest, a.DistanceTo(p.Pos));
                if (nearest > ConvoyFeature.BehindDistance.Value)
                {
                    HoldReason = $"waiting for {p.Name} to catch up ({nearest:0} m)";
                    return;
                }
            }
        }
    }

    /// <summary>A helper blocked by a waiting AI car asks it to pull over.</summary>
    private static void MakeWayChecks()
    {
        foreach (var h in Agents)
        {
            if (h.Job == null || !h.Job.Moving || Mathf.Abs(h.Speed) > 1f || h.Job.InPhase < 2f) continue;
            int dir = h.Cmd.Dir == 0 ? 1 : h.Cmd.Dir;
            foreach (var b in Agents)
            {
                if (b == h || b.Job != null || b.TowedBy != null || b.MakeWayUntil > Time.time) continue;
                if (b.Mode is not (AgentMode.Wait or AgentMode.Follow or AgentMode.Park)) continue;
                var l = h.Local(b.Pos);
                if (l.z * dir <= 0f || l.z * dir > 14f || Mathf.Abs(l.x) > 3.5f) continue;
                // Pull over to the side with more room, a few metres forward.
                var right = b.Right;
                var pr = ConvoySense.Profile(b, right, 3f, 6f);
                var pl = ConvoySense.Profile(b, -right, 3f, 6f);
                float sr = pr.Ground ? -Mathf.Abs(pr.Climb) - pr.Water * 10f : -99f;
                float sl = pl.Ground ? -Mathf.Abs(pl.Climb) - pl.Water * 10f : -99f;
                float side = sr >= sl ? 1f : -1f;
                b.MakeWaySpot = b.Pos + right * (side * 5f) + b.Fwd * 4f;
                b.MakeWayFor = h;
                b.MakeWayUntil = Time.time + 14f;
                Event($"{b.Name} pulls over to let {h.Name} past.");
            }
        }
    }

    /// <summary>AI cars far behind their car in front (not stuck, just slow / lost): catch them up.</summary>
    private static void LaggardChecks()
    {
        if (!ConvoyFeature.CatchUp.Value || !Trail.Usable) return;
        foreach (var a in Agents)
        {
            if (a.Mode != AgentMode.Follow || a.Job != null) continue;
            Front(a, out float frontS, out _, out _);
            if (frontS - a.S > ConvoyFeature.CatchUpDistance.Value) CatchUp(a);
        }
    }

    // ------------------------------------------------------------------ meteorites

    private static void FetchBrain()
    {
        if (Fetch != null)
        {
            Fetch.Update();
            if (Fetch.Done)
            {
                Event(Fetch.Result);
                if (Fetch.MeteoritePtr != IntPtr.Zero) DoneMeteorites.Add(Fetch.MeteoritePtr);
                Fetch = null;
            }
            return;
        }
        if (!ConvoyFeature.FetchMeteorites.Value || Leader == null) return;
        foreach (var inc in Incidents) if (!inc.Closed && StuckFamily(inc.Kind)) return;
        if (Mathf.Abs(Leader.Speed) > 1.2f) { _leaderStillSince = Time.time; return; }
        if (Time.time - _leaderStillSince < 4f) return;
        var m = ConvoyMeteorites.Nearest(Leader.Pos, ConvoyFeature.FetchRadius.Value, DoneMeteorites);
        if (m != null) StartFetch(m, manual: false);
    }

    /// <summary>Sends a team of AI cars to pull out the meteorite nearest to the leader.</summary>
    public static string FetchNearest()
    {
        var block = BlockReason();
        if (block != null) return "Blocked: " + block;
        if (Agents.Count == 0) return "Spawn the convoy first.";
        if (Fetch != null) return "Already pulling a meteorite.";
        if (Leader == null) return "No leader car.";
        var m = ConvoyMeteorites.Nearest(Leader.Pos, 300f, null);
        if (m == null) return "No meteorite in the ground within 300 m.";
        return StartFetch(m, manual: true);
    }

    private static string StartFetch(Meteorite m, bool manual)
    {
        int required = ConvoyMeteorites.Required(m);
        int playersOn = ConvoyMeteorites.WinchedCount(m);
        int need = Mathf.Max(1, required - playersOn);
        var free = new List<ConvoyAgent>();
        foreach (var a in Agents) if (FreeForJob(a) && ConvoyWinch.HasWinch(a)) free.Add(a);
        var mp = m.transform.position;
        free.Sort((x, y) => x.DistanceTo(mp).CompareTo(y.DistanceTo(mp)));
        if (free.Count < need)
        {
            string msg = $"This meteorite needs {required} winch(es) pulling; only {free.Count} AI car(s) free.";
            if (!manual) { try { DoneMeteorites.Add(m.Pointer); } catch (Exception ex) { DebugLog.Error("convoy", ex, "ConvoyAi.StartFetch"); } }
            Event(msg);
            return msg;
        }
        var team = free.GetRange(0, need);
        Fetch = new FetchTask(m, team, required);
        if (Fetch.Done) { string r = Fetch.Result; Fetch = null; Event(r); return r; }
        foreach (var a in team)
        {
            var job = new FetchJob(Fetch, a);
            Fetch.Jobs.Add(job);
            a.Job = job;
            a.SetMode(AgentMode.Help, job.Title);
        }
        AdvantageFlag.MarkUsed();
        string text = $"{need} AI car(s) go to pull out the {ConvoyMeteorites.SizeName(m)} meteorite ({Fetch.DistanceToLeader():0} m away). " +
                      $"Stay within {ConvoyMeteorites.PrizeDistance(m):0} m of it to get the pebbles.";
        Event(text);
        return text;
    }

    // ------------------------------------------------------------------ orders

    public static string ToggleHold()
    {
        if (!Active) return "No convoy.";
        Holding = !Holding;
        Parked = false;
        return Holding ? "Convoy: hold position." : "Convoy: go.";
    }

    public static string TogglePark()
    {
        if (!Active) return "No convoy.";
        Parked = !Parked;
        Holding = false;
        return Parked ? "Convoy parked (engines on, handbrakes set)." : "Convoy: go.";
    }

    public static string Regroup()
    {
        if (!Active) return "No convoy.";
        Holding = Parked = false;
        int n = 0;
        foreach (var a in Agents)
        {
            if (a.Job != null) continue;
            a.Stuck.Reset();
            a.MakeWayUntil = 0f;
            if (!a.OnTrail || a.Mode is AgentMode.Wait or AgentMode.Park)
            {
                a.SetMode(AgentMode.Regroup, "regrouping");
                n++;
            }
        }
        return n > 0 ? $"Regrouping {n} car(s)." : "Everyone is already in line.";
    }

    /// <summary>Asks the convoy to tow the local player (or the leader) right now.</summary>
    public static string HelpMe()
    {
        var block = BlockReason();
        if (block != null) return "Blocked: " + block;
        if (Agents.Count == 0) return "Spawn the convoy first.";
        PlayerMember me = null;
        foreach (var p in ConvoyMembers.Players) if (p.IsLocal) { me = p; break; }
        me ??= Leader;
        if (me == null) return "No car to help.";
        me.LastNear = Time.time;
        var kind = me.UpDot < 0.35f ? IncidentKind.Flipped : me.WaterDepth > 0.8f ? IncidentKind.Water : IncidentKind.Stuck;
        var inc = Ensure(kind, me, null, manual: true);
        inc.NextAssignAt = 0f;
        return inc.Helper != null ? $"{inc.Helper.Name} is already on the way." : "Help requested: the nearest free AI car is coming.";
    }

    public static string DetachAll()
    {
        int n = 0;
        foreach (var a in Agents)
        {
            if (ConvoyWinch.Attached(a)) { ConvoyWinch.Detach(a); n++; }
        }
        return n > 0 ? $"Released {n} rope(s)." : "No AI ropes attached.";
    }

    public static string RefillKits()
    {
        int kits = Mathf.RoundToInt(ConvoyFeature.RepairKits.Value);
        foreach (var a in Agents) a.RepairsLeft = kits;
        return $"Every AI car has {kits} repair kit(s) again.";
    }

    public static string SetLeader(PlayerMember p)
    {
        if (p == null || p.IsLocal) { LeaderRaw = int.MinValue; return "You lead the convoy."; }
        LeaderRaw = p.Raw;
        return $"{p.Name} leads the convoy.";
    }

    // ------------------------------------------------------------------ host powers

    /// <summary>Moves an AI car to safe ground near a spot (detaches its rope first). Host only.</summary>
    internal static bool Relocate(ConvoyAgent a, Vector3 near, float yaw, string why)
    {
        try
        {
            var ctx = new SafeSpot.Context();
            ctx.Ignore(a.Car);
            foreach (var b in Bodies) if (b.Rb != a.RbPtr) ctx.Occupied.Add(b.Pos);
            var res = SafeSpot.Find(near, yaw, ctx);
            if (!res.Ok) { a.Activity = "no safe ground to move to: " + res.Why; return false; }
            ConvoyWinch.Detach(a);
            AdvantageFlag.MarkUsed();
            a.Car.Reposition(res.Ground + Vector3.up * 1.0f, Quaternion.Euler(0f, yaw, 0f));
            try
            {
                if (a.Rb != null && !a.Rb.isKinematic)
                {
                    a.Rb.linearVelocity = Vector3.zero;
                    a.Rb.angularVelocity = Vector3.zero;
                }
            }
            catch (Exception ex) { DebugLog.Error("convoy", ex, "ConvoyAi.Relocate"); }
            a.Stuck.Reset();
            a.Recovery.Level = 0;
            a.TrailGen = -1;
            a.Nav.Turning = false;
            a.SmoothedSteer = 0f;
            ConvoyPlugin.Logger.LogInfo($"[Convoy] {a.Name} repositioned ({why}).");
            return true;
        }
        catch (Exception e)
        {
            DebugLog.Error("convoy", e, "ConvoyAi.Relocate");
            LogOnce("relocate", "repositioning an AI car: " + e.Message);
            return false;
        }
    }

    /// <summary>The game's own reset (upright, on the ground under it).</summary>
    internal static bool FlipUpright(ConvoyAgent a)
    {
        ConvoyWinch.Detach(a);
        if (!CarReset.Reset(a.Car, out string why))
        {
            a.Activity = "couldn't flip: " + why;
            return false;
        }
        AdvantageFlag.MarkUsed();
        a.Stuck.Reset();
        a.Recovery.Level = 0;
        a.Recovery.Rescues++;
        a.SetMode(AgentMode.Regroup, "back on its wheels");
        return true;
    }

    /// <summary>Last resort for a stuck AI car: back onto the trail a few metres behind where it got stuck.</summary>
    private static bool RescueReposition(ConvoyAgent a)
    {
        Vector3 pos;
        float yaw;
        if (Trail.Usable && a.TrailDist < 30f)
        {
            pos = Trail.PointAt(Mathf.Max(Trail.StartS, a.S - 7f), out var f, out _);
            yaw = Quaternion.LookRotation(f, Vector3.up).eulerAngles.y;
        }
        else
        {
            pos = a.Pos - a.Fwd * 6f;
            yaw = Quaternion.LookRotation(a.Fwd, Vector3.up).eulerAngles.y;
        }
        if (!Relocate(a, pos, yaw, "rescue")) return false;
        // Take a slightly different line past the spot.
        a.Nav.Nudge = (a.Recovery.Rescues % 2 == 0 ? 1.6f : -1.6f);
        a.Nav.NudgeUntil = Time.time + 12f;
        a.SetMode(AgentMode.Regroup, "rescued");
        return true;
    }
}
