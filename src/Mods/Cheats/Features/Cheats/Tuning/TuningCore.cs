using System;
using System.Collections.Generic;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using OverTheHillPlus.CheatsPlus;
using UnityEngine;
using VehiclePhysics;
using Object = UnityEngine.Object;

namespace OverTheHillPlus.Features.Cheats.Tuning;

/// <summary>
/// One tunable game value. Remembers the game's own ("base") value and the value we last wrote, so:
/// - if the game changes the value (damage debuff, engine-tier upgrade, gear table swap), the new game value
///   becomes the base and our multiplier is re-applied on top of it;
/// - on restore, the base is written back only if the value is still ours (a newer game value is kept).
/// </summary>
internal sealed class TuneSlot
{
    private readonly Func<float> _get;
    private readonly Action<float> _set;
    private float _last;
    private bool _has;

    public float Base { get; private set; }
    public bool Held => _has;

    public TuneSlot(Func<float> get, Action<float> set) { _get = get; _set = set; }

    public static TuneSlot Bool(Func<bool> get, Action<bool> set) =>
        new(() => get() ? 1f : 0f, v => set(v > 0.5f));

    private static bool Same(float a, float b) => Math.Abs(a - b) <= 1e-5f * Math.Max(1f, Math.Abs(a));

    /// <summary>Reads the live value; adopts it as the new base when it is not the value we wrote.</summary>
    public float Sync()
    {
        float cur = _get();
        if (!_has || !Same(cur, _last))
        {
            Base = cur;
            _last = cur;
            _has = true;
        }
        return Base;
    }

    /// <summary>Syncs, then writes <paramref name="target"/> if it differs from the live value.</summary>
    public void Write(float target)
    {
        Sync();
        if (float.IsNaN(target) || float.IsInfinity(target)) target = Base;
        if (!Same(target, _last)) _set(target);
        _last = target;
    }

    /// <summary>Puts the game's value back (if the live value is still ours) and forgets the slot.</summary>
    public void Restore()
    {
        if (!_has) return;
        _has = false;
        try
        {
            if (Same(_get(), _last) && !Same(Base, _last)) _set(Base);
        }
        catch (Exception ex) { DebugLog.Error("cheats", ex, "TuneSlot.Restore"); /* object gone: nothing to restore */ }
    }

    /// <summary>Forget without writing (used when the owning object was replaced or destroyed).</summary>
    public void Drop() => _has = false;
}

/// <summary>Everything we tune on one car, with the game's originals.</summary>
internal sealed class TunedCar
{
    public readonly VPVehicleController Car;
    public readonly IntPtr Ptr;
    public float Spool;          // turbo spool 0..1
    public float BoostFactor = 1f;

    private Engine.Settings _eng;
    private SpeedControl.Settings _sc;
    private Gearbox.Settings _gb;
    private HighLowGearsDifferentialSettings _hl;
    private Differential.Settings _diff;
    private FunselektorFusionVPStandardInput _input;
    private CarDamageSystem _dmg;
    private IntPtr _engPtr, _scPtr, _gbPtr, _hlPtr, _diffPtr;

    public float OriginalSpeedLimit = -1f; // CarDamageSystem's undamaged speed limit, -1 when unknown

    // Engine
    public TuneSlot IdleTorque, PeakTorque, TorqueCapLimit, PowerCapLimit, TorqueCap, PowerCap;
    public TuneSlot PeakRpm, MaxRpm, LimiterRpm, RpmLimiter, IdleRpm;
    // Speed control
    public TuneSlot SpeedLimiter, SpeedLimit;
    // Gearbox shift points (absolute rpm, scaled with the rev range)
    public readonly List<TuneSlot> ShiftRevs = new();
    // Gear ratios, final drive, damage throttle
    public readonly List<TuneSlot> GearRatios = new();
    private readonly HashSet<IntPtr> _gearArrays = new();
    private readonly List<Il2CppStructArray<float>> _keepAlive = new();
    public TuneSlot HighRatio, LowRatio, DiffRatio, ThrottleBias;

