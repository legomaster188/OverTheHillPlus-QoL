using System;
using System.Collections.Generic;
using Fusion;
using Il2CppInterop.Runtime;
using UnityEngine;
using Object = UnityEngine.Object;

namespace OverTheHillPlus.Features.Cheats.Convoy;

/// <summary>
/// Reading the game's meteorites (Meteorite / MeteoriteSpawnController, decompiled):
/// status 3 OnGround, 4 Extracting (a winch is on it), 5 ExtractingPulling (enough taut ropes within
/// maxPullSpreadAngle of each other, the extraction timer runs), 6 Extracted (CompleteObjectiveForInvolvedPlayers:
/// ropes come off, players within prizeAwardMaxDistance get the pebbles), then crater / gone.
/// </summary>
internal static class ConvoyMeteorites
{
    private const int OnGround = 3, Extracting = 4, Extracted = 6;
    private static readonly string[] Sizes = { "very small", "small", "medium", "large", "very large", "huge", "very huge" };

    private static readonly List<Meteorite> Cache = new();
    private static float _cacheAt = -100f;

    public static int Status(Meteorite m)
    {
        try { return (int)m.Status; } catch (Exception ex) { DebugLog.Error("convoy", ex, "ConvoyMeteorites.Status"); return -1; }
    }

    public static bool Alive(Meteorite m)
    {
        try { return m != null && m.Object != null && m.Object.IsValid; } catch (Exception ex) { DebugLog.Error("convoy", ex, "ConvoyMeteorites.Alive"); return false; }
    }

    public static bool Extractable(Meteorite m)
    {
        int s = Status(m);
        return s == OnGround || s == Extracting;
    }

    public static bool IsExtracted(Meteorite m) => Status(m) >= Extracted;

    public static int Required(Meteorite m)
    {
        try { return Mathf.Max(1, m.meteoriteConfig != null ? m.meteoriteConfig.RequiredNumberOfPlayersToExtract : 1); }
        catch (Exception ex) { DebugLog.Error("convoy", ex, "ConvoyMeteorites.Required"); return 1; }
    }

    public static int WinchedCount(Meteorite m)
    {
        try { return m.winchedPlayers != null ? m.winchedPlayers.Count : 0; } catch (Exception ex) { DebugLog.Error("convoy", ex, "ConvoyMeteorites.WinchedCount"); return 0; }
    }

    public static float SpreadAngle(Meteorite m)
    {
        try { return m.maxPullSpreadAngle > 1f ? m.maxPullSpreadAngle : 30f; } catch (Exception ex) { DebugLog.Error("convoy", ex, "ConvoyMeteorites.SpreadAngle"); return 30f; }
    }

    public static float PrizeDistance(Meteorite m)
    {
        try { return m.prizeAwardMaxDistance > 1f ? m.prizeAwardMaxDistance : 50f; } catch (Exception ex) { DebugLog.Error("convoy", ex, "ConvoyMeteorites.PrizeDistance"); return 50f; }
    }

    public static string SizeName(Meteorite m)
    {
        try
        {
            int s = (int)m.MeteoriteSize;
            return s >= 0 && s < Sizes.Length ? Sizes[s] : "";
        }
        catch (Exception ex) { DebugLog.Error("convoy", ex, "ConvoyMeteorites.SizeName"); return ""; }
    }

    public static Vector3 AttachPoint(Meteorite m)
    {
        try
        {
            if (m.attachPoint != null) return m.attachPoint.transform.position;
        }
        catch (Exception ex) { DebugLog.Error("convoy", ex, "ConvoyMeteorites.AttachPoint"); }
        return m.transform.position;
    }

