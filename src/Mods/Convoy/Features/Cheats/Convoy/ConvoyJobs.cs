using System;
using UnityEngine;

namespace OverTheHillPlus.Features.Cheats.Convoy;

/// <summary>A job that starts by driving to a car: along the trail when both are on it, else cross-country.</summary>
internal abstract class CarJob : ConvoyJob
{
    private float _targetS, _targetTrailDist = float.MaxValue, _nextProject;
    protected float SideAngle;   // approach from a different side after a failed attempt

    public override Vector3 TargetPos => Incident?.Pos ?? Vector3.zero;

    /// <summary>
    /// Drives the helper to within <paramref name="stopDist"/> of the target car (with a clear line to it when asked).
    /// True when there.
    /// </summary>
    protected bool Approach(float dt, float stopDist, bool needLine, float maxSpeed)
    {
        var a = Helper;
        var t = Incident;
        var tp = t.Pos;
        float d = a.DistanceTo(tp);
        bool lineOk = !needLine || ConvoySense.ClearLine(a, tp, t.RbPtr);
        if ((d <= stopDist && lineOk) || (a.WaterDepth > 0.45f && d <= stopDist * 1.6f))
        {
            a.Cmd = DriveCmd.Hold();
            a.TargetSpeed = 0f;
            return true;
        }

        var trail = ConvoyAi.Trail;
        if (Time.time >= _nextProject)
        {
            _nextProject = Time.time + 1f;
            if (trail.Usable) trail.ProjectGlobal(tp, out _targetS, out _targetTrailDist);
            else _targetTrailDist = float.MaxValue;
        }

        if (trail.Usable && a.OnTrail && _targetTrailDist < 12f && SideAngle == 0f && d > stopDist + 4f)
        {
            // Along the known-good road, turning round first if the target is back the way we came.
            float ds = _targetS - a.S;
            float stopS = _targetS - Mathf.Sign(ds) * stopDist * 0.8f;
            float look = Mathf.Clamp(5f + Mathf.Abs(a.Speed) * 0.5f, 6f, 12f);
            float s1 = ds > 0f ? Mathf.Min(a.S + look, stopS) : Mathf.Max(a.S - look, stopS);
            var goal = trail.PointAt(s1);
            ConvoyDriver.DriveTo(a, goal, 0.5f, maxSpeed, t.RbPtr, false, dt, Mathf.Abs(ds) > 25f);
            a.Activity = $"{Title}: driving back along the trail ({d:0} m)";
        }
        else
        {
            // Cross-country to a spot on our side of the target (or another side after a failed try).
            var away = ConvoyTrail.Flat(a.Pos - tp);
            if (away.sqrMagnitude < 0.01f) away = -t.Fwd;
            away = Quaternion.Euler(0f, SideAngle, 0f) * away.normalized;
            var goal = DrySpot(tp, away, stopDist * 0.8f, stopDist * 1.5f);
            ConvoyDriver.DriveTo(a, goal, 2.5f, maxSpeed, t.RbPtr, true, dt);
            a.Activity = $"{Title}: driving over ({d:0} m)";
        }
        return false;
    }

    /// <summary>A point along a direction from a car that is on dry ground (water-stuck cars are approached from land).</summary>
    protected static Vector3 DrySpot(Vector3 from, Vector3 dir, float near, float far)
    {
        for (float r = near; r <= far + 0.01f; r += 2f)
        {
            var p = from + dir * r;
            if (!ConvoySense.Ground(p, from.y, 4f, out float gy, out _)) continue;
            if (ConvoySense.WaterDepthOverGround(p, gy) < 0.35f) return new Vector3(p.x, gy, p.z);
        }
        return from + dir * near;
    }
}

/// <summary>
/// Tow a stuck car: drive near it, winch onto it, reel in with the handbrake on (the winch pulls the stuck car), then
/// back away gently while still reeling, until the car moves freely. Retries from another side, gives up after that.
/// </summary>
internal sealed class TowJob : CarJob
{
    private bool _attachTried;
    private float _attachAt;
    private Vector3 _targetStart;
    private float _bestMoved, _bestMovedAt;
    private int _pullDir = -1;
    private int _tries;
    private Vector3 _myWinchLocal;

    public TowJob(ConvoyAgent helper, Incident inc)
    {
        Helper = helper;
        Incident = inc;
        Phase = "approach";
    }

    public override string Title => $"tow {Incident.Name}";

    public override bool AllowsSelfRecovery => Phase == "approach";
    public override bool Moving => Phase == "approach";
    public override bool Busy => Phase is "attach" or "reel" or "pull" or "release";

