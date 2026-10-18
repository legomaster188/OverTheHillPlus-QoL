using System;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using OverTheHillPlus.CheatsPlus;
using UnityEngine;
using VehiclePhysics;

namespace OverTheHillPlus.Features.Cheats.Fun;

/// <summary>
/// The physics side of the Fun tab. Runs once per simulation tick from the car's physics step
/// (VPVehicleController.DoUpdateBlocks, the same hook the driving assists use), only for the local car and only
/// when <see cref="GameState.PhysicsAllowed"/>. Everything is a force on the local car's rigidbody, so no other
/// car or global physics setting is touched.
/// </summary>
internal static class FunPhysicsStep
{
    private const int MaxWheels = 16;

    // Telemetry for the menu / overlay.
    public static bool Stuck;
    public static int StuckWheels;
    public static float TiltDegrees;
    public static string RearWheelSummary = "-";

    // Wheel layout cache (per car).
    private static IntPtr _layoutFor;
    private static readonly bool[] Rear = new bool[MaxWheels];
    private static int _layoutWheels;

    // Landing / impact tracking.
    private static int _prevGrounded = -1;
    private static Vector3 _prevVelocity;
    private static float _lastBounce;

    // Auto recover.
    private static float _upsideDownSince = -1f;

    // Drift friction bookkeeping (see DriftFriction patches below).
    internal static readonly float[] GameFriction = CreateOnes();
    internal static bool DriftNow;
    internal static bool InSelfCall;
    private static bool _driftApplied;
    private static IntPtr _driftCar;

    private static Il2CppStructArray<RaycastHit> _hits;
    private static bool _loggedError;

    private static float[] CreateOnes()
    {
        var a = new float[MaxWheels];
        for (int i = 0; i < a.Length; i++) a[i] = 1f;
        return a;
    }

    internal static bool IsRear(int wheel) => wheel >= 0 && wheel < MaxWheels && Rear[wheel];

    /// <summary>Called every physics tick for every simulated car.</summary>
    internal static void Step(VPVehicleController v)
    {
        if (!GameState.IsLocalCar(v)) return;
        try { StepLocal(v); }
        catch (Exception e)
        {
            if (_loggedError) return;
            _loggedError = true;
            CheatsPlugin.Logger.LogError("Fun physics step failed (further errors suppressed): " + e);
        }
    }