    public TunedCar(VPVehicleController car)
    {
        Car = car;
        Ptr = car.Pointer;
        Rebind(force: true);
    }

    public bool HasHighLow => _hl != null;
    public bool HasInput => _input != null;
    public Engine.Settings Eng => _eng;
    public SpeedControl.Settings Sc => _sc;

    private static IntPtr P(Il2CppSystem.Object o) => o == null ? IntPtr.Zero : o.Pointer;

    /// <summary>
    /// Re-reads the settings object references (cheap enough at ~1 Hz). If the game swapped a settings object,
    /// the slots for it are rebuilt (old ones dropped, the new object's values become the base).
    /// </summary>
    public void Rebind(bool force = false)
    {
        var eng = Car.engine;
        if (force || P(eng) != _engPtr)
        {
            DropAll(IdleTorque, PeakTorque, TorqueCapLimit, PowerCapLimit, TorqueCap, PowerCap, PeakRpm, MaxRpm, LimiterRpm, RpmLimiter, IdleRpm);
            _eng = eng; _engPtr = P(eng);
            if (eng != null)
            {
                IdleTorque = new TuneSlot(() => eng.idleRpmTorque, v => eng.idleRpmTorque = v);
                PeakTorque = new TuneSlot(() => eng.peakRpmTorque, v => eng.peakRpmTorque = v);
                TorqueCapLimit = new TuneSlot(() => eng.torqueCapLimit, v => eng.torqueCapLimit = v);
                PowerCapLimit = new TuneSlot(() => eng.powerCapLimit, v => eng.powerCapLimit = v);
                TorqueCap = TuneSlot.Bool(() => eng.torqueCap, v => eng.torqueCap = v);
                PowerCap = TuneSlot.Bool(() => eng.powerCap, v => eng.powerCap = v);
                IdleRpm = new TuneSlot(() => eng.idleRpm, v => eng.idleRpm = v); // read-only use (never written)
                PeakRpm = new TuneSlot(() => eng.peakRpm, v => eng.peakRpm = v);
                MaxRpm = new TuneSlot(() => eng.maxRpm, v => eng.maxRpm = v);
                LimiterRpm = new TuneSlot(() => eng.rpmLimiterMax, v => eng.rpmLimiterMax = v);
                RpmLimiter = TuneSlot.Bool(() => eng.rpmLimiter, v => eng.rpmLimiter = v);
            }
            else IdleTorque = PeakTorque = TorqueCapLimit = PowerCapLimit = TorqueCap = PowerCap = PeakRpm = MaxRpm = LimiterRpm = RpmLimiter = IdleRpm = null;
        }

        var sc = Car.speedControl;
        if (force || P(sc) != _scPtr)
        {
            DropAll(SpeedLimiter, SpeedLimit);
            _sc = sc; _scPtr = P(sc);
            if (sc != null)
            {
                SpeedLimiter = TuneSlot.Bool(() => sc.speedLimiter, v => sc.speedLimiter = v);
                SpeedLimit = new TuneSlot(() => sc.speedLimit, v => sc.speedLimit = v);
            }
            else SpeedLimiter = SpeedLimit = null;
        }

        var gb = Car.gearbox;
        if (force || P(gb) != _gbPtr)
        {
            foreach (var s in ShiftRevs) s.Drop();
            ShiftRevs.Clear();
            _gb = gb; _gbPtr = P(gb);
            if (gb != null)
            {
                ShiftRevs.Add(new TuneSlot(() => gb.autoShiftUpRevs, v => gb.autoShiftUpRevs = v));
                ShiftRevs.Add(new TuneSlot(() => gb.autoShiftDownRevs, v => gb.autoShiftDownRevs = v));
                ShiftRevs.Add(new TuneSlot(() => gb.autoShiftFullThrottleUpRevs, v => gb.autoShiftFullThrottleUpRevs = v));
                ShiftRevs.Add(new TuneSlot(() => gb.autoShiftFullThrottleDownRevs, v => gb.autoShiftFullThrottleDownRevs = v));
                ShiftRevs.Add(new TuneSlot(() => gb.automaticGearUpRevs, v => gb.automaticGearUpRevs = v));
                ShiftRevs.Add(new TuneSlot(() => gb.automaticGearDownRevs, v => gb.automaticGearDownRevs = v));
                ShiftRevs.Add(new TuneSlot(() => gb.automaticFullThrottleGearUpRevs, v => gb.automaticFullThrottleGearUpRevs = v));
                ShiftRevs.Add(new TuneSlot(() => gb.automaticFullThrottleGearDownRevs, v => gb.automaticFullThrottleGearDownRevs = v));
                ShiftRevs.Add(new TuneSlot(() => gb.automaticMaxRpm, v => gb.automaticMaxRpm = v));
            }
        }

        var hl = Car.highLowGearsDifferential;
        if (force || P(hl) != _hlPtr)
        {
            DropAll(HighRatio, LowRatio);
            _hl = hl; _hlPtr = P(hl);
            if (hl != null)
            {
                HighRatio = new TuneSlot(() => hl.highGearsGearRatio, v => hl.highGearsGearRatio = v);
                LowRatio = new TuneSlot(() => hl.lowGearsGearRatio, v => hl.lowGearsGearRatio = v);
            }
            else HighRatio = LowRatio = null;
        }

        var diff = Car.differential;
        if (force || P(diff) != _diffPtr)
        {
            DropAll(DiffRatio);
            _diff = diff; _diffPtr = P(diff);
            DiffRatio = diff != null ? new TuneSlot(() => diff.gearRatio, v => diff.gearRatio = v) : null;
        }

        if (force || _input == null)
        {
            // The game's input component rewrites gearbox.forwardGearRatios (from its own arrays) and the axle
            // differential ratio (from highLowGearsDifferential) every input tick, so those are the values to tune.
            try
            {
                _input = Car.GetComponent<FunselektorFusionVPStandardInput>();
                if (_input == null) _input = Car.GetComponentInParent<FunselektorFusionVPStandardInput>();
            }
            catch (Exception ex) { DebugLog.Error("cheats", ex, "TunedCar.Rebind"); _input = null; }
            ThrottleBias?.Drop();
            var input = _input;
            ThrottleBias = input != null ? new TuneSlot(() => input.ThrottleBias, v => input.ThrottleBias = v) : null;
        }

        try
        {
            if (_dmg == null) _dmg = Car.GetComponent<CarDamageSystem>();
            OriginalSpeedLimit = _dmg != null ? _dmg.originalSpeedLimit : -1f;
        }
        catch (Exception ex) { DebugLog.Error("cheats", ex, "TunedCar.Rebind"); OriginalSpeedLimit = -1f; }

        // Gear tables: the input component's two forward tables plus whatever the gearbox points at now.
        try
        {
            if (_input != null)
            {
                AddGearArray(_input.forwardAllGears);
                AddGearArray(_input.forwardSecondGearOnly);
            }
            if (_gb != null) AddGearArray(_gb.forwardGearRatios);
        }
        catch (Exception ex) { DebugLog.Error("cheats", ex, "TunedCar.Rebind"); }
    }

