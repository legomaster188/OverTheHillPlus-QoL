using System;
using System.Collections.Generic;
using UnityEngine;
using VehiclePhysics;

namespace OverTheHillPlus.Features.Cheats.Convoy;

/// <summary>Per-car navigation memory (heading scan, three-point turns, progress towards a goal).</summary>
internal sealed class NavState
{
    public Vector3 Heading;           // last chosen heading off the trail
    public float NextScan;
    public float ScanCost;
    public string Why;
    public Vector3 Goal;
    public float BestDist = float.MaxValue;
    public float BestDistAt;
    public bool Turning;
    public int TurnPhase;             // even = forward, odd = reverse
    public float TurnPhaseAt;
    public int TurnSide = 1;
    public float BackOffUntil;
    public float NextObstacle;
    public float ObstacleCap = 99f;   // speed cap from the last obstacle scan
    public float Nudge;               // sideways shift of the trail target around something in the way
    public float NudgeUntil;

    public void NewGoal(Vector3 goal)
    {
        if ((goal - Goal).sqrMagnitude < 4f) return;
        Goal = goal;
        BestDist = float.MaxValue;
        BestDistAt = Time.time;
    }

    public float NoProgressFor => Time.time - BestDistAt;
}

/// <summary>A car the AI must not run into (players, other AIs, the leader).</summary>
internal struct CarBody
{
    public Vector3 Pos;
    public Vector3 Fwd;
    public float Speed;
    public bool IsPlayer;
    public IntPtr Rb;
}

/// <summary>
/// The AI's hands and feet: steering (pure pursuit), throttle/brake (speed controller with slope feed-forward and
/// traction care), forward/reverse changes, three-point turns, trail following with gap keeping, and finally writing
/// the command onto the car's VPP data bus every physics step.
/// </summary>
internal static class ConvoyDriver
{
    // ------------------------------------------------------------------ data bus ids (static props in the interop)

    private static bool _ids;
    private static int _chInput, _chVehicle, _chSettings;
    private static int _steer, _throttle, _brake, _handbrake, _manualGear, _gear, _engineWorking;
    private static int[] _diffIds;

    private static void Ids()
    {
        if (_ids) return;
        _chInput = Channel.Input;
        _chVehicle = Channel.Vehicle;
        _chSettings = Channel.Settings;
        _steer = InputData.Steer;
        _throttle = InputData.Throttle;
        _brake = InputData.Brake;
        _handbrake = InputData.Handbrake;
        _manualGear = InputData.ManualGear;
        _gear = VehicleData.GearboxGear;
        _engineWorking = VehicleData.EngineWorking;
        _diffIds = new[] { SettingsData.DifferentialLock, SettingsData.FrontDifferentialLock, SettingsData.RearDifferentialLock, SettingsData.DrivelineLock };
        _ids = true;
    }

    /// <summary>
    /// Every physics step, right before VPP's blocks read their inputs: the AI's command replaces whatever the game's
    /// input block wrote (it has no driver, so it wrote zeros). Gear changes follow the game's own auto
    /// forward/reverse rule: change direction only when (nearly) stopped, brake first otherwise.
    /// </summary>
    public static void Apply(ConvoyAgent a, VPVehicleController v)
    {
        Ids();
        var data = v.data;
        if (data == null) return;
        var c = a.Cmd;

        // Telemetry only the physics step has: wheel slip and spin.
        ReadWheels(a, v);

        bool engine = data.Get(_chVehicle, _engineWorking) > 0;
        if (!engine)
        {
            // Cranking: leave the gear and start key to the game's ignition, just hold the car.
            data.Set(_chInput, _throttle, 0);
            data.Set(_chInput, _brake, 10000);
            data.Set(_chInput, _handbrake, 10000);
            data.Set(_chInput, _steer, (int)(Mathf.Clamp(c.Steer, -1f, 1f) * 10000f));
            return;
        }

        int gear = data.Get(_chVehicle, _gear);
        float speed = a.Speed;
        float throttle = Mathf.Clamp01(c.Throttle), brake = Mathf.Clamp01(c.Brake);
        int manual = gear;
        if (c.Dir > 0 && gear <= 0)
        {
            if (speed < -0.5f) { throttle = 0f; brake = 1f; }
            else manual = 1;
        }
        else if (c.Dir < 0 && gear >= 0)
        {
            if (speed > 0.5f) { throttle = 0f; brake = 1f; }
            else manual = -1;
        }

        data.Set(_chInput, _steer, (int)(Mathf.Clamp(c.Steer, -1f, 1f) * 10000f));
        data.Set(_chInput, _throttle, (int)(throttle * 10000f));
        data.Set(_chInput, _brake, (int)(brake * 10000f));
        data.Set(_chInput, _handbrake, (int)(Mathf.Clamp01(c.Handbrake) * 10000f));
        if (manual != gear || c.Dir != 0) data.Set(_chInput, _manualGear, manual);

        // Differential locks (1 = ForceLocked), the same data bus settings the chassis tuning uses.
        if (c.DiffLock)
        {
            if (!a.DiffWritten)
            {
                for (int i = 0; i < 4; i++) a.DiffOriginal[i] = data.Get(_chSettings, _diffIds[i]);
                a.DiffWritten = true;
            }
            for (int i = 0; i < 4; i++) data.Set(_chSettings, _diffIds[i], 1);
        }
        else if (a.DiffWritten)
        {
            for (int i = 0; i < 4; i++) data.Set(_chSettings, _diffIds[i], a.DiffOriginal[i]);
            a.DiffWritten = false;
        }
    }

