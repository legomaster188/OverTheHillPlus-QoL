using System;
using Fusion;
using UnityEngine;
using VehiclePhysics;

namespace OverTheHillPlus.Features.Cheats.Convoy;

internal enum AgentMode
{
    Starting,   // engine cranking after spawn
    Follow,     // driving the leader's trail in formation
    Wait,       // holding position (convoy waiting for someone)
    Regroup,    // driving back to the trail / formation
    Recover,    // working itself free (back off, rock, ...)
    Help,       // on a job: tow, repair, flip help, meteorite
    BeingTowed, // stuck and attached to a helper: gentle throttle towards the helper
    Park,       // parked on command
}

/// <summary>What the AI wants the car to do. Written by the behaviours (per frame), applied every physics step.</summary>
internal struct DriveCmd
{
    public float Steer;      // -1..1 (right positive)
    public float Throttle;   // 0..1
    public float Brake;      // 0..1
    public float Handbrake;  // 0..1
    public int Dir;          // +1 forward, -1 reverse, 0 = keep gear
    public bool LowRange;
    public bool DiffLock;

    public static DriveCmd Hold(bool handbrake = true) =>
        new() { Brake = 1f, Handbrake = handbrake ? 1f : 0f, Dir = 0 };
}

/// <summary>One AI-driven car: its game components, live telemetry and control state.</summary>
internal sealed class ConvoyAgent
{
    private static int _nextId = 1;

    public readonly int Id = _nextId++;
    public string Name;              // "AI 3 (Mammoth)"
    public string Model;
    public NetworkObject Obj;
    public VPVehicleController Car;
    public Rigidbody Rb;
    public Transform T;
    public IntPtr CarPtr, RbPtr;
    public CarIgnition Ignition;
    public FunselektorFusionVPStandardInput Input;
    public VehicleWinchSystem Winch;
    public CarDamageSystem Damage;
    public NetworkedVehicleStatus Status;
    public FusionPlayer Fusion;

    // geometry (car-local, measured once)
    public float FrontZ = 2.4f, RearZ = -2.4f, HalfWidth = 1.1f, Wheelbase = 2.7f, MaxSteerDeg = 32f, TireRadius = 0.4f;

    // telemetry (refreshed every frame)
    public Vector3 Pos, Fwd, Right, Up;
    public float Speed;          // signed, along the car's forward axis (m/s)
    public float Pitch;          // degrees, nose up positive
    public float UpDot;          // 1 = upright, <0 = on its roof
    public float WaterDepth;     // water surface above the ground under the car (m), 0 = dry
    public float GroundY;        // ground height under the car
    private float _nextSlowRefresh;
    public bool Flooded;         // engine intake under water
    public bool EngineRunning;
    public float Damage01;
    public float MaxSlip;        // driven wheels' normalised slip (physics step)
    public int GroundedWheels;
    public float WheelSpeed;     // mean |wheel surface speed| (physics step)

    // trail progress
    public float S;
    public float TrailDist = float.MaxValue;
    public int TrailGen = -1;
    public bool OnTrail;
    public float LaneOffset;

    // control
    public DriveCmd Cmd = DriveCmd.Hold();
    public AgentMode Mode = AgentMode.Starting;
    public string Activity = "starting";
    public float ModeSince;
    public Vector3 DebugTarget, DebugLook;
    public bool HasDebugTarget;
    public float TargetSpeed;
    public float SpeedIntegral;
    public float SmoothedSteer;

    // engine
    public float LastStartTry = -100f;
    public float SpawnedAt;

    // stuck / recovery
    public readonly StuckMonitor Stuck = new();
    public readonly RecoveryLadder Recovery = new();

    // teamwork
    public ConvoyJob Job;
    public int RepairsLeft;
    public float BusyUntil;      // short cool-down after a job before the next one is given
    public ConvoyAgent TowedBy;  // set while another AI car has its winch on this one
    public float MakeWayUntil;   // pulling over to let a helper past
    public Vector3 MakeWaySpot;
    public ConvoyAgent MakeWayFor;
    public float NextCatchUp;    // cool-down for repositioning (catch-up, flip, rescue)

    // navigation memory
    public readonly NavState Nav = new();

    // diff lock bookkeeping (restored when unlocked)
    public bool DiffWritten;
    public readonly int[] DiffOriginal = new int[4];
    public bool LowRangeSet;

    public bool Alive
    {
        get
        {
            try { return Obj != null && Obj.IsValid && Car != null && T != null; }
            catch (Exception ex) { DebugLog.Error("convoy", ex, "ConvoyAgent.Alive"); return false; }
        }
    }

    public void SetMode(AgentMode m, string activity)
    {
        if (Mode != m)
        {
            Mode = m;
            ModeSince = Time.time;
            SpeedIntegral = 0f;
        }
        Activity = activity;
    }

    public float InMode => Time.time - ModeSince;

    public bool Busy => Job != null || Mode == AgentMode.Recover || Mode == AgentMode.BeingTowed || Mode == AgentMode.Starting;

