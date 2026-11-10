using System;
using System.Collections.Generic;
using OverTheHillPlus.Cars;
using OverTheHillPlus.Core.Shared;
using UnityEngine;
#if MELON
using Il2CppFusion;
using Il2CppVehiclePhysics;
#else
using Fusion;
using VehiclePhysics;
#endif

namespace OverTheHillPlus.Grayling;

/// <summary>
/// Getting the boat into the water and a car back out of it, decided by whoever runs the session (the host, or you solo),
/// for every player; so a guest needs nothing but the game's own "change car where you stand" request (the Grayling's
/// Drive button, or the garage) and nothing new is sent:
/// <list type="bullet">
/// <item><b>Launch.</b> A Grayling that appears for a player out of the water (they changed into it on the shore) goes
///   onto the nearest open water deep enough within 40 m, bow out. One that is beached later (run aground, put ashore) goes
///   back in when its driver holds the throttle for a moment with water close by.</item>
/// <item><b>Landing.</b> A player who changes from an afloat Grayling to a car gets the car on the nearest dry ground,
///   facing the land.</item>
/// <item><b>The boat trailer</b> (Trailers+, through core's <see cref="WorldSites"/>; <see cref="TrailerLaunch"/>). At a
///   boat ramp, back the loaded trailer in until the boat floats off (Trailers+ moors it astern of the trailer), pull the
///   empty trailer up and park it, and take the boat: you get into it where it floats. Coming back, change into your car
///   near the trailer: the car stands at the trailer's hitch, hitched, and the boat stays moored where you left it; back the
///   trailer in beside it and it is winched on. Taking the boat with the loaded trailer backed into the water launches it
///   straight off the bunks.</item>
/// </list>
/// A car change is noticed from the player's car object's network id (<see cref="CarSwapWatch"/>: the scan of the players'
/// cars is up to a second old, so a player can be missing from it, or in two cars, for a moment across a change).
/// </summary>
internal static class BoatLaunch
{
    private sealed class Rider
    {
        public readonly CarSwapWatch Watch = new();
        public Vector3 BoatPos;
        public float BoatYaw;
        public bool BoatAfloat;
        public float ThrottleSince = -1f;
        public float LastMove = -10f;
    }

    private static readonly Dictionary<int, Rider> Riders = new();
    private static readonly List<int> Gone = new();
    private static readonly Dictionary<int, List<(VehicleBase v, uint net)>> Seen = new();
    private static readonly List<uint> Ids = new();
    private static float _nextSlow;

    /// <summary>Every frame on the host (or solo).</summary>
    public static void Tick()
    {
        if (!GameState.IsAuthority || GameState.Runner == null) { Riders.Clear(); return; }
        float now = Time.unscaledTime;
        if (now < _nextSlow) return;
        _nextSlow = now + 0.25f;
        // every player's cars in the scan (a player can be in two for a moment: the old car not destroyed yet)
        foreach (var l in Seen.Values) l.Clear();
        foreach (var fp in GameState.FusionPlayers)
        {
            try
            {
                if (fp == null || fp.Object == null) continue;
                var who = fp.Object.InputAuthority;
                if (!who.IsRealPlayer) continue;
                var v = fp.vehicleBase;
                if (v == null) continue;
                int key = who.RawEncoded;
                if (!Seen.TryGetValue(key, out var l)) Seen[key] = l = new List<(VehicleBase, uint)>(2);
                l.Add((v, fp.Object.Id.Raw));
            }
            catch (Exception e) { DebugLog.Error(GraylingPlugin.ModId, e, "BoatLaunch.Tick scan"); }
        }
        foreach (var kv in Seen)
        {
            if (kv.Value.Count == 0) continue;
            try { Step(PlayerRef.FromEncoded(kv.Key), kv.Key, kv.Value, now); }
            catch (Exception e) { DebugLog.Error(GraylingPlugin.ModId, e, "BoatLaunch.Tick"); GraylingPlugin.LogOnce("launch", "the launch rules failed: " + e.Message); }
        }
        // a player missing from the scan keeps their record a while (across a car change they are, for a moment)
        Gone.Clear();
        foreach (var kv in Riders) if (kv.Value.Watch.Gone(now)) Gone.Add(kv.Key);
        foreach (var k in Gone) { Riders.Remove(k); Seen.Remove(k); }
    }

