using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;
using OverTheHillPlus.CheatsPlus;
using OverTheHillPlus.UI;
using UnityEngine;
using VehiclePhysics;

namespace OverTheHillPlus.Features.Cheats.Chassis;

/// <summary>
/// Chassis tuning for your own car: suspension (springs, dampers, travel, ride height), weight and centre of mass,
/// brakes (torque, bias, handbrake), steering lock, rolling resistance / tire pressure and differential locks.
///
/// How it stays safe:
/// - Only where this game simulates your car (<see cref="GameState.PhysicsAllowed"/>: solo or host). As a guest the
///   host's physics would overwrite it anyway, so everything is put back instead.
/// - Every value is remembered the first time it is changed and written back when the master switch is turned off,
///   a slider returns to neutral, you change car, or you join someone else's session.
/// - If the game changes a value itself (an upgrade being fitted, a car swap) the new value becomes the new stock
///   value and the tuning is re-applied on top of it, so upgrades are never lost.
///
/// Facts this relies on (from the game's code):
/// - VPFunselektor3D(Raycast)WheelCollider / VPStandardWheelCollider keep suspensionTravel, springRate, damperRate and
///   center as plain fields that RefreshPhysics() copies into the live collider (the game's suspension lift upgrade
///   writes suspensionTravel the same way).
/// - VPVehicleController.DoUpdateBlocks reads Channel.Settings (DifferentialLock, Front/RearDifferentialLock,
///   DrivelineLock) from the data bus every physics step and pushes them into the driveline overrides, so a lock is
///   written there right before it is read.
/// - Brakes and Steering read their Settings objects every step; Inertia.DoUpdate re-derives the inertia tensor when
///   the rigidbody's mass changes.
/// </summary>
internal sealed class ChassisTuning : IFeature
{
    private const string Sec = "Chassis Tuning";

    internal static ConfigEntry<bool> Enabled;
    internal static ConfigEntry<float> SpringMult, DamperMult, TravelMult, RideHeight;
    internal static ConfigEntry<float> MassMult, ComHeight, ComForward;
    internal static ConfigEntry<float> BrakeMult, HandbrakeMult, BrakeBias, SteerMult;
    internal static ConfigEntry<bool> BiasOverride, LockAxleDiffs, LockCenterDiff;
    internal static ConfigEntry<float> RollingMult, TirePressureMult;

    public void Register()
    {
        var f = CheatsConfig.File;
        Enabled = f.Bind(Sec, "Enabled", false, "Master switch for chassis tuning (solo/host only). Off restores the car's own values.");
        SpringMult = f.Bind(Sec, "SpringStiffness", 1f, "Suspension spring rate multiplier.");
        DamperMult = f.Bind(Sec, "Damping", 1f, "Suspension damper rate multiplier.");
        TravelMult = f.Bind(Sec, "SuspensionTravel", 1f, "Suspension travel multiplier.");
        RideHeight = f.Bind(Sec, "RideHeight", 0f, "Ride height offset in metres (positive = higher).");
        MassMult = f.Bind(Sec, "Mass", 1f, "Car mass multiplier.");
        ComHeight = f.Bind(Sec, "CenterOfMassHeight", 0f, "Centre of mass vertical offset in metres (negative = lower, more stable).");
        ComForward = f.Bind(Sec, "CenterOfMassForward", 0f, "Centre of mass forward offset in metres (positive = towards the front).");
        BrakeMult = f.Bind(Sec, "BrakeTorque", 1f, "Brake torque multiplier.");
        HandbrakeMult = f.Bind(Sec, "HandbrakeTorque", 1f, "Handbrake torque multiplier.");
        BiasOverride = f.Bind(Sec, "OverrideBrakeBias", false, "Replace the car's brake bias with BrakeBias.");
        BrakeBias = f.Bind(Sec, "BrakeBias", 0.6f, "Brake bias (0.5 = balanced, higher = more front brake).");
        SteerMult = f.Bind(Sec, "SteeringAngle", 1f, "Maximum steering angle multiplier.");
        LockAxleDiffs = f.Bind(Sec, "LockAxleDifferentials", false, "Force the front and rear axle differentials locked.");
        LockCenterDiff = f.Bind(Sec, "LockCenterDifferential", false, "Force the centre differential / driveline locked (4x4 cars).");
        RollingMult = f.Bind(Sec, "RollingResistance", 1f, "Rolling resistance multiplier (coasting drag of the tires).");
        TirePressureMult = f.Bind(Sec, "TirePressure", 1f, "Tire pressure multiplier (only on cars whose rolling friction uses the tire pressure model).");

        Registry.OnTick(Tick);
        Registry.Section(Registry.PlusMode, "Tuning", DrawMain, 10);
        Registry.Section(Registry.PlusMode, "Tuning", DrawSuspension, 11);
        Registry.Section(Registry.PlusMode, "Tuning", DrawWeight, 12);
        Registry.Section(Registry.PlusMode, "Tuning", DrawBrakesSteering, 13);
        Registry.Section(Registry.PlusMode, "Tuning", DrawDrivetrain, 14);
        Registry.Section(Registry.PlusMode, "Tuning", DrawReadout, 15);
    }

