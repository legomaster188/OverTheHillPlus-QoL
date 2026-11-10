using System;
using System.Collections.Generic;
using OverTheHillPlus.Cars;
using UnityEngine;

namespace OverTheHillPlus.Ermine;

/// <summary>
/// The Ermine's moving parts on the networked template and the garage model (core's <see cref="CarHooks.AfterModel"/>):
/// the skis, spindles, A-arms, shocks, handlebars and track wheels on their own objects (core's
/// <see cref="CarMovingParts"/>), the track's lugs copied round the loop, and a camera that looks at the rider rather
/// than over a car's roof.
/// </summary>
internal static class ErmineRig
{
    /// <summary>The game's chase camera looks at a point this high over the car's follow reference (every stock car: 1.5 m,
    /// over a car's roof); on a 1.2 m sled the rider's head is lower.</summary>
    public const float CameraHeight = 1.15f;

    public static ErmineGeometry Geometry { get; private set; }
    public static MeshVec3 LugPivot { get; private set; }
    public static float LugRestS { get; private set; }

    /// <summary>Reads the geometry from the car's model (once, at start). Null when it can be used, else why not.</summary>
    public static string Prepare(MeshBlob blob)
    {
        var g = ErmineGeometry.Read(blob, out string problem);
        if (g == null) return problem;
        Geometry = g;
        var lug = blob.Part(ErmineGeometry.LugPart);
        LugPivot = CarMovingParts.PivotOf(blob, lug);
        LugRestS = g.LugRestS(LugPivot);
        return null;
    }

    public static string AfterModel(GameObject root, bool template, CarModelPlace place)
    {
        var g = Geometry;
        if (g == null) return "the Ermine's model geometry was not read";
        int made = CarMovingParts.Build(root, place, ErmineDefinition.CarName);
        if (made == 0) return "the Ermine's model has no moving parts";
        var rt = root.transform;
        // the lugs: the modelled one and its copies, spaced round the loop at rest
        var lugs = CarMovingParts.Copies(rt, ErmineGeometry.LugPart, g.Lugs);
        PlaceLugs(lugs, 0f, place.Offset);
        if (template)
        {
            try
            {
                var cam = root.GetComponentInChildren<CameraFollowUpdater>(true);
                if (cam != null) cam.yOffset = CameraHeight;
            }
            catch (Exception e) { DebugLog.Error(ErminePlugin.ModId, e, "ErmineRig.camera"); }
            try { PlaceIntake(rt, place); }
            catch (Exception e) { DebugLog.Error(ErminePlugin.ModId, e, "ErmineRig.intake"); }
            ErminePlugin.Logger?.LogInfo($"[Ermine] rig: {made} moving parts, {lugs.Count} lugs round a {g.Loop.Length:0.00} m track, " +
                                         $"{g.Idlers.Count} track wheels.");
        }
        return null;
    }

    /// <summary>The model's anchor for the engine's air intake (under the hood behind the vents).</summary>
    public const string IntakeAnchor = "intake";

    /// <summary>
    /// The engine's flood probes (the donor's WaterDetector/EngineAirIntakeWaterDetector_*) go to the sled's own intake:
    /// a sled has no hood or snorkel slot for core to move them with, so they would stay at the Polyana's hood, in the air
    /// ahead of the nose. Every tier goes to the same place (there is no snorkel to raise it).
    /// </summary>
    private static void PlaceIntake(Transform rt, CarModelPlace place)
    {
        var a = place.Blob?.Anchor(IntakeAnchor);
        var node = rt.Find(CarUpgradeFit.WaterDetectorNode);
        if (a == null || node == null) return;
        var at = new Vector3(a.Position.X, a.Position.Y, a.Position.Z) + place.Offset;
        for (int i = 0; i < node.childCount; i++)
        {
            var p = node.GetChild(i);
            if (p.name.StartsWith("EngineAirIntakeWaterDetector", StringComparison.Ordinal)) p.position = rt.TransformPoint(at);
        }
    }

    /// <summary>Puts the lugs round the loop, <paramref name="travel"/> metres on from rest (root space = model + offset).</summary>
    public static void PlaceLugs(List<Transform> lugs, float travel, Vector3 offset)
    {
        var g = Geometry;
        if (g == null || lugs == null || lugs.Count == 0) return;
        float spacing = g.Loop.Length / lugs.Count;
        g.Loop.At(LugRestS, out _, out var restTan);
        float rest = TrackLoop.Angle(restTan);
        for (int k = 0; k < lugs.Count; k++)
        {
            var t = lugs[k];
            if (t == null) continue;
            float s = LugRestS + k * spacing + travel;
            g.Loop.At(s, out var p, out var tan);
            float turn = (TrackLoop.Angle(tan) - rest) * Mathf.Rad2Deg;
            t.localPosition = new Vector3(LugPivot.X, p.Y, p.Z) + offset;
            // rotating about +x by +a turns +z toward -y: the tangent's angle grows from +z toward +y, so the turn is negative
            t.localRotation = Quaternion.AngleAxis(-turn, Vector3.right);
        }
    }
}
