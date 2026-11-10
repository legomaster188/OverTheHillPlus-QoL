using System;
using OverTheHillPlus.Cars;

namespace OverTheHillPlus.CustomCosmetics;

/// <summary>
/// Which mesh of a part's blob is drawn. A blob may carry a shape made for one car, <c>part@&lt;car&gt;</c> (a bumper as
/// wide as that car, string lights along its roof line), next to the shared <c>part</c>; the car's own wins. Lower
/// detail is <c>part_lod1</c> (<c>part_lod1@&lt;car&gt;</c> for a car's own shape). Pure (unit-tested).
/// </summary>
public static class PartShape
{
    public const string Full = "part";
    public const string Lod1 = "part_lod1";

    /// <summary>The part named "part" (LOD0, full detail); a blob without one falls back to the first body or other part
    /// named "part..." that is not a lower LOD ("part_lod1", ...) nor a car's own shape. Null when there is none with triangles.</summary>
    public static MeshPart Pick(MeshBlob blob)
    {
        if (blob == null) return null;
        var p = blob.Part(Full);
        if (p != null && Drawable(p)) return p;
        foreach (var q in blob.Parts)
            if (q.Kind is MeshPartKind.Body or MeshPartKind.Other && q.Name.StartsWith(Full, StringComparison.Ordinal) &&
                !q.Name.StartsWith("part_lod", StringComparison.Ordinal) && q.Name.IndexOf('@') < 0 && Drawable(q)) return q;
        return null;
    }

    /// <summary>The shape for a car: the first of <paramref name="cars"/> (its CarInfo name, its mod id, ...) with a
    /// <c>part@&lt;car&gt;</c> of its own (any case), else the shared <see cref="Pick(MeshBlob)"/>. <paramref name="variant"/>
    /// is the car name the shape is for ("" for the shared one).</summary>
    public static MeshPart Pick(MeshBlob blob, out string variant, params string[] cars)
    {
        variant = "";
        if (blob == null) return null;
        if (cars != null)
            foreach (var car in cars)
            {
                if (string.IsNullOrEmpty(car)) continue;
                var own = Find(blob, Full + "@" + car);
                if (own != null) { variant = car; return own; }
            }
        return Pick(blob);
    }

    /// <summary>The lower detail mesh that goes with <paramref name="variant"/> (from <see cref="Pick(MeshBlob, out string, string[])"/>):
    /// the car's own <c>part_lod1@&lt;car&gt;</c> for a car's shape, <c>part_lod1</c> for the shared one. Never the shared
    /// LOD for a car's own shape (it would be another shape). Null when there is none.</summary>
    public static MeshPart PickLod1(MeshBlob blob, string variant)
    {
        if (blob == null) return null;
        return Find(blob, string.IsNullOrEmpty(variant) ? Lod1 : Lod1 + "@" + variant);
    }

    private static MeshPart Find(MeshBlob blob, string name)
    {
        foreach (var q in blob.Parts)
            if (string.Equals(q.Name, name, StringComparison.OrdinalIgnoreCase) && Drawable(q)) return q;
        return null;
    }

    private static bool Drawable(MeshPart p) => p.Kind != MeshPartKind.Collider && p.Indices != null && p.Indices.Length >= 3 && p.Positions != null;
}
