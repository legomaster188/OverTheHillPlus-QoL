using System;

namespace OverTheHillPlus.Cars;

/// <summary>
/// The arithmetic of a mesh's vertices in plain floats. In the game every Vector3 constructor, Matrix4x4.MultiplyPoint3x4,
/// .normalized and the like is a trip through il2cpp_runtime_invoke with a boxed answer, about a fifth of a microsecond
/// each: seven of those per vertex over six million vertices, twice at every start, was more than half of what the modded
/// cars cost. Matches what those calls do. Pure code (unit-tested).
/// </summary>
public static class MeshMath
{
    /// <summary>
    /// <paramref name="count"/> vertices through a 3 x 4 matrix (<paramref name="m"/>: row by row, m00 m01 m02 m03 m10 ...):
    /// positions as MultiplyPoint3x4, normals as MultiplyVector and then normalized (up for one that vanishes).
    /// </summary>
    public static void Transform(float[] m, float[] positions, float[] normals, int count, Span<float> outPositions, Span<float> outNormals)
    {
        float m00 = m[0], m01 = m[1], m02 = m[2], m03 = m[3], m10 = m[4], m11 = m[5], m12 = m[6], m13 = m[7], m20 = m[8], m21 = m[9], m22 = m[10], m23 = m[11];
        for (int i = 0, n = count * 3; i < n; i += 3)
        {
            float x = positions[i], y = positions[i + 1], z = positions[i + 2];
            outPositions[i] = m00 * x + m01 * y + m02 * z + m03;
            outPositions[i + 1] = m10 * x + m11 * y + m12 * z + m13;
            outPositions[i + 2] = m20 * x + m21 * y + m22 * z + m23;
            x = normals[i]; y = normals[i + 1]; z = normals[i + 2];
            float nx = m00 * x + m01 * y + m02 * z, ny = m10 * x + m11 * y + m12 * z, nz = m20 * x + m21 * y + m22 * z;
            float sq = nx * nx + ny * ny + nz * nz;
            if (sq > 1e-12f)
            {
                float mag = MathF.Sqrt(sq);
                if (mag > 1e-5f) { outNormals[i] = nx / mag; outNormals[i + 1] = ny / mag; outNormals[i + 2] = nz / mag; }
                else { outNormals[i] = 0f; outNormals[i + 1] = 0f; outNormals[i + 2] = 0f; }
            }
            else { outNormals[i] = 0f; outNormals[i + 1] = 1f; outNormals[i + 2] = 0f; }
        }
    }

    /// <summary>Grows a box (min x, y, z, max x, y, z) by the positions through a 3 x 4 matrix. False when there were none.</summary>
    public static bool Encapsulate(float[] m, float[] positions, float[] box, bool any)
    {
        for (int i = 0; i + 2 < positions.Length; i += 3)
        {
            float x = positions[i], y = positions[i + 1], z = positions[i + 2];
            float px = m[0] * x + m[1] * y + m[2] * z + m[3], py = m[4] * x + m[5] * y + m[6] * z + m[7], pz = m[8] * x + m[9] * y + m[10] * z + m[11];
            if (!any)
            {
                box[0] = box[3] = px;
                box[1] = box[4] = py;
                box[2] = box[5] = pz;
                any = true;
                continue;
            }
            if (px < box[0]) box[0] = px; else if (px > box[3]) box[3] = px;
            if (py < box[1]) box[1] = py; else if (py > box[4]) box[4] = py;
            if (pz < box[2]) box[2] = pz; else if (pz > box[5]) box[5] = pz;
        }
        return any;
    }
}
