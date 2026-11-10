using System;
using System.Collections.Generic;
using OverTheHillPlus.Cars;
using OverTheHillPlus.Core.Shared;
using UnityEngine;
#if MELON
using Il2CppVehiclePhysics;
#else
using VehiclePhysics;
#endif

namespace OverTheHillPlus.Grayling;

/// <summary>
/// The Grayling's water physics (<see cref="BoatHull"/>), every physics step where this game simulates the boat: the host
/// and solo every boat, a guest its own (which it predicts with the same numbers, so the host agrees and nothing extra is
/// sent; Fusion syncs the boat's body like any car's). The water's height comes from the game's own water bodies (every
/// lake and river of the stock maps, and any a map mod registers with the game's registry), asked once a step at the
/// hull's middle (lakes are flat; rivers barely slope over a boat's length). On land (the shore, a ramp) the boat sits on
/// its hidden wheels with the handbrake on and the throttle cut: an outboard doesn't drive on gravel.
/// </summary>
internal static class BoatPhysics
{
    private static List<HullPoint> _points;
    private static float _totalArea;
    private static Vector3 _offset;
    private static bool _ready;
    private static bool _waterBroken;

    /// <summary>Per boat: what the last step found (the frame side and the launch rules read it).</summary>
    internal sealed class State
    {
        public float At;
        /// <summary>Share of the boat's weight the water carried (0 on land, about 1 afloat).</summary>
        public float Floating;
        public bool PropWet;
        public float WaterY = float.NaN;
        public float Throttle, Reverse, Steer;
        public float Thrust;
    }

    private static readonly Dictionary<IntPtr, State> States = new();

    public static State Of(IntPtr car) => States.TryGetValue(car, out var s) ? s : null;

    public static void Forget(IntPtr car) => States.Remove(car);

    public static void Clear() => States.Clear();

    /// <summary>Ready once the car framework placed the model (its layout offset, model to car-root space).</summary>
    public static void Prepare(Vector3 offset)
    {
        _points = BoatHull.Points();
        _totalArea = 0f;
        foreach (var p in _points) _totalArea += p.Area;
        _offset = offset;
        _ready = true;
    }

    private static Vector3 Root(HullVec v) => new Vector3(v.X, v.Y, v.Z) + _offset;

    /// <summary>The water surface's height at a world point, or NaN (no water there, or the registry can't be asked).</summary>
    public static float WaterAt(Vector3 world)
    {
        if (_waterBroken) return float.NaN;
        try
        {
            float s = 0f;
            if (WaterBodyRegistry.TryGetWaterSurface(world, out s) && float.IsFinite(s)) return s;
            // a map mod's water the game's registry doesn't hold (its rivers); a frozen lake carries cars, it floats nothing
            if (OverTheHillPlus.Api.WorldPlaces.WaterAt(world.x, world.z, out var w, out float level) && !w.Frozen && float.IsFinite(level)) return level;
        }
        catch (Exception e)
        {
            _waterBroken = true;
            DebugLog.Error(GraylingPlugin.ModId, e, "BoatPhysics.WaterAt");
            GraylingPlugin.LogOnce("water", "the game's water registry can't be asked: the boat won't float this session (" + e.Message + ")");
        }
        return float.NaN;
    }

