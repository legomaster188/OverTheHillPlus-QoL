using System;

namespace OverTheHillPlus.Cars;

/// <summary>
/// Which transform a model part's vertices go through before they are mapped into a renderer's space. Body, glass,
/// collider and other parts are placed on the car by their local transform (translation, rotation, scale: car root
/// space). Rims and tyres are drawn in each wheel's own hub frame (the donor's wheel node, which already sits on the
/// hub and turns with it), so only their rotation and scale apply: their translation says where the wheel is on the
/// car, and applying it again would draw the wheel that far off its hub. Pure code (unit-tested).
/// <para>Matrices are 4x4, column-major like Unity's (index = column * 4 + row).</para>
/// </summary>
public static class MeshFrames
{
    /// <summary>Rims and tyres: drawn in the hub frame.</summary>
    public static bool InHubFrame(MeshPartKind kind) => kind is MeshPartKind.Wheel or MeshPartKind.Tyre;

    /// <summary>The part's matrix as drawn: translation × rotation × scale, without the translation for rims and tyres.</summary>
    public static float[] PartMatrix(MeshPart p) => Trs(p.Position, p.Rotation, p.Scale, withTranslation: !InHubFrame(p.Kind));

    public static float[] Trs(MeshVec3 t, MeshQuat q, MeshVec3 s, bool withTranslation)
    {
        float x = q.X, y = q.Y, z = q.Z, w = q.W;
        float n = MathF.Sqrt(x * x + y * y + z * z + w * w);
        if (n < 1e-8f) { x = y = z = 0f; w = 1f; }
        else { x /= n; y /= n; z /= n; w /= n; }
        // rotation matrix (row r, column c)
        float r00 = 1 - 2 * (y * y + z * z), r01 = 2 * (x * y - z * w), r02 = 2 * (x * z + y * w);
        float r10 = 2 * (x * y + z * w), r11 = 1 - 2 * (x * x + z * z), r12 = 2 * (y * z - x * w);
        float r20 = 2 * (x * z - y * w), r21 = 2 * (y * z + x * w), r22 = 1 - 2 * (x * x + y * y);
        var m = new float[16];
        m[0] = r00 * s.X; m[1] = r10 * s.X; m[2] = r20 * s.X;
        m[4] = r01 * s.Y; m[5] = r11 * s.Y; m[6] = r21 * s.Y;
        m[8] = r02 * s.Z; m[9] = r12 * s.Z; m[10] = r22 * s.Z;
        if (withTranslation) { m[12] = t.X; m[13] = t.Y; m[14] = t.Z; }
        m[15] = 1f;
        return m;
    }

    public static MeshVec3 Apply(float[] m, MeshVec3 v) => new(
        m[0] * v.X + m[4] * v.Y + m[8] * v.Z + m[12],
        m[1] * v.X + m[5] * v.Y + m[9] * v.Z + m[13],
        m[2] * v.X + m[6] * v.Y + m[10] * v.Z + m[14]);
}
