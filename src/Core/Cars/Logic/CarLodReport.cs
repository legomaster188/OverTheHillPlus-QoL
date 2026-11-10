using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace OverTheHillPlus.Cars;

/// <summary>
/// The one log line about a modded car's body levels of detail: how many body renderers each LOD has and how many
/// triangles they draw, so a donor with two body filters on one LOD (the car drawn twice) shows up. Pure (unit-tested).
/// </summary>
public static class CarLodReport
{
    /// <summary>"body per LOD: LOD0 1 × 405k tris, LOD1 1 × 120k tris" from (lod, triangles) per body renderer;
    /// <paramref name="doubled"/>: some LOD has more than one body renderer.</summary>
    public static string Summary(IEnumerable<(int lod, int triangles)> renderers, out bool doubled)
    {
        doubled = false;
        var byLod = new SortedDictionary<int, (int count, long tris)>();
        if (renderers != null)
            foreach (var (lod, tris) in renderers)
            {
                byLod.TryGetValue(lod, out var e);
                byLod[lod] = (e.count + 1, e.tris + tris);
            }
        if (byLod.Count == 0) return "body per LOD: none";
        var sb = new StringBuilder("body per LOD: ");
        bool first = true;
        foreach (var kv in byLod)
        {
            if (kv.Value.count > 1) doubled = true;
            if (!first) sb.Append(", ");
            first = false;
            sb.Append("LOD").Append(kv.Key.ToString(CultureInfo.InvariantCulture)).Append(' ')
              .Append(kv.Value.count.ToString(CultureInfo.InvariantCulture)).Append(" × ").Append(Tris(kv.Value.tris)).Append(" tris");
        }
        return sb.ToString();
    }

    /// <summary>How much of the screen's height an object of <paramref name="size"/> metres takes (Unity's LODGroup
    /// measure, before the LOD bias): size over the view's height at that distance.</summary>
    public static float RelativeHeight(float size, float distance, bool orthographic, float fovDegrees, float orthoSize)
    {
        if (size <= 0f) return 0f;
        if (orthographic) return orthoSize > 1e-4f ? size / (2f * orthoSize) : 0f;
        float view = 2f * Math.Max(distance, 1e-3f) * (float)Math.Tan(fovDegrees * 0.5 * Math.PI / 180.0);
        return view > 1e-6f ? size / view : 0f;
    }

    /// <summary>True when a LODGroup's world size and the body it holds disagree by more than 40 % either way (the group
    /// would switch LODs at the wrong distance).</summary>
    public static bool SizeOff(float groupSize, float bodySize) =>
        groupSize > 0f && bodySize > 0f && (groupSize < bodySize * 0.6f || groupSize > bodySize * 1.7f);

    private static string Tris(long n) => n >= 10000
        ? (n / 1000).ToString(CultureInfo.InvariantCulture) + "k"
        : n.ToString(CultureInfo.InvariantCulture);
}