    private static void ReadWheels(ConvoyAgent a, VPVehicleController v)
    {
        try
        {
            var states = v.wheelState;
            int n = states == null ? 0 : Math.Min(states.Length, 12);
            float maxSlip = 0f, wheelSpeed = 0f;
            int grounded = 0, counted = 0;
            for (int i = 0; i < n; i++)
            {
                var ws = states[i];
                if (ws == null) continue;
                if (ws.grounded) grounded++;
                if (ws.grounded && ws.driveTorque > 1f) maxSlip = Math.Max(maxSlip, Math.Abs(ws.normalizedSlip));
                wheelSpeed += Math.Abs(ws.angularVelocity) * a.TireRadius;
                counted++;
            }
            a.MaxSlip = maxSlip;
            a.GroundedWheels = grounded;
            a.WheelSpeed = counted > 0 ? wheelSpeed / counted : 0f;
        }
        catch (Exception ex) { DebugLog.Error("convoy", ex, "ConvoyDriver.ReadWheels"); }
    }

    // ------------------------------------------------------------------ steering

    /// <summary>Pure pursuit: steering that puts the car on an arc through <paramref name="target"/>.</summary>
    public static float Pursuit(ConvoyAgent a, Vector3 target, bool reverse)
    {
        var l = a.Local(target);
        float ld = Mathf.Max(1.5f, l.magnitude);
        // Reversing: the target's angle from the car's tail; turning the wheel right swings the tail right.
        float alpha = reverse ? Mathf.Atan2(l.x, -l.z) : Mathf.Atan2(l.x, l.z);
        float delta = Mathf.Atan(2f * a.Wheelbase * Mathf.Sin(alpha) / ld) * Mathf.Rad2Deg;
        return Mathf.Clamp(delta / a.MaxSteerDeg, -1f, 1f);
    }

    /// <summary>Rate-limited steering (a real driver can't snap the wheel from lock to lock).</summary>
    public static float SmoothSteer(ConvoyAgent a, float want, float dt)
    {
        float rate = 2.2f + Mathf.Clamp01(ConvoyFeature.Aggressiveness.Value) * 1.5f;
        a.SmoothedSteer = Mathf.MoveTowards(a.SmoothedSteer, Mathf.Clamp(want, -1f, 1f), rate * dt);
        return a.SmoothedSteer;
    }

    // ------------------------------------------------------------------ speed

