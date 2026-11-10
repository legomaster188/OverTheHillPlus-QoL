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

namespace OverTheHillPlus.Ermine;

/// <summary>
/// The Ermine at runtime: every frame each Ermine in the world gets its skis, suspension, handlebars and track moved
/// (<see cref="ErmineLive"/>, every peer); every physics step, where this game simulates a sled (the host and solo: every
/// sled; a guest: its own, which it predicts), its hidden wheels get a sled's grip for the ground under them
/// (<see cref="SledGround"/>, through core's <see cref="GroundGrip"/> like Tyre Pressure: one writer per wheel, Terrain+'s
/// factor multiplied in, its flotation published), the drag of skis scraping bare ground, a little push on deep snow where
/// the game's tyre-made snow drag would bog it, and the rider's lean against a tip-over in a hard turn.
/// </summary>
internal static class ErmineCore
{
    private static readonly Dictionary<IntPtr, ErmineLive> Live = new();
    private static readonly HashSet<IntPtr> Seen = new();
    private static readonly List<IntPtr> Gone = new();
    private static Vector3 _offset;
    private static bool _offsetKnown;

    /// <summary>Every frame (core's tick).</summary>
    public static void Tick()
    {
        if (ErmineRig.Geometry == null) return;
        var car = ModdedCars.ById(ErmineDefinition.Id);
        if (car == null || !car.Ready) return;
        if (!_offsetKnown && car.Layout != null) { _offset = car.Layout.Offset; _offsetKnown = true; }
        if (!_offsetKnown) return;
        float dt = Time.deltaTime;
        bool authority = GameState.IsAuthority;
        Seen.Clear();
        foreach (var fp in GameState.FusionPlayers)
        {
            VehicleBase v;
            bool mine;
            try
            {
                if (fp == null || fp.Object == null) continue;
                v = fp.vehicleBase;
                if (v == null || !v.isActiveAndEnabled) continue;
                if (VehicleTraits.ModdedId(v) != ErmineDefinition.Id) continue;
                mine = fp.Object.HasInputAuthority;
            }
            catch (Exception e) { DebugLog.Error(ErminePlugin.ModId, e, "ErmineCore.Tick player"); continue; }
            var ptr = v.Pointer;
            if (!Live.TryGetValue(ptr, out var live))
            {
                live = new ErmineLive(v, _offset);
                Live[ptr] = live;
            }
            Seen.Add(ptr);
            float steer = float.NaN;
            if (mine)
            {
                try
                {
                    var vp = v.TryCast<VPVehicleController>();
                    if (vp != null) steer = Mathf.Clamp(vp.data.Get(Channel.Input, InputData.Steer) / 10000f, -1f, 1f);
                }
                catch (Exception e) { DebugLog.Error(ErminePlugin.ModId, e, "ErmineCore.Tick steer"); }
            }
            try { live.Frame(dt, authority || mine, steer); }
            catch (Exception e) { DebugLog.Error(ErminePlugin.ModId, e, "ErmineLive.Frame"); ErminePlugin.LogOnce("frame", "a sled's skis and track failed: " + e.Message); }
        }
        Gone.Clear();
        foreach (var kv in Live) if (!Seen.Contains(kv.Key)) Gone.Add(kv.Key);
        foreach (var k in Gone)
        {
            Live.Remove(k);
            Steps.Remove(k);
        }
    }

    public static void Clear()
    {
        Live.Clear();
        Steps.Clear();
    }

    // ---------------------------------------------------------------- physics

    private sealed class StepState
    {
        public bool Wrote;
        public float Base = 1f, BaseAt = float.NegativeInfinity;
    }

    private static readonly Dictionary<IntPtr, StepState> Steps = new();

