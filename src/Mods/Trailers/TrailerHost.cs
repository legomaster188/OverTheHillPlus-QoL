using System;
using System.Collections.Generic;
#if MELON
using Il2CppFusion;
#else
using Fusion;
#endif
using UnityEngine;

namespace OverTheHillPlus.Trailers;

// After the namespace line on purpose: the game has its own global ModManager (mod.io).
using OverTheHillPlus.Core.Manager;

/// <summary>
/// The host's side (solo counts as hosting): it alone spawns, hitches, moves and despawns trailers, for its own player
/// and for guests who ask over the mod network, and it tells every modded guest each trailer's state. A player can
/// only ever act on their own trailers (the request's sender is the owner), so a guest can't touch anyone else's.
/// </summary>
internal static class TrailerHost
{
    /// <summary>Hitch requests need the coupler this close to the car's hitch ball, m.</summary>
    public const float HitchReach = 2.5f;
    /// <summary>Spawning, hitching and resetting need the car slower than this, m/s.</summary>
    public const float StillSpeed = 2.0f;

    private static float _nextCheck, _nextBoat;
    private static readonly List<TrailerLive> Scratch = new();

    public static bool Serving =>
        GameState.Runner != null && GameState.IsAuthority && TrailerPrefabs.Ready && ModManager.ServerModActive(TrailersPlugin.ModId);

    // ---------------------------------------------------------------- requests

    public static TrailerReply Handle(PlayerRef from, TrailerRequest r)
    {
        var reply = HandleCore(from, r);
        DebugLog.Info(TrailersPlugin.ModId, $"request {r.Op} {r.Kind} from player {from.PlayerId}: {reply} ({TrailerWorld.Count} trailers out)");
        return reply;
    }

    private static TrailerReply HandleCore(PlayerRef from, TrailerRequest r)
    {
        if (!TrailerPrefabs.Ready) return TrailerReply.NotReady;
        if (!Serving || !GameState.PhysicsAllowed) return TrailerReply.Off;
        var runner = GameState.Runner;
        bool self = false;
        try { self = runner.LocalPlayer.RawEncoded == from.RawEncoded; } catch (Exception ex) { DebugLog.Error("trailers", ex, "TrailerHost.Handle"); }
        if (!self && !TrailersConfig.GuestsMay.Value) return TrailerReply.Off;
        var car = CarOf(from);
        try
        {
            switch (r.Op)
            {
                case TrailerOp.Spawn: return Spawn(from, r.Kind, car);
                case TrailerOp.Remove:
                {
                    var t = Mine(from, car, any: true);
                    if (t == null) return TrailerReply.NoTrailer;
                    Despawn(t, DespawnReason.Removed);
                    return TrailerReply.Done;
                }
                case TrailerOp.Hitch: return Hitch(from, car);
                case TrailerOp.Unhitch:
                {
                    TrailerLive t = null;
                    foreach (var x in TrailerWorld.All)
                        if (x.State.Owner == from.RawEncoded && x.State.Hitched) { t = x; break; }
                    if (t == null) return TrailerReply.NoTrailer;
                    Unhitch(t);
                    return TrailerReply.Done;
                }
                case TrailerOp.Reset: return Reset(from, car);
                case TrailerOp.Load:
                case TrailerOp.Unload:
                {
                    TrailerLive t = null;
                    foreach (var x in TrailerWorld.All)
                        if (x.State.Owner == from.RawEncoded && x.Kind == TrailerKind.Hauler) { t = x; break; }
                    if (t == null) return TrailerReply.NotAHauler;
                    if (Speed(t.Rb) > 1f) return TrailerReply.TooFast;
                    uint want = r.Op == TrailerOp.Load ? r.Arg : 0u;
                    if (want != 0 && CargoModels.CarName(want) == null) return TrailerReply.NotACar;
                    var s = t.State.Clone();
                    s.CargoCarId = want;
                    Publish(t, s);
                    return TrailerReply.Done;
                }
            }
        }
        catch (Exception e)
        {
            DebugLog.Error(TrailersPlugin.ModId, e, r.Op + " request");
            TrailersPlugin.Logger.LogWarning($"[Trailers] {r.Op} failed: {e}");
            return TrailerReply.Failed;
        }
        return TrailerReply.Failed;
    }