    private void AddGearArray(Il2CppStructArray<float> arr)
    {
        if (arr == null) return;
        if (!_gearArrays.Add(arr.Pointer)) return;
        _keepAlive.Add(arr);
        for (int i = 0; i < arr.Length; i++)
        {
            int idx = i;
            GearRatios.Add(new TuneSlot(() => arr[idx], v => arr[idx] = v));
        }
    }

    private static void DropAll(params TuneSlot[] slots)
    {
        foreach (var s in slots) s?.Drop();
    }

    private IEnumerable<TuneSlot> All()
    {
        yield return IdleTorque; yield return PeakTorque; yield return TorqueCapLimit; yield return PowerCapLimit;
        yield return TorqueCap; yield return PowerCap; yield return PeakRpm; yield return MaxRpm; yield return LimiterRpm;
        yield return RpmLimiter; yield return IdleRpm; yield return SpeedLimiter; yield return SpeedLimit;
        yield return HighRatio; yield return LowRatio; yield return DiffRatio; yield return ThrottleBias;
        foreach (var s in ShiftRevs) yield return s;
        foreach (var s in GearRatios) yield return s;
    }

    /// <summary>Writes every game original back.</summary>
    public void RestoreAll()
    {
        foreach (var s in All())
        {
            if (s == null) continue;
            try { s.Restore(); } catch (Exception ex) { DebugLog.Error("cheats", ex, "TunedCar.RestoreAll"); }
        }
        Spool = 0f;
        BoostFactor = 1f;
    }
}