    // ------------------------------------------------------------------ remembered values

    /// <summary>One game value we changed: its stock value and what we last wrote, so it can be put back.</summary>
    private sealed class Slot
    {
        public UnityEngine.Object Life;   // the Unity object that owns the value (null check = destroyed)
        public Func<float> Get;
        public Action<float> Set;
        public Action AfterWrite;         // e.g. WheelColliderBehaviour.RefreshPhysics
        public float Original;
        public float Written;

        public void Restore()
        {
            try
            {
                if (Life == null) return; // Unity null: destroyed with its car, nothing to restore
                if (Same(Get(), Written))
                {
                    Set(Original);
                    AfterWrite?.Invoke();
                }
            }
            catch (Exception e) { DebugLog.Error("cheats", e, "Slot.Restore"); LogOnce("restore", e); }
        }
    }

    private static readonly Dictionary<string, Slot> Slots = new();
    private static readonly HashSet<string> Seen = new();

    private static bool Same(float a, float b) => Math.Abs(a - b) <= 1e-4f * Math.Max(1f, Math.Abs(a));
    private static bool Neutral(ConfigEntry<float> e, float neutral) => Math.Abs(e.Value - neutral) < 0.005f;

    /// <summary>
    /// Drives one value towards <paramref name="target"/>(stock), or back to stock when <paramref name="neutral"/>.
    /// Returns true when it wrote something.
    /// </summary>
    private static bool Apply(string key, UnityEngine.Object life, Func<float> get, Action<float> set,
        Func<float, float> target, bool neutral, Action afterWrite = null)
    {
        Slots.TryGetValue(key, out var s);
        if (neutral)
        {
            if (s == null) return false;
            s.Restore();
            Slots.Remove(key);
            return true;
        }

        float cur = get();
        if (s == null)
        {
            s = new Slot { Life = life, Get = get, Set = set, AfterWrite = afterWrite, Original = cur, Written = cur };
            Slots[key] = s;
        }
        else if (!Same(cur, s.Written))
        {
            // The game changed it (upgrade fitted, respawn...): that is the new stock value.
            s.Original = cur;
        }

        float want = target(s.Original);
        s.Written = want;
        if (Same(cur, want)) return false;
        set(want);
        afterWrite?.Invoke();
        return true;
    }

    private static void RestoreAll()
    {
        if (Slots.Count == 0) return;
        foreach (var s in Slots.Values) s.Restore();
        Slots.Clear();
    }

    private static void LogOnce(string what, Exception e)
    {
        if (Seen.Add(what)) CheatsPlugin.Logger.LogWarning($"Chassis tuning ({what}) failed, further errors suppressed: {e.Message}");
    }

    // ------------------------------------------------------------------ wheel collider access

