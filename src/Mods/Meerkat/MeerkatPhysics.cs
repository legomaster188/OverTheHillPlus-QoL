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

namespace OverTheHillPlus.Meerkat;

/// <summary>
/// The build's physics, where this game simulates the car (the host and solo: every Meerkat; a guest: its own, which it
/// predicts):
/// <list type="bullet">
/// <item>A free-play build (lift, engine stage, parts' weight): this mod's tune layer on the car (core's
///   <see cref="CarTuneLayers"/>), renewed every second. A career's build needs nothing here: the career mod puts the
///   career's upgrades on the car itself.</item>
/// <item>The tyres: road and mud treads change each wheel's grip for the ground under it (through core's
///   <see cref="GroundGrip"/>, Terrain+'s factor multiplied in, the one writer of the wheel while Tyre Pressure doesn't
///   write it); the 4 in lift's bigger tyres and the stock height's smaller ones take the wheel colliders' radius with
///   them.</item>
/// </list>
/// </summary>
internal static class MeerkatPhysics
{
    private const string Owner = "meerkat";

    private sealed class Wheels
    {
        public VehicleBase V;
        public readonly List<WheelColliderBehaviour> Cols = new();
        public MeerkatRadius[] Radius;
        public bool[] Wrote = new bool[GroundGrip.MaxWheels];
        public float Base = 1f, BaseAt = float.NegativeInfinity;
    }

    private static readonly Dictionary<IntPtr, Wheels> ByCar = new();
    private static readonly Dictionary<IntPtr, MeerkatBuild> Simulated = new();
    private static float _next;
    private static bool _hadLayer;

    /// <summary>The cars this game simulates and their builds, every second; the layers and the tyres' radius.</summary>
    internal static void Slow()
    {
        float now = Time.unscaledTime;
        if (now < _next && now >= _next - 3f) return;
        _next = now + 1f;
        Simulated.Clear();
        bool authority = GameState.IsAuthority;
        bool any = false;
        foreach (var fp in GameState.FusionPlayers)
        {
            try
            {
                if (fp == null || fp.Object == null) continue;
                var v = fp.vehicleBase;
                if (v == null || !v.isActiveAndEnabled || VehicleTraits.ModdedId(v) != MeerkatDefinition.Id) continue;
                bool mine = fp.Object.HasInputAuthority;
                if (!authority && !mine) continue;
                var b = MeerkatLive.BuildOf(fp.Object.InputAuthority.RawEncoded, out bool free);
                Simulated[v.Pointer] = b;
                if (free)
                {
                    var e = MeerkatUpgrades.EffectOf(b, MeerkatCareer.Travel);
                    if (!e.IsNone) { CarTuneLayers.Set(Owner, v.Pointer, e); any = true; }
                    else CarTuneLayers.Remove(Owner, v.Pointer);
                }
                else CarTuneLayers.Remove(Owner, v.Pointer);
                Radius(v, b);
            }
            catch (Exception e) { DebugLog.Error(MeerkatPlugin.ModId, e, "MeerkatPhysics.Slow"); MeerkatPlugin.LogOnce("slow", "the build's physics: " + e.Message); }
        }
        if (!any && _hadLayer) CarTuneLayers.RemoveOwner(Owner);
        _hadLayer = any;
        // Only a destroyed car: the entry holds the wheels' stock radius, and a new one would take the radius it finds as
        // stock, so a car that missed a pass (switched off for a moment) would shrink again on the small wheels.
        foreach (var k in MeerkatRadius.Dead(ByCar, w => w.V != null)) ByCar.Remove(k);
    }

    private static Wheels WheelsOf(VehicleBase v)
    {
        if (ByCar.TryGetValue(v.Pointer, out var w)) return w;
        var vp = v.TryCast<VPVehicleController>();
        var states = vp != null ? vp.wheelState : null;
        int n = states == null ? 0 : Math.Min(vp.wheelCount, states.Length);
        if (n == 0) return null;
        w = new Wheels { V = v };
        for (int i = 0; i < n; i++)
        {
            var col = states[i]?.wheelCol;
            if (col == null) return null; // not set up yet: asked again next time
            w.Cols.Add(col);
        }
        w.Radius = new MeerkatRadius[n];
        for (int i = 0; i < n; i++) w.Radius[i] = new MeerkatRadius();
        ByCar[v.Pointer] = w;
        return w;
    }

    /// <summary>The wheels a car shows. Where this game simulates it: the size its colliders carry, not the build's, so the
    /// tyres stand on the ground while another mod holds the radius where it was and before the first write (a car spawns
    /// on the model's radius). A car this game doesn't simulate: the build's. Its hubs stand where the car's simulator put
    /// them (the wheel's visual pose is the suspension's state, the local radius isn't in it).</summary>
    internal static TyreSize ShownSize(VehicleBase v, MeerkatBuild b, bool simulated)
    {
        if (!simulated) return b.Size;
        return ByCar.TryGetValue(v.Pointer, out var w) ? MeerkatRadius.Carried(w.Radius) : TyreSize.Model;
    }

