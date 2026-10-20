using System;
using Fusion;
using Fusion.Addons.Physics;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace OverTheHillPlus.Trailers;

/// <summary>
/// The trailers' wheels, grip and brakes, applied right before every physics step Fusion runs
/// (RunnerSimulatePhysics3D.OnBeforeSimulate: also before each resimulated step), on every peer that simulates a
/// trailer: the host, and its owner (who predicts it). Raycast wheels: a spring and damper per wheel, lateral grip
/// that stops sideways sliding up to the tyre's limit, rolling resistance, and the overrun or parking brake. All of it
/// is worked out from the body's pose and velocity, so a resimulated step gives the same forces again. Proxies get no
/// forces (Fusion places them); their wheels are only drawn.
/// </summary>
internal static class TrailerPhysics
{
    /// <summary>What a wheel stands on: Default, Terrain, Objects, Tree, Rock, the plank layer, CampableRock and
    /// DefaultAllowingPlacedTools (not cars, water, triggers or attach points).</summary>
    public const int GroundMask = (1 << 0) | (1 << 8) | (1 << 10) | (1 << 19) | (1 << 20) | (1 << 22) | (1 << 24) | (1 << 26);

    private static RunnerSimulatePhysics3D _sim;
    private static Il2CppSystem.Action _hook;
    private static readonly Il2CppStructArray<RaycastHit> Hits = new(32);

    public static int Steps { get; private set; }

    /// <summary>Hooks the running session's physics step (once per runner).</summary>
    public static void Ensure()
    {
        RunnerSimulatePhysics3D sim = null;
        try { sim = OverlandNetworkRunner.Instance != null ? OverlandNetworkRunner.Instance.RunnerSimulatePhysics3D : null; } catch (Exception ex) { DebugLog.Error("trailers", ex, "TrailerPhysics.Ensure"); }
        if (sim == null)
        {
            _sim = null;
            return;
        }
        if (_sim != null && _sim.Pointer == sim.Pointer) return;
        Unhook();
        _hook ??= DelegateSupport.ConvertDelegate<Il2CppSystem.Action>(new Action(BeforeSimulate));
        try
        {
            sim.add_OnBeforeSimulate(_hook);
            _sim = sim;
            TrailersPlugin.Logger.LogInfo("[Trailers] hooked Fusion's physics step.");
        }
        catch (Exception e) { DebugLog.Error("trailers", e, "TrailerPhysics.Ensure"); TrailersPlugin.LogOnce("hook", "[Trailers] could not hook Fusion's physics step: " + e.Message); }
    }

    public static void Unhook()
    {
        try { if (_sim != null && _hook != null) _sim.remove_OnBeforeSimulate(_hook); } catch (Exception ex) { DebugLog.Error("trailers", ex, "TrailerPhysics.Unhook"); }
        _sim = null;
    }

    private static void BeforeSimulate()
    {
        try
        {
            if (TrailerWorld.Count == 0) return;
            var runner = GameState.Runner;
            float dt = Time.fixedDeltaTime;
            int tick = 0;
            try
            {
                if (runner != null)
                {
                    dt = runner.DeltaTime;
                    tick = runner.Tick.Raw;
                }
            }
            catch (Exception ex) { DebugLog.Error("trailers", ex, "TrailerPhysics.BeforeSimulate"); }
            if (!(dt > 0f)) return;
            Steps++;
            foreach (var t in TrailerWorld.All)
            {
                if (!t.Alive || !t.Simulated) continue;
                try { Step(t, dt, tick); }
                catch (Exception e)
                {
                    DebugLog.Error(TrailersPlugin.ModId, e, "physics step");
                    TrailersPlugin.LogOnce("step", "[Trailers] physics step: " + e.Message);
                }
            }
        }
        catch (Exception e)
        {
            DebugLog.Error(TrailersPlugin.ModId, e, "physics");
            TrailersPlugin.LogOnce("simulate", "[Trailers] physics: " + e.Message);
        }
    }