    public override void Update(float dt)
    {
        var a = Helper;
        var t = Incident;
        if (!t.TargetAlive) { Finish(false, $"{t.Name} is gone"); return; }
        float range = ConvoyWinch.Range(a);
        float d = a.DistanceTo(t.Pos);
        switch (Phase)
        {
            case "approach":
            {
                float stop = Mathf.Clamp(range * 0.55f, 7f, 13f);
                if (Approach(dt, stop, true, 11f))
                {
                    SetPhase("attach");
                    _attachTried = false;
                }
                if (InPhase > 120f) Finish(false, $"couldn't reach {t.Name}");
                break;
            }
            case "attach":
            {
                a.Cmd = DriveCmd.Hold();
                a.Activity = $"hooking the winch onto {t.Name}";
                if (!_attachTried)
                {
                    if (d > range * 0.95f) { SetPhase("approach"); break; }
                    _attachTried = true;
                    _attachAt = Time.time;
                    if (!ConvoyWinch.AttachToCar(a, t, out string err)) { Finish(false, "winch: " + err); break; }
                    ConvoyWinch.MyPoint(a, t.Pos, out _myWinchLocal, out _);
                }
                else if (ConvoyWinch.Attached(a))
                {
                    _targetStart = t.Pos;
                    _bestMoved = 0f;
                    _bestMovedAt = Time.time;
                    // Winch at the front: pull by reversing; at the back: pull by driving forward.
                    _pullDir = _myWinchLocal.z >= 0f ? -1 : 1;
                    if (t.Agent != null) t.Agent.TowedBy = a;
                    SetPhase("reel");
                }
                else if (Time.time - _attachAt > 3f) Finish(false, "the winch didn't attach");
                break;
            }
            case "reel":
            {
                // Handbrake on: the helper is the anchor, the winch drags the stuck car.
                var hold = DriveCmd.Hold();
                hold.DiffLock = true;
                hold.LowRange = true;
                a.Cmd = hold;
                a.TargetSpeed = 0f;
                ConvoyWinch.ReelIn(a, dt);
                a.Activity = $"winching {t.Name} in";
                if (!ConvoyWinch.Attached(a)) { Retry("the rope came off"); break; }
                if (CheckFreed()) break;
                bool slack = ConvoyWinch.RopeLength(a) <= 2.5f;
                if ((ConvoyWinch.Taut(a) && InPhase > 2.5f) || slack || InPhase > 9f) SetPhase("pull");
                break;
            }
            case "pull":
            {
                if (!ConvoyWinch.Attached(a)) { Retry("the rope came off"); break; }
                ConvoyWinch.ReelIn(a, dt, 0.5f);
                var cmd = a.Cmd;
                var away = ConvoyTrail.Flat(a.Pos - t.Pos).normalized;
                var aim = a.Pos + away * 10f;
                cmd.Steer = ConvoyDriver.SmoothSteer(a, ConvoyDriver.Pursuit(a, aim, _pullDir < 0), dt);
                ConvoyDriver.SpeedControl(a, ref cmd, 1.3f, _pullDir, dt);
                cmd.Throttle = Mathf.Min(cmd.Throttle, 0.45f + 0.35f * Mathf.Clamp01(InPhase / 10f));
                cmd.LowRange = cmd.DiffLock = true;
                // Don't drag a car through the helper: stop pulling once it is close.
                if (d < 5.5f) cmd = DriveCmd.Hold();
                a.Cmd = cmd;
                a.Activity = $"pulling {t.Name} out";
                if (CheckFreed()) break;
                if (Time.time - _bestMovedAt > 12f) Retry("no progress");
                else if (InPhase > 45f) Retry("taking too long");
                break;
            }
            case "release":
            {
                ConvoyWinch.Detach(a);
                if (t.Agent != null && t.Agent.TowedBy == a) t.Agent.TowedBy = null;
                a.Cmd = DriveCmd.Hold();
                a.Activity = "rope off";
                if (InPhase > 1f) Finish(true, $"towed {t.Name} free");
                break;
            }
        }
    }

    /// <summary>True (and moves on to "release") when the stuck car has come free.</summary>
    private bool CheckFreed()
    {
        var t = Incident;
        float moved = ConvoyTrail.Flat(t.Pos - _targetStart).magnitude;
        if (moved > _bestMoved + 0.5f) { _bestMoved = moved; _bestMovedAt = Time.time; }
        bool upright = t.Kind != IncidentKind.Flipped || t.Stuck == null || t.Stuck.Kind != StuckKind.Flipped;
        bool freed = moved >= 5f && upright && (t.Stuck == null || !t.Stuck.IsStuck || Mathf.Abs(t.Speed) > 1f || moved >= 8f);
        bool close = moved > 2f && Helper.DistanceTo(t.Pos) < 6f;
        bool dry = t.Kind != IncidentKind.Water || t.WaterDepth < 0.5f;
        if ((freed || close) && dry)
        {
            SetPhase("release");
            return true;
        }
        return false;
    }

    private void Retry(string why)
    {
        ConvoyWinch.Detach(Helper);
        if (Incident.Agent != null && Incident.Agent.TowedBy == Helper) Incident.Agent.TowedBy = null;
        _tries++;
        if (_tries >= 2) { Finish(false, $"couldn't pull {Incident.Name} free ({why})"); return; }
        // Next try from another angle: 70 degrees round.
        SideAngle = SideAngle == 0f ? 70f : -SideAngle;
        ConvoyAi.Event($"{Helper.Name}: {why}, trying {Incident.Name} from another side.");
        SetPhase("approach");
    }