    /// <summary>Throttle and brake for a target speed in the given direction (+1 forward, -1 reverse).</summary>
    public static void SpeedControl(ConvoyAgent a, ref DriveCmd cmd, float target, int dir, float dt)
    {
        float aggr = Mathf.Clamp01(ConvoyFeature.Aggressiveness.Value);
        float maxThrottle = Mathf.Lerp(0.55f, 1f, aggr);
        cmd.Dir = dir;
        float v = a.Speed * dir; // speed in the wanted direction (negative = rolling the wrong way)
        a.TargetSpeed = target * dir;

        if (target < 0.25f)
        {
            cmd.Throttle = 0f;
            cmd.Brake = Mathf.Abs(a.Speed) < 0.7f ? 0.8f : Mathf.Clamp01(0.35f + Mathf.Abs(a.Speed) * 0.12f);
            cmd.Handbrake = Mathf.Abs(a.Speed) < 0.4f ? 1f : 0f;
            a.SpeedIntegral = 0f;
            return;
        }

        float err = target - v;
        a.SpeedIntegral = Mathf.Clamp(a.SpeedIntegral + err * dt * 0.07f, -0.15f, 0.45f);
        float uphill = a.Pitch * dir; // degrees climbing in the travel direction
        float ff = 0.14f + Mathf.Clamp(uphill * 0.022f, -0.12f, 0.45f);
        float thr = ff + 0.11f * err + a.SpeedIntegral;
        float brk = 0f;

        if (v < -0.4f) { thr = Mathf.Max(thr, 0.35f); } // rolling backwards on a hill: give it some
        if (err < -0.6f)
        {
            thr = Mathf.Min(thr, err < -1.2f ? 0f : 0.05f);
            brk = Mathf.Clamp01((-err - 0.6f) * (uphill < -6f ? 0.22f : 0.14f));
        }

        // Traction care: driven wheels slipping well past their peak means wasted throttle (and digging in).
        if (a.MaxSlip > 1.1f && Mathf.Abs(a.Speed) > 1.2f) thr *= Mathf.Clamp(1.6f - a.MaxSlip * 0.5f, 0.35f, 1f);

        cmd.Throttle = Mathf.Clamp(thr, 0f, maxThrottle);
        cmd.Brake = brk;
        cmd.Handbrake = 0f;
    }

    /// <summary>Speed limit from the cars around: never closer than a safe gap, more room for players.</summary>
    public static float CarCap(ConvoyAgent a, int dir, List<CarBody> cars, IntPtr ignore, float minClearAi, float minClearPlayer)
    {
        float cap = 99f;
        float myHalf = dir > 0 ? a.FrontZ : -a.RearZ;
        for (int i = 0; i < cars.Count; i++)
        {
            var b = cars[i];
            if (b.Rb == a.RbPtr || (ignore != IntPtr.Zero && b.Rb == ignore)) continue;
            // Where it will be in a moment (crossing traffic).
            var p = b.Pos + b.Fwd * (b.Speed * 0.8f);
            var l = a.Local(p);
            float along = l.z * dir;
            if (along <= 0f || along > 40f) continue;
            float lateral = Mathf.Abs(l.x);
            if (lateral > a.HalfWidth + 1.7f + along * 0.05f) continue;
            float clear = along - myHalf - 2.3f;
            float min = b.IsPlayer ? minClearPlayer : minClearAi;
            float theirs = Mathf.Max(0f, Vector3.Dot(b.Fwd * b.Speed, a.Fwd * dir));
            float c = clear <= min ? 0f : (clear - min) * 0.65f + theirs;
            if (c < cap) cap = c;
        }
        return cap;
    }

    // ------------------------------------------------------------------ trail following

