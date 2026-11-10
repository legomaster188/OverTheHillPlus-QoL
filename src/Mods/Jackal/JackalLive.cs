using System;
using OverTheHillPlus.Cars;
using UnityEngine;
#if MELON
using Il2CppVehiclePhysics;
#else
using VehiclePhysics;
#endif

namespace OverTheHillPlus.Jackal;

/// <summary>
/// One Jackal in the world, as this game draws it (every peer, every Jackal: yours and other players'). Each frame the running
/// gear follows the wheels the game draws: the front knuckles ride up and down and steer with their wheels, the trailing arms
/// and tie rods turn so their ends stay on the knuckles, the rear arms and half shafts follow the rear hubs, the coil-overs
/// stretch between their mounts, the steering wheel turns. Nothing here is sent or simulated: it only reads where the wheel
/// meshes are, so it is right for cars this game doesn't simulate too.
/// </summary>
internal sealed class JackalLive
{
    private sealed class Link
    {
        public Transform T;
        public Vector3 Pivot, End, Rest;
        public float RestLength;
        public Vector3 Axis;        // the rest direction's size on each axis (the stretch's share per axis)
    }

    private sealed class Side
    {
        public Transform FrontWheel, RearWheel, Knuckle;
        public Vector3 FrontHub, RearHub;
        public Link Upper, Lower, Tie, FrontCoil, RearArm, RearCoil, Shaft;
        public float Steer;
    }

    public readonly VehicleBase Car;
    public readonly IntPtr Ptr;
    private readonly Transform _t, _holder, _steerWheel;
    private readonly Renderer[] _body;
    private readonly Side _l, _r;
    private readonly Vector3 _steerPivot, _steerAxis;
    private bool _shown = true;
    private readonly bool _ok;

    public JackalLive(VehicleBase car, Vector3 offset)
    {
        Car = car;
        Ptr = car.Pointer;
        var g = JackalRig.Geometry;
        try
        {
            _t = car.transform;
            _holder = _t.Find(CarMovingParts.HolderName);
            _l = Bind(g.Left, offset, "FL", "RL");
            _r = Bind(g.Right, offset, "FR", "RR");
            _steerWheel = CarMovingParts.Find(_t, JackalGeometry.SteerPart);
            _steerPivot = V(g.SteerPivot) + offset;
            var axis = V(g.SteerAxisEnd) - V(g.SteerPivot);
            _steerAxis = axis.sqrMagnitude > 1e-6f ? axis.normalized : Vector3.forward;
            _body = BodyRenderers();
            _ok = _holder != null && _l.FrontWheel != null && _r.FrontWheel != null && _l.RearWheel != null && _r.RearWheel != null;
        }
        catch (Exception e) { DebugLog.Error(JackalPlugin.ModId, e, "JackalLive.ctor"); _ok = false; }
    }

    private static Vector3 V(MeshVec3 v) => new(v.X, v.Y, v.Z);

    private Side Bind(JackalSide g, Vector3 offset, string front, string rear) => new()
    {
        FrontWheel = WheelMesh(front),
        RearWheel = WheelMesh(rear),
        Knuckle = CarMovingParts.Find(_t, g.Knuckle),
        FrontHub = V(g.FrontHub) + offset,
        RearHub = V(g.RearHub) + offset,
        Upper = Bind(g.UpperArm, offset),
        Lower = Bind(g.LowerArm, offset),
        Tie = Bind(g.TieRod, offset),
        FrontCoil = Bind(g.FrontCoil, offset),
        RearArm = Bind(g.RearArm, offset),
        RearCoil = Bind(g.RearCoil, offset),
        Shaft = Bind(g.HalfShaft, offset),
    };

    private Link Bind(JackalLink g, Vector3 offset)
    {
        var l = new Link { T = CarMovingParts.Find(_t, g.Part), Pivot = V(g.Pivot) + offset, End = V(g.End) + offset };
        l.Rest = l.End - l.Pivot;
        l.RestLength = l.Rest.magnitude;
        var d = l.RestLength > 1e-4f ? l.Rest / l.RestLength : Vector3.up;
        l.Axis = new Vector3(Mathf.Abs(d.x), Mathf.Abs(d.y), Mathf.Abs(d.z));
        return l;
    }