    /// <summary>The three wheel collider front-ends the game uses share the same field names; this hides which one.</summary>
    private sealed class WheelRef
    {
        public readonly WheelColliderBehaviour Behaviour;
        private readonly VPFunselektor3DRaycastWheelCollider _r;
        private readonly VPFunselektor3DWheelCollider _f;
        private readonly VPStandardWheelCollider _s;

        public WheelRef(WheelColliderBehaviour b)
        {
            Behaviour = b;
            _r = b.TryCast<VPFunselektor3DRaycastWheelCollider>();
            if (_r == null) _f = b.TryCast<VPFunselektor3DWheelCollider>();
            if (_r == null && _f == null) _s = b.TryCast<VPStandardWheelCollider>();
        }

        public bool Valid => _r != null || _f != null || _s != null;
        public string Kind => _r != null ? "Funselektor raycast" : _f != null ? "Funselektor 3D" : _s != null ? "VPP standard" : "unsupported";

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

        /// <summary>Local Y of the wheel centre. Moving it down lifts the body by the same amount.</summary>
        public float CenterY
        {
            get => (_r != null ? _r.center : _f != null ? _f.center : _s.center).y;
            set
            {
                if (_r != null) { var c = _r.center; c.y = value; _r.center = c; }
                else if (_f != null) { var c = _f.center; c.y = value; _f.center = c; }
                else { var c = _s.center; c.y = value; _s.center = c; }
            }
        }

        public void Refresh()
        {
            try { Behaviour.RefreshPhysics(); }
            catch (Exception e) { DebugLog.Error("cheats", e, "WheelRef.Refresh"); LogOnce("RefreshPhysics", e); }
        }
    }

    private static readonly List<WheelRef> _wheels = new();
    private static IntPtr _carPtr;
    private static float _nextTick, _nextWheelScan;
    private static VPRollingFriction _rolling;

    private static void ScanCar(VPVehicleController car)
    {
        _wheels.Clear();
        _rolling = null;
        try
        {
            var states = car.wheelState;
            int n = states == null ? 0 : Math.Min(car.wheelCount, states.Length);
            for (int i = 0; i < n; i++)
            {
                var col = states[i]?.wheelCol;
                if (col == null) continue;
                var w = new WheelRef(col);
                if (w.Valid) _wheels.Add(w);
            }
        }
        catch (Exception e) { DebugLog.Error("cheats", e, "ChassisTuning.ScanCar"); LogOnce("wheel scan", e); }
        try
        {
            _rolling = car.GetComponent<VPRollingFriction>();
            if (_rolling == null) _rolling = car.GetComponentInChildren<VPRollingFriction>();
        }
        catch (Exception e) { DebugLog.Error("cheats", e, "ChassisTuning.ScanCar"); LogOnce("rolling friction lookup", e); }
    }

    // ------------------------------------------------------------------ per frame

    internal static bool Active => Enabled != null && Enabled.Value && GameState.PhysicsAllowed && GameState.LocalCar != null;

    private static void Tick()
    {
        if (Time.unscaledTime < _nextTick) return;
        _nextTick = Time.unscaledTime + 0.25f;

        var car = GameState.LocalCar;
        IntPtr ptr = car == null ? IntPtr.Zero : car.Pointer;
        if (ptr != _carPtr)
        {
            RestoreAll(); // best effort on the old car (skipped if it was destroyed)
            _wheels.Clear();
            _rolling = null;
            _carPtr = ptr;
            _nextWheelScan = 0f;
        }

        if (car != null && Time.unscaledTime >= _nextWheelScan)
        {
            _nextWheelScan = Time.unscaledTime + 2f;
            ScanCar(car);
        }

        if (!Active)
        {
            RestoreAll();
            Readout.Clear();
            if (car != null) Capture(car);
            return;
        }

        bool any = false;
        try { any |= ApplySuspension(); } catch (Exception e) { DebugLog.Error("cheats", e, "ChassisTuning.Tick"); LogOnce("suspension", e); }
        try { any |= ApplyWeight(car); } catch (Exception e) { DebugLog.Error("cheats", e, "ChassisTuning.Tick"); LogOnce("weight", e); }
        try { any |= ApplyBrakesSteering(car); } catch (Exception e) { DebugLog.Error("cheats", e, "ChassisTuning.Tick"); LogOnce("brakes/steering", e); }
        try { any |= ApplyRolling(); } catch (Exception e) { DebugLog.Error("cheats", e, "ChassisTuning.Tick"); LogOnce("rolling friction", e); }
        if (any || Slots.Count > 0 || LockAxleDiffs.Value || LockCenterDiff.Value) OverTheHillPlus.AdvantageFlag.MarkUsed();

        Capture(car);
    }

