using System;
using UnityEngine;
using VehiclePhysics;

namespace OverTheHillPlus.Ghosts;

/// <summary>
/// How a car's wheels are measured, shared by the recorder and the ghost car so both read them the same way:
/// steering from the wheel's state (degrees about the car's up axis), spin from its angular velocity (integrated by
/// the recorder), and suspension as the visual wheel's height in the car's own space.
/// </summary>
internal static class GhostRig
{
    /// <summary>The visual wheel and hub transforms the car's physics moves (either can be null).</summary>
    internal struct WheelVisual
    {
        public Transform Wheel, Hub;
    }

    public static WheelVisual[] Wheels(VPVehicleController car)
    {
        try
        {
            var states = car.wheelState;
            int n = states == null ? 0 : Math.Min(states.Length, GhostCodec.MaxWheels);
            var list = new WheelVisual[n];
            for (int i = 0; i < n; i++)
            {
                var col = states[i]?.wheelCol;
                if (col == null) continue;
                list[i] = Visual(col);
            }
            return list;
        }
        catch (Exception e)
        {
            DebugLog.Error("ghosts", e, "GhostRig.Wheels");
            GhostsPlugin.Logger.LogDebug("GhostRig.Wheels: " + e.Message);
            return Array.Empty<WheelVisual>();
        }
    }

    private static WheelVisual Visual(WheelColliderBehaviour col)
    {
        var v = new WheelVisual();
        try
        {
            col.GetVisualTransforms(out var wheel, out var hub, out _);
            v.Wheel = wheel;
            v.Hub = hub;
        }
        catch (Exception ex) { DebugLog.Error("ghosts", ex, "GhostRig.Visual"); }
        if (v.Wheel != null) return v;
        // The collider kinds the game uses keep the same three transforms as fields.
        try
        {
            var r = col.TryCast<VPFunselektor3DRaycastWheelCollider>();
            if (r != null) return new WheelVisual { Wheel = r.wheelTransform, Hub = r.wheelHubTransform };
            var f = col.TryCast<VPFunselektor3DWheelCollider>();
            if (f != null) return new WheelVisual { Wheel = f.wheelTransform, Hub = f.wheelHubTransform };
            var s = col.TryCast<VPStandardWheelCollider>();
            if (s != null) return new WheelVisual { Wheel = s.wheelTransform, Hub = s.wheelHubTransform };
        }
        catch (Exception ex) { DebugLog.Error("ghosts", ex, "GhostRig.Visual"); }
        return v;
    }

    /// <summary>The wheel's height in the car's own space (what the ghost's suspension replays).</summary>
    public static float Height(Transform root, Transform wheel) =>
        wheel == null ? 0f : root.InverseTransformPoint(wheel.position).y;

    /// <summary>Steering angles of every wheel now (degrees), into <paramref name="into"/>.</summary>
    public static void Steering(VPVehicleController car, float[] into)
    {
        try
        {
            var states = car.wheelState;
            int n = states == null ? 0 : Math.Min(states.Length, into.Length);
            for (int i = 0; i < n; i++) into[i] = states[i]?.steeringAngle ?? 0f;
        }
        catch (Exception ex) { DebugLog.Error("ghosts", ex, "GhostRig.Steering"); }
    }

    /// <summary>The car's name without Unity's "(Clone)".</summary>
    public static string CarName(VPVehicleController car)
    {
        try
        {
            string n = car.gameObject.name ?? "";
            return n.Replace("(Clone)", "").Trim();
        }
        catch (Exception ex) { DebugLog.Error("ghosts", ex, "GhostRig.CarName"); return ""; }
    }
}