    /// <summary>The object the game moves for a corner's wheel: the first mesh under Axles/WheelMeshes/&lt;corner&gt; (the tyre).</summary>
    private Transform WheelMesh(string corner)
    {
        var c = _t.Find("Axles/WheelMeshes/" + corner);
        if (c == null) return null;
        Transform any = null;
        foreach (var mf in c.GetComponentsInChildren<MeshFilter>(true))
        {
            if (mf == null) continue;
            if (mf.name.IndexOf("tyre", StringComparison.OrdinalIgnoreCase) >= 0 || mf.name.IndexOf("tire", StringComparison.OrdinalIgnoreCase) >= 0) return mf.transform;
            if (any == null && mf.name.IndexOf("wheel", StringComparison.OrdinalIgnoreCase) >= 0) any = mf.transform;
        }
        return any != null ? any : c;
    }

    private Renderer[] BodyRenderers()
    {
        var parts = _t.Find("Parts");
        if (parts == null) return Array.Empty<Renderer>();
        var list = new System.Collections.Generic.List<Renderer>();
        foreach (var r in parts.GetComponentsInChildren<MeshRenderer>(true))
            if (r != null && r.name.IndexOf("_body_LOD", StringComparison.Ordinal) >= 0) list.Add(r);
        return list.ToArray();
    }

    public bool Alive
    {
        get { try { return _ok && Car != null && _t != null; } catch (Exception ex) { DebugLog.Error(JackalPlugin.ModId, ex, "JackalLive.Alive"); return false; } }
    }

    /// <summary>One frame. The holder is off while the body's LOD group has the car culled (thin arms on their own would float
    /// out there as specks).</summary>
    public void Frame()
    {
        if (!_ok) return;
        bool show = _body.Length == 0;
        foreach (var r in _body)
            if (r != null && r.isVisible) { show = true; break; }
        if (show != _shown)
        {
            _shown = show;
            foreach (var r in _holder.GetComponentsInChildren<MeshRenderer>(true))
                if (r != null) r.enabled = show;
        }
        if (!show) return;
        Front(_l);
        Front(_r);
        Rear(_l);
        Rear(_r);
        if (_steerWheel != null)
        {
            _steerWheel.localPosition = _steerPivot;
            _steerWheel.localRotation = Quaternion.AngleAxis(-(_l.Steer + _r.Steer) * 0.5f * JackalGeometry.WheelRatio, _steerAxis);
        }
    }

    private Vector3 Hub(Transform wheel) => _t.InverseTransformPoint(wheel.position);

    private static float SteerOf(Transform t, Transform wheel)
    {
        var a = t.InverseTransformDirection(wheel.right);
        return JackalGeometry.SteerFromAxle(a.x, a.z);
    }

    private void Front(Side s)
    {
        var hub = Hub(s.FrontWheel);
        s.Steer = SteerOf(_t, s.FrontWheel);
        var yaw = Quaternion.AngleAxis(s.Steer, Vector3.up);
        if (s.Knuckle != null)
        {
            s.Knuckle.localPosition = hub;
            s.Knuckle.localRotation = yaw;
        }
        var upper = Aim(s.Upper, s.Upper.Pivot, hub + yaw * (s.Upper.End - s.FrontHub));
        var lower = Aim(s.Lower, s.Lower.Pivot, hub + yaw * (s.Lower.End - s.FrontHub));
        // the rack moves the tie rod's inner end across with the steering
        float rack = s.Steer / 34f * JackalGeometry.RackTravel;
        Aim(s.Tie, s.Tie.Pivot + Vector3.right * rack, hub + yaw * (s.Tie.End - s.FrontHub));
        // the coil-over's foot rides on the lower arm
        Aim(s.FrontCoil, s.FrontCoil.Pivot, s.Lower.Pivot + lower * (s.FrontCoil.End - s.Lower.Pivot));
    }

    private void Rear(Side s)
    {
        var d = Hub(s.RearWheel) - s.RearHub;
        var arm = Aim(s.RearArm, s.RearArm.Pivot, s.RearArm.End + d);
        Aim(s.RearCoil, s.RearCoil.Pivot, s.RearArm.Pivot + arm * (s.RearCoil.End - s.RearArm.Pivot));
        Aim(s.Shaft, s.Shaft.Pivot, s.Shaft.End + d);
    }

    /// <summary>Stands a link on its pivot, turned and stretched so its end is at <paramref name="end"/>. Returns the turn.</summary>
    private static Quaternion Aim(Link l, Vector3 pivot, Vector3 end)
    {
        var now = end - pivot;
        var q = Quaternion.FromToRotation(l.Rest, now);
        if (l.T == null) return q;
        l.T.localPosition = pivot;
        l.T.localRotation = q;
        float k = JackalGeometry.Stretch(l.RestLength, now.magnitude);
        l.T.localScale = Vector3.one + (k - 1f) * l.Axis;
        return q;
    }
}