/// <summary>Applies the tuning to the tuned cars and keeps track of which cars are tuned.</summary>
internal static class TuningCore
{
    internal static readonly Dictionary<IntPtr, TunedCar> Cars = new();

    private static bool _active;           // cached each frame: tuning on and allowed here
    private static float _nextRefresh;
    private static float _nextScan;
    private static readonly List<VPVehicleController> AllCars = new();
    private static bool _loggedApply, _loggedRefresh;

    public static bool Active => _active;
    public static string LastError;

    public static TunedCar LocalRecord
    {
        get
        {
            var p = GameState.LocalCarPtr;
            return p != IntPtr.Zero && Cars.TryGetValue(p, out var r) ? r : null;
        }
    }

    // ---- per frame (Update) ----

    public static void Tick()
    {
        bool want = TuningSettings.Enabled.Value && TuningSettings.AnythingOn && GameState.PhysicsAllowed;
        if (!want)
        {
            _active = false;
            if (Cars.Count > 0) RestoreEverything();
            return;
        }

        bool justEnabled = !_active;
        _active = true;
        AdvantageFlag.MarkUsed();
        if (!justEnabled && Time.unscaledTime < _nextRefresh) return;
        _nextRefresh = Time.unscaledTime + 1f;

        try { Refresh(); }
        catch (Exception e)
        {
            if (!_loggedRefresh) { _loggedRefresh = true; CheatsPlugin.Logger.LogError("Tuning refresh failed (further errors suppressed): " + e); }
            LastError = e.Message;
        }
    }

    private static void Refresh()
    {
        var targets = new Dictionary<IntPtr, VPVehicleController>();
        var local = GameState.LocalCar;
        if (local != null) targets[local.Pointer] = local;

        if (TuningSettings.AllCars.Value && GameState.IsAuthority && !GameState.IsPublicGuest)
        {
            if (Time.unscaledTime >= _nextScan)
            {
                _nextScan = Time.unscaledTime + 2f;
                AllCars.Clear();
                foreach (var o in Object.FindObjectsOfType(Il2CppType.Of<VPVehicleController>()))
                {
                    var v = o.TryCast<VPVehicleController>();
                    if (v != null) AllCars.Add(v);
                }
            }
            foreach (var v in AllCars)
            {
                if (v == null || !v.isActiveAndEnabled) continue;
                targets[v.Pointer] = v;
            }
        }

        // Drop cars that are gone or no longer targeted (restore those still alive).
        List<IntPtr> remove = null;
        foreach (var kv in Cars)
        {
            if (targets.ContainsKey(kv.Key) && kv.Value.Car != null) continue;
            (remove ??= new List<IntPtr>()).Add(kv.Key);
        }
        if (remove != null)
            foreach (var p in remove)
            {
                var rec = Cars[p];
                Cars.Remove(p);
                bool alive;
                try { alive = rec.Car != null; } catch (Exception ex) { DebugLog.Error("cheats", ex, "TuningCore.Refresh"); alive = false; }
                if (alive) rec.RestoreAll();
            }

        foreach (var kv in targets)
        {
            if (Cars.TryGetValue(kv.Key, out var rec))
            {
                rec.Rebind();
                continue;
            }
            try
            {
                Cars[kv.Key] = new TunedCar(kv.Value);
                CheatsPlugin.Logger.LogInfo($"Tuning: tracking car {kv.Value.name}");
            }
            catch (Exception e) { DebugLog.Error("cheats", e, "TuningCore.Refresh"); CheatsPlugin.Logger.LogWarning("Tuning: could not track car: " + e.Message); }
        }
    }

