using System;
using System.Collections.Generic;
using Fusion;
using Fusion.Addons.Physics;
using UnityEngine;
using Object = UnityEngine.Object;

namespace OverTheHillPlus.Trailers;

/// <summary>One trailer in the session, on this peer: its network object, body, wheels, hitch joint and load.</summary>
internal sealed class TrailerLive
{
    public sealed class Wheel
    {
        public Vector3 Local;          // hub at rest, trailer space
        public Transform Node, Spin;
        public float Angle, VisualDrop;
        // the snow/mud/sand track stamp (a copy of the cars' own), switched on while the wheel is on the ground
        public GameObject Trax;
        public bool TraxOn;
        public float GroundedFor;
    }

    public TrailerState State;
    public TrailerSpec Spec;
    public TrailerModel Model;
    public NetworkObject No;
    public Rigidbody Rb;
    public NetworkRigidbody3D Nrb;
    public Transform T;
    public readonly List<Wheel> Wheels = new();
    public Collider[] Colliders = Array.Empty<Collider>();

    // hitch, on the host and the owner
    public ConfigurableJoint Joint;
    public Rigidbody CarRb;
    public uint JointCar;
    // collisions ignored with a car (every peer): its colliders, until it is far enough away
    public uint IgnoredCar;
    public readonly List<Collider> IgnoredWith = new();
    public int IgnoredCount;

    // physics step
    public float PrevCarSpeed, CarDecel;
    public int PrevTick;
    public bool HavePrev;

    // drawn wheels
    public Vector3 LastPos;
    public bool HaveLast;

    // host bookkeeping
    public uint CarAtSpawn;
    public float NoCarSince = -1f, TiltSince = -1f;
    public float SpawnedAt;

    // load
    public GameObject CargoShown;
    public uint CargoShownId;

    public uint NetId => State.NetId;
    public TrailerKind Kind => State.Kind;

    public bool Alive
    {
        get
        {
            try { return No != null && T != null && No.IsValid; }
            catch (Exception ex) { DebugLog.Error("trailers", ex, "TrailerLive.Alive"); return false; }
        }
    }

    /// <summary>This peer simulates the trailer: the host, or the owner (input authority makes it predicted). On
    /// everyone else it is a kinematic proxy that Fusion places.</summary>
    public bool Simulated
    {
        get
        {
            try { return Rb != null && !Rb.isKinematic && No.IsInSimulation; }
            catch (Exception ex) { DebugLog.Error("trailers", ex, "TrailerLive.Simulated"); return false; }
        }
    }

    public Vector3 Hitch => Model.Anchor("hitch", Vector3.zero);

    public static TrailerLive Attach(NetworkObject no, TrailerState state)
    {
        var spec = TrailerSpecs.Of(state.Kind);
        var model = TrailerAssets.Model(state.Kind);
        if (no == null || spec == null || model == null) return null;
        var t = new TrailerLive
        {
            State = state.Clone(), Spec = spec, Model = model, No = no, T = no.transform,
            Rb = no.GetComponent<Rigidbody>(), Nrb = no.GetComponent<NetworkRigidbody3D>(), SpawnedAt = Time.unscaledTime,
        };
        if (t.Rb == null) return null;
        foreach (var a in model.Wheels)
        {
            var node = t.T.Find(a.Name);
            t.Wheels.Add(new Wheel { Local = TrailerAssets.V(a.Position), Node = node, Spin = node != null ? node.Find("Spin") : null });
        }
        var cols = new List<Collider>();
        foreach (var c in no.GetComponentsInChildren<Collider>(true))
            if (c != null) cols.Add(c);
        t.Colliders = cols.ToArray();
        // the template carries these, but a peer that simulates the trailer must not depend on it
        try
        {
            t.Rb.centerOfMass = new Vector3(0f, spec.ComHeight, spec.ComForward);
            // a light body on a hard joint to a heavy car needs more solver passes than the default 6/1 to settle
            t.Rb.solverIterations = 12;
            t.Rb.solverVelocityIterations = 4;
        }
        catch (Exception ex) { DebugLog.Error("trailers", ex, "TrailerLive.Attach"); }
        return t;
    }

    // ---------------------------------------------------------------- hitch joint (host and owner only)

