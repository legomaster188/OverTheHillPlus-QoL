using System;
using System.Collections.Generic;
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
        int i = Pick(Heads(blob));
        return i < 0 ? null : blob.Parts[i];
    }

    /// <summary>The shape for a car: the first of <paramref name="cars"/> (its CarInfo name, its mod id, ...) with a
    /// <c>part@&lt;car&gt;</c> of its own (any case), else the shared <see cref="Pick(MeshBlob)"/>. <paramref name="variant"/>
    /// is the car name the shape is for ("" for the shared one).</summary>
    public static MeshPart Pick(MeshBlob blob, out string variant, params string[] cars)
    {
        variant = "";
        if (blob == null) return null;
        int i = Pick(Heads(blob), out variant, cars);
        return i < 0 ? null : blob.Parts[i];
    }

    /// <summary>The lower detail mesh that goes with <paramref name="variant"/> (from <see cref="Pick(MeshBlob, out string, string[])"/>):
    /// the car's own <c>part_lod1@&lt;car&gt;</c> for a car's shape, <c>part_lod1</c> for the shared one. Never the shared
    /// LOD for a car's own shape (it would be another shape). Null when there is none.</summary>
    public static MeshPart PickLod1(MeshBlob blob, string variant)
    {
        if (blob == null) return null;
        int i = PickLod1(Heads(blob), variant);
        return i < 0 ? null : blob.Parts[i];
    }

    // The same choices over what a mesh file holds (PartMeshIndex), without its data: the place of the shape in the
    // list, -1 for none. The picks above go through these, so both always agree.

    /// <summary><see cref="Pick(MeshBlob)"/> by the shapes' heads: the place of the shared full-detail shape, -1 for none.</summary>
    public static int Pick(IReadOnlyList<ShapeHead> shapes)
    {
        if (shapes == null) return -1;
        for (int i = 0; i < shapes.Count; i++)
        {
            // the first one named exactly "part" decides, as MeshBlob.Part does
            if (!string.Equals(shapes[i].Name, Full, StringComparison.Ordinal)) continue;
            if (shapes[i].Drawable) return i;
            break;
        }
        for (int i = 0; i < shapes.Count; i++)
        {
            var q = shapes[i];
            if (q.Kind is MeshPartKind.Body or MeshPartKind.Other && q.Name.StartsWith(Full, StringComparison.Ordinal) &&
                !q.Name.StartsWith("part_lod", StringComparison.Ordinal) && q.Name.IndexOf('@') < 0 && q.Drawable) return i;
        }
        return -1;
    }

    /// <summary><see cref="Pick(MeshBlob, out string, string[])"/> by the shapes' heads.</summary>
    public static int Pick(IReadOnlyList<ShapeHead> shapes, out string variant, params string[] cars)
    {
        variant = "";
        if (shapes == null) return -1;
        if (cars != null)
            foreach (var car in cars)
            {
                if (string.IsNullOrEmpty(car)) continue;
                int own = Find(shapes, Full + "@" + car);
                if (own >= 0) { variant = car; return own; }
            }
        return Pick(shapes);
    }

    /// <summary><see cref="PickLod1(MeshBlob, string)"/> by the shapes' heads.</summary>
    public static int PickLod1(IReadOnlyList<ShapeHead> shapes, string variant)
    {
        if (shapes == null) return -1;
        return Find(shapes, string.IsNullOrEmpty(variant) ? Lod1 : Lod1 + "@" + variant);
    }

    private static int Find(IReadOnlyList<ShapeHead> shapes, string name)
    {
        for (int i = 0; i < shapes.Count; i++)
            if (string.Equals(shapes[i].Name, name, StringComparison.OrdinalIgnoreCase) && shapes[i].Drawable) return i;
        return -1;
    }

    private static List<ShapeHead> Heads(MeshBlob blob)
    {
        var list = new List<ShapeHead>(blob.Parts.Count);
        foreach (var p in blob.Parts)
            list.Add(new ShapeHead(p.Name, p.Kind, p.Positions != null ? p.VertexCount : 0, p.Indices != null && p.Positions != null ? p.Indices.Length : 0));
        return list;
    }
}