    private static void Step(TrailerLive t, float dt, int tick)
    {
        var rb = t.Rb;
        var spec = t.Spec;
        Vector3 pos = rb.position;
        Quaternion rot = rb.rotation;
        Vector3 up = rot * Vector3.up, fwd = rot * Vector3.forward, right = rot * Vector3.right;
        int n = t.Wheels.Count;
        float mass = rb.mass;
        float share = mass / Math.Max(n, 1);
        float load = share * WheelMath.G;
        float k = WheelMath.SpringRate(mass, n, spec.Sag);
        float c = WheelMath.DamperRate(k, mass, n, spec.Damping);
        float radius = t.Model.WheelRadius;
        // the ray starts at the hub's highest point (full bump); at the model's rest pose the spring is sagged by Sag
        float reach = spec.Travel + radius + spec.Sag;

        // brake: overrun from the towing car's deceleration while hitched, parking brake when not
        bool hitched = t.Joint != null && t.CarRb != null;
        float trailerSpeed = Vector3.Dot(rb.linearVelocity, fwd);
        if (hitched)
        {
            float carSpeed = Vector3.Dot(t.CarRb.linearVelocity, fwd);
            // only from one tick to the next: a resimulation starts back at an older tick, and the jump in speed
            // from the newest predicted tick to it is not a deceleration
            if (t.HavePrev && tick == t.PrevTick + 1)
            {
                float decel = (t.PrevCarSpeed - carSpeed) / dt;
                t.CarDecel += (decel - t.CarDecel) * 0.35f;   // smoothed: bumps don't pump the brakes
            }
            t.PrevCarSpeed = carSpeed;
            t.PrevTick = tick;
            t.HavePrev = true;
        }
        // the push on the ball, not the car's deceleration alone: climbing, the car slows without the trailer pushing
        float brake = OverrunBrake.Fraction(hitched, OverrunBrake.Push(t.CarDecel, fwd.y), trailerSpeed);
        float brakeGrip = OverrunBrake.BrakeGrip(hitched, spec.Grip);

        foreach (var w in t.Wheels)
        {
            Vector3 top = pos + rot * (w.Local + Vector3.up * spec.Travel);
            if (!Ground(top, -up, reach, t, out var hit)) continue;
            float compression = reach - hit.distance;    // Sag at the model's rest pose, 0 at full droop
            if (compression <= 0f) continue;
            Vector3 contact = hit.point;
            Vector3 v = rb.GetPointVelocity(contact);
            float sus = WheelMath.Suspension(compression, Vector3.Dot(v, up), k, c, load);
            if (sus <= 0f) continue;
            Vector3 nrm = hit.normal;
            Vector3 lat = Vector3.ProjectOnPlane(right, nrm).normalized;
            Vector3 lon = Vector3.Cross(lat, nrm).normalized;   // forward along the ground
            if (Vector3.Dot(lon, fwd) < 0f) lon = -lon;
            float grip = sus * spec.Grip;
            float fl = WheelMath.StopSlip(Vector3.Dot(v, lat), share, dt, grip);
            float fo = WheelMath.Longitudinal(Vector3.Dot(v, lon), sus, spec.Rolling, brake, brakeGrip, share, dt);
            (fl, fo) = WheelMath.FrictionCircle(fl, fo, grip + sus * spec.Rolling);
            rb.AddForceAtPosition(up * sus, contact, ForceMode.Force);
            // grip acts at hub height: the trailer leans less in corners than with the force at the contact patch
            Vector3 hub = pos + rot * w.Local;
            rb.AddForceAtPosition(lat * fl + lon * fo, Vector3.Lerp(contact, hub, 0.6f), ForceMode.Force);
        }
    }