    private static TrailerReply Spawn(PlayerRef owner, TrailerKind kind, NetworkObject car)
    {
        if (car == null) return TrailerReply.NoCar;
        var carRb = car.GetComponent<Rigidbody>();
        if (Speed(carRb) > StillSpeed) return TrailerReply.TooFast;
        int mine = 0;
        foreach (var x in TrailerWorld.All) if (x.State.Owner == owner.RawEncoded) mine++;
        switch (SpawnLimits.Check(mine, TrailerWorld.Count, TrailersConfig.PerPlayer.Value, TrailersConfig.SessionMax.Value))
        {
            case SpawnVerdict.PlayerLimit: return TrailerReply.PlayerLimit;
            case SpawnVerdict.SessionLimit: return TrailerReply.SessionLimit;
        }
        uint carId = car.Id.Raw;
        if (BallRule.HitchedTo(States(), carId) != 0) return TrailerReply.BallTaken;
        var spec = TrailerSpecs.Of(kind);
        if (!CarHitch(car, spec, out Vector3 carAnchor)) return NoHitchFor(spec);
        var tpl = TrailerPrefabs.Template(kind);
        var model = TrailerAssets.Model(kind);
        if (tpl == null || model == null) return TrailerReply.NotReady;
        Vector3 hitch = HitchFor(model, car, carAnchor, null);
        Pose(car.transform, carAnchor, hitch, null, out var pos, out var rot);
        // at a depot (a map's trailer yard, core's WorldSites): the trailer waits in a free bay, unhitched, ready to hitch
        bool parked = DepotBay(car, kind, carRb, out var bayPos, out var bayRot);
        if (parked) { pos = bayPos; rot = bayRot; }
        var runner = GameState.Runner;
        CrashTrail.Mark("trailers: spawn " + kind);
        DebugLog.Info(TrailersPlugin.ModId, $"spawn {kind} for player {owner.PlayerId} behind car {carId} at {pos}");
        NetworkObject no = runner.Spawn(tpl, new Il2CppSystem.Nullable<Vector3>(pos), new Il2CppSystem.Nullable<Quaternion>(rot),
                                        new Il2CppSystem.Nullable<PlayerRef>(owner), null, (NetworkSpawnFlags)0);
        CrashTrail.Mark("trailers: spawned " + kind);
        if (no == null)
        {
            DebugLog.Error(TrailersPlugin.ModId, $"spawning a {kind} returned no object");
            return TrailerReply.Failed;
        }
        var s = new TrailerState
        {
            NetId = no.Id.Raw, Kind = kind, Owner = owner.RawEncoded, Hitched = !parked, CarNetId = parked ? 0u : carId,
            Anchor = TrailerAssets.ToV3(hitch), CarAnchor = TrailerAssets.ToV3(carAnchor),
            // a boat trailer carries the boat a boat mod drives (the Grayling) when one runs, else its own runabout
            CargoCarId = kind == TrailerKind.Boat ? TrailerBoatCar() : 0u,
        };
        // body, joint and velocity only once the copy is awake (core wakes it on instantiate; if it is still asleep,
        // the world attaches it on a later frame and AfterAttach runs then)
        SpawnInfo[s.NetId] = (carId, parked || carRb == null ? Vector3.zero : carRb.linearVelocity);
        TrailerWorld.Apply(s, no);
        TrailerNet.BroadcastState(s);
        TrailersPlugin.Logger.LogInfo($"[Trailers] spawned a {TrailerSpecs.Of(kind).Name} (object {s.NetId}) for player {owner.PlayerId}, " +
                                      (parked ? "parked in a depot bay." : "hitched."));
        return TrailerReply.Done;
    }

    /// <summary>A free bay of a depot near the car (core's <see cref="Core.Shared.WorldSites"/>, published by map mods):
    /// the trailer's pose standing in it, hitch towards the bay's heading, wheels on the ground. False when the car is
    /// at no depot or every bay near it has a trailer in it.</summary>
    private static bool DepotBay(NetworkObject car, TrailerKind kind, Rigidbody carRb, out Vector3 pos, out Quaternion rot)
    {
        pos = default;
        rot = Quaternion.identity;
        try
        {
            var at = car.transform.position;
            if (!Core.Shared.WorldSites.AtDepot(at.x, at.z)) return false;
            bool Taken(Core.Shared.WorldSites.Bay b)
            {
                foreach (var t in TrailerWorld.All)
                {
                    if (!t.Alive) continue;
                    var p = t.T.position;
                    if ((p.x - b.X) * (p.x - b.X) + (p.z - b.Z) * (p.z - b.Z) < 4.5f * 4.5f) return true;
                }
                return false;
            }
            if (!Core.Shared.WorldSites.PickBay(at.x, at.z, kind.ToString().ToLowerInvariant(), Taken, out var bay)) return false;
            rot = Quaternion.Euler(0f, bay.Yaw, 0f);
            var p0 = new Vector3(bay.X, bay.Y, bay.Z);
            IntPtr carBody = carRb != null ? carRb.Pointer : IntPtr.Zero;
            float y = bay.Y;
            if (TrailerPhysics.Ground(p0 + Vector3.up * 6f, Vector3.down, 14f, IntPtr.Zero, carBody, null, out var hit)) y = hit.point.y;
            pos = new Vector3(bay.X, y + 0.08f, bay.Z);
            DebugLog.Info(TrailersPlugin.ModId, $"depot: {kind} goes to {bay.Name} at {pos}");
            return true;
        }
        catch (Exception e) { DebugLog.Error("trailers", e, "TrailerHost.DepotBay"); return false; }
    }

