using System;
using UnityEngine;
using VehiclePhysics;

namespace OverTheHillPlus;

/// <summary>
/// The local car's jumps, for the mods that need them (Trip Stats counts them, Auto Screenshot takes pictures of them)
/// without referencing each other. A mod calls <see cref="Use"/> once; from then on core samples the local car once
/// per frame into a <see cref="JumpDetector"/>. Read-only: nothing is changed on the car and nothing is sent.
///
/// "On the ground" is the vehicle physics' own wheel contact (VehicleBase.wheelState[i].grounded) for every enabled
/// wheel. When the car has no wheel state (it should always have one), NetworkedVehicleStatus.AllWheelsAirborne is
/// used instead.
/// </summary>
internal static class CarAir
{
    /// <summary>A jump of at least <see cref="JumpDetector.MinAirTime"/> just ended (main thread).</summary>
    internal static Action<JumpDetector.Landing> Landed;

    private static readonly JumpDetector Detector = new();
    private static bool _installed;
    private static IntPtr _carPtr;
    private static NetworkedVehicleStatus _status;
    private static bool _loggedError;

    public static bool InAir => Detector.InAir;
    public static float AirTime => Detector.AirTime;
    public static float TumbleDegrees => Detector.TumbleDegrees;
    public static int FlipsSoFar => JumpDetector.FlipsFor(Detector.TumbleDegrees);
    /// <summary>Counts take-offs: compare it to do something once per jump.</summary>
    public static int JumpNumber => Detector.JumpNumber;
    /// <summary>Wheels on the ground in the last sample (-1 when there was no car).</summary>
    public static int GroundedWheels { get; private set; } = -1;

    /// <summary>Starts sampling (once; later calls do nothing).</summary>
    internal static void Use()
    {
        if (_installed) return;
        _installed = true;
        CoreBehaviour.BeforeFeatureTicks += Tick;
    }

    private static void Tick()
    {
        try { Sample(); }
        catch (Exception e)
        {
            DebugLog.Error("core", e, "CarAir.Tick");
            Detector.Reset();
            if (_loggedError) return;
            _loggedError = true;
            Core.CorePlugin.Logger?.LogWarning("[CarAir] sampling the car failed (further errors not logged): " + e.Message);
        }
    }

    private static void Sample()
    {
        var car = GameState.LocalCar;
        if (car == null)
        {
            if (_carPtr != IntPtr.Zero) { _carPtr = IntPtr.Zero; _status = null; Detector.Reset(); }
            GroundedWheels = -1;
            return;
        }
        if (car.Pointer != _carPtr)
        {
            _carPtr = car.Pointer;
            _status = null;
            Detector.Reset();
        }

        bool grounded = Grounded(car, out int wheels);
        GroundedWheels = wheels;
        var t = car.transform;
        var p = t.position;
        var up = t.up;
        if (Detector.Step(Time.deltaTime, grounded, p.x, p.y, p.z, up.x, up.y, up.z, out var landing))
            Hooks.InvokeEach(Landed, landing, e => Core.CorePlugin.Logger?.LogError("[CarAir] landing hook: " + e));
    }

    private static bool Grounded(VPVehicleController car, out int groundedWheels)
    {
        groundedWheels = 0;
        int enabled = 0;
        // Each read of wheelState makes a new interop wrapper: read it once.
        var states = car.wheelState;
        int n = states == null ? 0 : Math.Min(car.wheelCount, states.Length);
        for (int i = 0; i < n; i++)
        {
            var ws = states[i];
            if (ws == null || !ws.enabled) continue;
            enabled++;
            if (ws.grounded) groundedWheels++;
        }
        if (enabled > 0) return groundedWheels > 0;

        // No wheel state: the car's networked status says whether all wheels are off the ground.
        if (_status == null)
        {
            _status = car.GetComponent<NetworkedVehicleStatus>();
            if (_status == null) _status = car.GetComponentInParent<NetworkedVehicleStatus>();
        }
        if (_status == null) return true;
        bool air = _status.AllWheelsAirborne;
        groundedWheels = air ? 0 : 1;
        return !air;
    }
}