    /// <summary>
    /// Follow the leader's trail keeping the gap to the car in front: pure pursuit on a look-ahead point that moves
    /// further out with speed, speed limited by the bend ahead, by how fast the leader drove there, by slope and by
    /// anything on the line.
    /// </summary>
    public static void FollowTrail(ConvoyAgent a, ConvoyTrail trail, float frontS, float frontSpeed, float carCap, float dt)
    {
        var cmd = a.Cmd;
        float v = Mathf.Abs(a.Speed);
        float aggr = Mathf.Clamp01(ConvoyFeature.Aggressiveness.Value);
        float s = a.S;

        float look = Mathf.Clamp(3.5f + 0.55f * v, 4.5f, 16f);
        float ts = Mathf.Min(s + look, trail.EndS - 0.3f);
        if (ts < s + 2f) ts = s + 2f;
        var p = trail.PointAt(ts, out var tf, out _);
        var tRight = new Vector3(tf.z, 0f, -tf.x);
        float lane = a.LaneOffset + a.Nav.Nudge;
        p += tRight * lane;
        a.DebugLook = p;

        float steer = Pursuit(a, p, false);
        // Off the line: blend in a correction towards the trail itself.
        cmd.Steer = SmoothSteer(a, steer, dt);

        // ---- target speed
        float maxSpeed = ConvoyFeature.MaxSpeed.Value / 3.6f;
        float gap = frontS - s;
        float desired = ConvoyFeature.Spacing.Value + Mathf.Abs(frontSpeed) * ConvoyFeature.TimeGap.Value;
        float vGap = gap < desired * 0.45f ? 0f : Mathf.Max(0f, Mathf.Abs(frontSpeed) + 0.45f * (gap - desired));

        float span = Mathf.Max(12f, v * 2.6f);
        float turn = trail.TurnAngle(s, s + span) * Mathf.Deg2Rad;
        float kappa = turn / span;
        float latAcc = Mathf.Lerp(2.2f, 4.5f, aggr);
        float vCurve = kappa < 1e-3f ? 99f : Mathf.Sqrt(latAcc / kappa);
        float vHint = trail.MinSpeed(s, s + 15f) * Mathf.Lerp(1.15f, 1.4f, aggr) + 2.5f;
        float vSteer = 4f + 14f * (1f - Mathf.Abs(cmd.Steer));
        float vSlope = a.Pitch < -12f ? Mathf.Max(2.5f, 8f - (-a.Pitch - 12f) * 0.35f) : 99f;

        // Something static on the line (fallen tree, rock, a parked car): slow down, try going around it.
        ObstacleScan(a, p, v);

        float target = Mathf.Min(Mathf.Min(maxSpeed, vGap), Mathf.Min(vCurve, vHint));
        target = Mathf.Min(target, Mathf.Min(vSteer, vSlope));
        target = Mathf.Min(target, Mathf.Min(a.Nav.ObstacleCap, carCap));
        // Off the trail by a few metres: slow down so the pursuit can pull it back on line.
        if (a.TrailDist > 3f) target = Mathf.Min(target, Mathf.Max(3f, 10f - a.TrailDist));

        SpeedControl(a, ref cmd, Mathf.Max(0f, target), 1, dt);
        cmd.LowRange = Mathf.Abs(a.Pitch) > 14f || (v < 3f && a.Pitch > 8f);
        cmd.DiffLock = a.MaxSlip > 1.4f && v < 4f;
        a.Cmd = cmd;
    }

    /// <summary>Checks the line ahead a few times a second; sets a speed cap and, if blocked, a sideways nudge.</summary>
    private static void ObstacleScan(ConvoyAgent a, Vector3 lookPoint, float v)
    {
        var nav = a.Nav;
        if (Time.time < nav.NextObstacle) return;
        nav.NextObstacle = Time.time + 0.2f;
        var dir = ConvoyTrail.Flat(lookPoint - a.Pos);
        if (dir.sqrMagnitude < 0.01f) dir = a.Fwd;
        dir.Normalize();
        float range = 5f + v * 1.6f;
        var kind = ConvoySense.Obstacle(a, dir, range, 0f, out float d, out _);
        if (kind == ConvoySense.HitKind.None)
        {
            nav.ObstacleCap = 99f;
            if (Time.time > nav.NudgeUntil) nav.Nudge = Mathf.MoveTowards(nav.Nudge, 0f, 0.5f);
            return;
        }
        // Cars are handled by the car-gap logic; only slow for them here.
        nav.ObstacleCap = Mathf.Max(0f, (d - 2.5f) * 0.8f);
        if (kind == ConvoySense.HitKind.Dynamic) return;
        // Static: look left and right for a way past.
        float bestSide = 0f, bestFree = d;
        foreach (float side in new[] { 2.2f, -2.2f, 3.5f, -3.5f })
        {
            var k = ConvoySense.Obstacle(a, dir, range, side, out float ds, out _);
            float free = k == ConvoySense.HitKind.None ? range : ds;
            if (free > bestFree + 1f) { bestFree = free; bestSide = side; }
        }
        if (bestSide != 0f)
        {
            nav.Nudge = bestSide;
            nav.NudgeUntil = Time.time + 3f;
            nav.ObstacleCap = Mathf.Max(nav.ObstacleCap, 3f);
        }
    }

    // ------------------------------------------------------------------ driving to a point