    private static void StepLocal(VPVehicleController v)
    {
        bool allowed = GameState.PhysicsAllowed;
        UpdateDrift(v, allowed);

        var rb = v.cachedRigidbody;
        if (rb == null) return;
        var t = rb.transform;
        Vector3 up = t.up;
        TiltDegrees = Vector3.Angle(up, Vector3.up);

        if (!allowed || !FunPhysics.AnyCarModeOn)
        {
            Stuck = false;
            StuckWheels = 0;
            _prevGrounded = -1;
            _upsideDownSince = -1f;
            return;
        }
        OverTheHillPlus.AdvantageFlag.MarkUsed();

        // Wheels on the ground and the average contact normal under them.
        // Read the array once: every wheelState getter call makes a new interop wrapper.
        var states = v.wheelState;
        int wheels = Math.Min(Math.Min(v.wheelCount, states?.Length ?? 0), MaxWheels);
        int grounded = 0;
        Vector3 normalSum = Vector3.zero;
        for (int i = 0; i < wheels; i++)
        {
            var ws = states[i];
            if (ws == null || !ws.grounded) continue;
            grounded++;
            try
            {
                var n = ws.wheelContact.normal;
                if (n.sqrMagnitude > 0.25f) normalSum += n;
            }
            catch (Exception ex) { DebugLog.Error("cheats", ex, "FunPhysicsStep.StepLocal"); }
        }
        Vector3 groundNormal = normalSum.sqrMagnitude > 0.01f ? normalSum.normalized : up;

        Vector3 gravity = Physics.gravity;
        float g = gravity.magnitude;
        bool usesGravity = rb.useGravity; // fly mode turns gravity off; leave the car alone then

        // ---- wall-climb ----
        Stuck = false;
        StuckWheels = grounded;
        if (FunPhysics.WallClimb.Value && usesGravity)
        {
            Vector3 surfaceNormal = groundNormal;
            bool viaRay = false;
            if (grounded == 0 && Probe(rb, rb.worldCenterOfMass, -up, FunPhysics.StickReach.Value + 1f, out var hit))
            {
                surfaceNormal = hit.normal;
                viaRay = true;
            }
            Stuck = grounded > 0 || viaRay;
            if (Stuck)
            {
                float stick = FunPhysics.StickStrength.Value * (g > 0.1f ? g : 9.81f);
                rb.AddForce(-gravity, ForceMode.Acceleration);                 // cancel gravity while stuck
                rb.AddForce(-surfaceNormal * stick, ForceMode.Acceleration);   // press onto the surface
                if (viaRay) AlignUp(rb, up, surfaceNormal, 6f, 2f);            // land wheels-first

                // Climb assist: a steep surface right in front (or behind when reversing) tips the car onto it.
                if (FunPhysics.ClimbAssist.Value && grounded > 0)
                {
                    Vector3 vel = rb.linearVelocity;
                    float along = Vector3.Dot(vel, t.forward);
                    if (Mathf.Abs(along) > 1f)
                    {
                        Vector3 dir = t.forward * Mathf.Sign(along);
                        float reach = 1.5f + Mathf.Min(Mathf.Abs(along) * 0.12f, 3f);
                        if (Probe(rb, rb.worldCenterOfMass, dir, reach + HalfLength(v, t), out var wall)
                            && Vector3.Angle(wall.normal, up) > 25f && Vector3.Dot(wall.normal, dir) < -0.3f)
                            AlignUp(rb, up, wall.normal, 9f, 2.5f);
                    }
                }
            }
        }

        // ---- moon / custom gravity (only while not held by wall-climb, which cancels gravity itself) ----
        if (FunPhysics.Moon.Value && usesGravity && !Stuck)
        {
            float scale = Mathf.Clamp(FunPhysics.GravityScale.Value, 0f, 3f);
            if (Mathf.Abs(scale - 1f) > 0.001f) rb.AddForce(gravity * (scale - 1f), ForceMode.Acceleration);
        }

        // ---- unflippable ----
        if (FunPhysics.Unflippable.Value && !Stuck) Unflip(v, rb, up, grounded);
        else _upsideDownSince = -1f;

        // ---- bouncy landings ----
        Vector3 velocity = rb.linearVelocity;
        if (FunPhysics.Bouncy.Value && FunPhysics.BouncyLandings.Value && _prevGrounded == 0 && grounded >= 2)
        {
            float into = -Vector3.Dot(_prevVelocity, groundNormal);
            if (into > 3f && Time.time - _lastBounce > 0.2f)
            {
                float outSpeed = Mathf.Min(into * FunPhysics.Bounciness.Value, 30f);
                float now = Vector3.Dot(velocity, groundNormal);
                if (outSpeed > now) rb.AddForce(groundNormal * (outSpeed - now), ForceMode.VelocityChange);
                _lastBounce = Time.time;
            }
        }
        _prevGrounded = grounded;
        _prevVelocity = velocity;
    }

    // ---- unflippable ----

