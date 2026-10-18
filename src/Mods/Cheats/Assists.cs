using System;
using HarmonyLib;
using OverTheHillPlus.CheatsPlus;
using UnityEngine;
using VehiclePhysics;

namespace OverTheHillPlus;

/// <summary>
/// Driving assists and car cheats for the local car. Runs inside the car's physics step
/// (VPVehicleController.DoUpdateBlocks) so changes land before the blocks read their inputs.
/// </summary>
internal static class Assists
{
    private const int MaxWheels = 16;
    private static readonly float[] GameBase = CreateBases();
    private static bool _inSelfCall;
    private static bool _gripApplied;
    private static IntPtr _gripAppliedTo;
    private static float _tcFactor = 1f;

    // Live telemetry for the menu.
    public static string Surface = "-";
    public static float MaxNormalizedSlip;
    public static float TcFactor => _tcFactor;
    public static int RawThrottle;
    public static float RollDegrees;

    public static bool Active => CheatsConfig.AssistsEnabled.Value && GameState.PhysicsAllowed;

    private static float[] CreateBases()
    {
        var a = new float[MaxWheels];
        for (int i = 0; i < a.Length; i++) a[i] = 1f;
        return a;
    }

    private static bool IsSlippery(MaterialLabel label) => label switch
    {
        MaterialLabel.Mud or MaterialLabel.DeepMud or MaterialLabel.Snow or MaterialLabel.DeepSnow
            or MaterialLabel.ShallowSnow or MaterialLabel.Ice or MaterialLabel.Sand or MaterialLabel.DeepSand
            or MaterialLabel.QuickSand or MaterialLabel.Water or MaterialLabel.LooseRock => true,
        _ => false
    };

    private static MaterialLabel? WheelSurface(VehicleBase v, int wheel)
    {
        try
        {
            var states = v.wheelState;
            return states == null || wheel < 0 || wheel >= states.Length ? null : WheelSurface(states[wheel]);
        }
        catch (Exception ex) { DebugLog.Error("cheats", ex, "Assists.WheelSurface"); return null; }
    }

    private static MaterialLabel? WheelSurface(VehicleBase.WheelState ws)
    {
        try
        {
            var cfg = ws?.groundMaterial?.Config;
            return cfg == null ? null : cfg.Label;
        }
        catch (Exception ex) { DebugLog.Error("cheats", ex, "Assists.WheelSurface"); return null; }
    }

    // Enum.ToString boxes and allocates; the surface label is published every physics step, so cache the text.
    private static MaterialLabel? _surfaceLabel;
    private static string _surfaceText = "-";

    private static string SurfaceName(MaterialLabel? label)
    {
        if (label != _surfaceLabel)
        {
            _surfaceLabel = label;
            _surfaceText = label?.ToString() ?? "-";
        }
        return _surfaceText;
    }

    internal static float GripFactor(VehicleBase v, int wheel)
    {
        float f = CheatsConfig.GripMultiplier.Value;
        if (CheatsConfig.SlipperyBoost.Value && WheelSurface(v, wheel) is { } label && IsSlippery(label))
            f *= CheatsConfig.SlipperyMultiplier.Value;
        return f;
    }

    internal static void OnGameSetFriction(int wheel, float value)
    {
        if (_inSelfCall || wheel < 0 || wheel >= MaxWheels) return;
        GameBase[wheel] = value;
    }

    /// <summary>Called every physics step for every simulated VPP car.</summary>
    internal static void PhysicsStep(VPVehicleController v)
    {
        if (!GameState.IsLocalCar(v))
        {
            return;
        }

        bool active = Active;
        if (active || CheatsActive || CheatsConfig.NoDamage.Value) AdvantageFlag.MarkUsed();
        ApplyGrip(v, active);

        var rb = v.cachedRigidbody;
        if (rb == null) return;
        var t = rb.transform;

        // v.wheelState builds a new interop array wrapper on every read: fetch it once per step, not once per wheel.
        var states = v.wheelState;
        int grounded = 0, wheels = states == null ? 0 : Math.Min(Math.Min(v.wheelCount, states.Length), MaxWheels);
        float maxSlip = 0f;
        for (int i = 0; i < wheels; i++)
        {
            var ws = states[i];
            if (ws == null || !ws.grounded) continue;
            grounded++;
            if (ws.driveTorque > 1f) maxSlip = Math.Max(maxSlip, Math.Abs(ws.normalizedSlip));
        }
        MaxNormalizedSlip = maxSlip;
        Surface = SurfaceName(wheels > 0 ? WheelSurface(states[0]) : null);

        var data = v.data;
        int throttle = data.Get(Channel.Input, InputData.Throttle);
        RawThrottle = throttle;
        // Telemetry above always runs (menu + HUD); everything below changes the car.
        if (!active && !CheatsActive) { _tcFactor = 1f; return; }

        if (active)
        {
            TractionControl(data, throttle, Math.Abs(v.speed) < MinTractionSpeed ? 0f : maxSlip);
            AntiRoll(rb, t);
            if (grounded > 0 && CheatsConfig.Downforce.Value > 0f)
                rb.AddForce(-t.up * (9.81f * CheatsConfig.Downforce.Value), ForceMode.Acceleration);
        }
        else _tcFactor = 1f;

        if (GameState.PhysicsAllowed && CheatsConfig.ExtraAcceleration.Value > 0f && throttle > 0 && grounded >= 2)
        {
            float dir = data.Get(Channel.Vehicle, VehicleData.GearboxGear) < 0 ? -1f : 1f;
            rb.AddForce(t.forward * (dir * CheatsConfig.ExtraAcceleration.Value * (throttle / 10000f)), ForceMode.Acceleration);
        }
    }