    /// <summary>
    /// Drive to a point away from the trail: straight when clear, around obstacles / steep ground / water when not,
    /// reversing to a point close behind, a three-point turn when the point is far behind. Returns true on arrival.
    /// </summary>
    public static bool DriveTo(ConvoyAgent a, Vector3 goal, float arrive, float maxSpeed, IntPtr ignoreRb, bool scan, float dt,
        bool turnIfBehind = false)
    {
        float capF = CarCap(a, 1, ConvoyAi.Bodies, ignoreRb, 3f, 5.5f);
        float capR = CarCap(a, -1, ConvoyAi.Bodies, ignoreRb, 3f, 5.5f);
        var cmd = a.Cmd;
        var nav = a.Nav;
        nav.NewGoal(goal);
        a.DebugTarget = goal;
        a.HasDebugTarget = true;
        var l = a.Local(goal);
        float dist = l.magnitude;
        if (dist < nav.BestDist - 1.5f) { nav.BestDist = dist; nav.BestDistAt = Time.time; }
        if (dist <= arrive)
        {
            nav.Turning = false;
            var hold = DriveCmd.Hold(handbrake: Mathf.Abs(a.Speed) < 0.5f);
            hold.Steer = a.SmoothedSteer;
            a.Cmd = hold;
            return true;
        }
        float angle = Mathf.Atan2(l.x, l.z) * Mathf.Rad2Deg;
        float aggr = Mathf.Clamp01(ConvoyFeature.Aggressiveness.Value);

        // Backing off after finding every direction blocked.
        if (Time.time < nav.BackOffUntil)
        {
            cmd.Steer = SmoothSteer(a, -Mathf.Sign(angle) * 0.6f, dt);
            SpeedControl(a, ref cmd, Mathf.Min(2.5f, capR), -1, dt);
            cmd.LowRange = true;
            a.Cmd = cmd;
            return false;
        }

        if (nav.Turning || (Mathf.Abs(angle) > 115f && (dist > 22f || turnIfBehind)))
        {
            if (!nav.Turning) StartTurn(a, angle);
            if (ThreePointTurn(a, angle, dt)) return false;
        }

        if (Mathf.Abs(angle) > 115f)
        {
            // Close behind: just reverse to it.
            cmd.Steer = SmoothSteer(a, Pursuit(a, goal, true), dt);
            float vr = Mathf.Min(3.5f, Mathf.Sqrt(2f * 1.2f * Mathf.Max(0f, dist - arrive)) + 0.4f);
            SpeedControl(a, ref cmd, Mathf.Min(vr, capR), -1, dt);
            cmd.LowRange = true;
            a.Cmd = cmd;
            a.DebugLook = goal;
            return false;
        }

        var goalDir = ConvoyTrail.Flat(goal - a.Pos).normalized;
        var heading = goalDir;
        if (scan)
        {
            if (Time.time >= nav.NextScan)
            {
                nav.NextScan = Time.time + 0.25f;
                float range = Mathf.Clamp(8f + Mathf.Abs(a.Speed) * 1.3f, 8f, 24f);
                nav.Heading = ConvoySense.ChooseHeading(a, goalDir, Mathf.Min(range, dist + 2f), ConvoyFeature.MaxClimb.Value,
                    ConvoyFeature.AvoidWater.Value, nav.Heading, out nav.ScanCost, out nav.Why);
                if (nav.ScanCost > 8.5f && Mathf.Abs(a.Speed) < 2f)
                {
                    nav.BackOffUntil = Time.time + 1.8f;
                    nav.Heading = Vector3.zero;
                }
            }
            if (nav.Heading.sqrMagnitude > 0.5f) heading = nav.Heading;
        }
        var aim = dist < 9f ? goal : a.Pos + heading * Mathf.Clamp(dist, 6f, 12f);
        a.DebugLook = aim;
        cmd.Steer = SmoothSteer(a, Pursuit(a, aim, false), dt);

        float vArrive = Mathf.Sqrt(2f * 1.6f * Mathf.Max(0f, dist - arrive)) + 0.6f;
        float vSteer = 3f + 12f * (1f - Mathf.Abs(cmd.Steer));
        float vSlope = a.Pitch < -12f ? Mathf.Max(2.5f, 7f - (-a.Pitch - 12f) * 0.3f) : 99f;
        float vScan = scan && nav.ScanCost > 3f ? Mathf.Lerp(6f, 3f, Mathf.Clamp01((nav.ScanCost - 3f) / 5f)) : 99f;
        float target = Mathf.Min(Mathf.Min(maxSpeed, vArrive), Mathf.Min(vSteer, vSlope));
        target = Mathf.Min(target, Mathf.Min(vScan, capF));
        target = Mathf.Min(target, Mathf.Lerp(9f, 15f, aggr));
        SpeedControl(a, ref cmd, target, 1, dt);
        cmd.LowRange = Mathf.Abs(a.Pitch) > 12f || Mathf.Abs(a.Speed) < 2.5f;
        cmd.DiffLock = a.MaxSlip > 1.3f && Mathf.Abs(a.Speed) < 4f;
        a.Cmd = cmd;
        return false;
    }