    private static bool ApplySuspension()
    {
        bool any = false;
        bool nSpring = Neutral(SpringMult, 1f), nDamp = Neutral(DamperMult, 1f), nTravel = Neutral(TravelMult, 1f), nRide = Neutral(RideHeight, 0f);
        float spring = Mathf.Clamp(SpringMult.Value, 0.2f, 4f);
        float damp = Mathf.Clamp(DamperMult.Value, 0.2f, 4f);
        float travel = Mathf.Clamp(TravelMult.Value, 0.3f, 3f);
        float ride = Mathf.Clamp(RideHeight.Value, -0.3f, 0.6f);

        foreach (var w in _wheels)
        {
            var b = w.Behaviour;
            if (b == null) continue;
            string k = b.Pointer.ToString();
            Action refresh = w.Refresh;
            any |= Apply(k + ":spring", b, () => w.Spring, v => w.Spring = v, o => o * spring, nSpring, refresh);
            any |= Apply(k + ":damper", b, () => w.Damper, v => w.Damper = v, o => o * damp, nDamp, refresh);
            any |= Apply(k + ":travel", b, () => w.Travel, v => w.Travel = v, o => o * travel, nTravel, refresh);
            any |= Apply(k + ":ride", b, () => w.CenterY, v => w.CenterY = v, o => o - ride, nRide, refresh);
        }
        return any;
    }

    private static bool ApplyWeight(VPVehicleController car)
    {
        var rb = car.cachedRigidbody;
        if (rb == null) return false;
        string k = rb.Pointer.ToString();
        bool any = false;
        float mass = Mathf.Clamp(MassMult.Value, 0.1f, 5f);
        float comY = Mathf.Clamp(ComHeight.Value, -1f, 1f);
        float comZ = Mathf.Clamp(ComForward.Value, -2f, 2f);

        any |= Apply(k + ":mass", rb, () => rb.mass, v => rb.mass = v, o => Mathf.Max(1f, o * mass), Neutral(MassMult, 1f));
        any |= Apply(k + ":comY", rb, () => rb.centerOfMass.y, v => { var c = rb.centerOfMass; c.y = v; rb.centerOfMass = c; },
            o => o + comY, Neutral(ComHeight, 0f));
        any |= Apply(k + ":comZ", rb, () => rb.centerOfMass.z, v => { var c = rb.centerOfMass; c.z = v; rb.centerOfMass = c; },
            o => o + comZ, Neutral(ComForward, 0f));
        return any;
    }

    private static Brakes.Settings BrakeSettings(VPVehicleController car)
    {
        Brakes.Settings s = null;
        try { s = car.m_brakes?.settings; } catch (Exception ex) { DebugLog.Error("cheats", ex, "ChassisTuning.BrakeSettings"); }
        if (s == null) try { s = car.brakes; } catch (Exception ex) { DebugLog.Error("cheats", ex, "ChassisTuning.BrakeSettings"); }
        return s;
    }

    private static Steering.Settings SteerSettings(VPVehicleController car)
    {
        Steering.Settings s = null;
        try { s = car.m_steering?.settings; } catch (Exception ex) { DebugLog.Error("cheats", ex, "ChassisTuning.SteerSettings"); }
        if (s == null) try { s = car.steering; } catch (Exception ex) { DebugLog.Error("cheats", ex, "ChassisTuning.SteerSettings"); }
        return s;
    }