    private static bool CheatsActive => GameState.PhysicsAllowed && CheatsConfig.ExtraAcceleration.Value > 0f;

    private static void ApplyGrip(VPVehicleController v, bool active)
    {
        int wheels = Math.Min(v.wheelCount, MaxWheels);
        if (!active)
        {
            if (!_gripApplied) return;
            // Hand control back to the game with the last values it set.
            if (_gripAppliedTo == v.Pointer) SetAll(v, wheels);
            _gripApplied = false;
            return;
        }
        // Re-apply every step so the surface boost follows the terrain under each wheel.
        SetAll(v, wheels);
        _gripApplied = true;
        _gripAppliedTo = v.Pointer;
    }

    private static void SetAll(VehicleBase v, int wheels)
    {
        _inSelfCall = true;
        try { for (int i = 0; i < wheels; i++) v.SetWheelTireFrictionMultiplier(i, GameBase[i]); }
        finally { _inSelfCall = false; }
    }

    // Below ~walking pace the slip value is meaningless (divides by near-zero speed) and would choke the throttle.
    private const float MinTractionSpeed = 2f;

    private static void TractionControl(DataBus data, int throttle, float maxSlip)
    {
        if (!CheatsConfig.TractionControl.Value || throttle <= 0) { _tcFactor = Mathf.MoveTowards(_tcFactor, 1f, 0.05f); return; }
        float strength = Mathf.Clamp01(CheatsConfig.TractionStrength.Value);
        float excess = Mathf.Max(0f, maxSlip - CheatsConfig.TractionTargetSlip.Value);
        float desired = Mathf.Clamp(1f - excess * 4f * strength, 1f - strength, 1f);
        // Cut fast, restore slowly.
        _tcFactor = Mathf.MoveTowards(_tcFactor, desired, desired < _tcFactor ? 0.25f : 0.04f);
        if (_tcFactor < 0.999f) data.Set(Channel.Input, InputData.Throttle, (int)(throttle * _tcFactor));
    }

    private static void AntiRoll(Rigidbody rb, Transform t)
    {
        Vector3 fwd = t.forward;
        float sinRoll = Vector3.Dot(Vector3.Cross(t.up, Vector3.up), fwd);
        RollDegrees = Mathf.Asin(Mathf.Clamp(sinRoll, -1f, 1f)) * Mathf.Rad2Deg;
        if (!CheatsConfig.AntiRoll.Value) return;

        const float deadzone = 0.2f; // ~12 degrees of lean is left alone
        float excess = Mathf.Sign(sinRoll) * Mathf.Max(0f, Mathf.Abs(sinRoll) - deadzone);
        if (excess == 0f) return;
        float s = CheatsConfig.AntiRollStrength.Value;
        float rollRate = Vector3.Dot(rb.angularVelocity, fwd);
        rb.AddTorque(fwd * (excess * 12f * s - rollRate * 1.5f * s), ForceMode.Acceleration);
    }

    public static void FlipUpright()
    {
        var v = GameState.LocalCar;
        if (v == null || !GameState.PhysicsAllowed) return;
        var t = v.cachedTransform != null ? v.cachedTransform : v.transform;
        Vector3 fwd = Vector3.ProjectOnPlane(t.forward, Vector3.up);
        if (fwd.sqrMagnitude < 0.01f) fwd = Vector3.ProjectOnPlane(t.up, Vector3.up);
        v.Reposition(t.position + Vector3.up * 1.5f, Quaternion.LookRotation(fwd.normalized, Vector3.up));
        CheatsPlugin.Logger.LogInfo("Flipped car upright");
    }

    public static void RepairNow()
    {
        var v = GameState.LocalCar;
        if (v == null || !GameState.PhysicsAllowed) return;
        var dmg = v.GetComponent<CarDamageSystem>();
        if (dmg == null) { CheatsPlugin.Logger.LogWarning("No CarDamageSystem on local car"); return; }
        dmg.Cheat_SetDamage(0);
        CheatsPlugin.Logger.LogInfo("Repaired car");
    }
}

[HarmonyPatch(typeof(VPVehicleController), nameof(VPVehicleController.DoUpdateBlocks))]
internal static class DoUpdateBlocksPatch
{
    private static bool _loggedError;

    private static void Prefix(VPVehicleController __instance)
    {
        try
        {
            Assists.PhysicsStep(__instance);
            if (GameState.IsLocalCar(__instance)) Movement.PhysicsStep(__instance);
        }
        catch (Exception e)
        {
            if (_loggedError) return;
            _loggedError = true;
            CheatsPlugin.Logger.LogError("Assists step failed (further errors suppressed): " + e);
        }
    }
}

[HarmonyPatch(typeof(VehicleBase), nameof(VehicleBase.SetWheelTireFrictionMultiplier))]
internal static class FrictionPatch
{
    private static void Prefix(VehicleBase __instance, int wheelIndex, ref float frictionMultiplier)
    {
        if (!GameState.IsLocalCar(__instance)) return;
        Assists.OnGameSetFriction(wheelIndex, frictionMultiplier);
        if (Assists.Active) frictionMultiplier *= Assists.GripFactor(__instance, wheelIndex);
    }
}

[HarmonyPatch(typeof(CarDamageSystem), nameof(CarDamageSystem.ApplyDamage))]
internal static class NoDamagePatch
{
    private static bool Prefix(CarDamageSystem __instance)
    {
        if (!CheatsConfig.NoDamage.Value || !GameState.PhysicsAllowed) return true;
        return !GameState.IsLocalCar(__instance.vehicle);
    }
}
