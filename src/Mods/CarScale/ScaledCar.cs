using System;
using System.Collections.Generic;
using UnityEngine;
using VehiclePhysics;

namespace OverTheHillPlus.CarScale;

/// <summary>
/// One car Car Scale has resized: its own size, and every physics value changed for it (to put back later).
///
/// How a car is resized (interop checked with the member dumper, Cpp2IL of the updated game):
/// - Visual: the car's root transform (the VehicleBase with the Rigidbody) gets localScale × size, so the body, its
///   colliders, the wheel meshes and the wheel positions all follow. Fusion syncs position and rotation only, which
///   is why modded players apply the same size themselves (the host's table, ModNet channel 12).
/// - Physics (only where this game simulates the car: the host, or solo): the wheel colliders' radius
///   (WheelColliderBehaviour.SetWheelRadius only stores the value; RefreshPhysics pushes it into the live collider),
///   suspensionTravel, springRate and damperRate on the three wheel collider types the game uses, the rigidbody's
///   mass, and the brake torques, by <see cref="ScaleMath.PhysicsFor"/>. Each value is written once when the size
///   changes and put back only while it still holds our value, so another mod's tuning or a game upgrade made
///   since is kept and nothing fights over a value every frame.
/// - Growing a car on the host lifts it first, so the bigger body doesn't start inside the ground.
/// The wheel collider access is a small copy of Cheats+'s chassis tuning (mods don't reference each other).
/// </summary>
internal sealed class ScaledCar
{
    private sealed class Slot
    {
        public UnityEngine.Object Life;
        public Func<float> Get;
        public Action<float> Set;
        public Action After;
        public float Original, Written;

        public void Restore()
        {
            try
            {
                if (Life == null) return; // destroyed with its car
                if (Same(Get(), Written))
                {
                    Set(Original);
                    After?.Invoke();
                }
            }
            catch (Exception e) { DebugLog.Error("carscale", e, "Slot.Restore"); CarScaleCore.LogOnce("restore", "putting a value back: " + e.Message); }
        }
    }

    public readonly VehicleBase Car;
    public readonly IntPtr Ptr;
    private readonly Transform _t;
    private readonly Vector3 _baseScale;
    private readonly List<Slot> _slots = new();

    public float Visual { get; private set; } = 1f;
    public float Physics { get; private set; } = 1f;

    public ScaledCar(VehicleBase car)
    {
        Car = car;
        Ptr = car.Pointer;
        _t = car.cachedTransform != null ? car.cachedTransform : car.transform;
        var s = _t.localScale;
        _baseScale = s.sqrMagnitude > 1e-6f ? s : Vector3.one;
    }

    /// <summary>Unity null test: false once the car was destroyed.</summary>
    public bool Alive
    {
        get { try { return Car != null && _t != null; } catch (Exception ex) { DebugLog.Error("carscale", ex, "ScaledCar.Alive"); return false; } }
    }

    private static bool Same(float a, float b) => Math.Abs(a - b) <= 1e-4f * Math.Max(1f, Math.Abs(a));

    // ---------------------------------------------------------------- visual

    public void SetVisual(float size)
    {
        size = ScaleMath.Clamp(size);
        var want = _baseScale * size;
        if (ScaleMath.Same(Visual, size) && (_t.localScale - want).sqrMagnitude < 1e-6f) return;
        _t.localScale = want;
        Visual = size;
    }

    // ---------------------------------------------------------------- physics

    public void SetPhysics(float size, bool scaleSuspension)
    {
        size = ScaleMath.Clamp(size);
        if (ScaleMath.Same(Physics, size)) return;
        float before = Physics;
        RestorePhysics();
        Physics = size;
        if (ScaleMath.IsNormal(size)) return;
        if (size > before) Lift((size - before) * 1.2f);
        Apply(ScaleMath.PhysicsFor(size), scaleSuspension);
    }

    public void RestorePhysics()
    {
        foreach (var s in _slots) s.Restore();
        _slots.Clear();
        Physics = 1f;
    }

    /// <summary>Back to the car's own size and values.</summary>
    public void Restore()
    {
        try { RestorePhysics(); } catch (Exception e) { DebugLog.Error("carscale", e, "ScaledCar.Restore"); CarScaleCore.LogOnce("restore-physics", e.Message); }
        try { if (_t != null) SetVisual(1f); } catch (Exception e) { DebugLog.Error("carscale", e, "ScaledCar.Restore"); CarScaleCore.LogOnce("restore-visual", e.Message); }
    }

    private void Write(UnityEngine.Object life, Func<float> get, Action<float> set, float factor, Action after = null)
    {
        if (Same(factor, 1f)) return;
        float cur = get();
        if (!float.IsFinite(cur)) return;
        float want = cur * factor;
        _slots.Add(new Slot { Life = life, Get = get, Set = set, After = after, Original = cur, Written = want });
        set(want);
        after?.Invoke();
    }