    private static bool ApplyBrakesSteering(VPVehicleController car)
    {
        bool any = false;
        var b = BrakeSettings(car);
        if (b != null)
        {
            string k = b.Pointer.ToString();
            float bm = Mathf.Clamp(BrakeMult.Value, 0f, 5f), hm = Mathf.Clamp(HandbrakeMult.Value, 0f, 5f);
            float bias = Mathf.Clamp(BrakeBias.Value, 0.1f, 0.9f);
            any |= Apply(k + ":brake", car, () => b.maxBrakeTorque, v => b.maxBrakeTorque = v, o => o * bm, Neutral(BrakeMult, 1f));
            any |= Apply(k + ":handbrake", car, () => b.handbrakeTorque, v => b.handbrakeTorque = v, o => o * hm, Neutral(HandbrakeMult, 1f));
            any |= Apply(k + ":bias", car, () => b.brakeBias, v => b.brakeBias = v, _ => bias, !BiasOverride.Value);
        }

        var s = SteerSettings(car);
        if (s != null)
        {
            float sm = Mathf.Clamp(SteerMult.Value, 0.3f, 2.5f);
            any |= Apply(s.Pointer + ":steer", car, () => s.maxSteerAngle, v => s.maxSteerAngle = v,
                o => Mathf.Clamp(o * sm, 1f, 80f), Neutral(SteerMult, 1f));
        }
        return any;
    }

    private static bool ApplyRolling()
    {
        var r = _rolling;
        if (r == null) return false;
        string k = r.Pointer.ToString();
        bool any = false;
        float rm = Mathf.Clamp(RollingMult.Value, 0f, 5f);
        bool nRoll = Neutral(RollingMult, 1f);
        any |= Apply(k + ":rollC", r, () => r.constantCoefficient, v => r.constantCoefficient = v, o => o * rm, nRoll);
        any |= Apply(k + ":rollL", r, () => r.linearCoefficient, v => r.linearCoefficient = v, o => o * rm, nRoll);
        bool pressureModel = false;
        try { pressureModel = r.dynamicModel == VPRollingFriction.Model.TirePressure; } catch (Exception ex) { DebugLog.Error("cheats", ex, "ChassisTuning.ApplyRolling"); }
        float pm = Mathf.Clamp(TirePressureMult.Value, 0.3f, 2f);
        any |= Apply(k + ":pressure", r, () => r.tirePressure, v => r.tirePressure = v, o => o * pm,
            Neutral(TirePressureMult, 1f) || !pressureModel);
        return any;
    }

    // ------------------------------------------------------------------ differential locks (every physics step)

    private static bool _diffWritten;
    private static IntPtr _diffCar;
    private static readonly int[] DiffOriginal = new int[4];

    private static int[] DiffIds() => new[]
    {
        SettingsData.DifferentialLock, SettingsData.FrontDifferentialLock,
        SettingsData.RearDifferentialLock, SettingsData.DrivelineLock
    };

    private static int[] _diffIds;

    /// <summary>Called right before VPVehicleController.DoUpdateBlocks reads the data bus.</summary>
    internal static void PhysicsStep(VPVehicleController v)
    {
        if (!GameState.IsLocalCar(v)) return;
        bool axle = Active && LockAxleDiffs.Value;
        bool center = Active && LockCenterDiff.Value;
        var data = v.data;
        if (data == null) return;
        _diffIds ??= DiffIds();
        int ch = Channel.Settings;

        if (!axle && !center)
        {
            if (!_diffWritten) return;
            if (_diffCar == v.Pointer)
                for (int i = 0; i < 4; i++) data.Set(ch, _diffIds[i], DiffOriginal[i]);
            _diffWritten = false;
            return;
        }

        if (!_diffWritten || _diffCar != v.Pointer)
        {
            for (int i = 0; i < 4; i++) DiffOriginal[i] = data.Get(ch, _diffIds[i]);
            _diffWritten = true;
            _diffCar = v.Pointer;
        }

        // 1 = Differential.Override.ForceLocked / Driveline.Override.ForceLocked.
        for (int i = 0; i < 3; i++) data.Set(ch, _diffIds[i], axle ? 1 : DiffOriginal[i]);
        data.Set(ch, _diffIds[3], center ? 1 : DiffOriginal[3]);
    }

