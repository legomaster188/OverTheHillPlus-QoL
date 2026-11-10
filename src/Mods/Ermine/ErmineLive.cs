using System;
using System.Collections.Generic;
using OverTheHillPlus.Cars;
using UnityEngine;
#if MELON
using Il2CppVehiclePhysics;
#else
using VehiclePhysics;
#endif

namespace OverTheHillPlus.Ermine;

/// <summary>
/// One Ermine in the world, as this game draws it (every peer, every Ermine: yours and other players'). Each frame the
/// skis find the ground under their tips and tails and ride up and down on the A-arms (the shocks follow), the skis and
/// the handlebars turn with the steering, and the track's lugs and wheels turn with the track. Nothing here is sent:
/// every peer works it out from where the sled is (Fusion syncs that) and the ground under it.
/// </summary>
internal sealed class ErmineLive
{
    private sealed class Side
    {
        public SkiSide Geo;
        public Transform Ski, Spindle, Upper, Lower, Shock;
        public float Rise, Pitch;
        public Vector3 ShockRest;     // the shock's rest axis (top to bottom), root space
    }

    public readonly VehicleBase Car;
    public readonly IntPtr Ptr;
    private readonly Transform _t;
    private readonly Rigidbody _rb;
    private readonly Side _l = new(), _r = new();
    private readonly Transform _bars;
    private readonly List<Transform> _lugs;
    private readonly List<(Transform t, float r)> _idlers = new();
    private readonly Vector3 _offset;
    private readonly Vector3 _postAxis;
    private float _travel, _steer, _wheelSpin;
    private Vector3 _lastPos;
    private Quaternion _lastRot;
    private bool _hasLast;
    private int _mask = ~0;
    private bool _ok;

    public ErmineLive(VehicleBase car, Vector3 offset)
    {
        Car = car;
        Ptr = car.Pointer;
        _offset = offset;
        var g = ErmineRig.Geometry;
        try
        {
            _t = car.transform;
            _rb = car.cachedRigidbody;
            Bind(_l, g.Left);
            Bind(_r, g.Right);
            _bars = CarMovingParts.Find(_t, ErmineGeometry.BarsPart);
            _lugs = CarMovingParts.Copies(_t, ErmineGeometry.LugPart, g.Lugs);
            foreach (var (part, _, r) in g.Idlers)
            {
                var it = CarMovingParts.Find(_t, part);
                if (it != null) _idlers.Add((it, r));
            }
            var axis = V(g.BarsPivot) - V(g.PostBase);
            _postAxis = axis.sqrMagnitude > 1e-6f ? axis.normalized : Vector3.up;
            // rays ignore the car's own layer (its body and bump stop colliders): the ground under the skis, not the sled
            var col = car.GetComponentInChildren<Collider>();
            if (col != null) _mask = ~(1 << col.gameObject.layer);
            _ok = _l.Ski != null && _r.Ski != null;
        }
        catch (Exception e) { DebugLog.Error(ErminePlugin.ModId, e, "ErmineLive.ctor"); _ok = false; }
    }

    private void Bind(Side s, SkiSide g)
    {
        s.Geo = g;
        s.Ski = CarMovingParts.Find(_t, ErmineGeometry.Part("ski", g.Side));
        s.Spindle = CarMovingParts.Find(_t, ErmineGeometry.Part("spindle", g.Side));
        s.Upper = CarMovingParts.Find(_t, ErmineGeometry.Part("arm_up", g.Side));
        s.Lower = CarMovingParts.Find(_t, ErmineGeometry.Part("arm_lo", g.Side));
        s.Shock = CarMovingParts.Find(_t, ErmineGeometry.Part("shock", g.Side));
        s.ShockRest = V(g.ShockBottom) - V(g.ShockTop);
    }

    public bool Alive
    {
        get { try { return _ok && Car != null && _t != null; } catch (Exception ex) { DebugLog.Error(ErminePlugin.ModId, ex, "ErmineLive.Alive"); return false; } }
    }

    private static Vector3 V(MeshVec3 v) => new(v.X, v.Y, v.Z);

    /// <summary>One frame: where the sled went since the last one gives its speed and yaw rate (also for other players'
    /// sleds, whose bodies this game does not simulate).</summary>
    public void Frame(float dt, bool simulated, float inputSteer)
    {
        if (!_ok || !(dt > 0f)) return;
        var g = ErmineRig.Geometry;
        var pos = _t.position;
        var rot = _t.rotation;
        float speed = 0f, yawRate = 0f;
        if (_hasLast)
        {
            var v = (pos - _lastPos) / dt;
            speed = Vector3.Dot(v, _t.forward);
            float dyaw = Mathf.DeltaAngle((Quaternion.Inverse(_lastRot) * rot).eulerAngles.y, 0f);
            yawRate = -dyaw / dt;
        }
        _lastPos = pos;
        _lastRot = rot;
        _hasLast = true;

        // steering: your own input on your sled; otherwise the wheels' synced angle, else from the turn rate
        float steer;
        if (!float.IsNaN(inputSteer)) steer = inputSteer * MaxSkiSteer;
        else
        {
            float wheel = SteerFromWheels();
            steer = !float.IsNaN(wheel) ? wheel : SkiMath.SteerFromYaw(yawRate, speed, ErmineDefinition.FrontZ - ErmineDefinition.RearZ, MaxSkiSteer);
        }
        _steer = SkiMath.Ease(_steer, steer, dt, 0.08f);

        // the track: driven by the rear wheels where this game simulates the sled (it spins when they do), else at the
        // sled's speed
        float trackSpeed = speed;
        if (simulated)
        {
            float w = RearWheelSpeed();
            if (!float.IsNaN(w)) trackSpeed = w;
        }
        _wheelSpin = SkiMath.Ease(_wheelSpin, trackSpeed, dt, 0.05f);
        float spacing = g.Loop.Length / Math.Max(1, _lugs.Count);
        _travel = TrackLoop.Advance(_travel, _wheelSpin, dt, spacing);
        ErmineRig.PlaceLugs(_lugs, _travel, _offset);
        foreach (var (it, r) in _idlers)
        {
            if (it == null) continue;
            it.localRotation *= Quaternion.AngleAxis(_wheelSpin * dt / r * Mathf.Rad2Deg, Vector3.right);
        }

        Ski(_l, dt);
        Ski(_r, dt);
        if (_bars != null) _bars.localRotation = Quaternion.AngleAxis(_steer * BarsRatio, _postAxis);
    }