    /// <summary>The sled's physics step (from the patch on DoUpdateBlocks: every simulated car of every kind).</summary>
    public static void PhysicsStep(VPVehicleController v)
    {
        if (ErmineRig.Geometry == null || v == null) return;
        if (VehicleTraits.ModdedId(v) != ErmineDefinition.Id) return;
        // where this game simulates the sled: the host / solo every sled, a guest only its own (it predicts it)
        if (!GameState.IsAuthority && !GameState.IsLocalCar(v)) return;
        var ptr = v.Pointer;
        if (!Steps.TryGetValue(ptr, out var st)) Steps[ptr] = st = new StepState();
        var rb = v.cachedRigidbody;
        var states = v.wheelState;
        if (rb == null || states == null) return;
        float now = Time.fixedTime, dt = Time.fixedDeltaTime;
        var vel = rb.linearVelocity;
        float speed = vel.magnitude;
        float baseGrip = GripBase(v, st);
        int n = Math.Min(Math.Min(v.wheelCount, states.Length), GroundGrip.MaxWheels);
        float drag = 0f, floats = 0f, softDrag = 0f;
        int grounded = 0;
        for (int i = 0; i < n; i++)
        {
            var ws = states[i];
            if (ws == null) continue;
            int label = 0;
            float gameDrag = 0f;
            if (ws.grounded)
            {
                grounded++;
                try
                {
                    var cfg = ws.groundMaterial?.Config;
                    if (cfg != null) { label = (int)cfg.Label; gameDrag = cfg.Drag; }
                }
                catch (Exception e) { DebugLog.Error(ErminePlugin.ModId, e, "ErmineCore.ground"); }
            }
            var surface = SledGround.Of(label);
            var feel = ws.grounded ? SledGround.Feel(surface, speed) : new SledFeel(1f, 0f, 0f);
            float factor = GroundGrip.Factor(ptr, i, now);
            try { v.SetWheelTireFrictionMultiplier(i, SledGround.Write(baseGrip, feel, factor)); }
            catch (Exception e) { DebugLog.Error(ErminePlugin.ModId, e, "ErmineCore.grip"); }
            GroundGrip.Claim(ptr, i, now);
            if (ws.grounded)
            {
                drag += feel.Drag;
                floats = Math.Max(floats, feel.Flotation);
                if (surface == SledSurface.Snow) softDrag += gameDrag;
            }
        }
        st.Wrote = true;
        GroundGrip.SetFlotation(ptr, floats, now);
        if (rb.isKinematic) return;
        var t = v.cachedTransform != null ? v.cachedTransform : v.transform;
        if (grounded > 0 && speed > 0.05f)
        {
            // bare ground: skis and lugs scraping (a share of g per grounded wheel's share of the weight)
            float decel = SledGround.DragDecel(drag / Math.Max(1, n), speed, dt);
            if (decel > 0f) rb.AddForce(-vel / speed * decel, ForceMode.Acceleration);
            // deep snow: the game drags a tyre down in it (its ground material's drag); a track on top of it pushes on,
            // so while the throttle is open most of that drag is given back
            float throttle = 0f;
            try { throttle = Mathf.Clamp01(v.data.Get(Channel.Input, InputData.Throttle) / 10000f); }
            catch (Exception e) { DebugLog.Error(ErminePlugin.ModId, e, "ErmineCore.throttle"); }
            float assist = SnowAssist(softDrag / Math.Max(1, n), throttle, speed);
            if (assist > 0f) rb.AddForce(t.forward * assist, ForceMode.Acceleration);
        }
        // the rider's lean: a tip toward the outside of a hard turn is held back
        if (grounded >= 2)
        {
            var fwd = t.forward;
            float roll = Vector3.SignedAngle(Vector3.ProjectOnPlane(Vector3.up, fwd), Vector3.ProjectOnPlane(t.up, fwd), fwd);
            float rate = Vector3.Dot(rb.angularVelocity, fwd) * Mathf.Rad2Deg;
            float lean = SkiMath.LeanTorque(roll, rate);
            if (lean != 0f) rb.AddTorque(fwd * lean, ForceMode.Acceleration);
        }
    }

    /// <summary>The push (m/s²) the track gives back on deep snow: most of the game's soft-ground drag (its material's drag
    /// fraction of g), with the throttle open and moving, never over 1.6 m/s².</summary>
    public static float SnowAssist(float gameDragG, float throttle, float speed)
    {
        if (!(gameDragG > 0f) || !(throttle > 0.05f) || speed < 0.3f) return 0f;
        return Mathf.Min(1.6f, 9.81f * gameDragG * 0.7f * throttle);
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
        catch (Exception e) { DebugLog.Error(ErminePlugin.ModId, e, "ErmineCore.GripBase"); }
        return st.Base;
    }
}
