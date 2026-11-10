using System;
using System.Collections.Generic;
#if MELON
using Il2CppFusion;
using Il2CppFusion.Addons.Physics;
#else
using Fusion;
using Fusion.Addons.Physics;
#endif
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
    /// <summary>The adjustable coupler's head (null on a model without one) and the offset it is drawn at, m.</summary>
    public Transform CouplerHead;
    public float CouplerOffset;

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

    /// <summary>The model's hitch: the coupler's ball socket with the head in the middle of its channel (a semi-trailer's
    /// kingpin).</summary>
    public Vector3 Hitch => Model.HitchPoint;

    /// <summary>A semi-trailer's landing gear node (null on the others) and whether it is drawn lowered.</summary>
    public Transform GearNode;
    public bool GearDown;

    /// <summary>Lowers the landing gear while the trailer stands unhitched and winds it up while hitched (every peer).</summary>
    public void ShowGear()
    {
        if (GearNode == null || !(Spec.GearDrop > 0f)) return;
        bool down = !State.Hitched;
        if (down == GearDown) return;
        GearDown = down;
        try { GearNode.localPosition = new Vector3(0f, down ? -Spec.GearDrop : 0f, 0f); }
        catch (Exception ex) { DebugLog.Error("trailers", ex, "TrailerLive.ShowGear"); }
    }

    /// <summary>The landing gear's feet in trailer space, lowered (empty for a trailer without gear).</summary>
    public IEnumerable<Vector3> GearFeet()
    {
        if (!(Spec.GearDrop > 0f)) yield break;
        foreach (var n in new[] { "gear_L", "gear_R" })
        {
            var a = Model.Blob.Anchor(n);
            if (a != null) yield return TrailerAssets.V(a.Position) - new Vector3(0f, Spec.GearDrop, 0f);
        }
    }

    /// <summary>Where the ball socket is now (trailer space): the model's hitch with the head at its current setting.</summary>
    public Vector3 CouplerPoint => Hitch + Vector3.up * CouplerOffset;

    /// <summary>Draws the coupler head at the height the state's hitch anchor stands for (every peer: the anchor comes
    /// with the state). The head keeps that setting when unhitched, like a bolted-in coupler.</summary>
    public void ShowCoupler()
    {
        float off = 0f;
        var a = TrailerAssets.V3(State.Anchor);
        var h = Hitch;
        if (CouplerHead != null && Mathf.Abs(a.x - h.x) < 0.05f && Mathf.Abs(a.z - h.z) < 0.05f)
            off = CouplerFit.FromAnchor(a.y, h.y);
        CouplerOffset = off;
        try { if (CouplerHead != null) CouplerHead.localPosition = new Vector3(0f, off, 0f); }
        catch (Exception ex) { DebugLog.Error("trailers", ex, "TrailerLive.ShowCoupler"); }
    }

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
        t.CouplerHead = t.T.Find(TrailerPrefabs.CouplerNode);
        t.GearNode = t.T.Find(TrailerPrefabs.GearNode);
        t.LoadNode = t.T.Find(TrailerPrefabs.LoadNode);
        var lc = t.T.Find(TrailerPrefabs.LoadColliderNode);
        t.LoadCollider = lc != null ? lc.GetComponent<Collider>() : null;
        t._drawnAt = state.Boat;
        t.GearDown = false;
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
        WorldScan.JointsChanged();
    }

    public void DropJoint()
    {
        try { if (Joint != null) Object.Destroy(Joint); } catch (Exception ex) { DebugLog.Error("trailers", ex, "TrailerLive.DropJoint"); }
        if (Joint != null || JointCar != 0) WorldScan.JointsChanged();
        Joint = null;
        CarRb = null;
        JointCar = 0;
        HavePrev = false;
    }

    /// <summary>The joint there is uses these anchors (true when there is none: nothing to remake).</summary>
    public bool JointMatches(Vector3 anchor, Vector3 carAnchor)
    {
        try
        {
            if (Joint == null || Joint.Pointer == IntPtr.Zero) return true;
            return (Joint.anchor - anchor).sqrMagnitude < 1e-6f && (Joint.connectedAnchor - carAnchor).sqrMagnitude < 1e-6f;
        }
        catch (Exception ex) { DebugLog.Error("trailers", ex, "TrailerLive.JointMatches"); return true; }
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
        if (Kind == TrailerKind.Boat) { ShowBoat(); return; }
        uint want = State.CargoCarId;
        if (want == CargoShownId && (want == 0 || CargoShown != null)) return;
        if (CargoShown != null) { try { Object.Destroy(CargoShown); } catch (Exception ex) { DebugLog.Error("trailers", ex, "TrailerLive.ShowCargo"); } }
        CargoShown = null;
        CargoShownId = want;
        if (want == 0) return;
        var deck = T.Find("Cargo");
        var go = CargoModels.Make(want, deck != null ? deck : T, Model);
        if (go == null) return;
        CargoShown = go;
    }

    // ---------------------------------------------------------------- the boat trailer's boat (every peer)

    /// <summary>The boat drawn on the bunks or moored (a boat mod's garage model; null: none, the trailer's own runabout
    /// shows instead), its item id, and its visible bounds in its root's space.</summary>
    public GameObject BoatShown;
    public uint BoatShownId;
    public Vector3 BoatLo, BoatHi;
    /// <summary>Where that boat's root stands on the bunks, trailer space (its keel on the bunks, its bow at the winch
    /// post's roller); false until a boat model was measured.</summary>
    public Vector3 SeatLocal;
    public bool HasSeat;
    /// <summary>The runabout and the hull collider that go with whatever boat is on the bunks.</summary>
    public Transform LoadNode;
    public Collider LoadCollider;
    private BoxCollider _mooredHull;
    private BoatPlace _drawnAt;
    private bool _drawnOnce;
    private float _slideStart = -1f;
    private Vector3 _slideFromPos;
    private Quaternion _slideFromRot = Quaternion.identity;
    private float _mooredWater = float.NaN;
    private bool _mooredWaterAsked;

    // the boat rules, on the host
    public float StillSince = -1f, CarChangedSince = -1f;
    /// <summary>A boat mod asked for this trailer to be hitched to its owner's car when it stands at the coupler (until then,
    /// unscaled time; -1: not asked).</summary>
    public float HitchWantedUntil = -1f;

    /// <summary>The middle of the keel's line on the bunks, trailer space (the model's "boat_keel" anchor).</summary>
    public Vector3 KeelLocal => Model.Anchor("boat_keel", new Vector3(0f, 0.59f, -0.30f));
    /// <summary>The bow stop: the face of the winch post's roller, trailer space (the model's "boat_bow" anchor).</summary>
    public Vector3 BowLocal => Model.Anchor("boat_bow", new Vector3(0f, 1.05f, 1.95f));

    /// <summary>The boat's root on the bunks, world (the trailer's root without a measured boat).</summary>
    public Vector3 SeatWorld => HasSeat ? T.TransformPoint(SeatLocal) : T.position;

    /// <summary>Makes the drawn boat match the state: the runabout and the hull collider only while a boat is on the
    /// bunks, a boat mod's boat built once and placed every frame (<see cref="FrameBoat"/>), a slide when the boat comes
    /// off or goes back on.</summary>
    public void ShowBoat()
    {
        var s = State;
        uint want = s.CargoCarId;
        if (want != BoatShownId)
        {
            DropBoat();
            BoatShownId = want;
            if (want != 0)
            {
                var go = CargoModels.Boat(want, out BoatLo, out BoatHi);
                if (go != null)
                {
                    BoatShown = go;
                    var keel = KeelLocal;
                    var bow = BowLocal;
                    // centred across the bunks, the keel on them, the bow at the roller
                    SeatLocal = new Vector3(-(BoatLo.x + BoatHi.x) * 0.5f, keel.y - BoatLo.y, bow.z - BoatHi.z);
                    HasSeat = true;
                    DebugLog.Verbose(TrailersPlugin.ModId, () => $"boat trailer {NetId}: its boat measures {BoatHi.z - BoatLo.z:0.00} m, keel {BoatLo.y:0.00}; seated at {SeatLocal}");
                    try
                    {
                        // a moored boat is something to bump into (off while it slides and while it is on the bunks: the
                        // trailer's own hull collider is there then)
                        var hull = new GameObject("MooredHull") { layer = TrailerPrefabs.BodyLayer };
                        hull.transform.SetParent(go.transform, false);
                        _mooredHull = hull.AddComponent<BoxCollider>();
                        float len = BoatHi.z - BoatLo.z;
                        _mooredHull.center = new Vector3((BoatLo.x + BoatHi.x) * 0.5f, BoatLo.y + 0.32f, BoatHi.z - len * 0.46f);
                        _mooredHull.size = new Vector3((BoatHi.x - BoatLo.x) * 0.92f, 0.56f, len * 0.86f);
                        _mooredHull.enabled = false;
                    }
                    catch (Exception ex) { DebugLog.Error("trailers", ex, "TrailerLive.ShowBoat collider"); _mooredHull = null; }
                }
            }
        }
        bool on = s.Boat == BoatPlace.OnTrailer;
        try
        {
            if (LoadNode != null) LoadNode.gameObject.SetActive(on && BoatShown == null);
            if (LoadCollider != null) LoadCollider.enabled = on;
        }
        catch (Exception ex) { DebugLog.Error("trailers", ex, "TrailerLive.ShowBoat"); }
        if (s.Boat != _drawnAt)
        {
            // off the bunks onto the water, or winched back on: it slides from where it is drawn now
            if (_drawnOnce && BoatShown != null && s.Boat != BoatPlace.Out && _drawnAt != BoatPlace.Out)
            {
                _slideStart = Time.time;
                _slideFromPos = BoatShown.transform.position;
                _slideFromRot = BoatShown.transform.rotation;
            }
            else _slideStart = -1f;
            _drawnAt = s.Boat;
        }
        _mooredWaterAsked = false;
        FrameBoat(Time.time);
    }

    /// <summary>Every frame: the boat on the bunks with the trailer, or moored on the water, bobbing (every peer).</summary>
    public void FrameBoat(float now)
    {
        var go = BoatShown;
        if (go == null) return;
        var s = State;
        bool visible = s.Boat != BoatPlace.Out;
        try
        {
            if (go.activeSelf != visible) go.SetActive(visible);
            if (!visible) return;
            Vector3 pos;
            Quaternion rot;
            if (s.Boat == BoatPlace.OnTrailer)
            {
                pos = T.TransformPoint(SeatLocal);
                rot = T.rotation;
            }
            else
            {
                if (!_mooredWaterAsked)
                {
                    _mooredWaterAsked = true;
                    _mooredWater = TrailerWater.At(new Vector3(s.BoatX, T.position.y + 0.5f, s.BoatZ));
                }
                // no water found there (it can't be asked): it floats at the height it came off the bunks at
                float water = float.IsFinite(_mooredWater) ? _mooredWater : T.TransformPoint(KeelLocal).y + Core.Shared.WorldSites.TrailerBoatDraft;
                var bob = BoatRules.Bob(now, NetId);
                pos = new Vector3(s.BoatX, water - Core.Shared.WorldSites.TrailerBoatDraft - BoatLo.y + bob.Heave, s.BoatZ);
                rot = Quaternion.Euler(bob.Pitch, s.BoatYaw, bob.Roll);
            }
            bool sliding = _slideStart >= 0f;
            if (sliding)
            {
                float u = (now - _slideStart) / BoatRules.SlideSeconds;
                float e = BoatRules.Ease(u);
                pos = Vector3.Lerp(_slideFromPos, pos, e);
                rot = Quaternion.Slerp(_slideFromRot, rot, e);
                if (u >= 1f) { _slideStart = -1f; sliding = false; }
            }
            go.transform.SetPositionAndRotation(pos, rot);
            if (_mooredHull != null)
            {
                bool moored = s.Boat == BoatPlace.Afloat && !sliding;
                if (_mooredHull.enabled != moored) _mooredHull.enabled = moored;
            }
            _drawnOnce = true;
        }
        catch (Exception ex) { DebugLog.Error("trailers", ex, "TrailerLive.FrameBoat"); TrailersPlugin.LogOnce("boat-frame", "[Trailers] the boat on its trailer: " + ex.Message); }
    }

    private void DropBoat()
    {
        if (BoatShown != null) { try { Object.Destroy(BoatShown); } catch (Exception ex) { DebugLog.Error("trailers", ex, "TrailerLive.DropBoat"); } }
        BoatShown = null;
        _mooredHull = null;
        HasSeat = false;
        BoatShownId = 0;
        _drawnOnce = false;
        _slideStart = -1f;
    }

    public void Forget()
    {
        DropJoint();
        RestoreCar();
        DropBoat();
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
