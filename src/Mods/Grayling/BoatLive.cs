using System;
using OverTheHillPlus.Cars;
using UnityEngine;
#if MELON
using Il2CppVehiclePhysics;
#else
using VehiclePhysics;
#endif

namespace OverTheHillPlus.Grayling;

/// <summary>
/// One Grayling as this game draws it (every peer, every boat): the outboard turns on its swivel with the steering, its
/// propeller spins with the throttle, and out of the water (on the shore, on its trailer) the motor is tilted up on its
/// clamp so the leg doesn't dig into the ground. On your own boat (and every boat on the host) the inputs are known; on
/// other players' boats the motor's angle comes from how the boat turns, the propeller from its speed and the tilt from
/// the water under the hull. Nothing is sent.
/// </summary>
internal sealed class BoatLive
{
    public readonly VehicleBase Car;
    private readonly Transform _t, _motor, _prop, _tiller;
    private readonly Quaternion _motorRest, _propRest, _tillerRest;
    private readonly Vector3 _motorPos, _tiltPivot;
    private float _angle, _spin, _spinSpeed, _tilt;
    private Vector3 _lastPos;
    private Quaternion _lastRot;
    private bool _hasLast;
    private float _nextWaterCheck;
    private bool _wetGuess = true;

    /// <summary>How far the motor tilts up out of the water, degrees (<see cref="MotorTilt"/>).</summary>
    public const float TiltUp = MotorTilt.Up;

    public BoatLive(VehicleBase car, Vector3 offset)
    {
        Car = car;
        _t = car.transform;
        _motor = CarMovingParts.Find(_t, BoatRig.MotorPart);
        _prop = _motor != null ? _motor.Find(BoatRig.PropPart) : null;
        if (_motor != null)
        {
            _motorRest = _motor.localRotation;
            _motorPos = _motor.localPosition;
        }
        if (_prop != null) _propRest = _prop.localRotation;
        _tiller = _motor != null ? _motor.Find(BoatRig.TillerPart) : null;
        if (_tiller != null) _tillerRest = _tiller.localRotation;
        var tp = BoatRig.TiltPivot;
        _tiltPivot = new Vector3(tp.X, tp.Y, tp.Z) + offset;
    }

    public bool Alive
    {
        get { try { return Car != null && _t != null; } catch (Exception ex) { DebugLog.Error(GraylingPlugin.ModId, ex, "BoatLive.Alive"); return false; } }
    }

    /// <summary>One frame. <paramref name="state"/>: the physics step's numbers where this game simulates the boat, else null.</summary>
    public void Frame(float dt, BoatPhysics.State state)
    {
        if (!(dt > 0f) || _motor == null) return;
        var pos = _t.position;
        var rot = _t.rotation;
        float speed = 0f, yawRate = 0f;
        if (_hasLast)
        {
            speed = Vector3.Dot((pos - _lastPos) / dt, _t.forward);
            yawRate = Mathf.DeltaAngle(0f, (Quaternion.Inverse(_lastRot) * rot).eulerAngles.y) / dt;
        }
        _lastPos = pos;
        _lastRot = rot;
        _hasLast = true;

        float angle, spinTarget;
        bool afloat;
        if (state != null && Time.fixedTime - state.At < 0.5f)
        {
            angle = BoatHull.MotorAngle(state.Steer);
            // a 15 hp outboard's propeller: shown far slower than its real 5000 rpm (a blur at the real rate)
            spinTarget = state.PropWet ? (state.Throttle - state.Reverse) * 900f : 0f;
            afloat = state.Floating >= BoatHull.FloatingShare * 0.5f;
        }
        else
        {
            // a boat turns about 0.9 rad/s at full lock at speed: the motor's angle from the turn, the propeller from the speed
            angle = Mathf.Abs(speed) > 0.6f ? Mathf.Clamp(yawRate / 50f, -1f, 1f) * BoatHull.MaxSteer : 0f;
            spinTarget = speed * 150f;
            afloat = WetGuess(pos);
        }
        _angle = Mathf.Lerp(_angle, angle, 1f - Mathf.Exp(-dt / 0.12f));
        _spinSpeed = Mathf.Lerp(_spinSpeed, spinTarget, 1f - Mathf.Exp(-dt / 0.25f));
        _spin = (_spin + _spinSpeed * dt) % 360f;
        _tilt = Mathf.MoveTowards(_tilt, afloat ? 0f : TiltUp, dt * 45f);

        // the swivel turns the motor about its upright axis (+ = the propeller pushes the stern left); the tilt swings the
        // whole motor up and back about the clamp's pin, and the tiller folds up with it
        var tilt = Quaternion.AngleAxis(MotorTilt.MotorAngle(_tilt), Vector3.right);
        _motor.localPosition = _tiltPivot + tilt * (_motorPos - _tiltPivot);
        _motor.localRotation = tilt * Quaternion.AngleAxis(-_angle, Vector3.up) * _motorRest;
        if (_prop != null) _prop.localRotation = _propRest * Quaternion.AngleAxis(_spin, Vector3.forward);
        if (_tiller != null) _tiller.localRotation = Quaternion.AngleAxis(MotorTilt.TillerAngle(_tilt), Vector3.right) * _tillerRest;
    }

    /// <summary>Is there water under the hull (other players' boats: twice a second)?</summary>
    private bool WetGuess(Vector3 pos)
    {
        if (Time.unscaledTime < _nextWaterCheck) return _wetGuess;
        _nextWaterCheck = Time.unscaledTime + 0.5f;
        float w = BoatPhysics.WaterAt(pos);
        _wetGuess = !float.IsNaN(w) && w > pos.y + GraylingCore.Offset.y - 0.05f;
        return _wetGuess;
    }
}