    private static readonly Dictionary<uint, (uint car, Vector3 velocity)> SpawnInfo = new();

    /// <summary>A trailer this host spawned is attached (awake, its body usable): note its car, give it the car's speed.</summary>
    public static void AfterAttach(TrailerLive t)
    {
        if (!SpawnInfo.TryGetValue(t.NetId, out var info)) return;
        SpawnInfo.Remove(t.NetId);
        t.CarAtSpawn = info.car;
        try { t.Rb.linearVelocity = info.velocity; } catch (Exception ex) { DebugLog.Error("trailers", ex, "TrailerHost.AfterAttach"); }
    }

    /// <summary>The session's trailers were dropped (session reset, runner change, scene change on a guest).</summary>
    public static void ForgetSpawns() => SpawnInfo.Clear();

    /// <summary>A spawned copy that never woke (or vanished) within 10 s: it is despawned and forgotten, so nothing
    /// stays in the session untracked.</summary>
    public static void GiveUpAsleep(uint netId, NetworkObject no)
    {
        TrailersPlugin.Logger.LogWarning($"[Trailers] trailer {netId} never woke up within 10 s: removed.");
        CrashTrail.Mark("trailers: despawn asleep copy");
        TrailerWorld.Remove(netId);
        SpawnInfo.Remove(netId);
        try { if (no != null && no.IsValid) GameState.Runner?.Despawn(no); } catch (Exception ex) { DebugLog.Error("trailers", ex, "TrailerHost.GiveUpAsleep"); }
        TrailerNet.BroadcastRemoved(netId, DespawnReason.Lost);
        CrashTrail.Mark("trailers: despawned");
    }

    private static List<TrailerState> States()
    {
        var l = new List<TrailerState>(TrailerWorld.Count);
        foreach (var x in TrailerWorld.All) l.Add(x.State);
        return l;
    }

    private static TrailerReply Hitch(PlayerRef owner, NetworkObject car)
    {
        if (car == null) return TrailerReply.NoCar;
        if (Speed(car.GetComponent<Rigidbody>()) > StillSpeed) return TrailerReply.TooFast;
        if (BallRule.HitchedTo(States(), car.Id.Raw) != 0) return TrailerReply.BallTaken;
        // the car's tow ball and, on a semi truck, its fifth wheel: each trailer hitches to the one it is made for
        bool hasBall = TowBar.Locate(car, out Vector3 ballAnchor);
        bool hasFifth = OverTheHillPlus.Cars.CarAnchors.Find(car.gameObject, TrailerSpecs.FifthWheelAnchor, out Vector3 fifthAnchor);
        TrailerLive best = null;
        float bestD = HitchReach;
        bool anyLoose = false, wrongHitch = false;
        foreach (var x in TrailerWorld.All)
        {
            if (x.State.Owner != owner.RawEncoded || x.State.Hitched || !x.Alive) continue;
            anyLoose = true;
            bool fifth = x.Spec.FifthWheel;
            if (fifth ? !hasFifth : !hasBall) { wrongHitch = true; continue; }
            Vector3 point = car.transform.TransformPoint(fifth ? fifthAnchor : ballAnchor);
            float d = (x.T.TransformPoint(x.CouplerPoint) - point).magnitude;
            if (d <= bestD) { best = x; bestD = d; }
        }
        if (best == null)
        {
            if (!anyLoose) return TrailerReply.NoTrailer;
            if (wrongHitch && !hasBall && !hasFifth) return TrailerReply.NoTowHitch;
            foreach (var x in TrailerWorld.All)
                if (x.State.Owner == owner.RawEncoded && !x.State.Hitched && x.Alive && (x.Spec.FifthWheel ? hasFifth : hasBall)) return TrailerReply.TooFar;
            return NoHitchFor(wrongHitch && hasBall ? TrailerSpecs.Of(TrailerKind.Semi) : TrailerSpecs.Of(TrailerKind.Utility));
        }
        Vector3 carAnchor = best.Spec.FifthWheel ? fifthAnchor : ballAnchor;
        // line it up behind the car on the ball (a short hop when the coupler was a little off)
        CrashTrail.Mark("trailers: hitch");
        Vector3 hitch = HitchFor(best.Model, car, carAnchor, best);
        Pose(car.transform, carAnchor, hitch, best, out var pos, out var rot);
        best.DropJoint();
        DebugLog.Verbose(TrailersPlugin.ModId, () => $"hitch: trailer {best.NetId} moved {Vector3.Distance(best.T.position, pos):0.0} m onto car {car.Id.Raw}'s ball");
        Teleport(best, pos, rot);
        var s = best.State.Clone();
        s.Hitched = true;
        s.CarNetId = car.Id.Raw;
        s.Anchor = TrailerAssets.ToV3(hitch);
        s.CarAnchor = TrailerAssets.ToV3(carAnchor);
        Publish(best, s);
        Adopt(best, s.CarNetId);
        return TrailerReply.Done;
    }