    /// <summary>The skis' lock either way, degrees, and how far the bars turn for a degree of ski.</summary>
    public const float MaxSkiSteer = 24f, BarsRatio = 1.25f;

    private float SteerFromWheels()
    {
        try
        {
            var ws = Car.wheelState;
            if (ws == null || ws.Length < 2) return float.NaN;
            float a = (ws[0].steeringAngle + ws[1].steeringAngle) * 0.5f;
            return float.IsFinite(a) && Mathf.Abs(a) > 0.05f ? Mathf.Clamp(a, -MaxSkiSteer, MaxSkiSteer) : float.NaN;
        }
        catch (Exception ex) { DebugLog.Error(ErminePlugin.ModId, ex, "ErmineLive.SteerFromWheels"); return float.NaN; }
    }

    private float RearWheelSpeed()
    {
        try
        {
            var ws = Car.wheelState;
            if (ws == null || ws.Length < 4) return float.NaN;
            float w = (ws[2].angularVelocity + ws[3].angularVelocity) * 0.5f;
            return float.IsFinite(w) ? w * ErmineDefinition.WheelRadius : float.NaN;
        }
        catch (Exception ex) { DebugLog.Error(ErminePlugin.ModId, ex, "ErmineLive.RearWheelSpeed"); return float.NaN; }
    }

    private const float RayUp = 0.55f, RayDown = 1.1f;

    /// <summary>The ground's height over a ski point at rest (car up axis, metres; + = the ground is higher), or null.</summary>
    private float? Ground(MeshVec3 modelPoint)
    {
        var up = _t.up;
        var p = _t.TransformPoint(V(modelPoint) + _offset);
        if (Physics.Raycast(p + up * RayUp, -up, out var hit, RayDown, _mask, QueryTriggerInteraction.Ignore))
            return RayUp - hit.distance;
        return null;
    }

    private void Ski(Side s, float dt)
    {
        var g = s.Geo;
        float? tip = Ground(g.Tip), tail = Ground(g.Tail);
        var (rise, pitch) = SkiMath.Pose(tip, tail, g.Span);
        s.Rise = SkiMath.Ease(s.Rise, rise, dt, 0.045f);
        s.Pitch = SkiMath.Ease(s.Pitch, pitch, dt, 0.06f);
        float sign = g.Side == "L" ? -1f : 1f;                       // the arm's outer end: -x on the left
        var yaw = Quaternion.AngleAxis(_steer, Vector3.up);
        var spindle = V(g.SpindlePivot) + _offset;
        if (s.Spindle != null)
        {
            s.Spindle.localPosition = spindle + new Vector3(0f, s.Rise, 0f);
            s.Spindle.localRotation = yaw;
        }
        if (s.Ski != null)
        {
            // the bolt swings round the steering axis with the spindle, then the ski pitches on it (nose up = about -x)
            var bolt = spindle + yaw * (V(g.SkiPivot) + _offset - spindle);
            s.Ski.localPosition = bolt + new Vector3(0f, s.Rise, 0f);
            s.Ski.localRotation = yaw * Quaternion.AngleAxis(-s.Pitch, Vector3.right);
        }
        float up = SkiMath.ArmAngle(s.Rise, g.UpperLength), lo = SkiMath.ArmAngle(s.Rise, g.LowerLength);
        var upRot = Quaternion.AngleAxis(sign * up, Vector3.forward);
        var loRot = Quaternion.AngleAxis(sign * lo, Vector3.forward);
        if (s.Upper != null) s.Upper.localRotation = upRot;
        if (s.Lower != null) s.Lower.localRotation = loRot;
        if (s.Shock != null && s.ShockRest.sqrMagnitude > 1e-6f)
        {
            // the shock's bottom rides on the lower arm: turned with it about the arm's inner pivot
            var pivot = V(g.LowerPivot);
            var bottom = pivot + loRot * (V(g.ShockBottom) - pivot);
            var axis = bottom - V(g.ShockTop);
            s.Shock.localRotation = Quaternion.FromToRotation(s.ShockRest, axis);
            float k = axis.magnitude / s.ShockRest.magnitude;
            // shortened or lengthened along its own axis only (the rest axis in the part's frame)
            var restDir = s.ShockRest.normalized;
            s.Shock.localScale = Vector3.one + (k - 1f) * new Vector3(Mathf.Abs(restDir.x), Mathf.Abs(restDir.y), Mathf.Abs(restDir.z));
        }
    }
}
