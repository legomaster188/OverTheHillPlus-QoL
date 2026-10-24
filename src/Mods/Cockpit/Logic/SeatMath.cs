using System;

namespace OverTheHillPlus.Cockpit;

/// <summary>The car's cabin box in its own space (x right, y up, z forward), from its renderers.</summary>
internal readonly struct CabinBox
{
    public readonly float MinX, MaxX, MinY, MaxY, MinZ, MaxZ;

    public CabinBox(float minX, float maxX, float minY, float maxY, float minZ, float maxZ)
    {
        MinX = minX; MaxX = maxX; MinY = minY; MaxY = maxY; MinZ = minZ; MaxZ = maxZ;
    }

    public float Width => MaxX - MinX;
    public float Height => MaxY - MinY;
    public float Length => MaxZ - MinZ;
    public float CenterX => (MinX + MaxX) * 0.5f;
    public float CenterZ => (MinZ + MaxZ) * 0.5f;

    /// <summary>A plausible car: between 1 and 15 m long, 0.5 and 6 m high, 0.8 and 5 m wide.</summary>
    public bool Plausible => Length is > 1f and < 15f && Height is > 0.5f and < 6f && Width is > 0.8f and < 5f;

    /// <summary>Used when the car's size can't be read: a mid-size truck around its origin.</summary>
    public static CabinBox Default => new(-1f, 1f, 0f, 2f, -2.4f, 2.4f);
}

/// <summary>
/// Where the driver's eyes are in the car and how big the cab around them is (metres). Eye* is in the car's space;
/// the rest is relative to the eyes (x right, y up, z forward), which is how the cockpit model is laid out.
/// </summary>
internal readonly struct SeatPlan
{
    public readonly float EyeX, EyeY, EyeZ;
    /// <summary>The cabin's centre line, relative to the eyes (the driver sits to one side of it).</summary>
    public readonly float CentreX;
    /// <summary>Half the inner width of the cab.</summary>
    public readonly float HalfWidth;
    /// <summary>The roof lining above the eyes.</summary>
    public readonly float RoofY;
    /// <summary>The floor below the eyes (negative).</summary>
    public readonly float FloorY;

    public SeatPlan(float eyeX, float eyeY, float eyeZ, float centreX, float halfWidth, float roofY, float floorY)
    {
        EyeX = eyeX; EyeY = eyeY; EyeZ = eyeZ; CentreX = centreX; HalfWidth = halfWidth; RoofY = roofY; FloorY = floorY;
    }
}

/// <summary>Seat placement from the car's box (no game types, unit tested).</summary>
internal static class SeatMath
{
    /// <summary>The windscreen's base is this far ahead of the eyes (the dashboard's depth).</summary>
    public const float DashDepth = 1.0f;
    /// <summary>The dashboard's top is this far below the eyes.</summary>
    public const float DashDrop = 0.28f;

    /// <summary>
    /// The driver's seat in a car of the given box: sat on the left or right of the centre line, the eyes
    /// <see cref="DashDepth"/> behind the windscreen's base (which is where Camera+'s hood camera sits, just ahead of
    /// the windscreen) and a little below the roof.
    /// </summary>
    public static SeatPlan Plan(CabinBox b, bool leftHandDrive)
    {
        float halfW = Clamp(b.Width * 0.5f - 0.18f, 0.55f, 0.95f);
        float eyeX = b.CenterX + (leftHandDrive ? -1f : 1f) * halfW * 0.45f;

        float windscreenZ = Clamp(b.CenterZ + b.Length * 0.18f, b.MinZ + b.Length * 0.35f, b.MaxZ - b.Length * 0.2f);
        float eyeZ = Clamp(windscreenZ - DashDepth, b.MinZ + 0.5f, b.MaxZ - 0.5f);

        float eyeY = Clamp(b.MaxY - 0.38f, b.MinY + b.Height * 0.5f, b.MaxY - 0.2f);
        float roof = Clamp(b.MaxY - eyeY - 0.07f, 0.2f, 0.55f);
        float floor = Clamp(b.MinY + 0.45f - eyeY, -1.25f, -0.7f);
        return new SeatPlan(eyeX, eyeY, eyeZ, b.CenterX - eyeX, halfW, roof, floor);
    }

    /// <summary>
    /// Should this car part be hidden in the cockpit view? Parts that surround the eyes (their box holds the eye point)
    /// would be drawn over the view. A part as big as most of the car is the body shell itself: it stays (its inside
    /// faces are culled, and the hood in front has to stay). <paramref name="steeringOrDriver"/> is a name that says
    /// it is the car's own steering wheel or driver, which the cockpit replaces.
    /// </summary>
    public static bool ShouldHide(float minX, float minY, float minZ, float maxX, float maxY, float maxZ,
        float eyeX, float eyeY, float eyeZ, CabinBox car, bool steeringOrDriver)
    {
        if (steeringOrDriver) return true;
        const float pad = 0.05f;
        bool holdsEye = eyeX >= minX - pad && eyeX <= maxX + pad && eyeY >= minY - pad && eyeY <= maxY + pad && eyeZ >= minZ - pad && eyeZ <= maxZ + pad;
        if (!holdsEye) return false;
        float carVolume = Math.Max(0.001f, car.Width * car.Height * car.Length);
        float volume = Math.Max(0f, maxX - minX) * Math.Max(0f, maxY - minY) * Math.Max(0f, maxZ - minZ);
        return volume < carVolume * 0.55f;
    }

    /// <summary>True for names of the car's own steering wheel or driver figure.</summary>
    public static bool IsSteeringOrDriver(string name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        string n = name.ToLowerInvariant();
        return n.Contains("steer") || n.Contains("driver") || n.Contains("mannequin");
    }

    private static float Clamp(float v, float lo, float hi) => v < lo ? lo : v > hi ? hi : v;
}