    /// <summary>A trailer hitched to a car belongs to that car from now on (its owner changing car later takes it away).</summary>
    private static void Adopt(TrailerLive t, uint carNetId)
    {
        t.CarAtSpawn = carNetId;
        t.CarChangedSince = -1f;
        t.HitchWantedUntil = -1f;
    }

    /// <summary>The boat a boat trailer carries in this session: the boat mod's (core's WorldSites.TrailerBoatCar) when its
    /// garage model can be named, else 0 (the trailer's own runabout).</summary>
    private static uint TrailerBoatCar()
    {
        uint id = Core.Shared.WorldSites.TrailerBoatCar;
        return id != 0 && CargoModels.CarName(id) != null ? id : 0u;
    }

    private static TrailerReply Reset(PlayerRef owner, NetworkObject car)
    {
        if (car == null) return TrailerReply.NoCar;
        if (Speed(car.GetComponent<Rigidbody>()) > StillSpeed) return TrailerReply.TooFast;
        // the trailer this car tows, else the player's nearest one; never onto a ball another trailer holds
        uint carId = car.Id.Raw;
        TrailerLive t = TrailerWorld.Get(BallRule.HitchedTo(States(), carId));
        if (t != null && t.State.Owner != owner.RawEncoded) t = null;
        t ??= Mine(owner, car, any: true);
        if (t == null) return TrailerReply.NoTrailer;
        if (BallRule.HitchedTo(States(), carId, except: t.NetId) != 0) return TrailerReply.BallTaken;
        if (!CarHitch(car, t.Spec, out Vector3 carAnchor)) return NoHitchFor(t.Spec);
        CrashTrail.Mark("trailers: reset");
        Vector3 hitch = HitchFor(t.Model, car, carAnchor, t);
        Pose(car.transform, carAnchor, hitch, t, out var pos, out var rot);
        t.DropJoint();
        Teleport(t, pos, rot);
        var s = t.State.Clone();
        s.Hitched = true;
        s.CarNetId = car.Id.Raw;
        s.Anchor = TrailerAssets.ToV3(hitch);
        s.CarAnchor = TrailerAssets.ToV3(carAnchor);
        Publish(t, s);
        Adopt(t, s.CarNetId);
        return TrailerReply.Done;
    }

    public static void Unhitch(TrailerLive t)
    {
        var s = t.State.Clone();
        s.Hitched = false;
        s.CarNetId = 0;
        DebugLog.Verbose(TrailersPlugin.ModId, () => $"unhitch: trailer {t.NetId} ({t.Kind}) let go of car {t.State.CarNetId}");
        Publish(t, s);
    }

    /// <summary>Applies a new state here and tells every modded guest.</summary>
    private static void Publish(TrailerLive t, TrailerState s)
    {
        TrailerWorld.Apply(s);
        TrailerNet.BroadcastState(s);
    }

    public static void Despawn(TrailerLive t, DespawnReason why)
    {
        uint id = t.NetId;
        CrashTrail.Mark("trailers: despawn " + why);
        TrailerWorld.Remove(id);
        SpawnInfo.Remove(id);
        try { if (t.No != null && t.No.IsValid) GameState.Runner?.Despawn(t.No); } catch (Exception ex) { DebugLog.Error("trailers", ex, "TrailerHost.Despawn"); }
        CrashTrail.Mark("trailers: despawned");
        TrailerNet.BroadcastRemoved(id, why);
        TrailersPlugin.Logger.LogInfo($"[Trailers] removed trailer {id} ({why}).");
        DebugLog.Info(TrailersPlugin.ModId, $"trailer {id} ({t.Kind}) despawned: {why}; {TrailerWorld.Count} left");
    }

    /// <summary>Host: removes every trailer (button, mod failure, scene change).</summary>
    public static int DespawnAll(DespawnReason why)
    {
        var all = new List<TrailerLive>(TrailerWorld.All);
        foreach (var t in all) Despawn(t, why);
        return all.Count;
    }

    // ---------------------------------------------------------------- rules (twice a second)