    /// <summary>All spawned meteorites (the spawn controller's list, else a scene scan), cached for 2 s.</summary>
    public static List<Meteorite> All()
    {
        if (Time.unscaledTime - _cacheAt < 2f) return Cache;
        _cacheAt = Time.unscaledTime;
        Cache.Clear();
        try
        {
            var list = MeteoriteSpawnController.Instance?.spawnedMeteorites;
            if (list != null)
            {
                for (int i = 0; i < list.Count; i++)
                {
                    var m = list[i];
                    if (m != null) Cache.Add(m);
                }
                return Cache;
            }
        }
        catch (Exception e) { DebugLog.Error("convoy", e, "ConvoyMeteorites.All"); ConvoyAi.LogOnce("meteor-list", "meteorite list: " + e.Message); }
        try
        {
            foreach (var o in Object.FindObjectsOfType(Il2CppType.Of<Meteorite>()))
            {
                var m = o.TryCast<Meteorite>();
                if (m != null) Cache.Add(m);
            }
        }
        catch (Exception e) { DebugLog.Error("convoy", e, "ConvoyMeteorites.All"); ConvoyAi.LogOnce("meteor-scan", "meteorite scan: " + e.Message); }
        return Cache;
    }

    /// <summary>The nearest meteorite still in the ground within <paramref name="radius"/> (skipping done ones).</summary>
    public static Meteorite Nearest(Vector3 from, float radius, HashSet<IntPtr> skip)
    {
        Meteorite best = null;
        float bestD = radius;
        foreach (var m in All())
        {
            try
            {
                if (!Alive(m) || !Extractable(m)) continue;
                if (skip != null && skip.Contains(m.Pointer)) continue;
                float d = ConvoyTrail.Flat(m.transform.position - from).magnitude;
                if (d < bestD) { bestD = d; best = m; }
            }
            catch (Exception ex) { DebugLog.Error("convoy", ex, "ConvoyMeteorites.Nearest"); }
        }
        return best;
    }
}

/// <summary>
/// A team of AI cars pulling one meteorite out: picks a pull direction towards the leader on drivable dry ground,
/// gives each car a slot side by side (so their ropes stay within the game's spread angle), waits until every rope
/// is on and taut, then they all back away together until the game reports it extracted.
/// </summary>
internal sealed class FetchTask
{
    public readonly Meteorite M;
    public readonly IntPtr MeteoritePtr;
    public readonly NetworkObject Obj;
    public readonly Vector3 MPos, AttachWorld;
    public readonly List<ConvoyAgent> Team;
    public readonly List<FetchJob> Jobs = new();
    public readonly int Required;
    public Vector3 PullDir;
    public readonly List<Vector3> Slots = new();
    public readonly float StartedAt = Time.time;
    public bool Done, Failed;
    public string Result;
    public bool Pulling;
    public float PullingSince;
    public string Phase = "driving to it";

    public FetchTask(Meteorite m, List<ConvoyAgent> team, int required)
    {
        M = m;
        Team = team;
        Required = required;
        try
        {
            MeteoritePtr = m.Pointer;
            Obj = m.Object;
            MPos = m.transform.position;
            AttachWorld = ConvoyMeteorites.AttachPoint(m);
        }
        catch (Exception e) { DebugLog.Error("convoy", e, "FetchTask..ctor"); Finish(false, "Couldn't read the meteorite: " + e.Message); return; }
        if (Obj == null) { Finish(false, "That meteorite isn't networked."); return; }
        PlanSlots();
    }

    public float DistanceToLeader()
    {
        var l = ConvoyAi.Leader;
        return l != null ? ConvoyTrail.Flat(l.Pos - MPos).magnitude : 0f;
    }