    /// <summary>The tyres' radius on the wheel colliders (<see cref="MeerkatRadius"/>: never while another mod holds any
    /// wheel of the car, a foreign change adopted, a factor written once per change), so it stays bounded next to Tyre
    /// Pressure and the car stands on one size.</summary>
    private static void Radius(VehicleBase v, MeerkatBuild b)
    {
        var w = WheelsOf(v);
        if (w == null) return;
        float factor = b.TyreRadiusFactor();
        var ptr = v.Pointer;
        float now = Time.fixedTime;
        bool held = false;
        for (int i = 0; i < w.Cols.Count && i < GroundGrip.MaxWheels; i++)
            if (GroundGrip.ClaimedByOther(ptr, i, now, Owner)) { held = true; break; }
        for (int i = 0; i < w.Cols.Count; i++)
        {
            var c = w.Cols[i];
            if (c == null) continue;
            float want = w.Radius[i].Next(c.tireRadius, factor, held);
            if (float.IsNaN(want)) continue;
            c.SetWheelRadius(want);
            try { c.RefreshPhysics(); }
            catch (Exception e) { DebugLog.Error(MeerkatPlugin.ModId, e, "MeerkatPhysics.Refresh"); }
        }
    }

    // ---------------------------------------------------------------- the physics step: grip

    internal static void Step(VPVehicleController v)
    {
        if (v == null || Simulated.Count == 0) return;
        if (!Simulated.TryGetValue(v.Pointer, out var b)) return;
        var w = WheelsOf(v);
        if (w == null) return;
        var states = v.wheelState;
        if (states == null) return;
        var ptr = v.Pointer;
        float now = Time.fixedTime;
        int n = Math.Min(Math.Min(v.wheelCount, states.Length), GroundGrip.MaxWheels);
        float baseGrip = GripBase(v, w);
        for (int i = 0; i < n; i++)
        {
            var ws = states[i];
            if (ws == null) continue;
            if (!b.WritesGrip || GroundGrip.ClaimedByOther(ptr, i, now, Owner))
            {
                // handing the wheel back: once, with the ground's factor in, so the next writer and we agree
                if (w.Wrote[i] && !GroundGrip.ClaimedByOther(ptr, i, now, Owner))
                {
                    try { v.SetWheelTireFrictionMultiplier(i, baseGrip * GroundGrip.Factor(ptr, i, now)); }
                    catch (Exception e) { DebugLog.Error(MeerkatPlugin.ModId, e, "MeerkatPhysics.release"); }
                }
                w.Wrote[i] = false;
                continue;
            }
            var g = ws.grounded ? Classify(ws) : Ground.Tarmac;
            float grip = MeerkatBuild.Grip(b.Tyre, g) * GroundGrip.Factor(ptr, i, now);
            try { v.SetWheelTireFrictionMultiplier(i, baseGrip * grip); }
            catch (Exception e) { DebugLog.Error(MeerkatPlugin.ModId, e, "MeerkatPhysics.grip"); continue; }
            GroundGrip.Claim(ptr, i, now, Owner);
            w.Wrote[i] = true;
        }
    }

    /// <summary>The game's own multiplier (the optional VPTireFrictionMultiplier component, else 1), every 2 s.</summary>
    private static float GripBase(VPVehicleController v, Wheels w)
    {
        float now = Time.unscaledTime;
        if (now - w.BaseAt < 2f && now >= w.BaseAt) return w.Base;
        w.BaseAt = now;
        w.Base = 1f;
        try
        {
            var m = v.GetComponent<VPTireFrictionMultiplier>();
            if (m != null && m.enabled && m.frictionMultiplier > 0f) w.Base = m.frictionMultiplier;
        }
        catch (Exception e) { DebugLog.Error(MeerkatPlugin.ModId, e, "MeerkatPhysics.GripBase"); }
        return w.Base;
    }

    /// <summary>What a wheel rolls on, from the game's ground material (the same buckets Tyre Pressure uses).</summary>
    private static Ground Classify(VehicleBase.WheelState ws)
    {
        MaterialLabel label;
        try
        {
            var cfg = ws.groundMaterial?.Config;
            if (cfg == null) return Ground.Tarmac;
            label = cfg.Label;
        }
        catch (Exception e) { DebugLog.Error(MeerkatPlugin.ModId, e, "MeerkatPhysics.Classify"); return Ground.Tarmac; }
        return label switch
        {
            MaterialLabel.Mud or MaterialLabel.DeepMud or MaterialLabel.Water => Ground.Mud,
            MaterialLabel.Snow or MaterialLabel.DeepSnow or MaterialLabel.ShallowSnow => Ground.Snow,
            MaterialLabel.Sand or MaterialLabel.DeepSand or MaterialLabel.QuickSand => Ground.Sand,
            MaterialLabel.Rock or MaterialLabel.LooseRock => Ground.Rock,
            MaterialLabel.Ice => Ground.Ice,
            MaterialLabel.Dirt or MaterialLabel.Gravel or MaterialLabel.Grass => Ground.Trail,
            _ => Ground.Tarmac,
        };
    }
}
