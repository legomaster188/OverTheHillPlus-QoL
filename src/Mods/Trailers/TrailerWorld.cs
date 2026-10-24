using System;
using System.Collections.Generic;
using Fusion;
using UnityEngine;

namespace OverTheHillPlus.Trailers;

/// <summary>
/// The trailers this peer knows of (every peer: host and guests alike). The host's messages say which network
/// objects are trailers and how they are hitched; this side finds the objects (they may arrive later than the
/// message, or only when they come into range), adds the hitch joint where this game simulates both bodies (the host,
/// and the owner), ignores collisions with the towing car, shows the load and draws the wheels.
/// </summary>
internal static class TrailerWorld
{
    private static readonly Dictionary<uint, TrailerLive> Live = new();
    private static readonly List<TrailerLive> List = new();
    private static readonly Dictionary<uint, (TrailerState state, float since)> Pending = new();
    private static float _nextSlow;

    public static int Count => List.Count;
    public static IReadOnlyList<TrailerLive> All => List;

    public static TrailerLive Get(uint netId) => Live.TryGetValue(netId, out var t) ? t : null;

    /// <summary>Takes a trailer's new state (from the host's message, or the host's own change).</summary>
    public static void Apply(TrailerState s, NetworkObject known = null)
    {
        if (s == null) return;
        if (Live.TryGetValue(s.NetId, out var t))
        {
            t.State = s.Clone();
            Sync(t);
            return;
        }
        var no = known ?? Find(s.NetId);
        // a copy still asleep (Fusion delays activating copies of inactive templates; core wakes them on instantiate,
        // this is the fallback): its body can't take a joint, a pose or a velocity yet, so it waits a frame
        if (no != null && !Awake(no)) no = null;
        if (no == null)
        {
            Pending[s.NetId] = (s.Clone(), Pending.TryGetValue(s.NetId, out var p) ? p.since : Time.unscaledTime);
            return;
        }
        Pending.Remove(s.NetId);
        t = TrailerLive.Attach(no, s);
        if (t == null)
        {
            TrailersPlugin.LogOnce("attach", $"[Trailers] network object {s.NetId} is not a {s.Kind} trailer here");
            return;
        }
        Live[s.NetId] = t;
        List.Add(t);
        DebugLog.Verbose(TrailersPlugin.ModId, () => $"trailer {s.NetId} ({s.Kind}) attached to its network object: {List.Count} live, owner {s.Owner}, hitched {s.Hitched}");
        if (GameState.IsAuthority) TrailerHost.AfterAttach(t);
        Sync(t);
    }

    private static bool Awake(NetworkObject no)
    {
        try
        {
            if (!no.gameObject.activeInHierarchy) return false;
            var nrb = no.GetComponent<Fusion.Addons.Physics.NetworkRigidbody3D>();
            return nrb == null || nrb.Rigidbody != null;
        }
        catch (Exception ex) { DebugLog.Error("trailers", ex, "TrailerWorld.Awake"); return false; }
    }

    public static void Remove(uint netId)
    {
        Pending.Remove(netId);
        if (!Live.TryGetValue(netId, out var t)) return;
        t.Forget();
        Live.Remove(netId);
        List.Remove(t);
        DebugLog.Verbose(TrailersPlugin.ModId, () => $"trailer {netId} forgotten: {List.Count} live");
    }

    public static void Clear()
    {
        if (List.Count > 0 || Pending.Count > 0) DebugLog.Info(TrailersPlugin.ModId, $"all trailers forgotten ({List.Count} live, {Pending.Count} waiting)");
        foreach (var t in List) t.Forget();
        Live.Clear();
        List.Clear();
        Pending.Clear();
        TrailerHost.ForgetSpawns();
    }

    public static NetworkObject Find(uint raw)
    {
        if (raw == 0) return null;
        var r = GameState.Runner;
        if (r == null) return null;
        try
        {
            NetworkObject o = null;
            if (r.TryFindObject(new NetworkId { Raw = raw }, out o) && o != null) return o;
        }
        catch (Exception ex) { DebugLog.Error("trailers", ex, "TrailerWorld.Find"); }
        return null;
    }

