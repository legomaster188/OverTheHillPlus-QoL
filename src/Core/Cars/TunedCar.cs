using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;
using VehiclePhysics;

namespace OverTheHillPlus.Cars;

/// <summary>
/// The runtime-tuned values of one car: mass, engine torque and power cap, rev range, speed limit, steering lock, gear
/// ratios and final drive, each a <see cref="CarTuneSlot"/> (a value the game rewrites itself, such as cargo weight,
/// engine-tier upgrades and damage, becomes the new base and the multiplier goes back on top). Used for every modded
/// car (<see cref="CarTuning"/>) and, with fewer <see cref="Parts"/>, for stock cars by the Realistic Handling mod.
/// <para>Main thread only. Call <see cref="Apply"/> about once a second while the car is simulated here, and
/// <see cref="Restore"/> when it must go back to the game's own numbers.</para>
/// </summary>
internal sealed class TunedCar
{
    /// <summary>Which values a tuned car touches.</summary>
    [Flags]
    public enum Parts
    {
        None = 0,
        Mass = 1,
        /// <summary>Engine torque and the power cap.</summary>
        Engine = 2,
        Revs = 4,
        /// <summary>The speed limiter's limit.</summary>
        TopSpeed = 8,
        /// <summary>Gear ratios and final drive.</summary>
        Drivetrain = 16,
        /// <summary>The steering lock (baked into a modded car's template instead).</summary>
        Steering = 32,
        /// <summary>Brake and handbrake torque (<see cref="CarRuntimeFit.Brakes"/>).</summary>
        Brakes = 64,
        /// <summary>The torque converter's capacity and lock / unlock rpm (<see cref="CarRuntimeFit.Clutch"/>).</summary>
        Clutch = 128,
        /// <summary>The engine's inertia and friction (<see cref="CarRuntimeFit.EngineMass"/>).</summary>
        EngineMass = 256,
        /// <summary>The parametric inertia box's width and length (<see cref="CarRuntimeFit.ChassisX"/>).</summary>
        Chassis = 512,
        /// <summary>What a profile's own multipliers tune (the default; the host tools' tuning uses it).</summary>
        Profile = Mass | Engine | Revs | TopSpeed | Drivetrain,
        /// <summary>Everything a modded car tunes at runtime: the profile's values and the physics fit's.</summary>
        Modded = Profile | Brakes | Clutch | EngineMass | Chassis,
    }

    private readonly VPVehicleController _car;
    private readonly Parts _parts;
    private IntPtr _eng, _gb, _hl, _diff, _input, _sc, _steer, _table, _brk, _clu, _inr;
    private readonly List<CarTuneSlot> _torque = new(), _power = new(), _revs = new(), _limit = new(), _gears = new(), _final = new(), _lock = new();
    private readonly List<CarTuneSlot> _engMass = new(), _brakes = new(), _clutch = new(), _clutchRpm = new(), _boxX = new(), _boxZ = new();
    private readonly HashSet<IntPtr> _gearArrays = new();
    private readonly List<Il2CppStructArray<float>> _keepAlive = new();
    private CarTuneSlot _mass;

    public TunedCar(VPVehicleController car, Parts parts = Parts.Profile)
    {
        _car = car;
        _parts = parts;
    }

    /// <summary>
    /// True for a settings object the car shares with others (its class setup's own brakes, converter or inertia box):
    /// those are never written, so a tuned car can't change every stock car of its class. Set by the owner; null: none.
    /// </summary>
    public Func<IntPtr, bool> Shared;
    /// <summary>Told once per kind when a settings object is left alone because it is shared.</summary>
    public Action<string> OnShared;
    private readonly HashSet<string> _toldShared = new();

    private T Own<T>(T settings, string what) where T : Il2CppSystem.Object
    {
        if (settings == null || Shared == null || !Shared(settings.Pointer)) return settings;
        if (_toldShared.Add(what)) OnShared?.Invoke(what);
        return null;
    }

    private static IntPtr P(Il2CppSystem.Object o) => o == null ? IntPtr.Zero : o.Pointer;

    private bool Has(Parts p) => (_parts & p) != 0;