    public static void Tick()
    {
        if (TrailerWorld.Count == 0 || GameState.Runner == null || !GameState.IsAuthority) return;
        Scratch.Clear();
        Scratch.AddRange(TrailerWorld.All);
        foreach (var t in Scratch)
        {
            // the hitch broke, or car or trailer rolled over: unhitch (checked every frame: a broken joint is gone)
            if (!t.State.Hitched || !t.Alive) continue;
            float carTilt = 0f;
            var car = TrailerWorld.Find(t.State.CarNetId);
            if (car != null) carTilt = Vector3.Angle(car.transform.up, Vector3.up);
            float trailerTilt = Vector3.Angle(t.T.up, Vector3.up);
            bool over = carTilt > HitchLimits.CarRolloverDeg || trailerTilt > HitchLimits.TrailerRolloverDeg;
            if (over && t.TiltSince < 0f) t.TiltSince = Time.time;
            if (!over) t.TiltSince = -1f;
            float tilted = t.TiltSince < 0f ? 0f : Time.time - t.TiltSince;
            // the tow bar came off: nothing to hold the trailer (checked with the twice-a-second rules below)
            bool noBar = car != null && Time.unscaledTime >= _nextCheck && !CarHitch(car, t.Spec, out _);
            if (HitchLimits.ShouldUnhitch(t.JointBroken, carTilt, trailerTilt, tilted) || car == null || noBar)
            {
                TrailersPlugin.Logger.LogInfo($"[Trailers] trailer {t.NetId} unhitched ({(t.JointBroken ? "the hitch broke" : car == null ? "its car is gone" : noBar ? "the tow hitch was taken off" : "rollover")}).");
                t.TiltSince = -1f;
                Unhitch(t);
            }
        }
        if (Time.unscaledTime >= _nextBoat)
        {
            _nextBoat = Time.unscaledTime + 0.25f;
            Scratch.Clear();
            Scratch.AddRange(TrailerWorld.All);
            foreach (var t in Scratch)
            {
                if (t.Kind != TrailerKind.Boat || !t.Alive) continue;
                try { BoatRulesTick(t); }
                catch (Exception e) { DebugLog.Error("trailers", e, "TrailerHost.BoatRulesTick"); TrailersPlugin.LogOnce("boat-rules", "[Trailers] boat trailer rules: " + e.Message); }
            }
        }
        if (Time.unscaledTime < _nextCheck) return;
        _nextCheck = Time.unscaledTime + 0.5f;
        bool modActive = ModManager.ServerModActive(TrailersPlugin.ModId);
        Scratch.Clear();
        Scratch.AddRange(TrailerWorld.All);
        foreach (var t in Scratch)
        {
            var owner = PlayerRef.FromEncoded(t.State.Owner);
            var car = CarOf(owner);
            uint carNow = 0;
            try { carNow = car != null ? car.Id.Raw : 0u; } catch (Exception ex) { DebugLog.Error("trailers", ex, "TrailerHost.Tick"); }
            if (carNow == 0 && t.NoCarSince < 0f) t.NoCarSince = Time.unscaledTime;
            if (carNow != 0) t.NoCarSince = -1f;
            if (t.CarAtSpawn == 0) t.CarAtSpawn = carNow;
            // how long the owner has been in another car (a boat trailer waits for the boat mod to say the boat came off)
            bool changed = (carNow != 0 && carNow != t.CarAtSpawn) || carNow == 0;
            if (!changed) t.CarChangedSince = -1f;
            else if (t.CarChangedSince < 0f) t.CarChangedSince = Time.unscaledTime;
            float y = 0f;
            try { y = t.T.position.y; } catch (Exception ex) { DebugLog.Error("trailers", ex, "TrailerHost.Tick"); }
            var why = DespawnRules.Decide(new DespawnInput
            {
                ObjectAlive = t.Alive, ModActive = modActive, SceneChanging = false, OwnerPresent = Present(owner),
                OwnerCarNow = carNow, OwnerCarAtSpawn = t.CarAtSpawn,
                NoCarFor = t.NoCarSince < 0f ? 0f : Time.unscaledTime - t.NoCarSince, Height = y,
                KeepThroughCarChange = BoatRules.KeepThroughCarChange(t.State, t.CarChangedSince < 0f ? 0f : Time.unscaledTime - t.CarChangedSince),
            });
            if (why != DespawnReason.Keep) Despawn(t, why);
        }
    }

    // ---------------------------------------------------------------- the boat trailer at a ramp (host, 4 times a second)

    /// <summary>Floats the boat off a trailer backed deep enough into the water, winches a moored boat back onto one
    /// backed in beside it (<see cref="BoatRules"/>), and hitches a trailer a boat mod asked for to its owner's car once
    /// that car stands at its coupler.</summary>
    private static void BoatRulesTick(TrailerLive t)
    {
        float now = Time.unscaledTime;
        float speed = Speed(t.Rb);
        if (speed < BoatRules.StillSpeed) { if (t.StillSince < 0f) t.StillSince = now; }
        else t.StillSince = -1f;
        float still = t.StillSince < 0f ? 0f : now - t.StillSince;
        var s = t.State;
        if (t.HitchWantedUntil > 0f) WantedHitch(t, now);
        if (!t.HasSeat || s.CargoCarId == 0 || !s.Hitched) return;
        float draft = Core.Shared.WorldSites.TrailerBoatDraft;
        var keel = t.T.TransformPoint(t.KeelLocal);
        float water = TrailerWater.At(keel + Vector3.up * 0.5f);
        float depth = water - keel.y;
        var seat = t.SeatWorld;
        float yaw = t.T.eulerAngles.y;
        bool atRamp = s.Boat == BoatPlace.OnTrailer && float.IsFinite(depth) &&
                      OverTheHillPlus.Api.WorldPlaces.Nearest(OverTheHillPlus.Api.WorldKinds.BoatRamp, keel.x, keel.z, BoatRules.RampReach) != null;
        if (BoatRules.FloatsOff(s, atRamp, depth, draft, speed, still))
        {
            var (x, z) = BoatRules.DriftSpot(seat.x, seat.z, yaw);
            var n = s.Clone();
            n.Boat = BoatPlace.Afloat;
            n.BoatX = x;
            n.BoatZ = z;
            n.BoatYaw = yaw;
            Publish(t, n);
            TrailersPlugin.Logger.LogInfo($"[Trailers] trailer {t.NetId}: the boat floated off ({depth:0.00} m of water over its keel), moored at ({x:0.0}, {z:0.0}).");
            return;
        }
        if (s.Boat == BoatPlace.Afloat)
        {
            float dx = s.BoatX - seat.x, dz = s.BoatZ - seat.z;
            float dist = Mathf.Sqrt(dx * dx + dz * dz);
            if (BoatRules.WinchesOn(s, dist, depth, draft, speed, still))
            {
                var n = s.Clone();
                n.Boat = BoatPlace.OnTrailer;
                n.BoatX = n.BoatZ = n.BoatYaw = 0f;
                Publish(t, n);
                Adopt(t, s.CarNetId);
                TrailersPlugin.Logger.LogInfo($"[Trailers] trailer {t.NetId}: the moored boat was winched on ({dist:0.0} m off its bunks).");
            }
        }
    }