    private static void Unflip(VPVehicleController v, Rigidbody rb, Vector3 up, int grounded)
    {
        float s = FunPhysics.UprightStrength.Value;
        float tilt = TiltDegrees;
        if (grounded == 0)
        {
            // In the air: steer gently towards wheels-down so every landing is on the wheels.
            if (tilt > 8f) AlignUp(rb, up, Vector3.up, 5f * s, 1.6f * s);
        }
        else if (grounded < 3 && tilt > 55f)
        {
            // Tipping over on two wheels or lying on the side/roof: push back over.
            AlignUp(rb, up, Vector3.up, 14f * s, 2.5f * s);
        }

        if (!FunPhysics.AutoRecover.Value) { _upsideDownSince = -1f; return; }
        bool stuckOnRoof = tilt > 100f && rb.linearVelocity.magnitude < 2.5f;
        if (!stuckOnRoof) { _upsideDownSince = -1f; return; }
        if (_upsideDownSince < 0f) { _upsideDownSince = Time.unscaledTime; return; }
        if (Time.unscaledTime - _upsideDownSince < 1.5f) return;
        _upsideDownSince = -1f;
        _recoverQueued = true; // done from Update, outside the physics step (like the Flip hotkey)
    }

    private static bool _recoverQueued;

    /// <summary>Per frame (Update): performs a queued auto-recover on the local car.</summary>
    internal static void RunQueued()
    {
        if (!_recoverQueued) return;
        _recoverQueued = false;
        var v = GameState.LocalCar;
        if (v == null || !GameState.PhysicsAllowed || !FunPhysics.Unflippable.Value) return;
        try
        {
            var t = v.cachedTransform != null ? v.cachedTransform : v.transform;
            Vector3 fwd = Vector3.ProjectOnPlane(t.forward, Vector3.up);
            if (fwd.sqrMagnitude < 0.01f) fwd = Vector3.ProjectOnPlane(-t.up, Vector3.up);
            if (fwd.sqrMagnitude < 0.01f) fwd = Vector3.forward;
            v.Reposition(t.position + Vector3.up * 1.5f, Quaternion.LookRotation(fwd.normalized, Vector3.up));
            CheatsPlugin.Logger.LogInfo("Fun: unflippable put the car back on its wheels");
        }
        catch (Exception e) { DebugLog.Error("cheats", e, "FunPhysicsStep.RunQueued"); CheatsPlugin.Logger.LogWarning("Fun: auto recover failed: " + e.Message); }
    }

    /// <summary>Angular acceleration that turns <paramref name="up"/> towards <paramref name="target"/>, damped.</summary>
    private static void AlignUp(Rigidbody rb, Vector3 up, Vector3 target, float k, float damping)
    {
        Vector3 axis = Vector3.Cross(up, target);
        float sin = axis.magnitude;
        float cos = Vector3.Dot(up, target);
        float angle = Mathf.Atan2(sin, cos); // 0..pi
        if (sin < 1e-4f)
        {
            if (cos > 0f) return;           // already aligned
            axis = rb.transform.forward;    // upside down exactly: roll over sideways
        }
        axis.Normalize();
        Vector3 w = rb.angularVelocity;
        Vector3 spinAroundTarget = Vector3.Project(w, target); // yaw about the target normal stays free
        Vector3 torque = axis * (angle * k) - (w - spinAroundTarget) * damping;
        if (torque.sqrMagnitude > 2500f) torque = torque.normalized * 50f;
        rb.AddTorque(torque, ForceMode.Acceleration);
    }