    public static void RestoreEverything()
    {
        foreach (var rec in Cars.Values)
        {
            bool alive;
            try { alive = rec.Car != null; } catch (Exception ex) { DebugLog.Error("cheats", ex, "TuningCore.RestoreEverything"); alive = false; }
            if (alive) rec.RestoreAll();
        }
        Cars.Clear();
        CheatsPlugin.Logger.LogInfo("Tuning: restored stock engine settings");
    }

    // ---- per physics step (VPVehicleController.DoUpdateBlocks prefix) ----

    internal static void PhysicsStep(VPVehicleController v)
    {
        if (!_active || Cars.Count == 0) return;
        if (!Cars.TryGetValue(v.Pointer, out var rec)) return;
        try { Apply(rec, v); }
        catch (Exception e)
        {
            if (!_loggedApply) { _loggedApply = true; CheatsPlugin.Logger.LogError("Tuning apply failed (further errors suppressed): " + e); }
            LastError = e.Message;
        }
    }

    private static void Apply(TunedCar r, VPVehicleController v)
    {
        var s = TuningSettings.Snapshot();

        // ---- turbo spool (driven by throttle and rpm, with lag) ----
        float boost = 1f;
        if (s.Turbo && s.Boost > 0f && r.PeakTorque != null)
        {
            float throttle = 0f, rpm = 0f;
            try
            {
                var d = v.data;
                throttle = Mathf.Clamp01(d.Get(Channel.Input, InputData.Throttle) / 10000f);
                rpm = d.Get(Channel.Vehicle, VehicleData.EngineRpm) / 1000f;
            }
            catch (Exception ex) { DebugLog.Error("cheats", ex, "TuningCore.Apply"); }
            float idle = r.IdleRpm?.Sync() ?? 800f;
            float max = r.MaxRpm != null ? r.MaxRpm.Sync() * s.Revs : 6000f;
            float rpmNorm = max > idle + 1f ? Mathf.Clamp01((rpm - idle) / (max - idle)) : 0f;
            float onset = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(s.Threshold, Mathf.Min(1f, s.Threshold + 0.25f), rpmNorm));
            float target = throttle * onset;
            float dt = Mathf.Max(0.001f, Time.fixedDeltaTime);
            float tau = target > r.Spool ? s.SpoolTime : s.SpoolTime * 0.35f; // blow-off is quicker than spool-up
            r.Spool += (target - r.Spool) * (1f - Mathf.Exp(-dt / Mathf.Max(0.02f, tau)));
            r.Spool = Mathf.Clamp01(r.Spool);
            boost = 1f + s.Boost * r.Spool;
        }
        else r.Spool = 0f;
        r.BoostFactor = boost;

        // ---- torque ----
        float torque = s.Torque * boost;
        bool torqueOn = !Approx(torque, 1f) || s.Turbo;
        Scale(r.IdleTorque, torqueOn, torque);
        Scale(r.PeakTorque, torqueOn, torque);
        Scale(r.TorqueCapLimit, torqueOn, torque);

        // ---- rev range (idle stays stock; peak/max/limiter and the shift points stretch together) ----
        float revs = s.Revs;
        bool revsOn = !Approx(revs, 1f);
        float idleRpm = r.IdleRpm?.Sync() ?? 0f;
        Scale(r.PeakRpm, revsOn, revs, idleRpm + 100f);
        Scale(r.MaxRpm, revsOn, revs, idleRpm + 200f);
        Scale(r.LimiterRpm, revsOn, revs, idleRpm + 200f);
        foreach (var slot in r.ShiftRevs) Scale(slot, revsOn, revs);
        Scale(r.PowerCapLimit, torqueOn || revsOn, torque * revs);

        // ---- caps, limiter, damage ----
        Const(r.TorqueCap, s.RemoveCaps || s.IgnoreDamage, 0f);
        Const(r.PowerCap, s.RemoveCaps, 0f);
        Const(r.RpmLimiter, s.NoRevLimiter, 0f);
        Scale(r.ThrottleBias, s.IgnoreDamage, 1f, 1f);