    // ------------------------------------------------------------------ readout (cached for the menu)

    private static class Readout
    {
        public static string Car = "-", Mass = "-", Com = "-", Spring = "-", Damper = "-", Travel = "-", Ride = "-";
        public static string Brake = "-", Bias = "-", Steer = "-", Rolling = "-", Diffs = "-", Collider = "-";
        public static int Wheels;

        public static void Clear()
        {
            Car = Mass = Com = Spring = Damper = Travel = Ride = Brake = Bias = Steer = Rolling = Diffs = Collider = "-";
            Wheels = 0;
        }
    }

    private static float Stock(string key, float current) => Slots.TryGetValue(key, out var s) ? s.Original : current;

    private static string Pair(float stock, float now, string fmt, string unit)
    {
        string a = stock.ToString(fmt) + unit;
        return Same(stock, now) ? a : $"{a}  →  {now.ToString(fmt)}{unit}";
    }

    private static void Capture(VPVehicleController car)
    {
        try
        {
            Readout.Car = car.name ?? "-";
            var rb = car.cachedRigidbody;
            if (rb != null)
            {
                string k = rb.Pointer.ToString();
                Readout.Mass = Pair(Stock(k + ":mass", rb.mass), rb.mass, "0", " kg");
                var c = rb.centerOfMass;
                Readout.Com = $"height {c.y:0.00} m, fwd {c.z:0.00} m";
            }

            Readout.Wheels = _wheels.Count;
            if (_wheels.Count > 0 && _wheels[0].Behaviour != null)
            {
                var w = _wheels[0];
                string k = w.Behaviour.Pointer.ToString();
                Readout.Collider = w.Kind;
                Readout.Spring = Pair(Stock(k + ":spring", w.Spring), w.Spring, "0", " N/m");
                Readout.Damper = Pair(Stock(k + ":damper", w.Damper), w.Damper, "0", " Ns/m");
                Readout.Travel = Pair(Stock(k + ":travel", w.Travel), w.Travel, "0.000", " m");
                float stockY = Stock(k + ":ride", w.CenterY);
                Readout.Ride = Same(stockY, w.CenterY) ? "stock" : $"{stockY - w.CenterY:+0.00;-0.00} m";
            }

            var b = BrakeSettings(car);
            if (b != null)
            {
                string k = b.Pointer.ToString();
                Readout.Brake = Pair(Stock(k + ":brake", b.maxBrakeTorque), b.maxBrakeTorque, "0", " Nm");
                Readout.Bias = Pair(Stock(k + ":bias", b.brakeBias) * 100f, b.brakeBias * 100f, "0", "% front");
            }

            var s = SteerSettings(car);
            if (s != null) Readout.Steer = Pair(Stock(s.Pointer + ":steer", s.maxSteerAngle), s.maxSteerAngle, "0.0", "°");

            if (_rolling != null)
            {
                string model = "?";
                try { model = _rolling.dynamicModel.ToString(); } catch (Exception ex) { DebugLog.Error("cheats", ex, "ChassisTuning.Capture"); }
                Readout.Rolling = $"{model}, coef {_rolling.constantCoefficient:0.000}, {_rolling.tirePressure:0.00} bar";
            }
            else Readout.Rolling = "none on this car";

            var data = car.data;
            if (data != null)
            {
                _diffIds ??= DiffIds();
                int ch = Channel.Settings;
                Readout.Diffs = $"axle {Lock(data.Get(ch, _diffIds[0]))}, centre {Lock(data.Get(ch, _diffIds[3]))}";
            }
        }
        catch (Exception e) { DebugLog.Error("cheats", e, "ChassisTuning.Capture"); LogOnce("readout", e); }
    }

    private static string Lock(int v) => v switch { 0 => "auto", 1 => "locked", 2 => "open", _ => v.ToString() };

    // ------------------------------------------------------------------ presets

