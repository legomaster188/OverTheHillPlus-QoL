using System;

namespace OverTheHillPlus.Cars;

/// <summary>
/// How a modded car's tyres meet the ground when they differ in size from the donor's.
/// <list type="bullet">
/// <item><b>The live car</b> (physics): the donor's wheel colliders (VPP raycast wheels) take the model's tyre radius and
///   stay where they are; the body is placed so the model's wheel centres meet the donor's. VPP then settles the whole
///   car lower (or higher) by the difference of the radii, so it stands on its own modelled tyres, and everything placed
///   on the root (centre of mass, winch points, lights, shadow) keeps its place relative to the body. This is how the
///   Vera always drove.</item>
/// <item><b>The garage model</b> has no physics, so nothing settles it: its wheels and body go down by the same
///   difference (<see cref="Drop"/>), so it stands on the floor instead of floating.</item>
/// </list>
/// Both use <see cref="Applies"/>, so the car and its garage model always agree. Pure (unit-tested).
/// </summary>
public static class CarWheelFit
{
    /// <summary>The model tyre radii the wheel colliders accept.</summary>
    public const float MinRadius = 0.2f, MaxRadius = 0.7f;
    /// <summary>The largest difference either way; a bigger one means a broken measurement.</summary>
    public const float MaxDrop = 0.3f;

    /// <summary>A model tyre radius in the range the wheel colliders accept.</summary>
    public static bool Usable(float modelRadius) => !float.IsNaN(modelRadius) && modelRadius > MinRadius && modelRadius < MaxRadius;

    /// <summary>Whether the model's tyre radius is used: a usable model radius, a measured donor radius, and a difference
    /// of at most <see cref="MaxDrop"/>. Otherwise the donor's radius stays on the car and the garage model doesn't move.</summary>
    public static bool Applies(float donorRadius, float modelRadius) =>
        Usable(modelRadius) && !float.IsNaN(donorRadius) && donorRadius > 0.05f && donorRadius <= 1.5f &&
        Math.Abs(donorRadius - modelRadius) <= MaxDrop;

    /// <summary>
    /// Metres the live car settles lower than the donor (negative: higher, for bigger tyres), and the garage model is
    /// lowered by: donor radius − model radius when <see cref="Applies"/>, otherwise 0.
    /// </summary>
    public static float Drop(float donorRadius, float modelRadius) =>
        Applies(donorRadius, modelRadius) ? donorRadius - modelRadius : 0f;

    /// <summary>A tyre part's radius: half its largest extent across the wheel (y or z, after its rotation and scale; the
    /// tyre's axis is x). 0 for no part.</summary>
    public static float TyreRadius(MeshPart tyre)
    {
        if (tyre == null || tyre.Positions == null || tyre.Positions.Length < 3) return 0f;
        var m = MeshFrames.PartMatrix(tyre);
        float y0 = float.MaxValue, y1 = float.MinValue, z0 = float.MaxValue, z1 = float.MinValue;
        for (int i = 0; i + 2 < tyre.Positions.Length; i += 3)
        {
            var v = MeshFrames.Apply(m, new MeshVec3(tyre.Positions[i], tyre.Positions[i + 1], tyre.Positions[i + 2]));
            y0 = Math.Min(y0, v.Y); y1 = Math.Max(y1, v.Y);
            z0 = Math.Min(z0, v.Z); z1 = Math.Max(z1, v.Z);
        }
        return Math.Max(y1 - y0, z1 - z0) * 0.5f;
    }

    /// <summary>The model's tyre radius (the tyre part, by name or kind).</summary>
    public static float TyreRadius(MeshBlob blob) => blob == null ? 0f : TyreRadius(blob.Pick(MeshPartKind.Tyre, MeshBlob.TyreName));
}