    /// <summary>Reads the car's components once after spawning. False if it isn't a drivable car.</summary>
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
            Input = obj.GetComponentInChildren<FunselektorFusionVPStandardInput>();
            Winch = obj.GetComponentInChildren<VehicleWinchSystem>();
            Damage = obj.GetComponentInChildren<CarDamageSystem>();
            Status = obj.GetComponentInChildren<NetworkedVehicleStatus>();
            Fusion = obj.GetComponentInChildren<FusionPlayer>();
        }
        catch (Exception e)
        {
            DebugLog.Error("convoy", e, "ConvoyAgent.Bind");
            ConvoyAi.LogOnce("bind", "reading the spawned car's parts: " + e.Message);
            return Car != null;
        }
        MeasureGeometry();
        SpawnedAt = Time.time;
        ModeSince = Time.time;
        return true;
    }

    private void MeasureGeometry()
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
                try { if (col.tireRadius > 0.15f) TireRadius = col.tireRadius; } catch (Exception ex) { DebugLog.Error("convoy", ex, "ConvoyAgent.MeasureGeometry"); }
                maxZ = Mathf.Max(maxZ, lp.z);
                minZ = Mathf.Min(minZ, lp.z);
                maxX = Mathf.Max(maxX, Mathf.Abs(lp.x));
            }
            if (maxZ > minZ)
            {
                FrontZ = maxZ + 1.0f;
                RearZ = minZ - 1.0f;
                HalfWidth = maxX + 0.35f;
                Wheelbase = Mathf.Clamp(maxZ - minZ, 1.8f, 4.5f);
            }
        }
        catch (Exception e) { DebugLog.Error("convoy", e, "ConvoyAgent.MeasureGeometry"); ConvoyAi.LogOnce("geometry", "measuring the car: " + e.Message); }
        try
        {
            if (Car.m_wheelbase > 1f) Wheelbase = Mathf.Clamp(Car.m_wheelbase, 1.8f, 4.5f);
        }
        catch (Exception ex) { DebugLog.Error("convoy", ex, "ConvoyAgent.MeasureGeometry"); }
        try
        {
            var st = Car.steering;
            if (st != null && st.maxSteerAngle > 5f) MaxSteerDeg = Mathf.Clamp(st.maxSteerAngle, 15f, 50f);
        }
        catch (Exception ex) { DebugLog.Error("convoy", ex, "ConvoyAgent.MeasureGeometry"); }
    }

    /// <summary>Per-frame telemetry. Cheap: a few transform reads and network property reads.</summary>
    public void Refresh()
    {
        Pos = T.position;
        var f = T.forward;
        Up = T.up;
        UpDot = Up.y;
        var flat = new Vector3(f.x, 0f, f.z);
        Fwd = flat.sqrMagnitude > 1e-4f ? flat.normalized : Vector3.ProjectOnPlane(-Up, Vector3.up).normalized;
        if (Fwd.sqrMagnitude < 0.5f) Fwd = Vector3.forward;
        Right = new Vector3(Fwd.z, 0f, -Fwd.x);
        Pitch = Mathf.Asin(Mathf.Clamp(f.y, -1f, 1f)) * Mathf.Rad2Deg;
        try { Speed = Car.speed; } catch (Exception ex) { DebugLog.Error("convoy", ex, "ConvoyAgent.Refresh"); Speed = 0f; }

        try { EngineRunning = Ignition == null || Ignition.EngineRunning; } catch (Exception ex) { DebugLog.Error("convoy", ex, "ConvoyAgent.Refresh"); EngineRunning = true; }
        try { Flooded = Status != null && Status.IsEngineIntakeFlooding; } catch (Exception ex) { DebugLog.Error("convoy", ex, "ConvoyAgent.Refresh"); Flooded = false; }
        try
        {
            if (Damage != null)
            {
                float max = Damage.MaxDamageAmount;
                Damage01 = max > 0f ? Mathf.Clamp01(Damage.DamageAmount / max) : 0f;
            }
        }
        catch (Exception ex) { DebugLog.Error("convoy", ex, "ConvoyAgent.Refresh"); Damage01 = 0f; }

        // Ground and water under the car: a raycast and a water lookup, 4 times a second is plenty.
        if (Time.time >= _nextSlowRefresh)
        {
            _nextSlowRefresh = Time.time + 0.25f;
            if (ConvoySense.Ground(Pos, Pos.y + 0.5f, 1.5f, out float gy, out _)) GroundY = gy;
            else GroundY = Pos.y - 0.6f;
            WaterDepth = ConvoySense.WaterDepthOverGround(Pos, GroundY);
        }
    }

    /// <summary>Starts the engine through the game's ignition (sets the VPP start key on the next input step).</summary>
    public void KeepEngineRunning()
    {
        if (EngineRunning || Ignition == null) return;
        if (Time.time - LastStartTry < 2.5f) return;
        LastStartTry = Time.time;
        try
        {
            if (!Ignition.IsEngineStarting) Ignition.StartEngine();
        }
        catch (Exception e) { DebugLog.Error("convoy", e, "ConvoyAgent.KeepEngineRunning"); ConvoyAi.LogOnce("ignition", "starting an engine: " + e.Message); }
    }

    /// <summary>Low range gear (host side networked flag of the car's input block).</summary>
    public void SetLowRange(bool on)
    {
        if (Input == null || LowRangeSet == on) return;
        try
        {
            if ((bool)Input.GearboxIsLowTransferCase != on) Input.GearboxIsLowTransferCase = on;
            LowRangeSet = on;
        }
        catch (Exception e)
        {
            DebugLog.Error("convoy", e, "ConvoyAgent.SetLowRange");
            Input = null; // don't keep trying a broken property
            ConvoyAi.LogOnce("lowrange", "low range: " + e.Message);
        }
    }

    public float DistanceTo(Vector3 p) => ConvoyTrail.Flat(p - Pos).magnitude;

    /// <summary>Point in the car's frame (x right, z forward), horizontal.</summary>
    public Vector3 Local(Vector3 world)
    {
        var d = ConvoyTrail.Flat(world - Pos);
        return new Vector3(Vector3.Dot(d, Right), 0f, Vector3.Dot(d, Fwd));
    }

    public override string ToString() => Name;
}
