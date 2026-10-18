using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;
using VehiclePhysics;

namespace OverTheHillPlus.Traffic;

/// <summary>A car the AI must not run into (players and other AI cars).</summary>
internal struct CarBody
{
    public Vector3 Pos, Fwd;
    public float Speed;
    public bool IsPlayer;
    public IntPtr Rb;
}

/// <summary>
/// AI Traffic's hands and feet, a trimmed copy of Convoy AI's driver (mods don't reference each other): pure pursuit
/// steering, a speed controller with slope feed-forward, gaps to other cars (more room for players), a look ahead for
/// obstacles, and writing the command onto the car's VPP data bus every physics step.
///
/// Game facts (Convoy AI's notes, Cpp2IL): FunselektorFusionVPStandardInput.ApplyVehicleInput writes the driver's
/// input to the data bus (Channel.Input: Steer / Throttle / Brake / Handbrake scaled to 10000, ManualGear = the gear,
/// ±1 to change direction once stopped); a car without a driver gets zeros. Our values go in a prefix of
/// VPVehicleController.DoUpdateBlocks, right before VPP's blocks read them.
/// </summary>
internal static class TrafficDriver
{
    private static bool _ids;
    private static int _chInput, _chVehicle, _steer, _throttle, _brake, _handbrake, _manualGear, _gear, _engineWorking;

    private const int Mask = ~(1 << 2); // everything but "Ignore Raycast"
    private static readonly Il2CppStructArray<RaycastHit> Hits = new(32);

    private static void Ids()
    {
        if (_ids) return;
        _chInput = Channel.Input;
        _chVehicle = Channel.Vehicle;
        _steer = InputData.Steer;
        _throttle = InputData.Throttle;
        _brake = InputData.Brake;
        _handbrake = InputData.Handbrake;
        _manualGear = InputData.ManualGear;
        _gear = VehicleData.GearboxGear;
        _engineWorking = VehicleData.EngineWorking;
        _ids = true;
    }

    /// <summary>Every physics step: the AI's command replaces the (empty) driver input.</summary>
    public static void Apply(TrafficAgent a, VPVehicleController v)
    {
        Ids();
        var data = v.data;
        if (data == null) return;
        var c = a.Cmd;
        bool engine = data.Get(_chVehicle, _engineWorking) > 0;
        if (!engine)
        {
            data.Set(_chInput, _throttle, 0);
            data.Set(_chInput, _brake, 10000);
            data.Set(_chInput, _handbrake, 10000);
            data.Set(_chInput, _steer, (int)(Mathf.Clamp(c.Steer, -1f, 1f) * 10000f));
            return;
        }
        int gear = data.Get(_chVehicle, _gear);
        float throttle = Mathf.Clamp01(c.Throttle), brake = Mathf.Clamp01(c.Brake);
        int manual = gear;
        if (c.Dir > 0 && gear <= 0)
        {
            if (a.Speed < -0.5f) { throttle = 0f; brake = 1f; }
            else manual = 1;
        }
        else if (c.Dir < 0 && gear >= 0)
        {
            if (a.Speed > 0.5f) { throttle = 0f; brake = 1f; }
            else manual = -1;
        }
        data.Set(_chInput, _steer, (int)(Mathf.Clamp(c.Steer, -1f, 1f) * 10000f));
        data.Set(_chInput, _throttle, (int)(throttle * 10000f));
        data.Set(_chInput, _brake, (int)(brake * 10000f));
        data.Set(_chInput, _handbrake, (int)(Mathf.Clamp01(c.Handbrake) * 10000f));
        if (manual != gear || c.Dir != 0) data.Set(_chInput, _manualGear, manual);
    }

    /// <summary>Pure pursuit: steering that puts the car on an arc through <paramref name="target"/>.</summary>
    public static float Pursuit(TrafficAgent a, Vector3 target)
    {
        var l = a.Local(target);
        float ld = Mathf.Max(1.5f, l.magnitude);
        float alpha = Mathf.Atan2(l.x, l.z);
        float delta = Mathf.Atan(2f * a.Wheelbase * Mathf.Sin(alpha) / ld) * Mathf.Rad2Deg;
        return Mathf.Clamp(delta / a.MaxSteerDeg, -1f, 1f);
    }

    public static float SmoothSteer(TrafficAgent a, float want, float dt)
    {
        a.SmoothedSteer = Mathf.MoveTowards(a.SmoothedSteer, Mathf.Clamp(want, -1f, 1f), 2.4f * dt);
        return a.SmoothedSteer;
    }

