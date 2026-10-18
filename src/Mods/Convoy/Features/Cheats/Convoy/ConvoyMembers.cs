using System;
using System.Collections.Generic;
using Fusion;
using OverTheHillPlus.Features.Public.PlayerList;
using UnityEngine;
using VehiclePhysics;

namespace OverTheHillPlus.Features.Cheats.Convoy;

/// <summary>
/// A human player's car as the convoy sees it. The host simulates every car, so the host can read each player's
/// applied pedal input from the car's data bus and tell "stuck while trying" from "parked on purpose".
/// </summary>
internal sealed class PlayerMember
{
    public PlayerRef Ref;
    public int Raw;
    public FusionPlayer Fp;
    public VehicleBase Car;
    public VPVehicleController Vpp;
    public Transform T;
    public IntPtr RbPtr;
    public NetworkObject Obj;
    public CarDamageSystem Damage;
    public NetworkedVehicleStatus Status;
    public VehicleWinchSystem Winch;
    public string Name = "?";
    public bool IsLocal;

    public Vector3 Pos, Fwd;
    public float Speed, UpDot, Effort, WaterDepth, Damage01, GroundY;
    public bool Flooded;
    public readonly StuckMonitor Stuck = new();

    /// <summary>Last time this player was close to the convoy (counts as a member for a while after).</summary>
    public float LastNear = -1000f;
    public bool InConvoy => Time.time - LastNear < 45f;

    private float _nextSlow;
    private static int _chInput = -1, _throttle;

    public bool Alive
    {
        get
        {
            try { return Fp != null && Car != null && T != null; }
            catch (Exception ex) { DebugLog.Error("convoy", ex, "PlayerMember.Alive"); return false; }
        }
    }

    public void Refresh(float dt)
    {
        Pos = T.position;
        var f = T.forward;
        var flat = new Vector3(f.x, 0f, f.z);
        Fwd = flat.sqrMagnitude > 1e-4f ? flat.normalized : Vector3.forward;
        UpDot = T.up.y;
        try { Speed = Car.speed; } catch (Exception ex) { DebugLog.Error("convoy", ex, "PlayerMember.Refresh"); Speed = 0f; }
        try
        {
            if (Vpp != null)
            {
                if (_chInput < 0) { _chInput = Channel.Input; _throttle = InputData.Throttle; }
                Effort = Mathf.Clamp01(Mathf.Abs(Vpp.data.Get(_chInput, _throttle)) / 10000f);
            }
        }
        catch (Exception ex) { DebugLog.Error("convoy", ex, "PlayerMember.Refresh"); Effort = 0f; }
        try { Flooded = Status != null && Status.IsEngineIntakeFlooding; } catch (Exception ex) { DebugLog.Error("convoy", ex, "PlayerMember.Refresh"); Flooded = false; }
        if (Time.time >= _nextSlow)
        {
            _nextSlow = Time.time + 0.35f;
            if (ConvoySense.Ground(Pos, Pos.y + 0.5f, 1.5f, out float gy, out _)) GroundY = gy;
            else GroundY = Pos.y - 0.6f;
            WaterDepth = ConvoySense.WaterDepthOverGround(Pos, GroundY);
            try
            {
                float max = Damage != null ? Damage.MaxDamageAmount : 0f;
                Damage01 = max > 0f ? Mathf.Clamp01(Damage.DamageAmount / max) : 0f;
            }
            catch (Exception ex) { DebugLog.Error("convoy", ex, "PlayerMember.Refresh"); Damage01 = 0f; }
        }
        Stuck.Update(dt, Pos, Speed, Effort, UpDot, WaterDepth, Flooded, ConvoyFeature.StuckSeconds.Value + 1.5f);
    }
}

/// <summary>Keeps the list of player cars (rescanned once a second from the shared FusionPlayer scan).</summary>
internal static class ConvoyMembers
{
    public static readonly List<PlayerMember> Players = new();
    private static float _nextScan;

    public static void Clear()
    {
        Players.Clear();
        _nextScan = 0f;
    }

    public static PlayerMember Find(int raw)
    {
        for (int i = 0; i < Players.Count; i++) if (Players[i].Raw == raw) return Players[i];
        return null;
    }

    public static PlayerMember FindByRb(IntPtr rb)
    {
        for (int i = 0; i < Players.Count; i++) if (Players[i].RbPtr == rb) return Players[i];
        return null;
    }

    /// <summary>Rescan (1 Hz) and refresh telemetry (every frame).</summary>
    public static void Tick(float dt, Func<IntPtr, bool> isAgentCar)
    {
        if (Time.unscaledTime >= _nextScan)
        {
            _nextScan = Time.unscaledTime + 1f;
            Rescan(isAgentCar);
        }
        for (int i = Players.Count - 1; i >= 0; i--)
        {
            var p = Players[i];
            if (!p.Alive) { Players.RemoveAt(i); continue; }
            try { p.Refresh(dt); }
            catch (Exception e)
            {
                DebugLog.Error("convoy", e, "ConvoyMembers.Tick");
                ConvoyAi.LogOnce("player-refresh", "reading a player's car: " + e.Message);
                Players.RemoveAt(i);
            }
        }
    }

    private static void Rescan(Func<IntPtr, bool> isAgentCar)
    {
        var seen = new HashSet<int>();
        int localRaw = 0;
        try { localRaw = GameState.Runner != null ? GameState.Runner.LocalPlayer.RawEncoded : 0; } catch (Exception ex) { DebugLog.Error("convoy", ex, "ConvoyMembers.Rescan"); }
        var sp = OverlandSessionPlayers.Instance;
        IntPtr localCar = ConvoyAi.LocalCarPtr;
        foreach (var fp in GameState.FusionPlayers)
        {
            try
            {
                if (fp == null || fp.Object == null) continue;
                var car = fp.vehicleBase;
                if (car == null) continue;
                if (isAgentCar(car.Pointer)) continue;
                var who = fp.Object.InputAuthority;
                // Single-player can leave the local car without a real input authority: it is still you.
                if (!who.IsRealPlayer)
                {
                    if (car.Pointer != localCar || GameState.Runner == null) continue;
                    who = GameState.Runner.LocalPlayer;
                }
                int raw = who.RawEncoded;
                seen.Add(raw);
                var m = Find(raw);
                if (m == null || m.Fp == null || m.Fp.Pointer != fp.Pointer || m.Car == null || m.Car.Pointer != car.Pointer)
                {
                    if (m != null) Players.Remove(m);
                    m = new PlayerMember { Ref = who, Raw = raw, Fp = fp, Car = car };
                    m.Vpp = car.TryCast<VPVehicleController>();
                    m.T = car.cachedTransform != null ? car.cachedTransform : car.transform;
                    var rb = car.cachedRigidbody;
                    m.RbPtr = rb != null ? rb.Pointer : IntPtr.Zero;
                    m.Obj = fp.Object;
                    m.Damage = fp.GetComponentInChildren<CarDamageSystem>();
                    m.Status = fp.GetComponentInChildren<NetworkedVehicleStatus>();
                    m.Winch = fp.GetComponentInChildren<VehicleWinchSystem>();
                    Players.Add(m);
                }
                m.IsLocal = raw == localRaw;
                m.Name = PlayerListRoster.NameOf(sp, who);
            }
            catch (Exception e) { DebugLog.Error("convoy", e, "ConvoyMembers.Rescan"); ConvoyAi.LogOnce("player-scan", "listing player cars: " + e.Message); }
        }
        for (int i = Players.Count - 1; i >= 0; i--)
            if (!seen.Contains(Players[i].Raw)) Players.RemoveAt(i);
    }
}