    private static void Step(PlayerRef who, int key, List<(VehicleBase v, uint net)> cars, float now)
    {
        if (!Riders.TryGetValue(key, out var r)) Riders[key] = r = new Rider();
        Ids.Clear();
        foreach (var c in cars) Ids.Add(c.net);
        int i = r.Watch.Pick(Ids);
        if (i < 0) return;
        var (v, net) = cars[i];
        bool boat = VehicleTraits.ModdedId(v) == GraylingDefinition.Id;
        switch (r.Watch.Saw(net, boat, now))
        {
            case CarSwap.IntoBoat:
                r.ThrottleSince = -1f;
                GraylingPlugin.Logger?.LogInfo($"[Grayling] player {who.PlayerId} changed into the boat.");
                OnBoatArrived(who, v, r);
                break;
            case CarSwap.OutOfBoat:
                r.ThrottleSince = -1f;
                GraylingPlugin.Logger?.LogInfo($"[Grayling] player {who.PlayerId} changed from the boat into a car ({(r.BoatAfloat ? "afloat" : "ashore")}).");
                OnCarArrived(who, v, r);
                break;
        }
        if (boat)
        {
            var t = v.transform;
            r.BoatPos = t.position;
            r.BoatYaw = t.eulerAngles.y;
            var st = BoatPhysics.Of(v.Pointer);
            r.BoatAfloat = st != null && st.Floating >= BoatHull.FloatingShare;
            Beached(who, v, r, st, now);
        }
    }

    // ---------------------------------------------------------------- the world probe

    /// <summary>Water and its depth at (x, z): the game's water surface and the ground under it (a ray down from it).</summary>
    public static WaterProbe Probe(float x, float z, float nearY)
    {
        float s = BoatPhysics.WaterAt(new Vector3(x, nearY, z));
        if (float.IsNaN(s)) return WaterProbe.Dry;
        float ground = s - 30f;
        if (Physics.Raycast(new Vector3(x, s + 2f, z), Vector3.down, out var hit, 40f, ~0, QueryTriggerInteraction.Ignore))
            ground = hit.point.y;
        return new WaterProbe(s > ground, s - ground);
    }

    // ---------------------------------------------------------------- the player's boat trailer (Trailers+)

    private static WorldSites.BoatTrailer? TrailerOf(PlayerRef who)
    {
        try
        {
            var t = WorldSites.BoatTrailerOf?.Invoke(who.RawEncoded);
            return t != null && t.Value.Drivable ? t : null;
        }
        catch (Exception e) { DebugLog.Error(GraylingPlugin.ModId, e, "BoatLaunch.TrailerOf"); return null; }
    }

    private static void SetBoat(WorldSites.BoatTrailer t, WorldSites.BoatAt at, float x = 0f, float z = 0f, float yaw = 0f)
    {
        try
        {
            string why = WorldSites.SetTrailerBoat?.Invoke(t.NetId, at, x, z, yaw);
            if (why != null) GraylingPlugin.Logger?.LogInfo($"[Grayling] the boat trailer said no ({at}): {why}.");
        }
        catch (Exception e) { DebugLog.Error(GraylingPlugin.ModId, e, "BoatLaunch.SetBoat"); }
    }

    private static float Flat(Vector3 a, float x, float z)
    {
        float dx = a.x - x, dz = a.z - z;
        return Mathf.Sqrt(dx * dx + dz * dz);
    }

    // ---------------------------------------------------------------- launch