    private static void Preset(float spring, float damp, float travel, float ride, float mass, float comY, float comZ,
        float brake, float hand, float steer, bool lockAxle, bool lockCenter, float rolling, float pressure)
    {
        SpringMult.Value = spring; DamperMult.Value = damp; TravelMult.Value = travel; RideHeight.Value = ride;
        MassMult.Value = mass; ComHeight.Value = comY; ComForward.Value = comZ;
        BrakeMult.Value = brake; HandbrakeMult.Value = hand; SteerMult.Value = steer;
        LockAxleDiffs.Value = lockAxle; LockCenterDiff.Value = lockCenter;
        RollingMult.Value = rolling; TirePressureMult.Value = pressure;
        BiasOverride.Value = false;
    }

    // ------------------------------------------------------------------ menu

    private static bool CanEdit => GameState.PhysicsAllowed;

    private static void DrawMain()
    {
        Menu.Card("Chassis tuning", "Suspension, weight, brakes, steering and diff locks for your car. Solo or host only.", () =>
        {
            if (!GameState.PhysicsAllowed)
                Menu.Banner("Blocked: the host simulates your car in this session, so tuning is paused and your car is stock.", Theme.Warn);
            else if (GameState.LocalCar == null)
                Menu.Small("No car yet: settings apply as soon as you're driving.");

            Menu.Switch(Enabled, "Enable chassis tuning", "Off puts every value back to the car's own", CanEdit);
            Menu.Chips(3,
                ("Stock", () => { Preset(1, 1, 1, 0, 1, 0, 0, 1, 1, 1, false, false, 1, 1); Menu.SetStatus("Tuning reset to stock."); }, true),
                ("Rock crawler", () => { Preset(0.7f, 1.2f, 1.5f, 0.12f, 1f, -0.25f, 0f, 1.3f, 1f, 1.25f, true, true, 1f, 0.7f); Enabled.Value = true; Menu.SetStatus("Rock crawler setup applied."); }, CanEdit),
                ("Rally", () => { Preset(1.6f, 1.4f, 0.9f, -0.04f, 0.9f, -0.15f, 0f, 1.5f, 2f, 1.15f, false, false, 0.8f, 1f); Enabled.Value = true; Menu.SetStatus("Rally setup applied."); }, CanEdit),
                ("Low & stable", () => { Preset(1.3f, 1.3f, 0.8f, -0.08f, 1f, -0.4f, 0f, 1.2f, 1f, 1f, false, false, 1f, 1f); Enabled.Value = true; Menu.SetStatus("Low & stable setup applied."); }, CanEdit),
                ("Monster lift", () => { Preset(0.9f, 1.1f, 1.8f, 0.35f, 1.3f, -0.3f, 0f, 1.5f, 1f, 1.1f, true, true, 1f, 1f); Enabled.Value = true; Menu.SetStatus("Monster lift setup applied."); }, CanEdit),
                ("Feather", () => { Preset(0.8f, 0.9f, 1.2f, 0.05f, 0.5f, -0.2f, 0f, 1f, 1f, 1.1f, false, false, 0.5f, 1f); Enabled.Value = true; Menu.SetStatus("Feather setup applied."); }, CanEdit));
        });
    }

    private static bool Editable => CanEdit && Enabled.Value;

    private static void DrawSuspension()
    {
        Menu.Card("Suspension setup", "Multipliers on each wheel's stock values. Very stiff + low damping can bounce.", () =>
        {
            bool on = Editable;
            Menu.SliderRow(SpringMult, "Spring stiffness", 0.2f, 4f, "0.00", "×", on);
            Menu.SliderRow(DamperMult, "Damping", 0.2f, 4f, "0.00", "×", on);
            Menu.SliderRow(TravelMult, "Suspension travel", 0.3f, 3f, "0.00", "×", on);
            Menu.SliderRow(RideHeight, "Ride height", -0.3f, 0.6f, "+0.00;-0.00;0.00", " m", on);
        });
    }

