using System;
using OverTheHillPlus.Cars;

namespace OverTheHillPlus.CustomCosmetics;

/// <summary>Which mesh of a part's blob is drawn. Pure (unit-tested).</summary>
public static class PartShape
{
    /// <summary>The part named "part" (LOD0, full detail); a blob without one falls back to the first body or other part
    /// named "part..." that is not a lower LOD ("part_lod1", ...). Null when there is none with triangles.</summary>
    public static MeshPart Pick(MeshBlob blob)
    {
        if (blob == null) return null;
        var p = blob.Part("part");
        if (p != null && Drawable(p)) return p;
        foreach (var q in blob.Parts)
            if (q.Kind is MeshPartKind.Body or MeshPartKind.Other && q.Name.StartsWith("part", StringComparison.Ordinal) &&
                !q.Name.StartsWith("part_lod", StringComparison.Ordinal) && Drawable(q)) return q;
        return null;
    }

    private static bool Drawable(MeshPart p) => p.Kind != MeshPartKind.Collider && p.Indices != null && p.Indices.Length >= 3 && p.Positions != null;
}
