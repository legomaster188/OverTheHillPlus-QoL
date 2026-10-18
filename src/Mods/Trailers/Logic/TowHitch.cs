namespace OverTheHillPlus.Trailers;

/// <summary>
/// The tow hitch a car needs before it can tow: the game's tow bar upgrade (every car has the item
/// <c>Visual/Rear_Bumper/TowBar</c>, switched on when it is mounted; modded cars keep the donor's item and the car
/// framework only moves its slot). The hitch ball sits on the bar's end. Pure code (unit-tested).
/// </summary>
public static class TowHitch
{
    /// <summary>The cosmetic item's object name under the rear bumper slot.</summary>
    public const string PartName = "TowBar";
    /// <summary>What a player is told when they try without one.</summary>
    public const string Need = "Mount a tow hitch on your car to tow trailers";

    /// <summary>The ball's centre in front of the bar mesh's rearmost point, m (a 50 mm ball: its radius).</summary>
    public const float BallFromRear = 0.05f;
    /// <summary>The ball's centre above the bar mesh's lowest point, m (measured on the game's tow bar mesh).</summary>
    public const float BallFromBottom = 0.10f;

    /// <summary>The ball's centre in the car's space, from the tow bar mesh's bounds in the car's space (the bar runs
    /// back along -z; the ball is centred across it).</summary>
    public static (float x, float y, float z) BallFromBounds(float minX, float minY, float minZ, float maxX, float maxY, float maxZ)
    {
        float x = (minX + maxX) * 0.5f;
        float y = minY + BallFromBottom;
        if (y > maxY) y = maxY;
        float z = minZ + BallFromRear;
        if (z > maxZ) z = maxZ;
        return (x, y, z);
    }
}