    private static void DrawWeight()
    {
        Menu.Card("Weight & balance", "Heavier = more momentum and grip load; lower centre of mass = harder to roll.", () =>
        {
            bool on = Editable;
            Menu.SliderRow(MassMult, "Mass", 0.1f, 5f, "0.00", "×", on);
            Menu.SliderRow(ComHeight, "Centre of mass height", -1f, 1f, "+0.00;-0.00;0.00", " m", on);
            Menu.SliderRow(ComForward, "Centre of mass forward", -2f, 2f, "+0.00;-0.00;0.00", " m", on);
        });
    }

    private static void DrawBrakesSteering()
    {
        Menu.Card("Brakes & steering", null, () =>
        {
            bool on = Editable;
            Menu.SliderRow(BrakeMult, "Brake torque", 0f, 5f, "0.00", "×", on);
            Menu.SliderRow(HandbrakeMult, "Handbrake torque", 0f, 5f, "0.00", "×", on);
            Menu.Switch(BiasOverride, "Override brake bias", "Use the slider below instead of the car's own balance", on);
            Menu.SliderRow(BrakeBias, "Front brake bias", 0.1f, 0.9f, "0.00", "", on && BiasOverride.Value, percent: true);
            Menu.SliderRow(SteerMult, "Steering angle", 0.3f, 2.5f, "0.00", "×", on);
        });
    }

    private static void DrawDrivetrain()
    {
        Menu.Card("Differentials & tires", "Locked diffs send equal speed to both sides: unstoppable off-road, scrubby in tight turns.", () =>
        {
            bool on = Editable;
            Menu.Switch(LockAxleDiffs, "Lock axle differentials", "Front and rear", on);
            Menu.Switch(LockCenterDiff, "Lock centre differential", "4x4 cars: fixed front/rear split", on);
            Menu.SliderRow(RollingMult, "Rolling resistance", 0f, 5f, "0.00", "×", on);
            bool pressure = false;
            try { pressure = _rolling != null && _rolling.dynamicModel == VPRollingFriction.Model.TirePressure; } catch (Exception ex) { DebugLog.Error("cheats", ex, "ChassisTuning.DrawDrivetrain"); }
            Menu.SliderRow(TirePressureMult, "Tire pressure", 0.3f, 2f, "0.00", "×", on && pressure);
            if (!pressure) Menu.Small("Tire pressure only matters on cars whose rolling friction uses the tire pressure model (see readout).");
        });
    }

    private static void DrawReadout()
    {
        Menu.Card("Chassis readout", "Stock → tuned, first wheel shown for suspension.", () =>
        {
            Menu.KeyValue("Car", Readout.Car);
            Menu.KeyValue("Mass", Readout.Mass);
            Menu.KeyValue("Centre of mass", Readout.Com);
            Menu.KeyValue("Wheels", $"{Readout.Wheels} ({Readout.Collider})");
            Menu.KeyValue("Spring rate", Readout.Spring);
            Menu.KeyValue("Damper rate", Readout.Damper);
            Menu.KeyValue("Travel", Readout.Travel);
            Menu.KeyValue("Ride height", Readout.Ride);
            Menu.KeyValue("Brake torque", Readout.Brake);
            Menu.KeyValue("Brake bias", Readout.Bias);
            Menu.KeyValue("Max steer angle", Readout.Steer);
            Menu.KeyValue("Rolling friction", Readout.Rolling);
            Menu.KeyValue("Differentials", Readout.Diffs);
            Menu.KeyValue("Values changed", Slots.Count.ToString());
        });
    }
}

[HarmonyPatch(typeof(VPVehicleController), nameof(VPVehicleController.DoUpdateBlocks))]
internal static class ChassisDiffLockPatch
{
    private static bool _loggedError;

    private static void Prefix(VPVehicleController __instance)
    {
        try { ChassisTuning.PhysicsStep(__instance); }
        catch (Exception e)
        {
            if (_loggedError) return;
            _loggedError = true;
            CheatsPlugin.Logger.LogError("Chassis diff lock step failed (further errors suppressed): " + e);
        }
    }
}
