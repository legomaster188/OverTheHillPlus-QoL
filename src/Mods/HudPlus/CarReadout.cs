using System;
using UnityEngine;
using VehiclePhysics;

namespace OverTheHillPlus.HudPlus;

/// <summary>
/// What HUD+ shows about your own car, read once per frame (read-only, any session):
/// - speed, RPM and gear from the vehicle physics' data bus (fixed point x1000), as the driving HUD reads them;
/// - health from CarDamageSystem (DamageAmount out of MaxDamageAmount);
/// - tyres from the vehicle physics' wheel states (on the ground, normalised slip) and the car's
///   NetworkedVehicleStatus.TireWaterLevel;
/// - the winch from VehicleWinchSystem (WinchState, IsTaut, NormalizedTension, RopeLength);
/// - the odometer from the car's Odometer component (mTravelled, metres).
/// The components are looked up once per car (again every 2 s while one is missing, at most 5 times per car).
/// </summary>
internal static class CarReadout
{
    internal struct Tyre
    {
        public float X, Z;
        public TyreGrip Grip;
    }

    public const int MaxTyres = 8;

    public static bool HasCar { get; private set; }
    public static float Kmh { get; private set; }
    public static float Rpm { get; private set; }
    public static float PeakRpm { get; private set; } = 6000f;
    public static int Gear { get; private set; }

    /// <summary>Health 0-100, or -1 when the car has no damage system.</summary>
    public static int Health { get; private set; } = -1;
    /// <summary>VehicleWinchSystem.WinchState as a number, or -1 when the car has no winch.</summary>
    public static int WinchState { get; private set; } = -1;
    public static bool WinchTaut { get; private set; }
    public static float WinchTension { get; private set; }
    public static float RopeLength { get; private set; }
    /// <summary>Metres on the car's odometer, or -1 when it has none.</summary>
    public static float OdometerM { get; private set; } = -1f;
    /// <summary>Tyre water level 0-1, or -1 when unknown.</summary>
    public static float Water { get; private set; } = -1f;

    public static readonly Tyre[] Tyres = new Tyre[MaxTyres];
    public static int TyreCount { get; private set; }

    private static IntPtr _car;
    private static CarDamageSystem _damage;
    private static VehicleWinchSystem _winch;
    private static Odometer _odometer;
    private static NetworkedVehicleStatus _status;
    private const int MaxFindTries = 5;
    private static float _nextFind;
    private static int _findTries;
    private static bool _loggedError;

    internal static void Tick()
    {
        try { Read(); }
        catch (Exception e)
        {
            DebugLog.Error("hudplus", e, "CarReadout.Tick");
            HasCar = false;
            if (_loggedError) return;
            _loggedError = true;
            HudPlusPlugin.Logger.LogWarning("HUD+: reading the car failed (further errors not logged): " + e.Message);
        }
    }

    private static void Read()
    {
        var car = GameState.LocalCar;
        HasCar = car != null;
        if (!HasCar)
        {
            _car = IntPtr.Zero;
            return;
        }
        if (car.Pointer != _car)
        {
            _car = car.Pointer;
            _damage = null;
            _winch = null;
            _odometer = null;
            _status = null;
            _nextFind = 0f;
            _findTries = 0;
            PeakRpm = 6000f;
        }
        Find(car);

        var d = car.data;
        Kmh = Mathf.Abs(d.Get(Channel.Vehicle, VehicleData.Speed) / 1000f) * 3.6f;
        Rpm = d.Get(Channel.Vehicle, VehicleData.EngineRpm) / 1000f;
        Gear = d.Get(Channel.Vehicle, VehicleData.GearboxGear);
        PeakRpm = Mathf.Max(PeakRpm * 0.9995f, Rpm, 3000f);

        bool wantStatus = HudPlusConfig.ShowStatus.Value;
        Health = -1;
        if (wantStatus && _damage != null)
        {
            try { Health = HudMath.HealthPercent(_damage.DamageAmount, _damage.MaxDamageAmount); }
            catch (Exception ex) { DebugLog.Error("hudplus", ex, "CarReadout.Read"); _damage = null; }
        }
        WinchState = -1;
        if (wantStatus && HudPlusConfig.ShowWinch.Value && _winch != null)
        {
            try
            {
                WinchState = (int)_winch.WinchState;
                WinchTaut = _winch.IsTaut;
                WinchTension = _winch.NormalizedTension;
                RopeLength = _winch.RopeLength;
            }
            catch (Exception ex) { DebugLog.Error("hudplus", ex, "CarReadout.Read"); _winch = null; WinchState = -1; }
        }
        OdometerM = -1f;
        if (wantStatus && HudPlusConfig.ShowOdometer.Value && _odometer != null)
        {
            try { OdometerM = _odometer.mTravelled; }
            catch (Exception ex) { DebugLog.Error("hudplus", ex, "CarReadout.Read"); _odometer = null; }
        }
        Water = -1f;
        TyreCount = 0;
        if (wantStatus && HudPlusConfig.ShowTyres.Value)
        {
            if (_status != null)
            {
                try { Water = _status.TireWaterLevel; }
                catch (Exception ex) { DebugLog.Error("hudplus", ex, "CarReadout.Read"); _status = null; }
            }
            ReadTyres(car);
        }
    }

    private static void ReadTyres(VPVehicleController car)
    {
        // Each read of wheelState makes a new interop wrapper: read it once.
        var states = car.wheelState;
        int n = states == null ? 0 : Math.Min(Math.Min(car.wheelCount, states.Length), MaxTyres);
        var ct = car.transform;
        int k = 0;
        for (int i = 0; i < n; i++)
        {
            var ws = states[i];
            if (ws == null || !ws.enabled) continue;
            Vector3 local = Vector3.zero;
            try
            {
                var col = ws.wheelCol;
                if (col != null) local = ct.InverseTransformPoint(col.transform.position);
            }
            catch (Exception ex) { DebugLog.Error("hudplus", ex, "CarReadout.ReadTyres"); }
            Tyres[k++] = new Tyre { X = local.x, Z = local.z, Grip = HudMath.Grip(ws.grounded, ws.normalizedSlip) };
        }
        TyreCount = k;
    }

    private static void Find(VPVehicleController car)
    {
        if (_damage != null && _winch != null && _odometer != null && _status != null) return;
        // A car without a winch (or odometer, ...) stays without one: stop looking after a few tries per car.
        if (_findTries >= MaxFindTries || Time.unscaledTime < _nextFind) return;
        _findTries++;
        _nextFind = Time.unscaledTime + 2f;
        if (_damage == null) _damage = Look<CarDamageSystem>(car);
        if (_winch == null) _winch = Look<VehicleWinchSystem>(car);
        if (_odometer == null) _odometer = Look<Odometer>(car);
        if (_status == null) _status = Look<NetworkedVehicleStatus>(car);
    }

    /// <summary>A component on the car, its children or its parents.</summary>
    private static T Look<T>(Component car) where T : Component
    {
        try
        {
            var c = car.GetComponent<T>();
            if (c == null) c = car.GetComponentInChildren<T>();
            if (c == null) c = car.GetComponentInParent<T>();
            return c;
        }
        catch (Exception ex) { DebugLog.Error("hudplus", ex, "CarReadout.Look"); return null; }
    }
}