    /// <summary>Raycast that ignores the car's own colliders and triggers.</summary>
    private static bool Probe(Rigidbody self, Vector3 origin, Vector3 dir, float distance, out RaycastHit best)
    {
        best = default;
        _hits ??= new Il2CppStructArray<RaycastHit>(12);
        int n = Physics.RaycastNonAlloc(new Ray(origin, dir), _hits, distance, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
        float bestDist = float.MaxValue;
        bool found = false;
        for (int i = 0; i < n && i < _hits.Length; i++)
        {
            var h = _hits[i];
            if (h.distance >= bestDist) continue;
            var col = h.collider;
            if (col == null) continue;
            var arb = col.attachedRigidbody;
            if (arb != null && arb.Pointer == self.Pointer) continue;
            best = h;
            bestDist = h.distance;
            found = true;
        }
        return found;
    }

    private static float _halfLength = 2f;
    private static IntPtr _halfLengthFor;

    private static float HalfLength(VPVehicleController v, Transform t)
    {
        if (_halfLengthFor == v.Pointer) return _halfLength;
        _halfLengthFor = v.Pointer;
        _halfLength = 2f;
        try
        {
            float max = 0f;
            var states = v.wheelState;
            int wheels = Math.Min(Math.Min(v.wheelCount, states?.Length ?? 0), MaxWheels);
            for (int i = 0; i < wheels; i++)
            {
                var wc = states[i]?.wheelCol;
                if (wc == null) continue;
                max = Mathf.Max(max, Mathf.Abs(t.InverseTransformPoint(wc.transform.position).z));
            }
            if (max > 0.3f) _halfLength = max + 0.6f;
        }
        catch (Exception ex) { DebugLog.Error("cheats", ex, "FunPhysicsStep.HalfLength"); }
        return _halfLength;
    }

    // ---- drift ----

    private static void UpdateDrift(VPVehicleController v, bool allowed)
    {
        bool want = allowed && FunPhysics.Drift.Value && FunPhysics.DriftKeyHeld;
        if (want) EnsureLayout(v);
        if (!want && !_driftApplied) { DriftNow = false; return; }

        // Car changed under us: the old one is gone or someone else's now; start fresh.
        if (_driftApplied && _driftCar != v.Pointer) _driftApplied = false;

        DriftNow = want;
        int wheels = Math.Min(v.wheelCount, MaxWheels);
        // Re-send the game's own multiplier; the prefix below scales it while DriftNow is set, and when DriftNow
        // is false this one call hands the game's value back.
        InSelfCall = true;
        try { for (int i = 0; i < wheels; i++) v.SetWheelTireFrictionMultiplier(i, GameFriction[i]); }
        finally { InSelfCall = false; }
        _driftApplied = want;
        _driftCar = v.Pointer;
    }

    private static void EnsureLayout(VPVehicleController v)
    {
        int wheels = Math.Min(v.wheelCount, MaxWheels);
        if (_layoutFor == v.Pointer && _layoutWheels == wheels) return;
        _layoutFor = v.Pointer;
        _layoutWheels = wheels;
        Array.Clear(Rear, 0, Rear.Length);
        int rearCount = 0;
        try
        {
            var rb = v.cachedRigidbody;
            var t = rb != null ? rb.transform : v.transform;
            Vector3 com = rb != null ? rb.worldCenterOfMass : t.position;
            bool anyPos = false;
            var states = v.wheelState;
            int n = Math.Min(wheels, states?.Length ?? 0);
            for (int i = 0; i < n; i++)
            {
                var wc = states[i]?.wheelCol;
                if (wc == null) continue;
                anyPos = true;
                Rear[i] = Vector3.Dot(wc.transform.position - com, t.forward) < 0f;
            }
            if (!anyPos) for (int i = wheels / 2; i < wheels; i++) Rear[i] = true; // VPP lists the front axle first
        }
        catch (Exception ex)
        {
            DebugLog.Error("cheats", ex, "FunPhysicsStep.EnsureLayout");
            for (int i = wheels / 2; i < wheels; i++) Rear[i] = true;
        }
        for (int i = 0; i < wheels; i++) if (Rear[i]) rearCount++;
        RearWheelSummary = $"{rearCount} of {wheels}";
    }
}

/// <summary>Fun physics runs in the same physics step as the driving assists (own patch, independent of theirs).</summary>
[HarmonyPatch(typeof(VPVehicleController), nameof(VPVehicleController.DoUpdateBlocks))]
internal static class FunDoUpdateBlocksPatch
{
    private static void Prefix(VPVehicleController __instance) => FunPhysicsStep.Step(__instance);
}

/// <summary>
/// Records the friction multiplier the game itself sets on the local car's wheels. Runs before every other prefix
/// (including the driving assists' grip patch) so it sees the game's raw value.
/// </summary>
[HarmonyPatch(typeof(VehicleBase), nameof(VehicleBase.SetWheelTireFrictionMultiplier))]
internal static class FunFrictionRecordPatch
{
    [HarmonyPriority(Priority.First)]
    private static void Prefix(VehicleBase __instance, int wheelIndex, float frictionMultiplier)
    {
        if (FunPhysicsStep.InSelfCall || wheelIndex < 0 || wheelIndex >= FunPhysicsStep.GameFriction.Length) return;
        if (!GameState.IsLocalCar(__instance)) return;
        FunPhysicsStep.GameFriction[wheelIndex] = frictionMultiplier;
    }
}

/// <summary>
/// Scales the multiplier for drift mode. Runs after every other prefix (after the assists' grip multiplier), so
/// drift stacks on top of it and the assists still record the game's own value.
/// </summary>
[HarmonyPatch(typeof(VehicleBase), nameof(VehicleBase.SetWheelTireFrictionMultiplier))]
internal static class FunFrictionDriftPatch
{
    [HarmonyPriority(Priority.Last)]
    private static void Prefix(VehicleBase __instance, int wheelIndex, ref float frictionMultiplier)
    {
        if (!FunPhysicsStep.DriftNow || !GameState.IsLocalCar(__instance)) return;
        frictionMultiplier *= FunPhysicsStep.IsRear(wheelIndex)
            ? Mathf.Clamp(FunPhysics.DriftRearGrip.Value, 0.05f, 1f)
            : Mathf.Clamp(FunPhysics.DriftFrontGrip.Value, 0.5f, 2f);
    }
}

/// <summary>
/// Bouncy car: body hits throw the car back off what it hit. VehicleBase.OnCollisionEnter is the car's own Unity
/// collision message (it runs on the simulating machine, i.e. solo/host, for every car), so it is filtered to the
/// local car. Wheels are not colliders, so landings on the wheels are handled in the physics step instead.
/// </summary>
[HarmonyPatch(typeof(VehicleBase), nameof(VehicleBase.OnCollisionEnter))]
internal static class FunBouncyCollisionPatch
{
    private static float _last;
    private static bool _loggedError;