    protected override void Cleanup()
    {
        ConvoyWinch.Detach(Helper);
        if (Incident?.Agent != null && Incident.Agent.TowedBy == Helper) Incident.Agent.TowedBy = null;
    }
}

/// <summary>Repair a damaged car with one of the helper's repair kits (the game's own heal, full repair).</summary>
internal sealed class RepairJob : CarJob
{
    public RepairJob(ConvoyAgent helper, Incident inc)
    {
        Helper = helper;
        Incident = inc;
        Phase = "approach";
    }

    public override string Title => $"repair {Incident.Name}";
    public override bool Moving => Phase == "approach";
    public override bool Busy => Phase == "repair";

    private float Range
    {
        get
        {
            float r = 8f;
            try { if (Helper.Damage != null && Helper.Damage.MaxRepairRange > 2f) r = Helper.Damage.MaxRepairRange; } catch (Exception ex) { DebugLog.Error("convoy", ex, "RepairJob.Range"); }
            return Mathf.Clamp(r * 0.75f, 4f, 10f);
        }
    }

    public override void Update(float dt)
    {
        var a = Helper;
        var t = Incident;
        if (!t.TargetAlive) { Finish(false, $"{t.Name} is gone"); return; }
        if (t.Damage01 < 0.05f) { Finish(true, $"{t.Name} is already fixed"); return; }
        switch (Phase)
        {
            case "approach":
                if (Approach(dt, Range, false, 12f)) SetPhase("repair");
                if (InPhase > 120f) Finish(false, $"couldn't reach {t.Name}");
                break;
            case "repair":
                a.Cmd = DriveCmd.Hold();
                a.TargetSpeed = 0f;
                if (a.DistanceTo(t.Pos) > Range + 4f) { SetPhase("approach"); break; }
                float need = ConvoyFeature.RepairSeconds.Value;
                a.Activity = $"repairing {t.Name} ({Mathf.Max(0f, need - InPhase):0.0} s)";
                if (InPhase < need) break;
                if (Heal(t, out string err))
                {
                    a.RepairsLeft = Mathf.Max(0, a.RepairsLeft - 1);
                    Finish(true, $"repaired {t.Name} ({a.RepairsLeft} kit(s) left)");
                }
                else Finish(false, "repair failed: " + err);
                break;
        }
    }

    /// <summary>What a repair kit does on the host: HealDamage(healer, full amount, reason).</summary>
    private static bool Heal(Incident t, out string error)
    {
        error = null;
        var dmg = t.TargetDamage;
        if (dmg == null) { error = "no damage system"; return false; }
        try
        {
            var runner = GameState.Runner;
            var healer = runner != null ? runner.LocalPlayer : default;
            var reason = t.Player != null && t.Player.IsLocal
                ? CarDamageSystem.HealthChangingReason.HealedBySelf
                : CarDamageSystem.HealthChangingReason.HealedByOther;
            AdvantageFlag.MarkUsed();
            dmg.HealDamage(healer, dmg.MaxDamageAmount, reason);
            return true;
        }
        catch (Exception e)
        {
            DebugLog.Error("convoy", e, "RepairJob.Heal");
            error = e.Message;
            ConvoyAi.LogOnce("heal", "repairing: " + e);
            return false;
        }
    }
}

/// <summary>A car on its roof: drive over, then put it back on its wheels (the game's reset, host power).</summary>
internal sealed class FlipJob : CarJob
{
    public FlipJob(ConvoyAgent helper, Incident inc)
    {
        Helper = helper;
        Incident = inc;
        Phase = "approach";
    }

    public override string Title => $"flip {Incident.Name} back";
    public override bool Moving => Phase == "approach";
    public override bool Busy => Phase == "flip";

    public override void Update(float dt)
    {
        var a = Helper;
        var t = Incident;
        if (!t.TargetAlive) { Finish(false, $"{t.Name} is gone"); return; }
        if (t.Stuck != null && t.Stuck.Kind != StuckKind.Flipped && t.T != null && t.T.up.y > 0.7f)
        {
            Finish(true, $"{t.Name} is back on its wheels");
            return;
        }
        switch (Phase)
        {
            case "approach":
                if (Approach(dt, 12f, false, 12f)) SetPhase("flip");
                if (InPhase > 120f) Finish(false, $"couldn't reach {t.Name}");
                break;
            case "flip":
                a.Cmd = DriveCmd.Hold();
                a.Activity = $"helping {t.Name} back onto its wheels";
                if (InPhase < 2f) break;
                if (t.Car != null && CarReset.Reset(t.Car, out string why))
                {
                    AdvantageFlag.MarkUsed();
                    t.Stuck?.Reset();
                    Finish(true, $"put {t.Name} back on its wheels");
                }
                else Finish(false, "couldn't flip it");
                break;
        }
    }
}