    /// <summary>A boat mod wants this trailer hitched to its owner's car (it put the car in front of it): done as soon as the
    /// car stands still with its tow ball near the coupler, the way a reset lines it up; given up after a few seconds.</summary>
    private static void WantedHitch(TrailerLive t, float now)
    {
        if (now > t.HitchWantedUntil || t.State.Hitched) { t.HitchWantedUntil = -1f; return; }
        var car = CarOf(PlayerRef.FromEncoded(t.State.Owner));
        if (car == null || Speed(car.GetComponent<Rigidbody>()) > 1.5f) return;
        if (BallRule.HitchedTo(States(), car.Id.Raw) != 0) { t.HitchWantedUntil = -1f; return; }
        if (!CarHitch(car, t.Spec, out Vector3 carAnchor))
        {
            t.HitchWantedUntil = -1f;
            TrailersPlugin.Logger.LogInfo($"[Trailers] boat trailer {t.NetId}: its owner's car has no tow hitch: it stays parked.");
            return;
        }
        var ball = car.transform.TransformPoint(carAnchor);
        if ((t.T.TransformPoint(t.CouplerPoint) - ball).magnitude > 6f) return;     // not there yet (the car is being moved)
        CrashTrail.Mark("trailers: boat hitch");
        Vector3 hitch = HitchFor(t.Model, car, carAnchor, t);
        Pose(car.transform, carAnchor, hitch, t, out var pos, out var rot);
        t.DropJoint();
        Teleport(t, pos, rot);
        var s = t.State.Clone();
        s.Hitched = true;
        s.CarNetId = car.Id.Raw;
        s.Anchor = TrailerAssets.ToV3(hitch);
        s.CarAnchor = TrailerAssets.ToV3(carAnchor);
        Publish(t, s);
        Adopt(t, s.CarNetId);
        TrailersPlugin.Logger.LogInfo($"[Trailers] boat trailer {t.NetId} hitched to its owner's car {s.CarNetId}.");
    }

    /// <summary>The player's boat trailer for a boat mod (core's WorldSites.BoatTrailerOf): the one they tow, else their
    /// nearest; null when they have none.</summary>
    public static Core.Shared.WorldSites.BoatTrailer? BoatTrailerOf(int owner)
    {
        TrailerLive pick = null;
        Vector3 at = Vector3.zero;
        var car = CarOf(PlayerRef.FromEncoded(owner));
        if (car != null) at = car.transform.position;
        float best = float.MaxValue;
        foreach (var t in TrailerWorld.All)
        {
            if (t.Kind != TrailerKind.Boat || t.State.Owner != owner || !t.Alive) continue;
            float d = t.State.Hitched ? -1f : (t.T.position - at).sqrMagnitude;
            if (d < best) { best = d; pick = t; }
        }
        if (pick == null) return null;
        var s = pick.State;
        var seat = pick.SeatWorld;
        var keel = pick.T.TransformPoint(pick.KeelLocal);
        var hitch = pick.T.TransformPoint(pick.CouplerPoint);
        return new Core.Shared.WorldSites.BoatTrailer(s.NetId, s.Owner, s.Hitched, s.CargoCarId != 0 && pick.HasSeat,
            (Core.Shared.WorldSites.BoatAt)(byte)s.Boat, seat.x, seat.z, keel.y, s.BoatX, s.BoatZ, s.BoatYaw,
            hitch.x, hitch.y, hitch.z, pick.T.eulerAngles.y);
    }