    private void Rebind()
    {
        if (_mass == null && Has(Parts.Mass))
        {
            var rb = _car.GetComponent<Rigidbody>();
            if (rb != null) _mass = new CarTuneSlot(() => rb.mass, v => rb.mass = v);
        }
        var eng = _car.engine;
        if (P(eng) != _eng)
        {
            _eng = P(eng);
            Drop(_torque); Drop(_power); Drop(_revs); Drop(_engMass);
            if (eng != null)
            {
                if (Has(Parts.EngineMass))
                {
                    _engMass.Add(new CarTuneSlot(() => eng.inertia, v => eng.inertia = v));
                    _engMass.Add(new CarTuneSlot(() => eng.frictionTorque, v => eng.frictionTorque = v));
                    _engMass.Add(new CarTuneSlot(() => eng.stalledFrictionTorque, v => eng.stalledFrictionTorque = v));
                }
                if (Has(Parts.Engine))
                {
                    _torque.Add(new CarTuneSlot(() => eng.idleRpmTorque, v => eng.idleRpmTorque = v));
                    _torque.Add(new CarTuneSlot(() => eng.peakRpmTorque, v => eng.peakRpmTorque = v));
                    _torque.Add(new CarTuneSlot(() => eng.torqueCapLimit, v => eng.torqueCapLimit = v));
                    _power.Add(new CarTuneSlot(() => eng.powerCapLimit, v => eng.powerCapLimit = v));
                }
                if (Has(Parts.Revs))
                {
                    _revs.Add(new CarTuneSlot(() => eng.peakRpm, v => eng.peakRpm = v));
                    _revs.Add(new CarTuneSlot(() => eng.maxRpm, v => eng.maxRpm = v));
                    _revs.Add(new CarTuneSlot(() => eng.rpmLimiterMax, v => eng.rpmLimiterMax = v));
                }
            }
        }
        var sc = _car.speedControl;
        if (P(sc) != _sc)
        {
            _sc = P(sc);
            Drop(_limit);
            if (sc != null && Has(Parts.TopSpeed)) _limit.Add(new CarTuneSlot(() => sc.speedLimit, v => sc.speedLimit = v));
        }
        var st = _car.steering;
        if (P(st) != _steer)
        {
            _steer = P(st);
            Drop(_lock);
            if (st != null && Has(Parts.Steering)) _lock.Add(new CarTuneSlot(() => st.maxSteerAngle, v => st.maxSteerAngle = v));
        }
        var brk = Has(Parts.Brakes) ? Own(_car.brakes, "brakes") : null;
        if (P(brk) != _brk)
        {
            _brk = P(brk);
            Drop(_brakes);
            if (brk != null)
            {
                _brakes.Add(new CarTuneSlot(() => brk.maxBrakeTorque, v => brk.maxBrakeTorque = v));
                _brakes.Add(new CarTuneSlot(() => brk.handbrakeTorque, v => brk.handbrakeTorque = v));
            }
        }
        var clu = Has(Parts.Clutch) ? Own(_car.clutch, "converter") : null;
        if (P(clu) != _clu)
        {
            _clu = P(clu);
            Drop(_clutch); Drop(_clutchRpm);
            if (clu != null)
            {
                _clutch.Add(new CarTuneSlot(() => clu.maxTorqueTransfer, v => clu.maxTorqueTransfer = v));
                _clutchRpm.Add(new CarTuneSlot(() => clu.unlockRpm, v => clu.unlockRpm = v));
                _clutchRpm.Add(new CarTuneSlot(() => clu.lockRpm, v => clu.lockRpm = v));
            }
        }
        // The parametric inertia box (VPP rebuilds the tensor from it and the mass whenever either changes).
        Inertia.Settings inr = null;
        if (Has(Parts.Chassis))
        {
            try { var s = Own(_car.inertia, "inertia box"); inr = s != null && s.mode == Inertia.Mode.Parametric ? s : null; }
            catch (Exception ex) { DebugLog.Error("core", ex, "TunedCar.Rebind inertia"); }
        }
        if (P(inr) != _inr)
        {
            _inr = P(inr);
            Drop(_boxX); Drop(_boxZ);
            if (inr != null)
            {
                _boxX.Add(new CarTuneSlot(() => inr.chassisDimensions.x, v => { var d = inr.chassisDimensions; d.x = v; inr.chassisDimensions = d; }));
                _boxZ.Add(new CarTuneSlot(() => inr.chassisDimensions.z, v => { var d = inr.chassisDimensions; d.z = v; inr.chassisDimensions = d; }));
            }
        }
        if (!Has(Parts.Drivetrain)) return;
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
        // The input's forward table is also what a gearbox mod replaces with its own, so its pointer counts as well: a new table is
        // tuned from its own values, and the old one gets the game's values back.
        IntPtr table = IntPtr.Zero;
        try { var fwd = input != null ? input.forwardAllGears : null; table = fwd != null ? fwd.Pointer : IntPtr.Zero; } catch (Exception ex) { DebugLog.Error("core", ex, "TunedCar.Rebind"); }
        if (P(gb) != _gb || P(input) != _input || table != _table)
        {
            Drop(_gears);
            _gearArrays.Clear(); _keepAlive.Clear();
            _gb = P(gb); _input = P(input); _table = table;
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

    /// <summary>Writes the profile's multipliers on top of the game's own values. <paramref name="towPower"/> and
    /// <paramref name="towSpeed"/> are the extra multipliers of an overloaded tow (<see cref="TowRules"/>), 1 when none.</summary>
    public void Apply(CarTuningProfile p, float towPower = 1f, float towSpeed = 1f, CarRuntimeFit fit = null)
    {
        Rebind();
        fit ??= CarRuntimeFit.None;
        foreach (var s in _engMass) s.Write(CarTuningProfile.Scale(s.Sync(), fit.EngineMass));
        foreach (var s in _brakes) s.Write(CarTuningProfile.Scale(s.Sync(), fit.Brakes));
        foreach (var s in _clutch) s.Write(CarTuningProfile.Scale(s.Sync(), fit.Clutch));
        foreach (var s in _clutchRpm) s.Write(CarTuningProfile.Scale(s.Sync(), fit.ClutchRpm, 300f));
        foreach (var s in _boxX) s.Write(CarTuningProfile.Scale(s.Sync(), fit.ChassisX, 0.2f));
        foreach (var s in _boxZ) s.Write(CarTuningProfile.Scale(s.Sync(), fit.ChassisZ, 0.2f));
        _mass?.Write(CarTuningProfile.Scale(_mass.Sync(), p.Mass, 50f));
        foreach (var s in _torque) s.Write(CarTuningProfile.Scale(s.Sync(), p.Torque * towPower));
        foreach (var s in _power) s.Write(CarTuningProfile.Scale(s.Sync(), p.Power * towPower));
        foreach (var s in _revs) s.Write(CarTuningProfile.Scale(s.Sync(), p.RevRange, 1000f));
        foreach (var s in _limit) s.Write(CarTuningProfile.Scale(s.Sync(), p.TopSpeed * towSpeed, 1f));
        foreach (var s in _lock) s.Write(CarTuningProfile.Scale(s.Sync(), p.SteeringLock, 5f));
        foreach (var s in _gears) s.Write(CarTuningProfile.Scale(s.Sync(), p.GearRatios));
        foreach (var s in _final) s.Write(CarTuningProfile.Scale(s.Sync(), p.FinalDrive));
    }

    /// <summary>Gives the game's own gear ratios back (and forgets them) so another mod can read the car's real table; the next
    /// <see cref="Apply"/> binds whichever table the car has then.</summary>
    public void ReleaseGears()
    {
        Drop(_gears);
        _gearArrays.Clear(); _keepAlive.Clear();
        _gb = _input = _table = IntPtr.Zero;
    }

    public void Restore()
    {
        _mass?.Restore();
        foreach (var l in new[] { _torque, _power, _revs, _limit, _lock, _gears, _final, _engMass, _brakes, _clutch, _clutchRpm, _boxX, _boxZ })
            foreach (var s in l) s.Restore();
        // Forget the bindings so the next Apply re-reads the game's values as the new bases.
        _eng = _gb = _hl = _diff = _input = _sc = _steer = _table = _brk = _clu = _inr = IntPtr.Zero;
        _torque.Clear(); _power.Clear(); _revs.Clear(); _limit.Clear(); _lock.Clear(); _gears.Clear(); _final.Clear();
        _engMass.Clear(); _brakes.Clear(); _clutch.Clear(); _clutchRpm.Clear(); _boxX.Clear(); _boxZ.Clear();
        _gearArrays.Clear(); _keepAlive.Clear();
    }

    /// <summary>Gives only the engine's own values back (torque, power cap, revs, inertia and friction, the speed limit),
    /// for the game's reading of its "original" engine values; the next <see cref="Apply"/> tunes them again.</summary>
    public void RestoreEngine()
    {
        foreach (var l in new[] { _torque, _power, _revs, _limit, _engMass })
            foreach (var s in l) s.Restore();
        _eng = _sc = IntPtr.Zero;
        _torque.Clear(); _power.Clear(); _revs.Clear(); _limit.Clear(); _engMass.Clear();
    }
}
