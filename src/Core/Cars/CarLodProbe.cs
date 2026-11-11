using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace OverTheHillPlus.Cars;

/// <summary>
/// Once per car object (the garage model and your car in the world, a few seconds after it is first seen, so the camera
/// has settled) the body LOD group's size is checked against the body it now holds and recalculated when the two
/// disagree (a donor group sized for something else would switch LODs at the wrong distance). That part always runs.
///
/// The rest is a diagnostic, made and written only while the debug log is at Verbose ([Debug] Level in oth.core.cfg,
/// Errors by default), for car objects first seen while it is: which body LOD the car really draws, as a line in the
/// car mod's log with every enabled camera's distance, field of view and the body's size on its screen against the
/// donor's LOD0 line, the LOD bias, and which body LOD renderers are visible. The owner saw lower-detail bodies close
/// up; the donor's thresholds are kept (CarBody reuses its LODGroup), so the line says whether a camera, the bias or the
/// group's size is behind it. Never reads the LOD structs (GetLODs): they don't cross into IL2CPP safely.
/// </summary>
internal static class CarLodProbe
{
    /// <summary>The donor body LODGroups' LOD0 line (screen-relative height), the same for every stock car within a few
    /// percent (0.104 to 0.124; the Grizzly 0.064).</summary>
    private const float Lod0Line = 0.12f;

    private static readonly Dictionary<IntPtr, float> FirstSeen = new();
    private static readonly HashSet<IntPtr> Done = new();

    /// <summary>Call on every look tick for a car object; runs once, <paramref name="delay"/> seconds after the first call.</summary>
    public static void Check(RuntimeCar car, Transform root, string what, float delay = 4f)
    {
        if (car == null || root == null) return;
        var key = root.Pointer;
        if (Done.Contains(key)) return;
        float now = Time.unscaledTime;
        if (!FirstSeen.TryGetValue(key, out float t0)) { FirstSeen[key] = now; return; }
        if (now - t0 < delay) return;
        Done.Add(key);
        try
        {
            string line = Report(car.Id, root, what, DebugLog.IsVerbose);
            if (line != null) car.Info(line);
        }
        catch (Exception e) { DebugLog.Error("core", e, "CarLodProbe.Check"); }
    }

    /// <summary>Checks the body LOD group's size and recalculates it when it is off. The log line when
    /// <paramref name="full"/>, else null: the cameras and the visible LODs are only looked at for the line.</summary>
    private static string Report(string carId, Transform root, string what, bool full)
    {
        var parts = root.Find("Parts");
        if (parts == null) return full ? $"LOD probe ({what}): no Parts node." : null;
        LODGroup group = null;
        var visible = new List<string>();
        Renderer lod0 = null;
        for (int i = 0; i < parts.childCount; i++)
        {
            var c = parts.GetChild(i);
            if (c.name.EndsWith("_CosmeticParts", StringComparison.Ordinal)) continue;
            group ??= c.GetComponent<LODGroup>();
            foreach (var r in c.GetComponentsInChildren<MeshRenderer>(true))
            {
                if (r == null || CarBody.BodyLodAt(r.name) < 0) continue;
                if (r.name.EndsWith("LOD0", StringComparison.OrdinalIgnoreCase)) lod0 = r;
                if (full && r.isVisible) visible.Add(r.name.Substring(Math.Max(0, r.name.Length - 4)));
            }
        }
        var sb = full ? new StringBuilder($"LOD probe ({what}): bias {QualitySettings.lodBias:0.00}, max LOD {QualitySettings.maximumLODLevel}") : null;
        float worldSize = 0f;
        if (group != null)
        {
            var ls = group.transform.lossyScale;
            float scale = Mathf.Max(Mathf.Abs(ls.x), Mathf.Max(Mathf.Abs(ls.y), Mathf.Abs(ls.z)));
            worldSize = group.size * scale;
            sb?.Append($", group {group.name} size {worldSize:0.00} m, {group.lodCount} LODs, {(group.enabled ? "on" : "OFF")}");
            if (lod0 != null)
            {
                var b = lod0.bounds;
                float body = Mathf.Max(b.size.x, Mathf.Max(b.size.y, b.size.z));
                if (CarLodReport.SizeOff(worldSize, body))
                {
                    group.RecalculateBounds();
                    ls = group.transform.lossyScale;
                    scale = Mathf.Max(Mathf.Abs(ls.x), Mathf.Max(Mathf.Abs(ls.y), Mathf.Abs(ls.z)));
                    float was = worldSize;
                    worldSize = group.size * scale;
                    string fit = $"the body is {body:0.00} m: recalculated from {was:0.00} to {worldSize:0.00} m";
                    if (full) sb.Append(" (").Append(fit).Append(')');
                    // A group that was resized still leaves a line in the debug log at Info, without the full report.
                    else DebugLog.Info(carId, $"LOD group of the {what}: {fit}");
                }
            }
        }
        else sb?.Append(", no LODGroup on the body");
        if (!full) return null;
        sb.Append("; visible body LODs: ").Append(visible.Count == 0 ? "none" : string.Join(", ", visible));
        var at = group != null ? group.transform.TransformPoint(group.localReferencePoint) : root.position;
        foreach (var cam in Camera.allCameras)
        {
            if (cam == null || !cam.isActiveAndEnabled) continue;
            float d = Vector3.Distance(cam.transform.position, at);
            float h = CarLodReport.RelativeHeight(worldSize, d, cam.orthographic, cam.fieldOfView, cam.orthographicSize) * QualitySettings.lodBias;
            sb.Append($"; camera {cam.name}{(cam == Camera.main ? " (main)" : "")}: {d:0.0} m, ")
              .Append(cam.orthographic ? $"ortho {cam.orthographicSize:0.0}" : $"fov {cam.fieldOfView:0}")
              .Append($", body {h:0.000} of the screen ({(h >= Lod0Line ? "LOD0" : "past LOD0")})");
        }
        return sb.Append('.').ToString();
    }
}
