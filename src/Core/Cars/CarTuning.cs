using System;
using System.Collections.Generic;
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
/// <item>Applied at runtime, once a second, to every such car in the scene (<see cref="TunedCar"/>): mass, engine torque and rev range, gear
///   ratios, final drive, the engine's power cap and the speed limiter's limit. These are values the game itself rewrites (cargo weight, engine-tier upgrades, damage), so
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

    /// <summary>The car's tuning profile (clamped), the same on every peer.</summary>
    public CarTuningProfile Profile => _profile;

    /// <summary>The car's tyre asset (a tuned copy of the donor's), shared by every copy and its CarInfo.</summary>
    public TireDataStandardAsset Tyres { get; private set; }

    /// <summary>The runtime multipliers worked out from the model and the donor when the template is built.</summary>
    public CarRuntimeFit Fit { get; private set; } = CarRuntimeFit.None;

    /// <summary>The template's tuned wheel (the first wheel collider after the bake), for the CarInfo's suspension setup
    /// (CarCatalog): whatever game code applies that setup to the wheels writes the car's own values. Null before the bake.</summary>
    public WheelValues Wheel { get; private set; }

    /// <summary>One wheel collider's values.</summary>
    public sealed class WheelValues
    {
        public float Radius, Inertia, SpringRate, DamperRate, UnsprungMass, BumpStopRate;
        public string Describe() => string.Format(System.Globalization.CultureInfo.InvariantCulture,
            "r {0:0.000} m, inertia {1:0.##} kg m2, spring {2:0} N/m, damper {3:0} N s/m, unsprung {4:0.#} kg, bump stop {5:0} N/m",
            Radius, Inertia, SpringRate, DamperRate, UnsprungMass, BumpStopRate);
    }

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
        // The rest of each wheel keeps the donor's proportions (CarPhysicsFit): wheel inertia and unsprung mass by the game's
        // r² rule times the extra load, bump stops with the spring.
        float wheelMass = CarPhysicsFit.WheelMassScale(p, lay.DonorTyreRadius, lay.TyreRadius);
        float bump = CarPhysicsFit.BumpStopScale(p);
        WheelValues first = null;
        foreach (var wc in tpl.GetComponentsInChildren<VPFunselektor3DRaycastWheelCollider>(true))
        {
            if (wc == null) continue;
            if (radius) wc.radius = lay.TyreRadius;
            wc.suspensionTravel *= p.SuspensionTravel;
            wc.springRate *= p.SpringRate;
            wc.damperRate *= p.DamperRate;
            wc.bumpStopRate *= bump;
            wc.unsprungMass *= wheelMass;
            wc.inertia *= wheelMass;
            first ??= new WheelValues
            {
                Radius = wc.radius, Inertia = wc.inertia, SpringRate = wc.springRate, DamperRate = wc.damperRate,
                UnsprungMass = wc.unsprungMass, BumpStopRate = wc.bumpStopRate,
            };
        }
        Wheel = first;
        float donorBase = lay.DonorFrontZ - lay.DonorRearZ, modelBase = lay.FrontZ - lay.RearZ;
        float donorTrack = Mathf.Abs(lay.DonorWheels[1].x - lay.DonorWheels[0].x), modelTrack = Mathf.Abs(lay.Wheels[1].x - lay.Wheels[0].x);
        Fit = CarRuntimeFit.For(p, lay.DonorTyreRadius, lay.TyreRadius, donorBase, modelBase, donorTrack, modelTrack);

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
        _car.Info($"tuning: {p.Describe()}; wheel radius {(radius ? lay.TyreRadius.ToString("0.000") + " m" : "the donor's")}; " +
                  $"wheel {(first != null ? first.Describe() : "none")} (inertia and unsprung mass x{wheelMass:0.##}, bump stops x{bump:0.##}); " +
                  $"at runtime {Fit.Describe()}; wheelbase {modelBase:0.00} m (donor {donorBase:0.00}), track {modelTrack:0.00} m (donor {donorTrack:0.00}).");
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
            // Another mod is setting these values by hand: stand back without letting go of the slots (see ClaimBook), so the
            // two never feed each other's writes back in.
            if (CarTuneClaims.IsHeld(car.Pointer)) continue;
            if (!_cars.TryGetValue(car.Pointer, out var rec))
            {
                _cars[car.Pointer] = rec = new TunedCar(car, TunedCar.Parts.Modded)
                {
                    Shared = IsClassSetting,
                    OnShared = what => _car.LogOnce("shared-" + what,
                        $"tuning: the car's {what} is its class's own settings object (shared with the stock cars): left as the game has it."),
                };
            }
            float load = _profile.TowKg > 0f ? CarTowLoads.Of(car.Pointer) : 0f;
            rec.Apply(_profile, TowRules.PowerFactor(load, _profile.TowKg), TowRules.SpeedFactor(load, _profile.TowKg), Fit);
            if (CarPhysicsProbe.Enabled) CarPhysicsProbe.Watch(_car, fp, car);
        }
        List<IntPtr> gone = null;
        foreach (var k in _cars.Keys)
            if (!seen.Contains(k)) (gone ??= new List<IntPtr>()).Add(k);
        if (gone != null) foreach (var k in gone) _cars.Remove(k);
    }

    private HashSet<IntPtr> _classSettings;

    /// <summary>True for the brakes, converter or inertia box object of the car's class setup (VehicleControllerSetup,
    /// one asset for every car of the class): writing it would tune every stock car of the class too.</summary>
    private bool IsClassSetting(IntPtr p)
    {
        if (p == IntPtr.Zero) return false;
        if (_classSettings == null)
        {
            _classSettings = new HashSet<IntPtr>();
            foreach (var info in new[] { _car.Catalog.Info, _car.Catalog.Base })
            {
                try
                {
                    var s = info != null ? info.carCategorySetup : null;
                    if (s == null) continue;
                    if (s.brakes != null) _classSettings.Add(s.brakes.Pointer);
                    if (s.clutch != null) _classSettings.Add(s.clutch.Pointer);
                    if (s.inertia != null) _classSettings.Add(s.inertia.Pointer);
                }
                catch (Exception e) { DebugLog.Error("core", e, "CarTuning.IsClassSetting"); }
            }
        }
        return _classSettings.Contains(p);
    }

    private void RestoreAll()
    {
        foreach (var r in _cars.Values) r.Restore();
        _cars.Clear();
    }

    /// <summary>Another mod (Gearbox+) is about to read or replace this car's gear table: the game's own ratios go back into it,
    /// so the tuned ones never become its "stock" ratios. The tuning binds the car's table again on the next tick.</summary>
    public void ReleaseGears(VPVehicleController car)
    {
        if (car != null && _cars.TryGetValue(car.Pointer, out var rec)) rec.ReleaseGears();
    }

    /// <summary>Before the game reads a car's "original" engine values, put the game's own engine values back (they are
    /// tuned again on the next tick), so tuned values never become the game's originals. Only the engine's: the mass,
    /// brakes and the rest stay, so a spawning 8.6 t truck never sits for a second at the donor's weight on its stiff springs.</summary>
    public void BeforeGameReadsBase(CarDamageSystem dmg)
    {
        if (_cars.Count == 0 || dmg == null) return;
        var v = dmg.vehicle;
        if (v != null && _cars.TryGetValue(v.Pointer, out var rec)) rec.RestoreEngine();
    }
}
