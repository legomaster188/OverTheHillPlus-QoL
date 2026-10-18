using System;
using Fusion;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace OverTheHillPlus.Features.Cheats.Convoy;

/// <summary>
/// The AI's use of the game's own winch (VehicleWinchSystem), host side. Facts from the decompiled game:
/// - The RPC_Attach* calls are made by the car's driver; an AI car has none, so the host calls the methods those RPCs
///   run on the state authority: AttachToPlayerCarAnchorPoint(local, PlayerRef, targetLocal) for a player's car
///   (it looks the car up with Runner.TryGetPlayerObject) and AttachToMovableObjectAnchorPoint(local, NetworkObject,
///   targetLocal) for anything else with a network rigidbody: another AI car (its own winch points are used) or a
///   meteorite. Both set the networked rope state, so every player sees the rope.
/// - The rope is a spring constraint on RopeLength; a driver reels in by holding a button, which the winch turns into
///   RopeLength -= winchReelSpeed * dt (clamped to minRopeLength..MaxRopeLength). The AI does the same by writing
///   RopeLength (a networked float) on the host.
/// - DetachOwnWinch() releases this car's rope (state authority only), the same call the release button ends in.
/// - Meteorites count every winch attached to them; one is extracted when enough ropes pull taut in roughly the same
///   direction (RequiredNumberOfPlayersToExtract, maxPullSpreadAngle) for meteoriteExtractionTime.
/// </summary>
internal static class ConvoyWinch
{
    public static bool HasWinch(ConvoyAgent a)
    {
        try { return a.Winch != null && a.Winch.winchPoints != null && a.Winch.winchPoints.Length > 0; }
        catch (Exception ex) { DebugLog.Error("convoy", ex, "ConvoyWinch.HasWinch"); return false; }
    }

    public static bool Attached(ConvoyAgent a)
    {
        try { return a.Winch != null && a.Winch.WinchIsAttached; }
        catch (Exception ex) { DebugLog.Error("convoy", ex, "ConvoyWinch.Attached"); return false; }
    }

    public static bool Taut(ConvoyAgent a)
    {
        try { return a.Winch != null && a.Winch.IsTaut; }
        catch (Exception ex) { DebugLog.Error("convoy", ex, "ConvoyWinch.Taut"); return false; }
    }

    public static float RopeLength(ConvoyAgent a)
    {
        try { return a.Winch != null ? a.Winch.RopeLength : 0f; }
        catch (Exception ex) { DebugLog.Error("convoy", ex, "ConvoyWinch.RopeLength"); return 0f; }
    }

    public static float Range(ConvoyAgent a)
    {
        try
        {
            float r = a.Winch != null ? a.Winch.MaxWinchRange : 0f;
            return r > 2f ? r : 18f;
        }
        catch (Exception ex) { DebugLog.Error("convoy", ex, "ConvoyWinch.Range"); return 18f; }
    }

    /// <summary>The car's winch point nearest to a world position (null if none).</summary>
    public static Transform ClosestPoint(Il2CppReferenceArray<Transform> points, Vector3 towards)
    {
        Transform best = null;
        float bestD = float.MaxValue;
        if (points == null) return null;
        for (int i = 0; i < points.Length; i++)
        {
            var p = points[i];
            if (p == null) continue;
            float d = (p.position - towards).sqrMagnitude;
            if (d < bestD) { bestD = d; best = p; }
        }
        return best;
    }

    /// <summary>Local position (in the helper's car frame) of the helper's winch point nearest to the target.</summary>
    public static bool MyPoint(ConvoyAgent a, Vector3 towards, out Vector3 local, out Vector3 world)
    {
        local = world = default;
        try
        {
            var p = ClosestPoint(a.Winch.winchPoints, towards);
            if (p == null) return false;
            world = p.position;
            local = a.T.InverseTransformPoint(world);
            return true;
        }
        catch (Exception ex) { DebugLog.Error("convoy", ex, "ConvoyWinch.MyPoint"); return false; }
    }

