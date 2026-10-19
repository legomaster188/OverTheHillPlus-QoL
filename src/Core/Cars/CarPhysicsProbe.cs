using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;
using VehiclePhysics;

namespace OverTheHillPlus.Cars;

/// <summary>
/// The modded cars' physics log ([Debug] CarPhysicsLog in oth.core.cfg, off by default), for checking a car in game
/// without a debugger. Once per spawned car it writes what the physics really runs on (the rigidbody, the inertia box,
/// brakes, converter and engine, each wheel as configured and as the suspension solver sees it), every collider with its
/// place and size on the car, and every light, flare and particle emitter with its place. Then, while the car stands
/// still, every 2 s: how much the body bounces, pitches and rolls, how often each wheel's spin changes direction, the
/// engine's rpm range and each wheel's spring force range. Lines go to the car mod's log (BepInEx/LogOutput.log), each
/// starting with "physics". Costs nothing while the switch is off.
/// </summary>
internal static class CarPhysicsProbe
{
    private sealed class Watched
    {
        public RuntimeCar Car;
        public FusionPlayer Player;
        public VPVehicleController Vehicle;
        public Rigidbody Body;
        public float WindowStart;
        public int Lines, Samples;
        public double Vy2, Pitch2, Roll2;
        public float RpmMin = float.MaxValue, RpmMax = float.MinValue, MaxSpeed;
        public float[] LastSpin = new float[4];
        public int[] Flips = new int[4];
        public float[] SpringMin = { float.MaxValue, float.MaxValue, float.MaxValue, float.MaxValue };
        public float[] SpringMax = { float.MinValue, float.MinValue, float.MinValue, float.MinValue };
        public float[] CompMax = new float[4];
    }

    private static readonly Dictionary<IntPtr, Watched> Cars = new();
    private static readonly HashSet<IntPtr> Dumped = new();
    private const int MaxLinesPerCar = 40;

    public static bool Enabled => Logging.DebugConfig.Get(Logging.DebugConfig.CarPhysicsLog, false);

    private static string F(float v) => v.ToString("0.###", CultureInfo.InvariantCulture);
    private static string V(Vector3 v) => "(" + F(v.x) + ", " + F(v.y) + ", " + F(v.z) + ")";

    /// <summary>Called by the car's tuning tick for every live copy: the first time a copy is seen, its spawn dump.</summary>
    public static void Watch(RuntimeCar car, FusionPlayer fp, VPVehicleController v)
    {
        if (car == null || fp == null || v == null) return;
        var key = v.Pointer;
        if (Cars.ContainsKey(key)) return;
        var w = new Watched { Car = car, Player = fp, Vehicle = v, Body = v.GetComponent<Rigidbody>(), WindowStart = Time.unscaledTime };
        Cars[key] = w;
        if (Dumped.Add(key))
        {
            try { Dump(w); }
            catch (Exception e) { DebugLog.Error("core", e, "CarPhysicsProbe.Dump"); car.Info("physics: dump failed: " + e.Message); }
        }
    }

    /// <summary>Every frame (the framework's shared tick).</summary>
    public static void Tick()
    {
        if (Cars.Count == 0) return;
        if (!Enabled) { Cars.Clear(); return; }
        List<IntPtr> gone = null;
        foreach (var kv in Cars)
        {
            var w = kv.Value;
            try
            {
                if (w.Vehicle == null || w.Body == null || !w.Vehicle.isActiveAndEnabled) { (gone ??= new List<IntPtr>()).Add(kv.Key); continue; }
                Sample(w);
            }
            catch (Exception e) { DebugLog.Error("core", e, "CarPhysicsProbe.Tick"); (gone ??= new List<IntPtr>()).Add(kv.Key); }
        }
        if (gone != null) foreach (var k in gone) Cars.Remove(k);
    }