    /// <summary>Host: a boat mod says where a boat trailer's boat is now (core's WorldSites.SetTrailerBoat). Null or why not.</summary>
    public static string SetTrailerBoat(uint netId, Core.Shared.WorldSites.BoatAt at, float x, float z, float yaw)
    {
        if (!GameState.IsAuthority) return "only the host moves boats";
        var t = TrailerWorld.Get(netId);
        if (t == null || t.Kind != TrailerKind.Boat) return "no such boat trailer";
        var place = (BoatPlace)(byte)at;
        if (place > BoatPlace.Afloat) return "no such place";
        if (place != BoatPlace.OnTrailer && t.State.CargoCarId == 0) return "its boat is only for show";
        if (place == BoatPlace.Afloat && !(float.IsFinite(x) && float.IsFinite(z) && float.IsFinite(yaw))) return "no place to moor it";
        var s = t.State.Clone();
        s.Boat = place;
        s.BoatX = place == BoatPlace.Afloat ? x : 0f;
        s.BoatZ = place == BoatPlace.Afloat ? z : 0f;
        s.BoatYaw = place == BoatPlace.Afloat ? yaw : 0f;
        if (s.SameAs(t.State)) return null;
        Publish(t, s);
        // back on its bunks: the trailer goes with whatever car its owner drives now (the next check notes it)
        if (place == BoatPlace.OnTrailer && !s.Hitched) { t.CarAtSpawn = 0; t.CarChangedSince = -1f; }
        TrailersPlugin.Logger.LogInfo($"[Trailers] boat trailer {netId}: its boat is {place}" + (place == BoatPlace.Afloat ? $" at ({x:0.0}, {z:0.0})." : "."));
        return null;
    }

    /// <summary>Host: a boat mod asks for the trailer to be hitched to its owner's car as soon as that car is at its coupler
    /// (core's WorldSites.HitchBoatTrailer). Null or why not.</summary>
    public static string HitchBoatTrailer(uint netId, int owner)
    {
        if (!GameState.IsAuthority) return "only the host hitches";
        var t = TrailerWorld.Get(netId);
        if (t == null || t.Kind != TrailerKind.Boat || t.State.Owner != owner) return "no such boat trailer";
        if (t.State.Hitched) return "it is hitched already";
        t.HitchWantedUntil = Time.unscaledTime + 6f;
        return null;
    }

    /// <summary>The scene is about to change (portal, region): every trailer goes now, before Fusion tears it down.</summary>
    public static void SceneChanging()
    {
        if (!GameState.IsAuthority) { TrailerWorld.Clear(); return; }
        if (TrailerWorld.Count > 0) DespawnAll(DespawnReason.SceneChanged);
    }

    /// <summary>A modded guest left: their trailers go at once (the twice-a-second check covers vanilla leavers too).</summary>
    public static void PeerGone(PlayerRef p)
    {
        if (!GameState.IsAuthority) return;
        foreach (var t in new List<TrailerLive>(TrailerWorld.All))
            if (t.State.Owner == p.RawEncoded) Despawn(t, DespawnReason.OwnerLeft);
    }

    // ---------------------------------------------------------------- helpers

    private static bool Present(PlayerRef p)
    {
        var r = GameState.Runner;
        if (r == null) return false;
        try { if (r.LocalPlayer.RawEncoded == p.RawEncoded) return true; } catch (Exception ex) { DebugLog.Error("trailers", ex, "TrailerHost.Present"); }
        try { return r.IsPlayerValid(p); }
        catch (Exception ex) { DebugLog.Error("trailers", ex, "TrailerHost.Present"); return true; } // can't tell: never throw a trailer away on a guess
    }

    /// <summary>The player's car (its network object), or null.</summary>
    public static NetworkObject CarOf(PlayerRef p)
    {
        foreach (var fp in GameState.FusionPlayers)
        {
            try
            {
                if (fp == null || fp.Object == null) continue;
                if (fp.Object.InputAuthority.RawEncoded == p.RawEncoded) return fp.Object;
            }
            catch (Exception ex) { DebugLog.Error("trailers", ex, "TrailerHost.CarOf"); }
        }
        try
        {
            var r = GameState.Runner;
            NetworkObject o = null;
            if (r != null && r.TryGetPlayerObject(p, out o) && o != null && o.GetComponent<FusionPlayer>() != null) return o;
        }
        catch (Exception ex) { DebugLog.Error("trailers", ex, "TrailerHost.CarOf"); }
        return null;
    }

    /// <summary>The player's trailer nearest to their car (or any of theirs).</summary>
    private static TrailerLive Mine(PlayerRef p, NetworkObject car, bool any)
    {
        TrailerLive best = null;
        float bestD = float.MaxValue;
        Vector3 at = car != null ? car.transform.position : Vector3.zero;
        foreach (var x in TrailerWorld.All)
        {
            if (x.State.Owner != p.RawEncoded || !x.Alive) continue;
            float d = car != null ? (x.T.position - at).sqrMagnitude : 0f;
            if (d < bestD) { best = x; bestD = d; }
        }
        return any || bestD < 100f ? best : null;
    }