        // ---- top speed ----
        Const(r.SpeedLimiter, s.NoSpeedLimit, 0f);
        float orig = r.OriginalSpeedLimit;
        bool undoDamage = s.IgnoreDamage && orig > 0f;
        bool speedOn = !s.NoSpeedLimit && (!Approx(s.TopSpeed, 1f) || undoDamage);
        if (r.SpeedLimit != null)
        {
            if (!speedOn) { if (r.SpeedLimit.Held) r.SpeedLimit.Restore(); }
            else
            {
                float b = r.SpeedLimit.Sync();
                r.SpeedLimit.Write((undoDamage ? Mathf.Max(b, orig) : b) * s.TopSpeed);
            }
        }

        // ---- gearing ----
        bool gearsOn = !Approx(s.Gears, 1f);
        foreach (var slot in r.GearRatios) Scale(slot, gearsOn, s.Gears);
        bool finalOn = !Approx(s.FinalDrive, 1f);
        if (r.HasHighLow)
        {
            Scale(r.HighRatio, finalOn, s.FinalDrive);
            Scale(r.LowRatio, finalOn, s.FinalDrive);
        }
        else Scale(r.DiffRatio, finalOn, s.FinalDrive);
    }

    private static bool Approx(float a, float b) => Math.Abs(a - b) < 0.001f;

    /// <summary>on: value = max(base * mul, min); off: give the slot back to the game.</summary>
    private static void Scale(TuneSlot slot, bool on, float mul, float min = float.MinValue)
    {
        if (slot == null) return;
        if (!on) { if (slot.Held) slot.Restore(); return; }
        slot.Write(Mathf.Max(slot.Sync() * mul, min));
    }

    private static void Const(TuneSlot slot, bool on, float value)
    {
        if (slot == null) return;
        if (!on) { if (slot.Held) slot.Restore(); return; }
        slot.Write(value);
    }

    /// <summary>Before the game re-reads a car's "original" engine values, put the stock values back.</summary>
    internal static void BeforeGameReadsBase(CarDamageSystem dmg)
    {
        if (Cars.Count == 0 || dmg == null) return;
        var v = dmg.vehicle;
        if (v == null) return;
        if (Cars.TryGetValue(v.Pointer, out var rec)) rec.RestoreAll(); // re-synced on the next physics step
    }
}

[HarmonyPatch(typeof(VPVehicleController), nameof(VPVehicleController.DoUpdateBlocks))]
internal static class TuningPhysicsPatch
{
    private static void Prefix(VPVehicleController __instance)
    {
        if (!TuningCore.Active) return;
        TuningCore.PhysicsStep(__instance);
    }
}

/// <summary>
/// CarDamageSystem captures originalSpeedLimit / originalMaxTorque from the live settings in these two methods
/// (engine-tier upgrade, spawn). Restore stock first so the boosted values never become the game's "originals".
/// </summary>
[HarmonyPatch(typeof(CarDamageSystem), nameof(CarDamageSystem.EngineTierChanged))]
internal static class TuningTierPatch
{
    private static void Prefix(CarDamageSystem __instance)
    {
        try { TuningCore.BeforeGameReadsBase(__instance); }
        catch (Exception e) { DebugLog.Error("cheats", e, "TuningTierPatch.Prefix"); CheatsPlugin.Logger.LogDebug("Tuning tier prefix: " + e.Message); }
    }
}

[HarmonyPatch(typeof(CarDamageSystem), nameof(CarDamageSystem.GetBaseValuesFromVehicleConfig))]
internal static class TuningBaseValuesPatch
{
    private static void Prefix(CarDamageSystem __instance)
    {
        try { TuningCore.BeforeGameReadsBase(__instance); }
        catch (Exception e) { DebugLog.Error("cheats", e, "TuningBaseValuesPatch.Prefix"); CheatsPlugin.Logger.LogDebug("Tuning base prefix: " + e.Message); }
    }
}