    private static void Sample(Watched w)
    {
        var rb = w.Body;
        var t = w.Vehicle.transform;
        var vel = rb.velocity;
        var ang = t.InverseTransformDirection(rb.angularVelocity);
        w.MaxSpeed = Mathf.Max(w.MaxSpeed, vel.magnitude);
        w.Vy2 += vel.y * vel.y;
        w.Pitch2 += ang.x * ang.x;
        w.Roll2 += ang.z * ang.z;
        w.Samples++;
        try
        {
            var eng = w.Vehicle.m_engine;
            if (eng != null)
            {
                float rpm = eng.sensorRpm;
                w.RpmMin = Mathf.Min(w.RpmMin, rpm);
                w.RpmMax = Mathf.Max(w.RpmMax, rpm);
            }
        }
        catch (Exception e) { DebugLog.Error("core", e, "CarPhysicsProbe.Sample engine"); }
        var ws = w.Vehicle.wheelState;
        int n = ws == null ? 0 : Math.Min(4, ws.Length);
        for (int i = 0; i < n; i++)
        {
            var s = ws[i];
            if (s == null) continue;
            float spin = s.angularVelocity;
            if (Mathf.Abs(spin) > 0.05f && Mathf.Abs(w.LastSpin[i]) > 0.05f && Mathf.Sign(spin) != Mathf.Sign(w.LastSpin[i])) w.Flips[i]++;
            if (Mathf.Abs(spin) > 0.05f) w.LastSpin[i] = spin;
            var ss = s.suspensionState;
            w.SpringMin[i] = Mathf.Min(w.SpringMin[i], ss.springForce);
            w.SpringMax[i] = Mathf.Max(w.SpringMax[i], ss.springForce);
            w.CompMax[i] = Mathf.Max(w.CompMax[i], ss.compressionRatio);
        }

        float now = Time.unscaledTime;
        if (now - w.WindowStart < 2f) return;
        // a line for a window in which the car stood still (that is when it should not move at all)
        if (w.MaxSpeed < 0.5f && w.Lines < MaxLinesPerCar && w.Samples > 0)
        {
            w.Lines++;
            var sb = new StringBuilder("physics: standing still ");
            sb.Append(w.Samples).Append(" frames: bounce ").Append(F((float)Math.Sqrt(w.Vy2 / w.Samples))).Append(" m/s rms, pitch ")
              .Append(F((float)Math.Sqrt(w.Pitch2 / w.Samples))).Append(" rad/s rms, roll ").Append(F((float)Math.Sqrt(w.Roll2 / w.Samples)))
              .Append(" rad/s rms, top speed ").Append(F(w.MaxSpeed)).Append(" m/s; engine ")
              .Append(w.RpmMin <= w.RpmMax ? F(w.RpmMin) + ".." + F(w.RpmMax) + " rpm" : "?");
            for (int i = 0; i < n; i++)
                sb.Append("; wheel ").Append(i).Append(" spin flips ").Append(w.Flips[i]).Append(", spring ")
                  .Append(F(w.SpringMin[i])).Append("..").Append(F(w.SpringMax[i])).Append(" N, compression max ").Append(F(w.CompMax[i]));
            sb.Append(" (a still car: bounce and pitch under 0.01, no spin flips, rpm steady)");
            w.Car.Info(sb.ToString());
        }
        w.WindowStart = now;
        w.Samples = 0; w.Vy2 = w.Pitch2 = w.Roll2 = 0; w.MaxSpeed = 0f;
        w.RpmMin = float.MaxValue; w.RpmMax = float.MinValue;
        for (int i = 0; i < 4; i++) { w.Flips[i] = 0; w.SpringMin[i] = float.MaxValue; w.SpringMax[i] = float.MinValue; w.CompMax[i] = 0f; }
    }

    // ---------------------------------------------------------------- the spawn dump