    /// <summary>The trailer's hitch anchor for this car: the model's ball socket with the adjustable coupler's head
    /// bolted at the ball's height over the ground (<see cref="CouplerFit"/>), so the trailer rides level behind it. A
    /// model without a head keeps its fixed hitch.</summary>
    private static Vector3 HitchFor(TrailerModel model, NetworkObject car, Vector3 carAnchor, TrailerLive moving)
    {
        Vector3 hitch = model.HitchPoint;
        if (model.Blob.Part(TrailerAssets.CouplerPart) == null || car == null) return hitch;
        try
        {
            Vector3 ball = car.transform.TransformPoint(carAnchor);
            IntPtr own = moving != null && moving.Rb != null ? moving.Rb.Pointer : IntPtr.Zero;
            IntPtr carBody = IntPtr.Zero;
            var cr = car.GetComponent<Rigidbody>();
            if (cr != null) carBody = cr.Pointer;
            if (TrailerPhysics.Ground(ball + Vector3.up * 0.5f, Vector3.down, 3f, own, carBody, null, out var hit))
            {
                float off = CouplerFit.Offset(ball.y - hit.point.y, hitch.y);
                DebugLog.Verbose(TrailersPlugin.ModId, () => $"coupler: ball {ball.y - hit.point.y:0.00} m up, coupler {hitch.y:0.00} m: head set {off:+0.00;-0.00} m");
                hitch.y += off;
            }
        }
        catch (Exception ex) { DebugLog.Error("trailers", ex, "TrailerHost.HitchFor"); }
        return hitch;
    }

    /// <summary>Where a trailer goes behind a car: its hitch on the ball, straight behind it, pitched so its wheels
    /// rest on the ground under them.</summary>
    public static void Pose(Transform car, Vector3 carAnchor, Vector3 hitch, TrailerLive moving, out Vector3 pos, out Quaternion rot)
    {
        Vector3 ball = car.TransformPoint(carAnchor);
        var yaw = Quaternion.Euler(0f, car.eulerAngles.y, 0f);
        Vector3 axle = ball - yaw * new Vector3(0f, hitch.y, hitch.z);
        float ground = axle.y - hitch.y;
        // the same ground the wheels stand on, leaving out the trailer being moved and the car
        IntPtr own = moving != null && moving.Rb != null ? moving.Rb.Pointer : IntPtr.Zero;
        IntPtr carBody = IntPtr.Zero;
        try { var cr = car.GetComponent<Rigidbody>(); if (cr != null) carBody = cr.Pointer; } catch (Exception ex) { DebugLog.Error("trailers", ex, "TrailerHost.Pose"); }
        if (TrailerPhysics.Ground(axle + Vector3.up * 3f, Vector3.down, 8f, own, carBody, null, out var hit))
            ground = hit.point.y;
        float rise = ball.y - (ground + hitch.y);
        float pitch = Mathf.Clamp(Mathf.Atan2(rise, Mathf.Max(hitch.z, 0.5f)) * Mathf.Rad2Deg, -35f, 35f);
        rot = yaw * Quaternion.Euler(-pitch, 0f, 0f);
        pos = ball - rot * hitch + Vector3.up * 0.03f;
    }

    private static void Teleport(TrailerLive t, Vector3 pos, Quaternion rot)
    {
        try
        {
            t.Rb.linearVelocity = Vector3.zero;
            t.Rb.angularVelocity = Vector3.zero;
            t.Rb.position = pos;
            t.Rb.rotation = rot;
            t.T.SetPositionAndRotation(pos, rot);
            t.Nrb?.Teleport(new Il2CppSystem.Nullable<Vector3>(pos), new Il2CppSystem.Nullable<Quaternion>(rot));
        }
        catch (Exception e) { DebugLog.Error("trailers", e, "TrailerHost.Teleport"); TrailersPlugin.LogOnce("teleport", "[Trailers] could not move a trailer: " + e.Message); }
    }

    /// <summary>Where on the car this trailer hitches, car space: a semi-trailer on the fifth wheel of a semi truck (a modded
    /// car whose model has one), the others on the tow ball of a mounted tow bar. False when the car has none.</summary>
    public static bool CarHitch(NetworkObject car, TrailerSpec spec, out Vector3 anchor)
    {
        anchor = default;
        if (car == null) return false;
        if (spec != null && spec.FifthWheel) return OverTheHillPlus.Cars.CarAnchors.Find(car.gameObject, TrailerSpecs.FifthWheelAnchor, out anchor);
        return TowBar.Locate(car, out anchor);
    }

    private static TrailerReply NoHitchFor(TrailerSpec spec) => spec != null && spec.FifthWheel ? TrailerReply.NoFifthWheel : TrailerReply.NoTowHitch;

    private static float Speed(Rigidbody rb)
    {
        try { return rb != null ? rb.linearVelocity.magnitude : 0f; }
        catch (Exception ex) { DebugLog.Error("trailers", ex, "TrailerHost.Speed"); return 0f; }
    }
}
