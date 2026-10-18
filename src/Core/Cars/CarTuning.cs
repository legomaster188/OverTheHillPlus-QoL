using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;
using VehiclePhysics;
using Object = UnityEngine.Object;

namespace OverTheHillPlus.Cars;

// After the namespace line on purpose: the game has its own global ModManager (mod.io).
using OverTheHillPlus.Core.Manager;

/// <summary>
/// How a modded car drives (<see cref="CarDefinition.Tuning"/>), in two parts:
/// <list type="bullet">
/// <item>Baked into the template once (identical on every peer, since every peer builds it with the same code): wheel
///   radius (the model's tyre on the unmoved colliders, <see cref="CarWheelFit"/>), suspension travel / spring / damper, tyre grip (a copy of the
///   donor's tyre asset), steering lock and the centre of mass.</item>
/// <item>Applied at runtime, once a second, to every such car in the scene: mass, engine torque and rev range, gear
///   ratios and final drive. These are values the game itself rewrites (cargo weight, engine-tier upgrades, damage), so
///   each is a <see cref="CarTuneSlot"/>: a game change becomes the new base and the car's multiplier goes back on top.
///   Runs where this game simulates the car (offline, solo, host) and on modded guests while the host runs the car
///   too, so a guest's prediction matches the host's physics. Nowhere else.</item>
/// </list>
/// </summary>
internal sealed class CarTuning
{
    private readonly RuntimeCar _car;
    private readonly CarTuningProfile _profile;
    private readonly Dictionary<IntPtr, TunedCar> _cars = new();
    private float _next;

    /// <summary>The car's tyre asset (a tuned copy of the donor's), shared by every copy and its CarInfo.</summary>
    public TireDataStandardAsset Tyres { get; private set; }

    public CarTuning(RuntimeCar car)
    {
        _car = car;
        _profile = (car.Def.Tuning ?? new CarTuningProfile()).Clamped();
    }

    // ---------------------------------------------------------------- template

