using System;
using System.Collections.Generic;
using OverTheHillPlus.Cars;
using OverTheHillPlus.Core.Shared;
using OverTheHillPlus.Shared;
using UnityEngine;
#if MELON
using Il2CppVehiclePhysics;
#else
using VehiclePhysics;
#endif

namespace OverTheHillPlus.Jackal;

/// <summary>
/// The Jackal at runtime: every frame each Jackal in the world gets its running gear moved (<see cref="JackalLive"/>, every peer);
/// every physics step, where this game simulates the car (the host and solo: every Jackal; a guest: its own, which it predicts),
/// each wheel gets its tyre's grip for the ground under it (<see cref="JackalTyres"/>, through core's <see cref="GroundGrip"/>:
/// Terrain+'s factor multiplied in, the one writer of the wheel while Tyre Pressure doesn't write it), the rail's flotation goes
/// to Terrain+, and the paddles give back part of the game's own soft-ground drag.
/// </summary>
internal static class JackalCore
{
    private const string Owner = "jackal";

    private sealed class StepState
    {
        public bool[] Wrote = new bool[GroundGrip.MaxWheels];
        public float Base = 1f, BaseAt = float.NegativeInfinity;
    }

    private static readonly Dictionary<IntPtr, JackalLive> Live = new();
    private static readonly Dictionary<IntPtr, StepState> Steps = new();
    private static readonly HashSet<IntPtr> Seen = new();
    private static readonly List<IntPtr> Gone = new();
    private static Vector3 _offset;
    private static bool _offsetKnown;
    private static float _nextRadiusCheck;

    /// <summary>Every frame (core's tick).</summary>
    public static void Tick()
    {
        if (JackalRig.Geometry == null) return;
        var car = ModdedCars.ById(JackalDefinition.Id);
        if (car == null || !car.Ready) return;
        if (!_offsetKnown && car.Layout != null) { _offset = car.Layout.Offset; _offsetKnown = true; }
        if (!_offsetKnown) return;
        Seen.Clear();
        float clock = Time.unscaledTime;
        bool checkRadius = clock >= _nextRadiusCheck || clock < _nextRadiusCheck - 3f;
        if (checkRadius) _nextRadiusCheck = clock + 1f;
        bool authority = GameState.IsAuthority;
        foreach (var fp in GameState.FusionPlayers)
        {
            VehicleBase v;
            try
            {
                if (fp == null || fp.Object == null) continue;
                v = fp.vehicleBase;
                if (v == null || !v.isActiveAndEnabled) continue;
                if (VehicleTraits.ModdedId(v) != JackalDefinition.Id) continue;
                if (checkRadius && (authority || fp.Object.HasInputAuthority)) KeepRearTyres(v);
            }
            catch (Exception e) { DebugLog.Error(JackalPlugin.ModId, e, "JackalCore.Tick player"); continue; }
            var ptr = v.Pointer;
            if (!Live.TryGetValue(ptr, out var live))
            {
                live = new JackalLive(v, _offset);
                Live[ptr] = live;
            }
            Seen.Add(ptr);
            try { live.Frame(); }
            catch (Exception e) { DebugLog.Error(JackalPlugin.ModId, e, "JackalLive.Frame"); JackalPlugin.LogOnce("frame", "the running gear failed: " + e.Message); }
        }
        Gone.Clear();
        foreach (var kv in Live) if (!Seen.Contains(kv.Key)) Gone.Add(kv.Key);
        foreach (var k in Gone)
        {
            Live.Remove(k);
            Steps.Remove(k);
        }
    }

    /// <summary>The rear wheel colliders keep the paddles' size against the front's, where this game simulates the car: game
    /// code that hands every wheel the CarInfo's one radius would drop the rear of the car by the difference. Other mods' changes
    /// keep the ratio (Tyre Pressure works on each wheel's own radius), so nothing here fights them.</summary>
    private static void KeepRearTyres(VehicleBase v)
    {
        var states = v.wheelState;
        if (states == null || states.Length < 4) return;
        var front = states[0]?.wheelCol;
        if (front == null || !(front.tireRadius > 0f)) return;
        float f = front.tireRadius;
        for (int i = 2; i < 4; i++)
        {
            var c = states[i]?.wheelCol;
            if (c == null || !JackalTyres.RearLost(f, c.tireRadius)) continue;
            c.SetWheelRadius(JackalTyres.RearFor(f));
            c.RefreshPhysics();
            JackalPlugin.LogOnce("radius", "a rear wheel lost the paddles' size: put back");
        }
    }

