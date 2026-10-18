using System;
using Fusion;
using UnityEngine;
using VehiclePhysics;

namespace OverTheHillPlus.Features.Cheats.Convoy;

internal enum IncidentKind
{
    Stuck,
    Water,
    Flipped,
    Damaged,
}

/// <summary>
/// Something the convoy has to deal with: a car (player or AI) that is stuck, flipped, in deep water or badly
/// damaged. Lives on the shared blackboard (<see cref="ConvoyAi.Incidents"/>); the brain assigns the best-placed AI
/// as helper and closes it when the car is fine again.
/// </summary>
internal sealed class Incident
{
    private static int _nextId = 1;
    public readonly int Id = _nextId++;
    public IncidentKind Kind;
    public PlayerMember Player;   // one of these two
    public ConvoyAgent Agent;
    public float Since = Time.time;
    public ConvoyAgent Helper;
    public ConvoyJob Job;
    public bool Closed;
    public string Note = "";
    public int Attempts;
    public bool Manual;          // asked for with "Help me": handled even if the car doesn't look stuck
    public float NextAssignAt;
    public float LastHelperAt = -100f;

    public bool IsPlayer => Player != null;

    public bool TargetAlive => Player != null ? Player.Alive : Agent != null && Agent.Alive;

    public string Name => Player != null ? Player.Name : Agent?.Name ?? "?";

    public Vector3 Pos => Player != null ? Player.Pos : Agent != null ? Agent.Pos : Vector3.zero;
    public Vector3 Fwd => Player != null ? Player.Fwd : Agent != null ? Agent.Fwd : Vector3.forward;
    public float Speed => Player != null ? Player.Speed : Agent != null ? Agent.Speed : 0f;
    public IntPtr RbPtr => Player != null ? Player.RbPtr : Agent != null ? Agent.RbPtr : IntPtr.Zero;
    public VehicleBase Car => Player != null ? Player.Car : Agent?.Car;
    public NetworkObject Obj => Player != null ? Player.Obj : Agent?.Obj;
    public VehicleWinchSystem TargetWinch => Player != null ? Player.Winch : Agent?.Winch;
    public CarDamageSystem TargetDamage => Player != null ? Player.Damage : Agent?.Damage;
    public Transform T => Player != null ? Player.T : Agent?.T;
    public StuckMonitor Stuck => Player != null ? Player.Stuck : Agent?.Stuck;
    public float Damage01 => Player != null ? Player.Damage01 : Agent != null ? Agent.Damage01 : 0f;
    public float WaterDepth => Player != null ? Player.WaterDepth : Agent != null ? Agent.WaterDepth : 0f;
    public float GroundY => Player != null ? Player.GroundY : Agent != null ? Agent.GroundY : Pos.y;

    public bool SameTarget(PlayerMember p, ConvoyAgent a) => (p != null && Player == p) || (a != null && Agent == a);

    public string Describe() => Kind switch
    {
        IncidentKind.Stuck => $"{Name} is stuck",
        IncidentKind.Water => $"{Name} is in deep water",
        IncidentKind.Flipped => $"{Name} is on its roof",
        IncidentKind.Damaged => $"{Name} needs repairs",
        _ => Name,
    };
}

/// <summary>A task an AI car carries out for the convoy (tow, repair, flip help, meteorite pull).</summary>
internal abstract class ConvoyJob
{
    public ConvoyAgent Helper;
    public Incident Incident;
    public string Phase = "start";
    public float PhaseAt = Time.time;
    public readonly float StartedAt = Time.time;
    public bool Done;
    public bool Failed;
    public string Result;

    public abstract string Title { get; }

    /// <summary>The helper may drop the job to free itself when it gets stuck (true while just driving there).</summary>
    public virtual bool AllowsSelfRecovery => true;

    /// <summary>Driving somewhere (other cars should make way).</summary>
    public virtual bool Moving => false;

    /// <summary>In the middle of the actual work (don't cancel just because the target looks fine for a moment).</summary>
    public virtual bool Busy => false;

    /// <summary>Where the overlay draws this job's target.</summary>
    public abstract Vector3 TargetPos { get; }

    /// <summary>Called every frame while the job runs; sets the helper's drive command.</summary>
    public abstract void Update(float dt);

    /// <summary>Stop now (target gone, convoy dismissed, helper stuck): undo anything left attached.</summary>
    public virtual void Abort(string why)
    {
        if (Done) return;
        Done = true;
        Failed = true;
        Result = why;
        Cleanup();
    }

    protected virtual void Cleanup() { }

    public float InPhase => Time.time - PhaseAt;

    protected void SetPhase(string p)
    {
        if (Phase == p) return;
        Phase = p;
        PhaseAt = Time.time;
    }

    protected void Finish(bool ok, string result)
    {
        if (Done) return;
        Done = true;
        Failed = !ok;
        Result = result;
        Cleanup();
    }

    public string Status => $"{Title}: {Phase}";
}