    private static void Dump(Watched w)
    {
        var car = w.Car;
        var v = w.Vehicle;
        var rt = v.transform;
        var rb = w.Body;
        var sb = new StringBuilder();
        if (rb != null)
        {
            sb.Append("physics: rigidbody mass ").Append(F(rb.mass)).Append(" kg, centre of mass ").Append(V(rb.centerOfMass))
              .Append(", inertia ").Append(V(rb.inertiaTensor)).Append(", interpolation ").Append(rb.interpolation)
              .Append(", solver ").Append(rb.solverIterations).Append("/").Append(rb.solverVelocityIterations)
              .Append(", sleep threshold ").Append(F(rb.sleepThreshold)).Append(", max depenetration ").Append(F(rb.maxDepenetrationVelocity))
              .Append(", collision ").Append(rb.collisionDetectionMode).Append(", fixed step ").Append(F(Time.fixedDeltaTime)).Append(" s");
        }
        try
        {
            var inr = v.inertia;
            if (inr != null) sb.Append("; inertia box ").Append(inr.mode).Append(" ").Append(V(inr.chassisDimensions));
            sb.Append("; substeps ").Append(v.solverSubsteps).Append(", spring rate limit factor ").Append(F(v.springRateLimitFactor))
              .Append(", compression limit ").Append(F(v.suspensionCompressionLimit));
        }
        catch (Exception e) { DebugLog.Error("core", e, "CarPhysicsProbe.Dump inertia"); }
        try
        {
            var b = v.brakes;
            var c = v.clutch;
            var e = v.engine;
            if (b != null)
            {
                float r = WheelRadius(v);
                sb.Append("; brakes ").Append(F(b.maxBrakeTorque)).Append(" N m, handbrake ").Append(F(b.handbrakeTorque))
                  .Append(" N m (holds ").Append(F(CarPhysicsFit.HoldSlopeDegrees(b.handbrakeTorque, rb != null ? rb.mass : 0f, r))).Append(" deg)");
            }
            if (c != null)
                sb.Append("; converter ").Append(c.type).Append(" ").Append(F(c.maxTorqueTransfer)).Append(" N m, lock ")
                  .Append(F(c.unlockRpm)).Append("..").Append(F(c.lockRpm)).Append(" rpm");
            if (e != null)
                sb.Append("; engine ").Append(F(e.idleRpmTorque)).Append("/").Append(F(e.peakRpmTorque)).Append(" N m at ")
                  .Append(F(e.idleRpm)).Append("/").Append(F(e.peakRpm)).Append(" rpm, cap ").Append(F(e.torqueCapLimit)).Append(" N m / ")
                  .Append(F(e.powerCapLimit)).Append(" kW, inertia ").Append(F(e.inertia)).Append(", friction ").Append(F(e.frictionTorque))
                  .Append(", idle control ").Append(e.idleControl);
        }
        catch (Exception e) { DebugLog.Error("core", e, "CarPhysicsProbe.Dump drivetrain"); }
        sb.Append("; ").Append(SharedReport(car, v));
        car.Info(sb.ToString() + ".");

        // wheels: as configured (the behaviour) and as the solver sees them (the suspension state)
        var ws = v.wheelState;
        int n = ws == null ? 0 : ws.Length;
        for (int i = 0; i < n; i++)
        {
            try
            {
                var s = ws[i];
                var wc = s?.wheelCol?.TryCast<VPFunselektor3DRaycastWheelCollider>();
                if (wc == null) continue;
                var ss = s.suspensionState;
                var api = wc.m_wheelCollider;
                float sprung = ss.estimatedSprungMass;
                car.Info("physics: wheel " + i + " " + wc.name + " at " + V(rt.InverseTransformPoint(wc.transform.position)) +
                         ": radius " + F(wc.radius) + ", inertia " + F(wc.inertia) + ", spring " + F(wc.springRate) + " (solver " + F(ss.springRate) +
                         (api != null ? ", limit " + F(api.springRateLimit) : "") + "), damper " + F(wc.damperRate) +
                         ", unsprung " + F(wc.unsprungMass) + " kg, bump stop " + F(wc.bumpStopRate) + ", travel " + F(wc.suspensionTravel) +
                         (api != null ? ", compression limit " + F(api.suspensionCompressionLimit) : "") + "; sprung mass " + F(sprung) +
                         " kg, damping ratio " + F(CarPhysicsFit.DampingRatio(ss.springRate > 0f ? ss.springRate : wc.springRate, wc.damperRate, sprung)) +
                         ", compression " + F(ss.compressionRatio) + ", contact depth " + F(ss.contactDepth) + ", load " + F(ss.wheelLoad) + " N.");
            }
            catch (Exception e) { DebugLog.Error("core", e, "CarPhysicsProbe.Dump wheel"); }
        }

        // colliders: every one, where it is and how big (car-root space)
        var lines = new List<string>();
        foreach (var c in rt.GetComponentsInChildren<Collider>(true))
        {
            if (c == null) continue;
            bool live = c.enabled && c.gameObject.activeInHierarchy;
            if (!live && !c.gameObject.activeInHierarchy && Path(rt, c.transform).StartsWith("Parts/", StringComparison.Ordinal)) continue; // hidden cosmetics
            var b = RootBox(rt, c.bounds);
            string kind = c.TryCast<MeshCollider>() is { } mc ? "mesh " + (mc.sharedMesh != null ? mc.sharedMesh.name : "-") :
                          c.TryCast<BoxCollider>() != null ? "box" : c.TryCast<SphereCollider>() != null ? "sphere" :
                          c.TryCast<CapsuleCollider>() != null ? "capsule" : c.GetIl2CppType().Name;
            lines.Add(Path(rt, c.transform) + " [" + kind + (c.isTrigger ? ", trigger" : "") + ", layer " + c.gameObject.layer +
                      (live ? "" : ", off") + "] " + V(b.min) + ".." + V(b.max));
        }
        car.Info("physics: colliders (car-root space, solid and on first): " + string.Join("; ", Sorted(lines)) + ".");

        // lights, flares and emitters
        lines.Clear();
        foreach (var l in rt.GetComponentsInChildren<Light>(true))
            lines.Add(Path(rt, l.transform) + " light " + l.type + (l.enabled ? "" : " off") + " intensity " + F(l.intensity) + " at " + V(rt.InverseTransformPoint(l.transform.position)));
        foreach (var f in rt.GetComponentsInChildren<UnityEngine.Rendering.LensFlareComponentSRP>(true))
            lines.Add(Path(rt, f.transform) + " flare" + (f.enabled ? "" : " off") + " intensity " + F(f.intensity) + " at " + V(rt.InverseTransformPoint(f.transform.position)));
        foreach (var p in rt.GetComponentsInChildren<ParticleSystem>(true))
        {
            var parent = p.transform.parent;
            if (parent != null && parent.GetComponent<ParticleSystem>() != null) continue; // sub-emitters move with their system
            lines.Add(Path(rt, p.transform) + " particles" + (p.gameObject.activeInHierarchy ? "" : " off") + " at " + V(rt.InverseTransformPoint(p.transform.position)));
        }
        car.Info("physics: lights, flares and emitters (car-root space): " + string.Join("; ", Sorted(lines)) + ".");
    }

