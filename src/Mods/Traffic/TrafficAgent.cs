using System;
using Fusion;
using UnityEngine;
using VehiclePhysics;

namespace OverTheHillPlus.Traffic;

/// <summary>What the AI wants the car to do. Written per frame, applied every physics step.</summary>
internal struct DriveCmd
{
    public float Steer;     // -1..1 (right positive)
    public float Throttle;  // 0..1
    public float Brake;     // 0..1
    public float Handbrake; // 0..1
    public int Dir;         // +1 forward, -1 reverse, 0 = keep gear

    public static DriveCmd Hold() => new() { Brake = 1f, Handbrake = 1f, Dir = 0 };
}

/// <summary>
/// One AI Traffic car: its game components, telemetry, road position and control state. A trimmed copy of Convoy
/// AI's agent (mods don't reference each other): no jobs, winch or recovery; a stuck traffic car is simply replaced.
/// </summary>
internal sealed class TrafficAgent
{
    private static int _nextId = 1;

    public readonly int Id = _nextId++;
    public string Model;
    public NetworkObject Obj;
    public VPVehicleController Car;
    public Rigidbody Rb;
    public Transform T;
    public IntPtr CarPtr, RbPtr;
    public CarIgnition Ignition;

    // geometry (car-local, measured once)
    public float FrontZ = 2.4f, HalfWidth = 1.1f, Wheelbase = 2.7f, MaxSteerDeg = 32f;

    // telemetry
    public Vector3 Pos, Fwd, Right;
    public float Speed, Pitch, UpDot;
    public bool EngineRunning;

    // road
    public RoadNet.Cursor Route;
    public int RouteVersion = -1;
    public bool DeadEnd;
    public string Activity = "starting";

    // control
    public DriveCmd Cmd = DriveCmd.Hold();
    public float SmoothedSteer, SpeedIntegral, TargetSpeed;
    public float ObstacleCap = 99f, NextObstacle;
    public float LastStartTry = -100f, SpawnedAt;

    // planner input
    public float StuckFor, BlockingFor, UpsideDownFor;
    private Vector3 _progressAt;
    private float _progressTime;

    public bool Alive
    {
        get
        {
            try { return Obj != null && Obj.IsValid && Car != null && T != null; }
            catch (Exception ex) { DebugLog.Error("traffic", ex, "TrafficAgent.Alive"); return false; }
        }
    }

    /// <summary>Reads the spawned car's parts. False if it isn't a drivable car.</summary>
    public bool Bind(NetworkObject obj, string model)
    {
        Obj = obj;
        Model = model;
        try
        {
            Car = obj.GetComponentInChildren<VPVehicleController>();
            if (Car == null) return false;
            CarPtr = Car.Pointer;
            T = Car.cachedTransform != null ? Car.cachedTransform : Car.transform;
            Rb = Car.cachedRigidbody;
            RbPtr = Rb != null ? Rb.Pointer : IntPtr.Zero;
            Ignition = obj.GetComponentInChildren<CarIgnition>();
        }
        catch (Exception e)
        {
            DebugLog.Error("traffic", e, "TrafficAgent.Bind");
            TrafficAi.LogOnce("bind", "reading the spawned car's parts: " + e.Message);
            if (Car == null) return false;
        }
        Measure();
        SpawnedAt = Time.time;
        _progressAt = T.position;
        _progressTime = Time.time;
        return true;
    }