    private void Apply(ScaleMath.PhysicsScale p, bool scaleSuspension)
    {
        var vp = Car.TryCast<VPVehicleController>();
        if (vp != null)
        {
            try
            {
                var states = vp.wheelState;
                int n = states == null ? 0 : Math.Min(vp.wheelCount, states.Length);
                for (int i = 0; i < n; i++)
                {
                    var col = states[i]?.wheelCol;
                    if (col == null) continue;
                    var w = new WheelRef(col);
                    Action refresh = w.Refresh;
                    Write(col, () => col.tireRadius, v => col.SetWheelRadius(v), p.Radius, refresh);
                    if (!w.Valid || !scaleSuspension) continue;
                    Write(col, () => w.Travel, v => w.Travel = v, p.Travel, refresh);
                    Write(col, () => w.Spring, v => w.Spring = v, p.Spring, refresh);
                    Write(col, () => w.Damper, v => w.Damper = v, p.Damper, refresh);
                }
            }
            catch (Exception e) { DebugLog.Error("carscale", e, "ScaledCar.Apply"); CarScaleCore.LogOnce("wheels", "wheels: " + e.Message); }

            try
            {
                Brakes.Settings b = null;
                try { b = vp.m_brakes?.settings; } catch (Exception ex) { DebugLog.Error("carscale", ex, "ScaledCar.Apply"); }
                if (b == null) try { b = vp.brakes; } catch (Exception ex) { DebugLog.Error("carscale", ex, "ScaledCar.Apply"); }
                if (b != null)
                {
                    Write(vp, () => b.maxBrakeTorque, v => b.maxBrakeTorque = v, p.Brake);
                    Write(vp, () => b.handbrakeTorque, v => b.handbrakeTorque = v, p.Brake);
                }
            }
            catch (Exception e) { DebugLog.Error("carscale", e, "ScaledCar.Apply"); CarScaleCore.LogOnce("brakes", "brakes: " + e.Message); }
        }

        try
        {
            var rb = Car.cachedRigidbody;
            if (rb != null) Write(rb, () => rb.mass, v => rb.mass = Mathf.Max(1f, v), p.Mass);
        }
        catch (Exception e) { DebugLog.Error("carscale", e, "ScaledCar.Apply"); CarScaleCore.LogOnce("mass", "mass: " + e.Message); }
    }

    private void Lift(float metres)
    {
        try
        {
            var pos = _t.position + Vector3.up * metres;
            var fwd = Vector3.ProjectOnPlane(_t.forward, Vector3.up);
            var rot = fwd.sqrMagnitude > 0.01f ? Quaternion.LookRotation(fwd.normalized, Vector3.up) : _t.rotation;
            Car.Reposition(pos, rot);
        }
        catch (Exception e) { DebugLog.Error("carscale", e, "ScaledCar.Lift"); CarScaleCore.LogOnce("lift", "lifting a growing car: " + e.Message); }
    }

    /// <summary>The three wheel collider front-ends the game uses share these field names; this hides which one.</summary>
    private sealed class WheelRef
    {
        private readonly WheelColliderBehaviour _b;
        private readonly VPFunselektor3DRaycastWheelCollider _r;
        private readonly VPFunselektor3DWheelCollider _f;
        private readonly VPStandardWheelCollider _s;

        public WheelRef(WheelColliderBehaviour b)
        {
            _b = b;
            _r = b.TryCast<VPFunselektor3DRaycastWheelCollider>();
            if (_r == null) _f = b.TryCast<VPFunselektor3DWheelCollider>();
            if (_r == null && _f == null) _s = b.TryCast<VPStandardWheelCollider>();
        }

        public bool Valid => _r != null || _f != null || _s != null;

        public float Spring
        {
            get => _r != null ? _r.springRate : _f != null ? _f.springRate : _s.springRate;
            set { if (_r != null) _r.springRate = value; else if (_f != null) _f.springRate = value; else _s.springRate = value; }
        }

        public float Damper
        {
            get => _r != null ? _r.damperRate : _f != null ? _f.damperRate : _s.damperRate;
            set { if (_r != null) _r.damperRate = value; else if (_f != null) _f.damperRate = value; else _s.damperRate = value; }
        }

        public float Travel
        {
            get => _r != null ? _r.suspensionTravel : _f != null ? _f.suspensionTravel : _s.suspensionTravel;
            set { if (_r != null) _r.suspensionTravel = value; else if (_f != null) _f.suspensionTravel = value; else _s.suspensionTravel = value; }
        }

        public void Refresh()
        {
            try { _b.RefreshPhysics(); }
            catch (Exception e) { DebugLog.Error("carscale", e, "WheelRef.Refresh"); CarScaleCore.LogOnce("refresh", "RefreshPhysics: " + e.Message); }
        }
    }
}