    /// <summary>Whether the car's settings objects are its class setup's own (one asset for every car of the class): a
    /// value written into a shared one would change every stock car of the class too.</summary>
    private static string SharedReport(RuntimeCar car, VPVehicleController v)
    {
        try
        {
            var setup = car.Catalog.Info != null ? car.Catalog.Info.carCategorySetup : null;
            if (setup == null) return "class setup unknown";
            var mine = new Dictionary<string, IntPtr>
            {
                ["engine"] = v.engine != null ? v.engine.Pointer : IntPtr.Zero,
                ["speed control"] = v.speedControl != null ? v.speedControl.Pointer : IntPtr.Zero,
                ["brakes"] = v.brakes != null ? v.brakes.Pointer : IntPtr.Zero,
                ["converter"] = v.clutch != null ? v.clutch.Pointer : IntPtr.Zero,
                ["inertia box"] = v.inertia != null ? v.inertia.Pointer : IntPtr.Zero,
            };
            var theirs = new HashSet<IntPtr>();
            void Add(Il2CppSystem.Object o) { if (o != null) theirs.Add(o.Pointer); }
            Add(setup.brakes); Add(setup.clutch); Add(setup.inertia);
            var tiers = setup.engineTiers;
            if (tiers != null)
                for (int i = 0; i < tiers.Length; i++)
                    if (tiers[i] != null) { Add(tiers[i].engine); Add(tiers[i].speedControl); }
            var shared = new List<string>();
            foreach (var kv in mine) if (kv.Value != IntPtr.Zero && theirs.Contains(kv.Value)) shared.Add(kv.Key);
            return shared.Count == 0 ? "settings objects all the car's own (none shared with its class)"
                : "SHARED with the class setup (tuning them would change stock cars): " + string.Join(", ", shared);
        }
        catch (Exception e) { DebugLog.Error("core", e, "CarPhysicsProbe.SharedReport"); return "shared check failed: " + e.Message; }
    }

    private static float WheelRadius(VPVehicleController v)
    {
        var wc = v.GetComponentInChildren<VPFunselektor3DRaycastWheelCollider>(true);
        return wc != null ? wc.radius : 0.4f;
    }

    private static List<string> Sorted(List<string> l)
    {
        var copy = new List<string>(l);
        copy.Sort(StringComparer.Ordinal);
        return copy;
    }

    private static Bounds RootBox(Transform root, Bounds world)
    {
        var b = new Bounds();
        for (int i = 0; i < 8; i++)
        {
            var c = world.center + Vector3.Scale(world.extents, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
            var p = root.InverseTransformPoint(c);
            if (i == 0) b = new Bounds(p, Vector3.zero);
            else b.Encapsulate(p);
        }
        return b;
    }

    private static string Path(Transform root, Transform t)
    {
        var parts = new List<string>();
        for (var c = t; c != null && c != root; c = c.parent) parts.Add(c.name);
        parts.Reverse();
        return string.Join("/", parts);
    }
}