    private void Measure()
    {
        try
        {
            float maxZ = float.MinValue, minZ = float.MaxValue, maxX = 0f;
            var states = Car.wheelState;
            int n = states == null ? 0 : states.Length;
            for (int i = 0; i < n; i++)
            {
                var col = states[i]?.wheelCol;
                if (col == null) continue;
                var lp = T.InverseTransformPoint(col.transform.position);
                maxZ = Mathf.Max(maxZ, lp.z);
                minZ = Mathf.Min(minZ, lp.z);
                maxX = Mathf.Max(maxX, Mathf.Abs(lp.x));
            }
            if (maxZ > minZ)
            {
                FrontZ = maxZ + 1.0f;
                HalfWidth = maxX + 0.35f;
                Wheelbase = Mathf.Clamp(maxZ - minZ, 1.8f, 4.5f);
            }
        }
        catch (Exception e) { DebugLog.Error("traffic", e, "TrafficAgent.Measure"); TrafficAi.LogOnce("geometry", "measuring the car: " + e.Message); }
        try { if (Car.m_wheelbase > 1f) Wheelbase = Mathf.Clamp(Car.m_wheelbase, 1.8f, 4.5f); } catch (Exception ex) { DebugLog.Error("traffic", ex, "TrafficAgent.Measure"); }
        try
        {
            var st = Car.steering;
            if (st != null && st.maxSteerAngle > 5f) MaxSteerDeg = Mathf.Clamp(st.maxSteerAngle, 15f, 50f);
        }
        catch (Exception ex) { DebugLog.Error("traffic", ex, "TrafficAgent.Measure"); }
    }

    /// <summary>Per-frame telemetry and the stuck / upside-down / blocking timers for the planner.</summary>
    public void Refresh(float dt, float nearestPlayer)
    {
        Pos = T.position;
        var f = T.forward;
        float up = T.up.y;
        UpDot = up;
        var flat = new Vector3(f.x, 0f, f.z);
        Fwd = flat.sqrMagnitude > 1e-4f ? flat.normalized : Vector3.forward;
        Right = new Vector3(Fwd.z, 0f, -Fwd.x);
        Pitch = Mathf.Asin(Mathf.Clamp(f.y, -1f, 1f)) * Mathf.Rad2Deg;
        try { Speed = Car.speed; } catch (Exception ex) { DebugLog.Error("traffic", ex, "TrafficAgent.Refresh"); Speed = 0f; }
        try { EngineRunning = Ignition == null || Ignition.EngineRunning; } catch (Exception ex) { DebugLog.Error("traffic", ex, "TrafficAgent.Refresh"); EngineRunning = true; }

        // No progress: under 4 m in the last 8 s while it wanted to move (or a dead end).
        if ((Pos - _progressAt).sqrMagnitude > 16f)
        {
            _progressAt = Pos;
            _progressTime = Time.time;
        }
        bool wantsToMove = TargetSpeed > 0.5f || DeadEnd;
        float noProgress = Time.time - _progressTime;
        StuckFor = wantsToMove && noProgress > 8f ? noProgress : 0f;
        UpsideDownFor = up < 0.3f ? UpsideDownFor + dt : 0f;
        if (UpsideDownFor > StuckFor) StuckFor = UpsideDownFor * 4f; // flipped: gone after a few seconds

        // Standing still right next to a player: it may be in their way.
        BlockingFor = nearestPlayer < 18f && Mathf.Abs(Speed) < 1f ? BlockingFor + dt : 0f;
    }

    /// <summary>Starts the engine through the game's ignition (the VPP start key), retried every 2.5 s.</summary>
    public void KeepEngineRunning()
    {
        if (EngineRunning || Ignition == null) return;
        if (Time.time - LastStartTry < 2.5f) return;
        LastStartTry = Time.time;
        try { if (!Ignition.IsEngineStarting) Ignition.StartEngine(); }
        catch (Exception e) { DebugLog.Error("traffic", e, "TrafficAgent.KeepEngineRunning"); TrafficAi.LogOnce("ignition", "starting an engine: " + e.Message); }
    }

    /// <summary>Point in the car's frame (x right, z forward), horizontal.</summary>
    public Vector3 Local(Vector3 world)
    {
        var d = world - Pos;
        d.y = 0f;
        return new Vector3(Vector3.Dot(d, Right), 0f, Vector3.Dot(d, Fwd));
    }
}
