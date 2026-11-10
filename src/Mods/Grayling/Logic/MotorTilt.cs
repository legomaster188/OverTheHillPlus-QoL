using System;
using OverTheHillPlus.Cars;

namespace OverTheHillPlus.Grayling;

/// <summary>
/// The outboard out of the water: tilted up on its clamp's pin (the model's <c>motor_tilt</c> anchor) so the leg and the
/// propeller swing aft and up clear of the ground, and its tiller folded up on its hinge (<c>pivot_tiller</c>) so it stays
/// over the stern seat instead of swinging down through it. Rotations are about +x in Unity's convention: a positive
/// angle turns +y towards +z (the bow), so +<see cref="Up"/> swings the leg under the pin towards the stern.
/// (The first version tilted by -62 degrees: the leg swung forward under the hull, 26 cm into the ground.) Pure
/// (unit-tested against the shipped model).
/// </summary>
public static class MotorTilt
{
    /// <summary>How far the motor tilts up out of the water, degrees (the skeg 10 cm over the ground, the cowl clear of the
    /// stern seat).</summary>
    public const float Up = 55f;
    /// <summary>How far the tiller folds up on its hinge, relative to the motor, when the motor is tilted all the way.</summary>
    public const float TillerFold = 55f;

    /// <summary>The motor's rotation about +x for a tilt of <paramref name="tilt"/> degrees (0 = down in the water).</summary>
    public static float MotorAngle(float tilt) => Math.Clamp(tilt, 0f, Up);

    /// <summary>The tiller's rotation about +x relative to the motor (negative: its grip rises), folded in step with the tilt.</summary>
    public static float TillerAngle(float tilt) => -Math.Clamp(tilt, 0f, Up) / Up * TillerFold;

    /// <summary>A point turned <paramref name="degrees"/> about the x axis through <paramref name="pivot"/>, as Unity's
    /// Quaternion.AngleAxis(degrees, Vector3.right) turns it.</summary>
    public static MeshVec3 RotateX(MeshVec3 p, MeshVec3 pivot, float degrees)
    {
        float a = degrees * MathF.PI / 180f, c = MathF.Cos(a), s = MathF.Sin(a);
        float y = p.Y - pivot.Y, z = p.Z - pivot.Z;
        return new MeshVec3(p.X, pivot.Y + y * c - z * s, pivot.Z + y * s + z * c);
    }
}
