using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace OverTheHillPlus.Cars;

/// <summary>
/// Which body LOD a modded car really draws, logged once per car object (the garage model and each car in the world, a
/// few seconds after it is first seen, so the camera has settled): every enabled camera's distance, field of view and the
/// body's size on its screen against the donor's LOD0 line, the LOD bias, and which body LOD renderers are visible.
/// The owner saw lower-detail bodies close up; the donor's thresholds are kept (CarBody reuses its LODGroup), so this
/// says in the log whether a camera, the bias or the group's size is behind it. It also checks the group's size against
/// the body it now holds and recalculates it when the two disagree (a donor group sized for something else would
/// switch LODs at the wrong distance). Never reads the LOD structs (GetLODs): they don't cross into IL2CPP safely.
/// </summary>
internal static class CarLodProbe
{
    /// <summary>The donor body LODGroups' LOD0 line (screen-relative height), the same for every stock car within a few
    /// percent (0.104 to 0.124; the Grizzly 0.064).</summary>
    private const float Lod0Line = 0.12f;

    private static readonly Dictionary<IntPtr, float> FirstSeen = new();
    private static readonly HashSet<IntPtr> Done = new();

    /// <summary>Call on every look tick for a car object; logs once, <paramref name="delay"/> seconds after the first call.</summary>
    public static void Check(RuntimeCar car, Transform root, string what, float delay = 4f)
    {
        if (car == null || root == null) return;
        var key = root.Pointer;
        if (Done.Contains(key)) return;
        float now = Time.unscaledTime;
        if (!FirstSeen.TryGetValue(key, out float t0)) { FirstSeen[key] = now; return; }
        if (now - t0 < delay) return;
        Done.Add(key);
        try { car.Info(Report(root, what)); }
        catch (Exception e) { DebugLog.Error("core", e, "CarLodProbe.Check"); }
    }

    private static string Report(Transform root, string what)
    {
        var parts = root.Find("Parts");
        if (parts == null) return $"LOD probe ({what}): no Parts node.";
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
                if (r.isVisible) visible.Add(r.name.Substring(Math.Max(0, r.name.Length - 4)));
            }
        }
        var sb = new StringBuilder($"LOD probe ({what}): bias {QualitySettings.lodBias:0.00}, max LOD {QualitySettings.maximumLODLevel}");
        float worldSize = 0f;
        if (group != null)
        {
            var ls = group.transform.lossyScale;
            float scale = Mathf.Max(Mathf.Abs(ls.x), Mathf.Max(Mathf.Abs(ls.y), Mathf.Abs(ls.z)));
            worldSize = group.size * scale;
            sb.Append($", group {group.name} size {worldSize:0.00} m, {group.lodCount} LODs, {(group.enabled ? "on" : "OFF")}");
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
                    sb.Append($" (the body is {body:0.00} m: recalculated from {was:0.00} to {worldSize:0.00} m)");
                }
            }
        }
        else sb.Append(", no LODGroup on the body");
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