    private static void Postfix(VehicleBase __instance, Collision collision)
    {
        var bouncy = FunPhysics.Bouncy; // null if FunPhysics failed to register: never throw into the game's callback
        if (bouncy == null || !bouncy.Value || collision == null) return;
        try
        {
            if (!GameState.PhysicsAllowed || !GameState.IsLocalCar(__instance)) return;
            if (Time.time - _last < 0.12f) return;
            var rb = __instance.cachedRigidbody;
            if (rb == null || collision.contactCount <= 0) return;

            // Average the contact normals and orient them from the surface towards the car.
            Vector3 com = rb.worldCenterOfMass;
            Vector3 n = Vector3.zero;
            int count = Math.Min(collision.contactCount, 4);
            for (int i = 0; i < count; i++)
            {
                var c = collision.GetContact(i);
                Vector3 cn = c.normal;
                if (Vector3.Dot(cn, com - c.point) < 0f) cn = -cn;
                n += cn;
            }
            if (n.sqrMagnitude < 0.01f) return;
            n.Normalize();

            float approach = Mathf.Abs(Vector3.Dot(collision.relativeVelocity, n));
            if (approach < 2f) return;
            float outSpeed = Mathf.Min(approach * Mathf.Clamp(FunPhysics.Bounciness.Value, 0.1f, 1.6f), 35f);
            float now = Vector3.Dot(rb.linearVelocity, n);
            if (outSpeed <= now) return;
            rb.AddForce(n * (outSpeed - now), ForceMode.VelocityChange);
            _last = Time.time;
            OverTheHillPlus.AdvantageFlag.MarkUsed();
        }
        catch (Exception e)
        {
            if (_loggedError) return;
            _loggedError = true;
            CheatsPlugin.Logger.LogError("Fun: bouncy collision failed (further errors suppressed): " + e);
        }
    }
}