    /// <summary>The nearest ground hit below a point that is not this trailer or its towing car.</summary>
    public static bool Ground(Vector3 from, Vector3 dir, float dist, TrailerLive t, out RaycastHit best)
    {
        IntPtr own = t.Rb != null ? t.Rb.Pointer : IntPtr.Zero;
        IntPtr car = t.CarRb != null ? t.CarRb.Pointer : IntPtr.Zero;
        return Ground(from, dir, dist, own, car, t.IgnoredCar != 0 && t.CarRb == null ? t : null, out best);
    }

    /// <summary>The nearest ground hit along a ray, leaving out two bodies (and, with <paramref name="ignoredOf"/>, the
    /// colliders of the car that trailer ignores). The hit buffer holds 32: Unity returns hits unsorted and drops
    /// the ones past the buffer, so it is big enough for dense ground.</summary>
    public static bool Ground(Vector3 from, Vector3 dir, float dist, IntPtr own, IntPtr car, TrailerLive ignoredOf, out RaycastHit best)
    {
        best = default;
        int count;
        try { count = Physics.RaycastNonAlloc(from, dir, Hits, dist, GroundMask, QueryTriggerInteraction.Ignore); }
        catch (Exception ex) { DebugLog.Error("trailers", ex, "TrailerPhysics.Ground"); return false; }
        float bestDist = float.MaxValue;
        bool found = false;
        for (int i = 0; i < count && i < Hits.Length; i++)
        {
            var h = Hits[i];
            if (h.distance >= bestDist) continue;
            var body = h.rigidbody;
            if (body != null && (body.Pointer == own || body.Pointer == car)) continue;
            if (body != null && ignoredOf != null && IsIgnored(ignoredOf, h.collider)) continue;
            best = h;
            bestDist = h.distance;
            found = true;
        }
        return found;
    }

    private static bool IsIgnored(TrailerLive t, Collider c)
    {
        if (c == null) return false;
        foreach (var x in t.IgnoredWith)
            if (x != null && x.Pointer == c.Pointer) return true;
        return false;
    }

    // ---------------------------------------------------------------- drawn wheels (every peer, every frame)

    /// <summary>Drops each drawn wheel onto the ground under it (within the suspension's travel) and spins it with the
    /// trailer's speed. Proxies too: the wheel is a child of the body Fusion places.</summary>
    public static void DrawWheels(TrailerLive t, float dt)
    {
        var spec = t.Spec;
        float radius = t.Model.WheelRadius;
        if (!(radius > 0.05f) || !(dt > 0f)) return;
        var tr = t.T;
        Vector3 up = tr.up;
        // proxies are placed by Fusion (their body reads no velocity): the speed comes from how far they moved
        Vector3 p = tr.position;
        float speed = t.HaveLast ? Vector3.Dot(p - t.LastPos, tr.forward) / dt : 0f;
        t.LastPos = p;
        t.HaveLast = true;
        float reach = spec.Travel + radius + spec.Sag;
        foreach (var w in t.Wheels)
        {
            if (w.Node == null) continue;
            Vector3 top = tr.TransformPoint(w.Local + Vector3.up * spec.Travel);
            // offset of the hub from the model's rest pose: -Sag hanging free, up to +Travel at full bump
            bool onGround = Ground(top, -up, reach, t, out var hit);
            float offset = onGround ? Mathf.Clamp(reach - hit.distance - spec.Sag, -spec.Sag, spec.Travel) : -spec.Sag;
            TrailerTrax.Update(w, onGround, dt);
            w.VisualDrop = Mathf.Lerp(w.VisualDrop, offset, 0.5f);
            w.Node.localPosition = w.Local + Vector3.up * w.VisualDrop;
            w.Angle = (w.Angle + speed / radius * dt * Mathf.Rad2Deg) % 360f;
            if (w.Spin != null)
            {
                // right-side wheels are yawed 180 degrees: the same forward roll is the other way round their x
                bool right = w.Local.x > 0f;
                w.Spin.localRotation = Quaternion.Euler(right ? -w.Angle : w.Angle, 0f, 0f);
            }
        }
    }
}