    private static void OnBoatArrived(PlayerRef who, VehicleBase boat, Rider r)
    {
        var t = boat.transform;
        var at = t.position;
        var trailer = TrailerOf(who);
        if (trailer != null)
        {
            var tr = trailer.Value;
            bool moored = tr.At == WorldSites.BoatAt.Afloat;
            float seatWater = BoatPhysics.WaterAt(new Vector3(tr.SeatX, tr.KeelY + 0.5f, tr.SeatZ));
            float depth = float.IsNaN(seatWater) ? float.NaN : seatWater - tr.KeelY;
            float toBoat = moored ? Flat(at, tr.BoatX, tr.BoatZ) : float.MaxValue, toSeat = Flat(at, tr.SeatX, tr.SeatZ);
            switch (TrailerLaunch.PickLaunch(true, moored, tr.Loaded, toBoat, toSeat, depth))
            {
                case Launch.Moored:
                {
                    // into the boat where it floats, moored
                    float w = BoatPhysics.WaterAt(new Vector3(tr.BoatX, at.y, tr.BoatZ));
                    if (float.IsNaN(w)) w = float.IsNaN(seatWater) ? at.y : seatWater;
                    Move(boat, new Vector3(tr.BoatX, RestY(w), tr.BoatZ), tr.BoatYaw, "to where it was moored");
                    SetBoat(tr, WorldSites.BoatAt.Out);
                    r.LastMove = Time.unscaledTime;
                    return;
                }
                case Launch.Bunks:
                    // off the bunks: it floats where it sat, bow towards the land like the trailer
                    Move(boat, new Vector3(tr.SeatX, RestY(seatWater), tr.SeatZ), tr.Yaw, "off its trailer");
                    SetBoat(tr, WorldSites.BoatAt.Out);
                    r.LastMove = Time.unscaledTime;
                    return;
            }
            if (TrailerLaunch.TrailerEmptiesOnShoreLaunch(true, moored, tr.Loaded, toSeat)) SetBoat(tr, WorldSites.BoatAt.Out);
        }
        if (Afloat(boat)) return;
        LaunchFrom(boat, at, t.eulerAngles.y, r, "launched");
    }

    private static bool Afloat(VehicleBase boat)
    {
        var p = boat.transform.position;
        var w = Probe(p.x, p.z, p.y);
        return w.Water && w.Depth >= ShoreSearch.LaunchDepth;
    }

    private static bool LaunchFrom(VehicleBase boat, Vector3 from, float yaw, Rider r, string why)
    {
        float y = from.y;
        if (!ShoreSearch.Launch(from.x, from.z, (x, z) => Probe(x, z, y), out var spot))
        {
            GraylingPlugin.Logger?.LogInfo($"[Grayling] no open water within {ShoreSearch.Reach:0} m of the boat: it stays ashore.");
            return false;
        }
        float water = BoatPhysics.WaterAt(new Vector3(spot.X, y, spot.Z));
        if (float.IsNaN(water)) return false;
        float heading = spot.Distance > 0.1f ? spot.Yaw : yaw;
        Move(boat, new Vector3(spot.X, RestY(water), spot.Z), heading, why);
        r.LastMove = Time.unscaledTime;
        return true;
    }

    /// <summary>The boat's root height that floats it level on water at <paramref name="water"/>.</summary>
    private static float RestY(float water) => water - BoatHull.RestDraft(BoatHull.Mass, BoatHull.Points()) - BoatHull.KeelY - GraylingCore.Offset.y + 0.02f;

    /// <summary>A beached boat whose driver holds the throttle for a second with water close by goes back in.</summary>
    private static void Beached(PlayerRef who, VehicleBase boat, Rider r, BoatPhysics.State st, float now)
    {
        if (st == null || r.BoatAfloat || now - r.LastMove < 4f) { r.ThrottleSince = -1f; return; }
        float speed = 0f;
        try { var rb = boat.cachedRigidbody; if (rb != null) speed = rb.linearVelocity.magnitude; } catch (Exception ex) { DebugLog.Error(GraylingPlugin.ModId, ex, "BoatLaunch.Beached"); }
        if (st.Throttle < 0.5f || speed > 1.5f) { r.ThrottleSince = -1f; return; }
        if (r.ThrottleSince < 0f) { r.ThrottleSince = now; return; }
        if (now - r.ThrottleSince < 1.2f) return;
        r.ThrottleSince = -1f;
        var t = boat.transform;
        LaunchFrom(boat, t.position, t.eulerAngles.y, r, "pushed off the shore");
    }

    // ---------------------------------------------------------------- landing