    private void PlanSlots()
    {
        float range = 18f;
        foreach (var a in Team) range = Mathf.Min(range, ConvoyWinch.Range(a));
        float dist = Mathf.Clamp(range * 0.6f, 8f, 14f);

        // Pull towards the leader if that side is drivable, else the nearest side that is.
        var leader = ConvoyAi.Leader;
        var baseDir = leader != null ? ConvoyTrail.Flat(leader.Pos - MPos) : Vector3.forward;
        if (baseDir.sqrMagnitude < 0.01f) baseDir = Vector3.forward;
        baseDir.Normalize();
        PullDir = baseDir;
        foreach (float ang in new[] { 0f, 30f, -30f, 60f, -60f, 90f, -90f, 135f, -135f, 180f })
        {
            var d = Quaternion.Euler(0f, ang, 0f) * baseDir;
            var p = MPos + d * dist;
            if (!ConvoySense.Ground(p, MPos.y, 5f, out float gy, out var n)) continue;
            if (ConvoySense.WaterDepthOverGround(p, gy) > 0.35f) continue;
            if (Vector3.Angle(n, Vector3.up) > 24f) continue;
            if (Mathf.Abs(gy - MPos.y) > dist * 0.45f) continue;
            PullDir = d;
            break;
        }
        var right = new Vector3(PullDir.z, 0f, -PullDir.x);
        float yaw = Quaternion.LookRotation(PullDir, Vector3.up).eulerAngles.y;
        var ctx = new SafeSpot.Context();
        foreach (var a in Team) ctx.Ignore(a.Car);
        int count = Team.Count;
        for (int i = 0; i < count; i++)
        {
            float lateral = (i - (count - 1) * 0.5f) * 3.4f;
            var want = MPos + PullDir * dist + right * lateral;
            var res = SafeSpot.Find(want, yaw, ctx);
            var spot = res.Ok && res.Offset < 6f ? res.Ground : want;
            ctx.Occupied.Add(spot);
            Slots.Add(spot);
        }
        // Nearest car takes the nearest slot.
        Team.Sort((a, b) => a.DistanceTo(Slots[0]).CompareTo(b.DistanceTo(Slots[0])));
    }

    public Vector3 SlotFor(ConvoyAgent a)
    {
        int i = Team.IndexOf(a);
        return i >= 0 && i < Slots.Count ? Slots[i] : MPos + PullDir * 10f;
    }

    public void Update()
    {
        if (Done) return;
        if (!ConvoyMeteorites.Alive(M)) { Finish(false, "The meteorite is gone."); return; }
        if (ConvoyMeteorites.IsExtracted(M))
        {
            Finish(true, $"Meteorite pulled out! Players within {ConvoyMeteorites.PrizeDistance(M):0} m of it get the pebbles.");
            return;
        }
        int live = 0, attached = 0, ready = 0;
        foreach (var j in Jobs)
        {
            if (j.Done) continue;
            live++;
            if (j.Attached) attached++;
            if (j.ReadyToPull) ready++;
        }
        if (live == 0) { Finish(false, "No AI car left on the meteorite job."); return; }
        int players = Mathf.Max(0, ConvoyMeteorites.WinchedCount(M) - attached);
        if (!Pulling)
        {
            Phase = attached == 0 ? "driving to it" : $"hooking on ({attached}/{live})";
            if (ready == live && attached + players >= Required)
            {
                Pulling = true;
                PullingSince = Time.time;
                Phase = "pulling";
                ConvoyAi.Event($"All ropes on the meteorite: pulling ({attached} AI + {players} player rope(s)).");
            }
            else if (live < Required - players && Time.time - StartedAt > 20f)
            {
                Finish(false, $"Not enough cars for this meteorite (needs {Required}).");
                return;
            }
        }
        else Phase = ConvoyMeteorites.Status(M) == 5 ? "pulling: it's coming out" : "pulling";
        if (Time.time - StartedAt > 200f) Finish(false, "Gave up on the meteorite (took too long).");
    }

    public void Abort(string why) => Finish(false, why);

    private void Finish(bool ok, string result)
    {
        if (Done) return;
        Done = true;
        Failed = !ok;
        Result = result;
        foreach (var j in Jobs) j.EndFromTask(ok, result);
    }
}

/// <summary>One AI car's part of a meteorite pull: drive to its slot, hook on, reel taut, then pull with the team.</summary>
internal sealed class FetchJob : ConvoyJob
{
    public readonly FetchTask Task;
    public bool ReadyToPull;
    private bool _attachTried;
    private float _attachAt;
    private int _attachTries;
    private int _pullDir = -1;

    public FetchJob(FetchTask task, ConvoyAgent helper)
    {
        Task = task;
        Helper = helper;
        Phase = "drive";
    }

