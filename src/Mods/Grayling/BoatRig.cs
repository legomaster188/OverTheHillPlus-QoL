using System;
using OverTheHillPlus.Cars;
using UnityEngine;

namespace OverTheHillPlus.Grayling;

/// <summary>
/// The Grayling's outboard on the networked template and the garage model (core's <see cref="CarHooks.AfterModel"/>): the
/// motor (turning on its swivel bracket) and the propeller (spinning on the motor's leg) on their own objects (core's
/// <see cref="CarMovingParts"/>), the water probes where a boat needs them (the touching-water probe under the hull for the
/// wake and splashes, the others up on the gunwales and the motor's cowl, so a boat afloat never counts as drowned or
/// flooded), and a chase camera that looks at the boat, not over a car's roof.
/// </summary>
internal static class BoatRig
{
    public const string MotorPart = "mv_motor", PropPart = "mv_prop", TillerPart = "mv_tiller";
    public const float CameraHeight = 1.2f;

    /// <summary>The swivel's axis point and the propeller's centre (model space), read from the model at start.</summary>
    public static MeshVec3 MotorPivot { get; private set; }
    public static MeshVec3 PropPivot { get; private set; }
    /// <summary>The top of the motor's cowl (model space): where the engine's air intake is.</summary>
    public static MeshVec3 CowlTop { get; private set; }

    public static string Prepare(MeshBlob blob)
    {
        if (blob == null) return "no model";
        var motor = blob.Part(MotorPart);
        var prop = blob.Part(PropPart);
        if (motor == null || prop == null) return "its model has no outboard (mv_motor, mv_prop)";
        MotorPivot = CarMovingParts.PivotOf(blob, motor);
        PropPivot = CarMovingParts.PivotOf(blob, prop);
        motor.Bounds(out var lo, out var hi);
        CowlTop = new MeshVec3((lo.X + hi.X) * 0.5f, hi.Y - 0.05f, (lo.Z + hi.Z) * 0.5f);
        var tilt = blob.Anchor("motor_tilt");
        TiltPivot = tilt != null ? tilt.Position : MotorPivot;
        return null;
    }

    /// <summary>The clamp bracket's tilt pin (model space): the motor tilts up about it when the boat is out of the water.</summary>
    public static MeshVec3 TiltPivot { get; private set; }

    public static string AfterModel(GameObject root, bool template, CarModelPlace place)
    {
        int made = CarMovingParts.Build(root, place, GraylingDefinition.CarName);
        if (made == 0) return "the Grayling's model has no moving parts";
        var rt = root.transform;
        // the propeller and the tiller turn with the motor: they hang under it and stick out of it
        var motor = CarMovingParts.Find(rt, MotorPart);
        var prop = CarMovingParts.Find(rt, PropPart);
        var tiller = CarMovingParts.Find(rt, TillerPart);
        if (motor != null && prop != null) prop.SetParent(motor, true);
        if (motor != null && tiller != null) tiller.SetParent(motor, true);
        if (!template)
        {
            // the garage model stands on the floor with nothing moving it: its motor is tilted up as on the shore (aft and
            // up on the clamp's pin), or the leg would stand through the floor, and its tiller folded up
            if (motor != null)
            {
                var pivot = place.ToRoot(TiltPivot);
                var tilt = Quaternion.AngleAxis(MotorTilt.MotorAngle(MotorTilt.Up), Vector3.right);
                motor.localPosition = pivot + tilt * (motor.localPosition - pivot);
                motor.localRotation = tilt * motor.localRotation;
                if (tiller != null) tiller.localRotation = Quaternion.AngleAxis(MotorTilt.TillerAngle(MotorTilt.Up), Vector3.right) * tiller.localRotation;
            }
            return null;
        }
        try
        {
            var cam = root.GetComponentInChildren<CameraFollowUpdater>(true);
            if (cam != null) cam.yOffset = CameraHeight;
        }
        catch (Exception e) { DebugLog.Error(GraylingPlugin.ModId, e, "BoatRig.camera"); }
        PlaceWaterProbes(rt, place);
        return null;
    }

    /// <summary>The donor's water probes for a boat: touching water at the keel (the wake), submerged and engine-intake
    /// probes up on the gunwales and the cowl.</summary>
    private static void PlaceWaterProbes(Transform rt, CarModelPlace place)
    {
        var node = rt.Find(CarUpgradeFit.WaterDetectorNode);
        if (node == null) return;
        var moved = new System.Collections.Generic.List<string>();
        for (int i = 0; i < node.childCount; i++)
        {
            var p = node.GetChild(i);
            string n = p.name;
            MeshVec3? want = null;
            if (n.StartsWith("EngineAirIntakeWaterDetector", StringComparison.Ordinal)) want = CowlTop;
            else if (n == "FrontCarSubmerged") want = new MeshVec3(0f, BoatHull.GunwaleY(1.2f) + 0.05f, 1.2f);
            else if (n == "BackCarSubmerged") want = new MeshVec3(0f, BoatHull.GunwaleY(-1.6f) + 0.05f, -1.6f);
            else if (n == "IsTouchingWaterDetector") want = new MeshVec3(0f, BoatHull.KeelY + 0.03f, -0.4f);
            else if (n == "TiresFullySubmergedWaterDetector") want = new MeshVec3(0f, BoatHull.KeelY + 0.35f, -0.4f);
            if (want == null) continue;
            p.position = rt.TransformPoint(place.ToRoot(want.Value));
            moved.Add(n);
        }
        if (moved.Count > 0) GraylingPlugin.Logger?.LogInfo("[Grayling] water probes moved for a boat: " + string.Join(", ", moved) + ".");
    }
}