    public void BakeTemplate(GameObject tpl, CarLayout lay)
    {
        var p = _profile;
        var car = tpl.GetComponent<VPVehicleController>();
        if (car == null) return;

        // tyres: our own copy, so no stock car changes
        if (car.tireData != null)
        {
            var t = Object.Instantiate(car.tireData);
            t.name = _car.Def.CarName + "_Tyres";
            var s = t.tireSettings;
            if (s != null)
            {
                s.longitudinalPeakFriction *= p.TyreGrip;
                s.longitudinalLimitFriction *= p.TyreGrip;
                s.lateralPeakFriction *= p.TyreGrip;
                s.lateralLimitFriction *= p.TyreGrip;
            }
            t.hideFlags = HideFlags.DontUnloadUnusedAsset;
            Object.DontDestroyOnLoad(t);
            car.tireData = t;
            Tyres = t;
        }

        if (car.steering != null) car.steering.maxSteerAngle *= p.SteeringLock;

        // The model's tyre radius on the unmoved colliders: VPP settles the car onto its own tyres. The same predicate
        // as the garage model's drop (CarWheelFit.Applies), so the car and its garage model always agree.
        bool radius = CarWheelFit.Applies(lay.DonorTyreRadius, lay.TyreRadius);
        foreach (var wc in tpl.GetComponentsInChildren<VPFunselektor3DRaycastWheelCollider>(true))
        {
            if (wc == null) continue;
            if (radius) wc.radius = lay.TyreRadius;
            wc.suspensionTravel *= p.SuspensionTravel;
            wc.springRate *= p.SpringRate;
            wc.damperRate *= p.DamperRate;
        }

        var com = car.centerOfMass;
        if (com != null)
        {
            var root = tpl.transform;
            var c = root.InverseTransformPoint(com.position);
            float span = lay.DonorFrontZ - lay.DonorRearZ;
            float t = Mathf.Abs(span) > 0.01f ? (c.z - lay.DonorRearZ) / span : 0.5f;
            float z = Mathf.LerpUnclamped(lay.RearZ, lay.FrontZ, t);
            com.position = root.TransformPoint(new Vector3(c.x, c.y - p.CentreOfMassDrop, z));
        }
        _car.Info($"tuning: {p.Describe()}; wheel radius {(radius ? lay.TyreRadius.ToString("0.000") + " m" : "the donor's")}.");
    }

    // ---------------------------------------------------------------- runtime

    private bool Allowed => _car.Ready && (GameState.PhysicsAllowed || ModManager.ServerModActive(_car.Id));

    public void Tick()
    {
        if (Time.unscaledTime < _next) return;
        _next = Time.unscaledTime + 1f;
        if (!Allowed)
        {
            if (_cars.Count > 0) RestoreAll();
            return;
        }
        try { Refresh(); }
        catch (Exception e) { DebugLog.Error("core", e, "CarTuning.Tick"); _car.LogOnce("tuning", "tuning: " + e.Message); }
    }

    private void Refresh()
    {
        var seen = new HashSet<IntPtr>();
        foreach (var fp in GameState.FusionPlayers)
        {
            if (fp == null) continue;
            var vcc = fp.GetComponent<VehicleCustomizationController>();
            if (!_car.Catalog.IsThis(vcc)) continue;
            var car = fp.GetComponent<VPVehicleController>();
            if (car == null || !car.isActiveAndEnabled) continue;
            seen.Add(car.Pointer);
            if (!_cars.TryGetValue(car.Pointer, out var rec)) _cars[car.Pointer] = rec = new TunedCar(car);
            rec.Apply(_profile);
        }
        List<IntPtr> gone = null;
        foreach (var k in _cars.Keys)
            if (!seen.Contains(k)) (gone ??= new List<IntPtr>()).Add(k);
        if (gone != null) foreach (var k in gone) _cars.Remove(k);
    }

    private void RestoreAll()
    {
        foreach (var r in _cars.Values) r.Restore();
        _cars.Clear();
    }

    /// <summary>Before the game reads a car's "original" engine values, put the game's own values back (they are
    /// tuned again on the next tick), so tuned values never become the game's originals.</summary>
    public void BeforeGameReadsBase(CarDamageSystem dmg)
    {
        if (_cars.Count == 0 || dmg == null) return;
        var v = dmg.vehicle;
        if (v != null && _cars.TryGetValue(v.Pointer, out var rec)) rec.Restore();
    }

    /// <summary>The runtime-tuned values of one car.</summary>
    private sealed class TunedCar
    {
        private readonly VPVehicleController _car;
        private IntPtr _eng, _gb, _hl, _diff, _input;
        private readonly List<CarTuneSlot> _torque = new(), _revs = new(), _gears = new(), _final = new();
        private readonly HashSet<IntPtr> _gearArrays = new();
        private readonly List<Il2CppStructArray<float>> _keepAlive = new();
        private CarTuneSlot _mass;

        public TunedCar(VPVehicleController car) { _car = car; }

        private static IntPtr P(Il2CppSystem.Object o) => o == null ? IntPtr.Zero : o.Pointer;

        private void Rebind()
        {
            if (_mass == null)
            {
                var rb = _car.GetComponent<Rigidbody>();
                if (rb != null) _mass = new CarTuneSlot(() => rb.mass, v => rb.mass = v);
            }
            var eng = _car.engine;
            if (P(eng) != _eng)
            {
                _eng = P(eng);
                Drop(_torque); Drop(_revs);
                if (eng != null)
                {
                    _torque.Add(new CarTuneSlot(() => eng.idleRpmTorque, v => eng.idleRpmTorque = v));
                    _torque.Add(new CarTuneSlot(() => eng.peakRpmTorque, v => eng.peakRpmTorque = v));
                    _torque.Add(new CarTuneSlot(() => eng.torqueCapLimit, v => eng.torqueCapLimit = v));
                    _revs.Add(new CarTuneSlot(() => eng.peakRpm, v => eng.peakRpm = v));
                    _revs.Add(new CarTuneSlot(() => eng.maxRpm, v => eng.maxRpm = v));
                    _revs.Add(new CarTuneSlot(() => eng.rpmLimiterMax, v => eng.rpmLimiterMax = v));
                }
            }
            var hl = _car.highLowGearsDifferential;
            var diff = _car.differential;
            if (P(hl) != _hl || P(diff) != _diff)
            {
                _hl = P(hl); _diff = P(diff);
                Drop(_final);
                if (hl != null)
                {
                    _final.Add(new CarTuneSlot(() => hl.highGearsGearRatio, v => hl.highGearsGearRatio = v));
                    _final.Add(new CarTuneSlot(() => hl.lowGearsGearRatio, v => hl.lowGearsGearRatio = v));
                }
                else if (diff != null) _final.Add(new CarTuneSlot(() => diff.gearRatio, v => diff.gearRatio = v));
            }
            // Gear tables: the input component rewrites the gearbox's ratios from its own two tables every input tick.
            var gb = _car.gearbox;
            FunselektorFusionVPStandardInput input = null;
            try { input = _car.GetComponent<FunselektorFusionVPStandardInput>(); } catch (Exception ex) { DebugLog.Error("core", ex, "TunedCar.Rebind"); }
            if (P(gb) != _gb || P(input) != _input)
            {
                _gb = P(gb); _input = P(input);
                if (input != null)
                {
                    AddGears(input.forwardAllGears);
                    AddGears(input.forwardSecondGearOnly);
                }
                if (gb != null) AddGears(gb.forwardGearRatios);
            }
        }

        private void AddGears(Il2CppStructArray<float> arr)
        {
            if (arr == null || !_gearArrays.Add(arr.Pointer)) return;
            _keepAlive.Add(arr);
            for (int i = 0; i < arr.Length; i++)
            {
                int idx = i;
                _gears.Add(new CarTuneSlot(() => arr[idx], v => arr[idx] = v));
            }
        }

        private static void Drop(List<CarTuneSlot> slots)
        {
            foreach (var s in slots) s.Restore();
            slots.Clear();
        }

        public void Apply(CarTuningProfile p)
        {
            Rebind();
            _mass?.Write(CarTuningProfile.Scale(_mass.Sync(), p.Mass, 50f));
            foreach (var s in _torque) s.Write(CarTuningProfile.Scale(s.Sync(), p.Torque));
            foreach (var s in _revs) s.Write(CarTuningProfile.Scale(s.Sync(), p.RevRange, 1000f));
            foreach (var s in _gears) s.Write(CarTuningProfile.Scale(s.Sync(), p.GearRatios));
            foreach (var s in _final) s.Write(CarTuningProfile.Scale(s.Sync(), p.FinalDrive));
        }

        public void Restore()
        {
            _mass?.Restore();
            foreach (var l in new[] { _torque, _revs, _gears, _final })
                foreach (var s in l) s.Restore();
            // Forget the bindings so the next Apply re-reads the game's values as the new bases.
            _eng = _gb = _hl = _diff = _input = IntPtr.Zero;
            _torque.Clear(); _revs.Clear(); _gears.Clear(); _final.Clear();
            _gearArrays.Clear(); _keepAlive.Clear();
        }
    }
}