    public static void Step(VPVehicleController v)
    {
        if (!_ready || v == null) return;
        if (VehicleTraits.ModdedId(v) != GraylingDefinition.Id) return;
        if (!GameState.IsAuthority && !GameState.IsLocalCar(v)) return;
        var rb = v.cachedRigidbody;
        if (rb == null || rb.isKinematic) return;
        var ptr = v.Pointer;
        if (!States.TryGetValue(ptr, out var st)) States[ptr] = st = new State();
        var t = v.cachedTransform != null ? v.cachedTransform : v.transform;
        float now = Time.fixedTime, dt = Time.fixedDeltaTime;
        st.At = now;

        // the inputs this step (the game set them from the player's input before the blocks run)
        var data = v.data;
        float throttle = Mathf.Clamp01(data.Get(Channel.Input, InputData.Throttle) / 10000f);
        float brake = Mathf.Clamp01(data.Get(Channel.Input, InputData.Brake) / 10000f);
        float steer = Mathf.Clamp(data.Get(Channel.Input, InputData.Steer) / 10000f, -1f, 1f);
        st.Throttle = throttle;
        st.Reverse = brake;
        st.Steer = steer;

        // the water's height: at the hull's middle, else at the bow or the stern (a boat half up a ramp)
        float water = WaterAt(t.TransformPoint(Root(new HullVec(0f, 0f, 0f))));
        if (float.IsNaN(water)) water = WaterAt(t.TransformPoint(Root(new HullVec(0f, 0f, BoatHull.SternZ + 0.3f))));
        if (float.IsNaN(water)) water = WaterAt(t.TransformPoint(Root(new HullVec(0f, 0f, BoatHull.BowZ - 0.4f))));
        st.WaterY = water;

        float mass = Mathf.Max(rb.mass, 1f);
        float lift = 0f;
        int under = 0;
        if (!float.IsNaN(water))
        {
            var up = Vector3.up;
            foreach (var p in _points)
            {
                var w = t.TransformPoint(Root(p.At));
                float depth = water - w.y;
                if (depth <= 0f) continue;
                under++;
                float upSpeed = Vector3.Dot(rb.GetPointVelocity(w), up);
                float f = BoatHull.Lift(p, depth, upSpeed, _totalArea, mass);
                if (f <= 0f) continue;
                lift += f;
                rb.AddForceAtPosition(up * f, w, ForceMode.Force);
            }
        }
        st.Floating = Mathf.Clamp01(lift / (mass * BoatHull.G));
        float wet = BoatHull.WetShare(under, _points.Count);

        // the hull's drag and the water's damping of its turning, rolling and pitching
        if (wet > 0f)
        {
            var clr = t.TransformPoint(Root(new HullVec(0f, BoatHull.KeelY + 0.12f, -0.25f)));
            var vLocal = t.InverseTransformDirection(rb.GetPointVelocity(clr));
            var drag = BoatHull.Drag(new HullVec(vLocal.x, vLocal.y, vLocal.z), wet);
            rb.AddForceAtPosition(t.TransformDirection(new Vector3(drag.X, 0f, drag.Z)), clr, ForceMode.Force);
            var w = t.InverseTransformDirection(rb.angularVelocity);
            var damp = new Vector3(-w.x * BoatHull.RollDamping, -w.y * BoatHull.YawDamping, -w.z * BoatHull.RollDamping) * Mathf.Min(1f, wet * 1.5f);
            rb.AddTorque(t.TransformDirection(damp), ForceMode.Force);
        }

        // the outboard: its propeller pushes where it is (under the transom), along the way the motor is turned
        var prop = t.TransformPoint(Root(Hv(BoatRig.PropPivot)));
        st.PropWet = !float.IsNaN(water) && water - prop.y > 0.08f;
        float speedAlong = Vector3.Dot(rb.linearVelocity, t.forward);
        float push = BoatHull.PropThrust(throttle, brake, speedAlong, st.PropWet);
        st.Thrust = push;
        if (push != 0f)
        {
            var f = BoatHull.ThrustVector(push, BoatHull.MotorAngle(steer));
            rb.AddForceAtPosition(t.TransformDirection(new Vector3(f.X, 0f, f.Z)), prop, ForceMode.Force);
        }

        // on land: no driving on the hidden wheels, and it stays put on a slope
        if (st.Floating < BoatHull.FloatingShare && Grounded(v) > 0)
        {
            data.Set(Channel.Input, InputData.Throttle, 0);
            data.Set(Channel.Input, InputData.Handbrake, 10000);
        }
    }

    private static HullVec Hv(MeshVec3 m) => new(m.X, m.Y, m.Z);

    private static int Grounded(VPVehicleController v)
    {
        int n = 0;
        try
        {
            var ws = v.wheelState;
            if (ws == null) return 0;
            for (int i = 0; i < ws.Length; i++) if (ws[i] != null && ws[i].grounded) n++;
        }
        catch (Exception e) { DebugLog.Error(GraylingPlugin.ModId, e, "BoatPhysics.Grounded"); }
        return n;
    }
}