    private static void StartTurn(ConvoyAgent a, float angleToGoal)
    {
        var nav = a.Nav;
        nav.Turning = true;
        nav.TurnPhase = 0;
        nav.TurnPhaseAt = Time.time;
        // Turn towards the side with more room (and towards the goal when it's a tie).
        ConvoySense.Obstacle(a, Quaternion.Euler(0f, 70f, 0f) * a.Fwd, 10f, 0f, out float dr, out _);
        ConvoySense.Obstacle(a, Quaternion.Euler(0f, -70f, 0f) * a.Fwd, 10f, 0f, out float dl, out _);
        var pr = ConvoySense.Profile(a, Quaternion.Euler(0f, 60f, 0f) * a.Fwd, 4f, 8f);
        var pl = ConvoySense.Profile(a, Quaternion.Euler(0f, -60f, 0f) * a.Fwd, 4f, 8f);
        float scoreR = dr - (pr.Ground ? Mathf.Abs(pr.Climb) * 0.15f + pr.Water * 4f : 8f);
        float scoreL = dl - (pl.Ground ? Mathf.Abs(pl.Climb) * 0.15f + pl.Water * 4f : 8f);
        nav.TurnSide = Mathf.Abs(scoreR - scoreL) < 1.5f ? (angleToGoal >= 0f ? 1 : -1) : (scoreR > scoreL ? 1 : -1);
    }

    /// <summary>Forward on full lock, back on the opposite lock, until facing the goal. False when finished.</summary>
    private static bool ThreePointTurn(ConvoyAgent a, float angleToGoal, float dt)
    {
        var nav = a.Nav;
        var cmd = a.Cmd;
        if (Mathf.Abs(angleToGoal) < 55f || nav.TurnPhase > 7)
        {
            nav.Turning = false;
            return false;
        }
        bool forward = nav.TurnPhase % 2 == 0;
        float t = Time.time - nav.TurnPhaseAt;
        // End this leg after a while, or early when something is close in the travel direction.
        bool blocked = false;
        if (t > 0.6f && Time.time >= nav.NextObstacle)
        {
            nav.NextObstacle = Time.time + 0.2f;
            var dir = forward ? a.Fwd : -a.Fwd;
            blocked = ConvoySense.Obstacle(a, dir, 3.2f, 0f, out _, out _) != ConvoySense.HitKind.None;
            if (!blocked)
            {
                var prof = ConvoySense.Profile(a, dir, 3f, 6f);
                blocked = !prof.Ground || prof.Drop > 1.5f || Mathf.Abs(prof.Climb) > ConvoyFeature.MaxClimb.Value + 6f || prof.Water > 0.8f;
            }
        }
        if (t > 3.2f || blocked || (t > 1.2f && Mathf.Abs(a.Speed) < 0.2f && a.Cmd.Throttle > 0.3f))
        {
            nav.TurnPhase++;
            nav.TurnPhaseAt = Time.time;
            forward = nav.TurnPhase % 2 == 0;
        }
        float lockSide = forward ? nav.TurnSide : -nav.TurnSide;
        // Wait until the wheels are turned before rolling (keeps the turn tight).
        cmd.Steer = SmoothSteer(a, lockSide, dt);
        float target = Mathf.Abs(a.SmoothedSteer - lockSide) < 0.3f ? 2.6f : 0f;
        SpeedControl(a, ref cmd, target, forward ? 1 : -1, dt);
        cmd.LowRange = true;
        a.Cmd = cmd;
        a.Activity = "three-point turn";
        return true;
    }
}