    /// <summary>Hooks the AI's winch onto a car (player or AI). False + reason when it couldn't.</summary>
    public static bool AttachToCar(ConvoyAgent a, Incident target, out string error)
    {
        error = null;
        if (!HasWinch(a)) { error = "no winch on this car"; return false; }
        try
        {
            var targetT = target.T;
            if (targetT == null) { error = "target car is gone"; return false; }
            if (!MyPoint(a, targetT.position, out var myLocal, out var myWorld)) { error = "no winch point"; return false; }

            // Their attach point nearest to us (the game picks the closest of their winch points to this local spot).
            Vector3 theirLocal = Vector3.zero;
            var tw = target.TargetWinch;
            if (tw != null)
            {
                var tp = ClosestPoint(tw.winchPoints, myWorld);
                if (tp != null) theirLocal = targetT.InverseTransformPoint(tp.position);
            }

            if (Attached(a)) a.Winch.DetachOwnWinch();
            AdvantageFlag.MarkUsed();
            if (target.IsPlayer && PlayerObjectKnown(target.Player.Ref))
            {
                a.Winch.AttachToPlayerCarAnchorPoint(myLocal, target.Player.Ref, theirLocal);
            }
            else
            {
                var obj = target.Obj;
                if (obj == null) { error = "target has no network object"; return false; }
                a.Winch.AttachToMovableObjectAnchorPoint(myLocal, obj, theirLocal);
            }
            return true; // the caller checks Attached() over the next ticks
        }
        catch (Exception e)
        {
            DebugLog.Error("convoy", e, "ConvoyWinch.AttachToCar");
            error = e.Message;
            ConvoyAi.LogOnce("attach-car", "winch attach: " + e);
            return false;
        }
    }

    /// <summary>Hooks the AI's winch onto a meteorite's attach point.</summary>
    public static bool AttachToObject(ConvoyAgent a, NetworkObject obj, Vector3 worldPoint, out string error)
    {
        error = null;
        if (!HasWinch(a)) { error = "no winch on this car"; return false; }
        try
        {
            if (obj == null || !obj.IsValid) { error = "object is gone"; return false; }
            if (!MyPoint(a, worldPoint, out var myLocal, out _)) { error = "no winch point"; return false; }
            var objLocal = obj.transform.InverseTransformPoint(worldPoint);
            if (Attached(a)) a.Winch.DetachOwnWinch();
            AdvantageFlag.MarkUsed();
            a.Winch.AttachToMovableObjectAnchorPoint(myLocal, obj, objLocal);
            return true;
        }
        catch (Exception e)
        {
            DebugLog.Error("convoy", e, "ConvoyWinch.AttachToObject");
            error = e.Message;
            ConvoyAi.LogOnce("attach-obj", "winch attach to object: " + e);
            return false;
        }
    }

    /// <summary>Reels in like holding the winch button: shorter rope by the winch's own reel speed.</summary>
    public static void ReelIn(ConvoyAgent a, float dt, float speedScale = 1f)
    {
        try
        {
            var w = a.Winch;
            if (w == null || !w.WinchIsAttached) return;
            float reel = w.winchReelSpeed > 0.05f ? w.winchReelSpeed : 1.5f;
            float min = Mathf.Max(0.5f, w.minRopeLength);
            float len = w.RopeLength;
            float next = Mathf.Max(min, len - reel * speedScale * dt);
            if (next < len - 0.0001f) w.RopeLength = next;
        }
        catch (Exception e) { DebugLog.Error("convoy", e, "ConvoyWinch.ReelIn"); ConvoyAi.LogOnce("reel", "reeling in: " + e.Message); }
    }

    /// <summary>Gives the rope some slack (e.g. while repositioning).</summary>
    public static void PayOut(ConvoyAgent a, float dt, float speedScale = 1f)
    {
        try
        {
            var w = a.Winch;
            if (w == null || !w.WinchIsAttached) return;
            float reel = w.winchReelSpeed > 0.05f ? w.winchReelSpeed : 1.5f;
            float max = w.MaxRopeLength;
            w.RopeLength = Mathf.Min(max, w.RopeLength + reel * speedScale * dt);
        }
        catch (Exception e) { DebugLog.Error("convoy", e, "ConvoyWinch.PayOut"); ConvoyAi.LogOnce("payout", "paying out: " + e.Message); }
    }

    public static void Detach(ConvoyAgent a)
    {
        try
        {
            if (a.Winch != null && a.Winch.WinchIsAttached) a.Winch.DetachOwnWinch();
        }
        catch (Exception e) { DebugLog.Error("convoy", e, "ConvoyWinch.Detach"); ConvoyAi.LogOnce("detach", "detaching a winch: " + e.Message); }
    }

    private static bool PlayerObjectKnown(PlayerRef p)
    {
        try
        {
            var r = GameState.Runner;
            return r != null && r.TryGetPlayerObject(p, out var o) && o != null;
        }
        catch (Exception ex) { DebugLog.Error("convoy", ex, "ConvoyWinch.PlayerObjectKnown"); return false; }
    }
}