    /// <summary>Adds the hitch joint to the car's body. The joint's rest frame is taken with the trailer straight behind
    /// the car (same axes), on the host and on the owner alike, so both get the same limits whatever the poses are
    /// when the message arrives.</summary>
    public void MakeJoint(Rigidbody car, uint carNetId, Vector3 anchor, Vector3 carAnchor, bool breakable)
    {
        DropJoint();
        if (car == null) return;
        var limits = HitchLimits.For(Spec, Rb.mass);
        var keepPos = T.position;
        var keepRot = T.rotation;
        // rest frame: trailer axes parallel to the car's, the hitch on the ball
        var restRot = car.transform.rotation;
        var restPos = car.transform.TransformPoint(carAnchor) - restRot * anchor;
        T.SetPositionAndRotation(restPos, restRot);
        Rb.position = restPos;
        Rb.rotation = restRot;
        var j = No.gameObject.AddComponent<ConfigurableJoint>();
        j.autoConfigureConnectedAnchor = false;
        j.anchor = anchor;
        j.axis = Vector3.right;            // angular X: pitch
        j.secondaryAxis = Vector3.up;      // angular Y: yaw (jackknife), angular Z: roll
        j.connectedBody = car;
        j.connectedAnchor = carAnchor;
        j.xMotion = j.yMotion = j.zMotion = ConfigurableJointMotion.Locked;
        j.angularXMotion = j.angularYMotion = j.angularZMotion = ConfigurableJointMotion.Limited;
        // generous limits (a crest or a dip bends car and trailer against each other by the slope's change), and the
        // solver starts pushing a few degrees early so the stop doesn't chatter
        float soft = HitchLimits.ContactDeg;
        j.lowAngularXLimit = new SoftJointLimit { limit = -limits.Pitch, contactDistance = soft };
        j.highAngularXLimit = new SoftJointLimit { limit = limits.Pitch, contactDistance = soft };
        j.angularYLimit = new SoftJointLimit { limit = limits.Yaw, contactDistance = soft };
        j.angularZLimit = new SoftJointLimit { limit = limits.Roll, contactDistance = soft };
        j.angularYZLimitSpring = new SoftJointLimitSpring { spring = 0f, damper = 0f };
        // rotational friction of the ball (no spring): the trailer settles behind the car instead of snaking
        j.rotationDriveMode = RotationDriveMode.XYAndZ;
        var friction = new JointDrive { positionSpring = 0f, positionDamper = HitchLimits.AngularDamper(Rb.mass), maximumForce = float.MaxValue };
        j.angularXDrive = friction;
        j.angularYZDrive = friction;
        j.enableCollision = false;
        j.enablePreprocessing = false;
        // only a gross stretch is pulled back; correcting the rotation too made the hitch pop on bumps
        j.projectionMode = JointProjectionMode.PositionOnly;
        j.projectionDistance = 0.15f;
        j.breakForce = breakable ? limits.BreakForce : float.PositiveInfinity;
        j.breakTorque = float.PositiveInfinity;
        T.SetPositionAndRotation(keepPos, keepRot);
        Rb.position = keepPos;
        Rb.rotation = keepRot;
        Joint = j;
        CarRb = car;
        JointCar = carNetId;
        HavePrev = false;
    }

    public void DropJoint()
    {
        try { if (Joint != null) Object.Destroy(Joint); } catch (Exception ex) { DebugLog.Error("trailers", ex, "TrailerLive.DropJoint"); }
        Joint = null;
        CarRb = null;
        JointCar = 0;
        HavePrev = false;
    }

    /// <summary>The joint was there and is gone (it broke).</summary>
    public bool JointBroken => JointCar != 0 && (Joint == null || Joint.Pointer == IntPtr.Zero);

    // ---------------------------------------------------------------- collisions with the towing car (every peer)

    /// <summary>Ignores collisions between the trailer and a car (re-run when the car's collider count changes, e.g.
    /// an upgrade was mounted). Idempotent and cheap when nothing changed.</summary>
    public void IgnoreCar(NetworkObject car, uint carNetId)
    {
        if (car == null) return;
        Collider[] cc;
        try { cc = car.GetComponentsInChildren<Collider>(true); } catch (Exception ex) { DebugLog.Error("trailers", ex, "TrailerLive.IgnoreCar"); return; }
        if (IgnoredCar == carNetId && IgnoredCount == cc.Length) return;
        if (IgnoredCar != carNetId) RestoreCar();
        else IgnoredWith.Clear();   // same car, other colliders (an upgrade came or went): list them again
        IgnoredCar = carNetId;
        IgnoredCount = cc.Length;
        foreach (var c in cc)
        {
            if (c == null || c.isTrigger) continue;
            foreach (var mine in Colliders)
                try { if (mine != null) Physics.IgnoreCollision(mine, c, true); } catch (Exception ex) { DebugLog.Error("trailers", ex, "TrailerLive.IgnoreCar"); }
            IgnoredWith.Add(c);
        }
    }

    public void RestoreCar()
    {
        foreach (var c in IgnoredWith)
        {
            if (c == null) continue;
            foreach (var mine in Colliders)
                try { if (mine != null) Physics.IgnoreCollision(mine, c, false); } catch (Exception ex) { DebugLog.Error("trailers", ex, "TrailerLive.RestoreCar"); }
        }
        IgnoredWith.Clear();
        IgnoredCar = 0;
        IgnoredCount = 0;
    }

    // ---------------------------------------------------------------- load (car hauler)

    /// <summary>Shows the garage model of the car <see cref="TrailerState.CargoCarId"/> on the deck (every peer), or
    /// nothing. The model has no colliders or bodies: it is a picture of a car, not a car.</summary>
    public void ShowCargo()
    {
        uint want = State.CargoCarId;
        if (want == CargoShownId && (want == 0 || CargoShown != null)) return;
        if (CargoShown != null) { try { Object.Destroy(CargoShown); } catch (Exception ex) { DebugLog.Error("trailers", ex, "TrailerLive.ShowCargo"); } }
        CargoShown = null;
        CargoShownId = want;
        if (want == 0) return;
        var deck = T.Find("Cargo");
        var go = CargoModels.Make(want, deck != null ? deck : T);
        if (go == null) return;
        CargoShown = go;
    }

    public void Forget()
    {
        DropJoint();
        RestoreCar();
        foreach (var w in Wheels)
        {
            if (w.Trax != null) { try { Object.Destroy(w.Trax); } catch (Exception ex) { DebugLog.Error("trailers", ex, "TrailerLive.Forget"); } }
            w.Trax = null;
            w.TraxOn = false;
        }
        if (CargoShown != null) { try { Object.Destroy(CargoShown); } catch (Exception ex) { DebugLog.Error("trailers", ex, "TrailerLive.Forget"); } }
        CargoShown = null;
    }
}
