using System;
using Il2CppInterop.Runtime;
using OverTheHillPlus.CheatsPlus;
using UnityEngine;
using UnityEngine.InputSystem;
using VehiclePhysics;
using Object = UnityEngine.Object;

namespace OverTheHillPlus;

/// <summary>
/// Fly mode, nitro, super jump, instant stop and fling. All of it moves rigidbodies, so it only runs
/// where this game simulates the car: solo or hosting (as host it can also fling other players' cars).
/// </summary>
internal static class Movement
{
    public static bool Flying;
    private static bool _nitroHeld, _jumpQueued, _stopQueued;
    private static float _flingQueued;

    /// <summary>Per frame: read hotkeys (physics happens in <see cref="PhysicsStep"/>).</summary>
    public static void Tick(Keyboard kb)
    {
        if (kb == null || !GameState.PhysicsAllowed)
        {
            // Through SetFlying so the car gets its gravity back (a bare flag change left it floating).
            if (Flying) { try { SetFlying(false); } catch (Exception ex) { DebugLog.Error("cheats", ex, "Movement.Tick"); Flying = false; } }
            _nitroHeld = false;
            return;
        }
        if (kb[CheatsConfig.FlyKey.Value].wasPressedThisFrame) SetFlying(!Flying);
        _nitroHeld = kb[CheatsConfig.NitroKey.Value].isPressed;
        if (kb[CheatsConfig.JumpKey.Value].wasPressedThisFrame) _jumpQueued = true;
    }

    public static void SetFlying(bool on)
    {
        if (on && !GameState.PhysicsAllowed) return;
        Flying = on;
        var rb = GameState.LocalCar?.cachedRigidbody;
        if (rb != null) rb.useGravity = !on;
        if (on) AdvantageFlag.MarkUsed();
    }

    public static void QueueStop() => _stopQueued = true;
    public static void QueueLaunch(float upSpeed) => _flingQueued = upSpeed;

    /// <summary>Called from the car's physics step (local car only).</summary>
    public static void PhysicsStep(VPVehicleController v)
    {
        if (!GameState.PhysicsAllowed) { if (Flying) SetFlying(false); return; }
        var rb = v.cachedRigidbody;
        if (rb == null) return;
        var t = rb.transform;

        if (_stopQueued)
        {
            _stopQueued = false;
            rb.linearVelocity = Vector3.zero;
            rb.angularVelocity = Vector3.zero;
            AdvantageFlag.MarkUsed();
        }
        if (_flingQueued > 0f)
        {
            rb.AddForce(Vector3.up * _flingQueued + t.forward * (_flingQueued * 0.6f), ForceMode.VelocityChange);
            _flingQueued = 0f;
            AdvantageFlag.MarkUsed();
        }
        if (_jumpQueued)
        {
            _jumpQueued = false;
            rb.AddForce(Vector3.up * CheatsConfig.JumpSpeed.Value, ForceMode.VelocityChange);
            AdvantageFlag.MarkUsed();
        }
        if (_nitroHeld && !Flying)
        {
            rb.AddForce(t.forward * CheatsConfig.NitroAcceleration.Value, ForceMode.Acceleration);
            AdvantageFlag.MarkUsed();
        }
        if (Flying) Fly(rb, t);
    }

    private static void Fly(Rigidbody rb, Transform t)
    {
        rb.useGravity = false;
        var kb = Keyboard.current;
        var cam = Camera.main;
        Vector3 fwd = Vector3.ProjectOnPlane(cam != null ? cam.transform.forward : t.forward, Vector3.up).normalized;
        if (fwd.sqrMagnitude < 0.01f) fwd = Vector3.ProjectOnPlane(t.forward, Vector3.up).normalized;
        Vector3 right = Vector3.Cross(Vector3.up, fwd);

        Vector3 dir = Vector3.zero;
        if (kb != null)
        {
            if (kb[Key.W].isPressed) dir += fwd;
            if (kb[Key.S].isPressed) dir -= fwd;
            if (kb[Key.D].isPressed) dir += right;
            if (kb[Key.A].isPressed) dir -= right;
            if (kb[Key.Space].isPressed) dir += Vector3.up;
            if (kb[Key.LeftCtrl].isPressed) dir -= Vector3.up;
        }
        float speed = CheatsConfig.FlySpeed.Value * (kb != null && kb[Key.LeftShift].isPressed ? 3f : 1f);
        Vector3 target = dir.sqrMagnitude > 0f ? dir.normalized * speed : Vector3.zero;
        rb.linearVelocity = Vector3.Lerp(rb.linearVelocity, target, 0.2f);

        // Level out and face the camera direction.
        var wanted = Quaternion.LookRotation(fwd, Vector3.up);
        rb.MoveRotation(Quaternion.Slerp(rb.rotation, wanted, 0.1f));
        rb.angularVelocity = Vector3.zero;
    }

    /// <summary>Host: launch every other car into the air. Solo: there are no others.</summary>
    public static string FlingOthers(float upSpeed)
    {
        if (!GameState.IsAuthority) return "Blocked: only the host simulates other cars.";
        int n = 0;
        foreach (var o in Object.FindObjectsOfType(Il2CppType.Of<VPVehicleController>()))
        {
            var v = o.TryCast<VPVehicleController>();
            if (v == null || GameState.IsLocalCar(v)) continue;
            var rb = v.cachedRigidbody;
            if (rb == null) continue;
            var dir = Vector3.up * upSpeed + UnityEngine.Random.insideUnitSphere * (upSpeed * 0.4f);
            rb.AddForce(dir, ForceMode.VelocityChange);
            n++;
        }
        AdvantageFlag.MarkUsed();
        return n == 0 ? "No other cars nearby." : $"Flung {n} car(s).";
    }
}