    public override string Title => "pull the meteorite";
    public override Vector3 TargetPos => Task.MPos;
    public override bool Moving => Phase == "drive";
    public override bool Busy => Phase != "drive";
    public override bool AllowsSelfRecovery => Phase == "drive";
    public bool Attached => ConvoyWinch.Attached(Helper);

    public void EndFromTask(bool ok, string result) => Finish(ok, result);

    public override void Update(float dt)
    {
        var a = Helper;
        if (Task.Done) { Finish(!Task.Failed, Task.Result); return; }
        float range = ConvoyWinch.Range(a);
        float toAnchor = a.DistanceTo(Task.AttachWorld);
        switch (Phase)
        {
            case "drive":
            {
                var slot = Task.SlotFor(a);
                bool there = ConvoyDriver.DriveTo(a, slot, 2.5f, 10f, IntPtr.Zero, true, dt);
                a.Activity = $"meteorite: driving to its spot ({a.DistanceTo(slot):0} m)";
                if (there || (InPhase > 45f && toAnchor < range * 0.85f)) SetPhase("attach");
                else if (InPhase > 120f) Finish(false, "couldn't reach the meteorite");
                break;
            }
            case "attach":
            {
                a.Cmd = DriveCmd.Hold();
                a.TargetSpeed = 0f;
                a.Activity = "meteorite: hooking the winch on";
                if (!_attachTried)
                {
                    if (toAnchor > range * 0.95f) { SetPhase("drive"); break; }
                    _attachTried = true;
                    _attachAt = Time.time;
                    _attachTries++;
                    if (!ConvoyWinch.AttachToObject(a, Task.Obj, Task.AttachWorld, out string err)) { Finish(false, "winch: " + err); break; }
                    ConvoyWinch.MyPoint(a, Task.AttachWorld, out var local, out _);
                    _pullDir = local.z >= 0f ? -1 : 1;
                }
                else if (Attached) SetPhase("reel");
                else if (Time.time - _attachAt > 3f)
                {
                    if (_attachTries >= 2) Finish(false, "the winch wouldn't hook onto the meteorite");
                    else _attachTried = false;
                }
                break;
            }
            case "reel":
            {
                var hold = DriveCmd.Hold();
                hold.LowRange = hold.DiffLock = true;
                a.Cmd = hold;
                a.TargetSpeed = 0f;
                if (!Attached) { _attachTried = false; SetPhase("attach"); break; }
                if (!ConvoyWinch.Taut(a)) ConvoyWinch.ReelIn(a, dt, 0.8f);
                ReadyToPull = ConvoyWinch.Taut(a) || InPhase > 8f || ConvoyWinch.RopeLength(a) <= 2.5f;
                a.Activity = ReadyToPull ? "meteorite: rope on, waiting for the team" : "meteorite: reeling in";
                if (Task.Pulling) SetPhase("pull");
                break;
            }
            case "pull":
            {
                if (!Attached)
                {
                    // The game takes the ropes off when it comes out; otherwise hook back on.
                    if (ConvoyMeteorites.IsExtracted(Task.M)) { Finish(true, null); break; }
                    _attachTried = false;
                    SetPhase("attach");
                    break;
                }
                ConvoyWinch.ReelIn(a, dt, 0.35f);
                var cmd = a.Cmd;
                var aim = a.Pos + Task.PullDir * 10f;
                cmd.Steer = ConvoyDriver.SmoothSteer(a, ConvoyDriver.Pursuit(a, aim, _pullDir < 0), dt);
                float target = toAnchor > range * 0.9f ? 0f : 1.1f;
                ConvoyDriver.SpeedControl(a, ref cmd, target, _pullDir, dt);
                cmd.Throttle = Mathf.Min(cmd.Throttle, 0.55f);
                cmd.LowRange = cmd.DiffLock = true;
                a.Cmd = cmd;
                a.Activity = "meteorite: " + Task.Phase;
                break;
            }
        }
    }

    protected override void Cleanup() => ConvoyWinch.Detach(Helper);
}