    /// <summary>Makes this peer's copy match the state: joint (host and owner), collisions, load, mass.</summary>
    public static void Sync(TrailerLive t)
    {
        if (!t.Alive) return;
        var s = t.State;
        bool host = GameState.IsAuthority;
        bool owner = false;
        try { owner = t.No.HasInputAuthority; } catch (Exception ex) { DebugLog.Error("trailers", ex, "TrailerWorld.Sync"); }
        NetworkObject car = s.Hitched ? Find(s.CarNetId) : null;

        // the load's weight first (where the body is simulated; Fusion also syncs the mass), so a new joint's break
        // force counts it
        if (host || t.Simulated)
        {
            float mass = t.Spec.Mass + (s.CargoCarId != 0 ? t.Spec.CargoMass : 0f);
            try { if (Math.Abs(t.Rb.mass - mass) > 0.5f) t.Rb.mass = mass; } catch (Exception ex) { DebugLog.Error("trailers", ex, "TrailerWorld.Sync"); }
        }

        // the joint lives where both bodies are simulated: the host, and the owner (who predicts car and trailer)
        bool wantJoint = s.Hitched && car != null && (host || (owner && t.Simulated));
        if (!wantJoint)
        {
            if (t.Joint != null || t.JointCar != 0) t.DropJoint();
        }
        else if (t.JointCar != s.CarNetId || (t.Joint == null && !host) || !t.JointMatches(TrailerAssets.V3(s.Anchor), TrailerAssets.V3(s.CarAnchor)))
        {
            // a new car, a lost joint, or new anchors (a reset onto the same car can move the coupler head)
            var carRb = car.GetComponent<Rigidbody>();
            CrashTrail.Mark("trailers: joint " + s.Kind);
            t.MakeJoint(carRb, s.CarNetId, TrailerAssets.V3(s.Anchor), TrailerAssets.V3(s.CarAnchor), breakable: host);
            CrashTrail.Mark("trailers: joint made");
        }
        // the host's hitch breaks at a force that follows the trailer's weight (a loaded hauler holds more)
        if (host && t.Joint != null)
        {
            float bf = HitchLimits.For(t.Spec, t.Rb.mass).BreakForce;
            try { if (Math.Abs(t.Joint.breakForce - bf) > 1f) t.Joint.breakForce = bf; } catch (Exception ex) { DebugLog.Error("trailers", ex, "TrailerWorld.Sync"); }
        }

        // no collisions between a trailer and the car towing it, on every peer (proxies included)
        if (s.Hitched && car != null) t.IgnoreCar(car, s.CarNetId);

        // the coupler head at the height the hitch set, and the load's picture, on every peer
        t.ShowCoupler();
        t.ShowCargo();
    }

    /// <summary>Tells the towing car how much trailer it pulls (the weight from the spec and the load, the same number on every
    /// peer): a car with a tow rating (modded cars, and stock cars under Realistic Handling) loses power and top speed past it.</summary>
    private static void ReportTow(NetworkObject car, TrailerLive t)
    {
        try
        {
            var vpc = car.GetComponent<VehiclePhysics.VPVehicleController>();
            if (vpc == null) return;
            float kg = t.Spec.Mass + (t.State.CargoCarId != 0 ? t.Spec.CargoMass : 0f);
            OverTheHillPlus.Cars.CarTowLoads.Report(vpc.Pointer, kg);
        }
        catch (Exception ex) { DebugLog.Error("trailers", ex, "TrailerWorld.ReportTow"); }
    }

    /// <summary>Every frame: find late objects, keep joints and collision filters right, draw the wheels.</summary>
    public static void Tick()
    {
        float dt = Time.deltaTime;
        if (Pending.Count > 0 && Time.unscaledTime >= _nextSlow)
        {
            foreach (var kv in new List<KeyValuePair<uint, (TrailerState state, float since)>>(Pending))
            {
                var no = Find(kv.Key);
                // a guest keeps it until the host says it is gone (it may come back into range any time); on the host
                // it only waits for its copy to wake, and a copy still asleep (or gone) after 10 s is taken away
                if (GameState.IsAuthority && Time.unscaledTime - kv.Value.since > 10f) TrailerHost.GiveUpAsleep(kv.Key, no);
                else if (no != null) Apply(kv.Value.state, no);
            }
        }
        bool slow = Time.unscaledTime >= _nextSlow;
        if (slow) _nextSlow = Time.unscaledTime + 0.5f;
        for (int i = List.Count - 1; i >= 0; i--)
        {
            var t = List[i];
            if (!t.Alive)
            {
                // gone here (out of range, or despawned before the message): forget it until it comes back
                var s = t.State;
                t.Forget();
                Live.Remove(s.NetId);
                List.RemoveAt(i);
                if (!GameState.IsAuthority) Pending[s.NetId] = (s, Time.unscaledTime);
                else TrailerNet.BroadcastRemoved(s.NetId, DespawnReason.Lost);
                continue;
            }
            // the towing car went (despawned, respawned): a joint without its car would pin the trailer to the world
            if (t.JointCar != 0 && t.Joint != null && t.CarRb == null) t.DropJoint();
            try { TrailerPhysics.DrawWheels(t, dt); } catch (Exception e) { DebugLog.Error("trailers", e, "TrailerWorld.Tick"); TrailersPlugin.LogOnce("wheels", "[Trailers] wheels: " + e.Message); }
            if (!slow) continue;
            try
            {
                // the owner's game may get the object after the message, or lose its joint with a car respawn
                bool owner = false;
                try { owner = t.No.HasInputAuthority; } catch (Exception ex) { DebugLog.Error("trailers", ex, "TrailerWorld.Tick"); }
                if (t.State.Hitched && !GameState.IsAuthority && owner && t.Joint == null) Sync(t);
                if (t.State.Hitched)
                {
                    var car = Find(t.State.CarNetId);
                    if (car != null) t.IgnoreCar(car, t.State.CarNetId);   // re-run when the car's colliders change
                    if (car != null) ReportTow(car, t);
                }
                else if (t.IgnoredCar != 0)
                {
                    // unhitched: collide with that car again once it has moved clear
                    var car = Find(t.IgnoredCar);
                    if (car == null || (car.transform.position - t.T.position).magnitude > 9f) t.RestoreCar();
                }
            }
            catch (Exception e) { DebugLog.Error("trailers", e, "TrailerWorld.Tick"); TrailersPlugin.LogOnce("sync", "[Trailers] sync: " + e.Message); }
        }
    }
}