    private static void OnCarArrived(PlayerRef who, VehicleBase car, Rider r)
    {
        var trailer = TrailerOf(who);
        if (trailer != null)
        {
            var tr = trailer.Value;
            switch (TrailerLaunch.PickLanding(true, tr.At == WorldSites.BoatAt.Out, r.BoatAfloat, Flat(r.BoatPos, tr.SeatX, tr.SeatZ)))
            {
                case Landing.ToTrailerMoored:
                    // the boat stays where they left it, moored; the car goes to the trailer for them to back it in
                    SetBoat(tr, WorldSites.BoatAt.Afloat, r.BoatPos.x, r.BoatPos.z, r.BoatYaw);
                    CarToTrailer(who, car, tr, r);
                    return;
                case Landing.ToTrailerLoaded:
                    SetBoat(tr, WorldSites.BoatAt.OnTrailer);
                    CarToTrailer(who, car, tr, r);
                    return;
                case Landing.ShoreBoatHome:
                    SetBoat(tr, WorldSites.BoatAt.OnTrailer);
                    break;
            }
        }
        if (!r.BoatAfloat) return;      // it was ashore: the car stands where the boat was
        var from = r.BoatPos;
        if (!ShoreSearch.Landing(from.x, from.z, (x, z) => Probe(x, z, from.y), out var spot))
        {
            GraylingPlugin.Logger?.LogInfo($"[Grayling] no dry ground within {ShoreSearch.Reach:0} m of the boat for the car.");
            return;
        }
        // on the ground there (the game's own repositioning drops it onto its wheels)
        float y = from.y + 2f;
        if (Physics.Raycast(new Vector3(spot.X, from.y + 30f, spot.Z), Vector3.down, out var hit, 80f, ~0, QueryTriggerInteraction.Ignore))
            y = hit.point.y + 0.4f;
        // facing the land: the way from the boat to the spot
        Move(car, new Vector3(spot.X, y, spot.Z), spot.Distance > 0.1f ? spot.Yaw : r.BoatYaw + 180f, "ashore");
        r.LastMove = Time.unscaledTime;
    }

    /// <summary>The car stands ahead of the trailer's hitch, facing away from the water (the way the trailer points), and
    /// Trailers+ hitches the trailer to it as soon as it stands there.</summary>
    private static void CarToTrailer(PlayerRef who, VehicleBase car, WorldSites.BoatTrailer tr, Rider r)
    {
        var q = Quaternion.Euler(0f, tr.Yaw, 0f);
        var at = new Vector3(tr.HitchX, tr.HitchY, tr.HitchZ) + q * Vector3.forward * 2.6f;
        float y = at.y + 0.5f;
        if (Physics.Raycast(at + Vector3.up * 20f, Vector3.down, out var hit, 40f, ~0, QueryTriggerInteraction.Ignore)) y = hit.point.y + 0.4f;
        Move(car, new Vector3(at.x, y, at.z), tr.Yaw, "to its boat trailer");
        try
        {
            string why = WorldSites.HitchBoatTrailer?.Invoke(tr.NetId, who.RawEncoded);
            if (why != null) GraylingPlugin.Logger?.LogInfo($"[Grayling] the boat trailer isn't hitched: {why}.");
        }
        catch (Exception e) { DebugLog.Error(GraylingPlugin.ModId, e, "BoatLaunch.CarToTrailer"); }
        r.LastMove = Time.unscaledTime;
    }

    private static void Move(VehicleBase car, Vector3 pos, float yaw, string why)
    {
        try
        {
            var winch = car.GetComponentInChildren<VehicleWinchSystem>();
            if (winch != null) winch.DetachAllWinches();
        }
        catch (Exception e) { DebugLog.Error(GraylingPlugin.ModId, e, "BoatLaunch.Move winch"); }
        OverTheHillPlus.Cars.CarMove.Place(car, pos, Quaternion.Euler(0f, yaw, 0f));
        try
        {
            var rb = car.cachedRigidbody;
            if (rb != null && !rb.isKinematic)
            {
                rb.linearVelocity = Vector3.zero;
                rb.angularVelocity = Vector3.zero;
            }
        }
        catch (Exception e) { DebugLog.Error(GraylingPlugin.ModId, e, "BoatLaunch.Move stop"); }
        GraylingPlugin.Logger?.LogInfo($"[Grayling] {why}: moved to {pos} heading {yaw:0}.");
    }

    public static void Clear()
    {
        Riders.Clear();
        Seen.Clear();
    }
}