    /// <summary>Throttle and brake for a target speed forward (m/s); a target near 0 holds the car.</summary>
    public static void SpeedControl(TrafficAgent a, ref DriveCmd cmd, float target, float dt)
    {
        cmd.Dir = 1;
        a.TargetSpeed = target;
        if (target < 0.25f)
        {
            cmd.Throttle = 0f;
            cmd.Brake = Mathf.Abs(a.Speed) < 0.7f ? 0.8f : Mathf.Clamp01(0.35f + Mathf.Abs(a.Speed) * 0.12f);
            cmd.Handbrake = Mathf.Abs(a.Speed) < 0.4f ? 1f : 0f;
            a.SpeedIntegral = 0f;
            return;
        }
        float v = a.Speed;
        float err = target - v;
        a.SpeedIntegral = Mathf.Clamp(a.SpeedIntegral + err * dt * 0.07f, -0.15f, 0.45f);
        float ff = 0.14f + Mathf.Clamp(a.Pitch * 0.022f, -0.12f, 0.45f);
        float thr = ff + 0.11f * err + a.SpeedIntegral;
        float brk = 0f;
        if (v < -0.4f) thr = Mathf.Max(thr, 0.35f); // rolling back on a hill
        if (err < -0.6f)
        {
            thr = Mathf.Min(thr, err < -1.2f ? 0f : 0.05f);
            brk = Mathf.Clamp01((-err - 0.6f) * (a.Pitch < -6f ? 0.22f : 0.14f));
        }
        cmd.Throttle = Mathf.Clamp(thr, 0f, 0.8f);
        cmd.Brake = brk;
        cmd.Handbrake = 0f;
    }

    /// <summary>Speed limit from the cars ahead: never closer than a safe gap, much more room for players.</summary>
    public static float CarCap(TrafficAgent a, List<CarBody> cars, float minClearAi, float minClearPlayer)
    {
        float cap = 99f;
        for (int i = 0; i < cars.Count; i++)
        {
            var b = cars[i];
            if (b.Rb == a.RbPtr) continue;
            var p = b.Pos + b.Fwd * (b.Speed * 0.8f); // where it will be in a moment (crossing traffic)
            var l = a.Local(p);
            if (l.z <= 0f || l.z > 45f) continue;
            if (Mathf.Abs(l.x) > a.HalfWidth + 1.9f + l.z * 0.06f) continue;
            float clear = l.z - a.FrontZ - 2.3f;
            float min = b.IsPlayer ? minClearPlayer : minClearAi;
            float theirs = Mathf.Max(0f, Vector3.Dot(b.Fwd * b.Speed, a.Fwd));
            float c = clear <= min ? 0f : (clear - min) * 0.6f + theirs;
            if (c < cap) cap = c;
        }
        return cap;
    }

    /// <summary>A few times a second: something solid (tree, rock, wall) on the line ahead caps the speed.</summary>
    public static void ObstacleScan(TrafficAgent a, Vector3 towards)
    {
        if (Time.time < a.NextObstacle) return;
        a.NextObstacle = Time.time + 0.25f;
        var dir = towards - a.Pos;
        dir.y = 0f;
        if (dir.sqrMagnitude < 0.01f) dir = a.Fwd;
        dir.Normalize();
        float v = Mathf.Abs(a.Speed);
        float range = 6f + v * 1.6f;
        var origin = a.Pos + dir * (a.FrontZ * 0.9f) + Vector3.up * 0.6f;
        float hit = range;
        int n = 0;
        try { n = Physics.RaycastNonAlloc(origin, dir, Hits, range, Mask, QueryTriggerInteraction.Ignore); }
        catch (Exception e) { DebugLog.Error("traffic", e, "TrafficDriver.ObstacleScan"); TrafficAi.LogOnce("ray", "obstacle raycast: " + e.Message); }
        for (int i = 0; i < n; i++)
        {
            var h = Hits[i];
            if (h.distance <= 0.01f || h.distance >= hit) continue;
            var rb = h.rigidbody;
            if (rb != null && rb.Pointer == a.RbPtr) continue;
            if (h.normal.y > 0.72f) continue; // a drivable slope, not a wall
            if (rb != null && !rb.isKinematic) continue; // cars are handled by the car gap
            hit = h.distance;
        }
        a.ObstacleCap = hit >= range ? 99f : Mathf.Max(0f, (hit - 2.5f) * 0.8f);
    }
}
