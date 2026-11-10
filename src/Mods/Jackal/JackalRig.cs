using System;
using OverTheHillPlus.Cars;
using UnityEngine;
#if MELON
using Il2CppVehiclePhysics;
#else
using VehiclePhysics;
#endif

namespace OverTheHillPlus.Jackal;

/// <summary>
/// The Jackal on the networked template and the garage model (core's <see cref="CarHooks"/>): the running gear on its own
/// objects (core's <see cref="CarMovingParts"/>), and on the finished template rear-wheel drive, the weight towards the rear
/// axle and springs and dampers that follow the load.
/// </summary>
internal static class JackalRig
{
    public static JackalGeometry Geometry { get; private set; }

    /// <summary>Reads the running gear from the car's model (once, at start). Null when it can be used, else why not.</summary>
    public static string Prepare(MeshBlob blob)
    {
        var g = JackalGeometry.Read(blob, out string problem);
        if (g == null) return problem;
        Geometry = g;
        return null;
    }

    public static string AfterModel(GameObject root, bool template, CarModelPlace place)
    {
        int made = CarMovingParts.Build(root, place, JackalDefinition.CarName);
        if (made == 0) return "the Jackal's model has no moving parts";
        if (template) JackalPlugin.Logger?.LogInfo($"[Jackal] rig: {made} moving parts.");
        return null;
    }

    /// <summary>The template is complete (tuned): what makes it a rear-engined, rear-wheel drive rail.</summary>
    public static string AfterTemplate(GameObject template)
    {
        try { RearWheelDrive(template); }
        catch (Exception e)
        {
            DebugLog.Error(JackalPlugin.ModId, e, "JackalRig.RearWheelDrive");
            JackalPlugin.LogOnce("rwd", "rear-wheel drive could not be set (it drives all four): " + e.Message);
        }
        try { Balance(template); }
        catch (Exception e)
        {
            DebugLog.Error(JackalPlugin.ModId, e, "JackalRig.Balance");
            JackalPlugin.LogOnce("balance", "the rear weight could not be set: " + e.Message);
        }
        return null;
    }

    /// <summary>One driven axle, the rear one (the axle whose wheels sit furthest back): VPP's driveline is set up from these
    /// settings when the car wakes, on every peer the same.</summary>
    private static void RearWheelDrive(GameObject template)
    {
        var vp = template.GetComponent<VPVehicleController>();
        if (vp == null) return;
        var axles = vp.axles;
        if (axles == null || axles.Length < 2) return;
        var rt = template.transform;
        int rear = -1;
        float best = float.MaxValue;
        for (int i = 0; i < axles.Length; i++)
        {
            var a = axles[i];
            var w = a?.leftWheel;
            if (w == null) continue;
            float z = rt.InverseTransformPoint(w.transform.position).z;
            if (z < best) { best = z; rear = i; }
        }
        if (rear < 0) return;
        var d = vp.driveline;
        if (d == null) return;
        d.drivenAxles = Driveline.DrivenAxles.SingleAxle;
        d.firstDrivenAxle = rear;
        JackalPlugin.Logger?.LogInfo($"[Jackal] rear-wheel drive: axle {rear} of {axles.Length} driven.");
    }

    /// <summary>The centre of mass towards the rear axle (the engine hangs behind it) and the springs and dampers following the
    /// load, so both axles sag alike and the car stands level.</summary>
    private static void Balance(GameObject template)
    {
        var rt = template.transform;
        float front = float.MinValue, rear = float.MaxValue;
        var wheels = template.GetComponentsInChildren<VPFunselektor3DRaycastWheelCollider>(true);
        foreach (var wc in wheels)
        {
            if (wc == null) continue;
            float z = rt.InverseTransformPoint(wc.transform.position).z;
            front = Math.Max(front, z);
            rear = Math.Min(rear, z);
        }
        if (!(front > rear)) return;
        float mid = (front + rear) * 0.5f;
        foreach (var wc in wheels)
        {
            if (wc == null) continue;
            float k = JackalBalance.SpringFactor(rt.InverseTransformPoint(wc.transform.position).z < mid);
            wc.springRate *= k;
            wc.damperRate *= k;
        }
        var vp = template.GetComponent<VPVehicleController>();
        var com = vp != null ? vp.centerOfMass : null;
        if (com == null) return;
        var c = rt.InverseTransformPoint(com.position);
        com.position = rt.TransformPoint(new Vector3(c.x, c.y, rear + (front - rear) * JackalBalance.ComFromRear));
        JackalPlugin.Logger?.LogInfo($"[Jackal] weight {JackalBalance.RearShare:0.00} on the rear axle: centre of mass at z {rear + (front - rear) * JackalBalance.ComFromRear:0.00}, " +
                                     $"springs x{JackalBalance.SpringFactor(false):0.00} front, x{JackalBalance.SpringFactor(true):0.00} rear.");
    }
}