    /// <summary>The car's physics step (from the patch on DoUpdateBlocks: every simulated car of every kind).</summary>
    public static void PhysicsStep(VPVehicleController v)
    {
        if (v == null || JackalRig.Geometry == null) return;
        if (VehicleTraits.ModdedId(v) != JackalDefinition.Id) return;
        // where this game simulates the car: the host / solo every rail, a guest only its own (it predicts it)
        if (!GameState.IsAuthority && !GameState.IsLocalCar(v)) return;
        var ptr = v.Pointer;
        if (!Steps.TryGetValue(ptr, out var st)) Steps[ptr] = st = new StepState();
        var rb = v.cachedRigidbody;
        var states = v.wheelState;
        if (rb == null || states == null) return;
        float now = Time.fixedTime;
        float baseGrip = GripBase(v, st);
        int n = Math.Min(Math.Min(v.wheelCount, states.Length), GroundGrip.MaxWheels);
        float softDrag = 0f;
        int grounded = 0;
        for (int i = 0; i < n; i++)
        {
            var ws = states[i];
            if (ws == null) continue;
            var ground = JackalGround.Hard;
            float gameDrag = 0f;
            if (ws.grounded)
            {
                grounded++;
                ground = Classify(ws, out gameDrag);
                if (ground == JackalGround.Sand || ground == JackalGround.Snow) softDrag += gameDrag;
            }
            if (GroundGrip.ClaimedByOther(ptr, i, now, Owner))
            {
                st.Wrote[i] = false;
                continue;
            }
            float grip = JackalTyres.Grip(i >= 2, ground) * GroundGrip.Factor(ptr, i, now);
            try { v.SetWheelTireFrictionMultiplier(i, baseGrip * grip); }
            catch (Exception e) { DebugLog.Error(JackalPlugin.ModId, e, "JackalCore.grip"); continue; }
            GroundGrip.Claim(ptr, i, now, Owner);
            st.Wrote[i] = true;
        }
        GroundGrip.SetFlotation(ptr, JackalTyres.Flotation, now);
        if (rb.isKinematic || grounded == 0) return;
        var vel = rb.linearVelocity;
        float speed = vel.magnitude;
        float throttle = 0f;
        try { throttle = Mathf.Clamp01(v.data.Get(Channel.Input, InputData.Throttle) / 10000f); }
        catch (Exception e) { DebugLog.Error(JackalPlugin.ModId, e, "JackalCore.throttle"); }
        float push = JackalTyres.SoftPush(softDrag / Math.Max(1, n), throttle, speed);
        if (push > 0f)
        {
            // along the way it is going: forward or in reverse
            var t = v.cachedTransform != null ? v.cachedTransform : v.transform;
            var way = Vector3.Dot(vel, t.forward) >= 0f ? t.forward : -t.forward;
            rb.AddForce(way * push, ForceMode.Acceleration);
        }
    }

    private static JackalGround Classify(VehicleBase.WheelState ws, out float drag)
    {
        drag = 0f;
        MaterialLabel label;
        try
        {
            var cfg = ws.groundMaterial?.Config;
            if (cfg == null) return JackalGround.Hard;
            label = cfg.Label;
            drag = cfg.Drag;
        }
        catch (Exception e) { DebugLog.Error(JackalPlugin.ModId, e, "JackalCore.Classify"); return JackalGround.Hard; }
        return label switch
        {
            MaterialLabel.Mud or MaterialLabel.DeepMud or MaterialLabel.Water => JackalGround.Mud,
            MaterialLabel.Snow or MaterialLabel.DeepSnow or MaterialLabel.ShallowSnow => JackalGround.Snow,
            MaterialLabel.Sand or MaterialLabel.DeepSand or MaterialLabel.QuickSand => JackalGround.Sand,
            MaterialLabel.Rock or MaterialLabel.LooseRock => JackalGround.Rock,
            MaterialLabel.Ice => JackalGround.Ice,
            MaterialLabel.Dirt or MaterialLabel.Gravel or MaterialLabel.Grass => JackalGround.Trail,
            _ => JackalGround.Hard,
        };
    }

    /// <summary>The game's own multiplier (the optional VPTireFrictionMultiplier component, else 1), every 2 s.</summary>
    private static float GripBase(VPVehicleController v, StepState st)
    {
        float now = Time.unscaledTime;
        if (now - st.BaseAt < 2f && now >= st.BaseAt) return st.Base;
        st.BaseAt = now;
        st.Base = 1f;
        try
        {
            var m = v.GetComponent<VPTireFrictionMultiplier>();
            if (m != null && m.enabled && m.frictionMultiplier > 0f) st.Base = m.frictionMultiplier;
        }
        catch (Exception e) { DebugLog.Error(JackalPlugin.ModId, e, "JackalCore.GripBase"); }
        return st.Base;
    }
}
