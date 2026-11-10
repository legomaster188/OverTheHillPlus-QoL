using System;
using OverTheHillPlus.Cars;
using UnityEngine;
#if MELON
using Il2CppVehiclePhysics;
#else
using VehiclePhysics;
#endif

namespace OverTheHillPlus.Stoat;

/// <summary>The Stoat on its finished template (core's <see cref="CarHooks"/>): the Boar drives all four wheels, a delivery van only
/// the rear ones.</summary>
internal static class StoatRig
{
    public static string AfterTemplate(GameObject template)
    {
        try { RearWheelDrive(template); }
        catch (Exception e)
        {
            DebugLog.Error(StoatPlugin.ModId, e, "StoatRig.RearWheelDrive");
            StoatPlugin.Logger?.LogWarning("[Stoat] rear-wheel drive could not be set (it drives all four): " + e.Message);
        }
        return null;
    }

    /// <summary>One driven axle, the rear one (the axle whose wheels sit furthest back): VPP's driveline is set up from these settings
    /// when the car wakes, on every peer the same.</summary>
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
        StoatPlugin.Logger?.LogInfo($"[Stoat] rear-wheel drive: axle {rear} of {axles.Length} driven.");
    }
}
