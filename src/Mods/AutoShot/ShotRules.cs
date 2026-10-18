using System;

namespace OverTheHillPlus.AutoShot;

/// <summary>Why a screenshot was taken.</summary>
internal enum ShotReason
{
    BigJump,
    Flip,
    Landmark,
}

/// <summary>
/// When Auto Screenshot takes a picture (pure logic, unit tested):
/// - a big jump: once the car has been in the air for <see cref="MinAirTime"/> seconds, mid-air;
/// - a flip: once the car has turned over <see cref="FlipDegrees"/> degrees in the air;
/// - a landmark: when you reach a point of interest for the first time;
/// at most one picture per jump, and never within <see cref="Cooldown"/> seconds of the last one.
/// </summary>
internal sealed class ShotRules
{
    public float Cooldown = 20f;
    public float MinAirTime = 1f;
    public float FlipDegrees = 300f;

    private float _lastAt = float.NegativeInfinity;
    private int _shotJump = -1;

    public float LastAt => _lastAt;

    public bool CoolingDown(float now) => now - _lastAt < Math.Max(0f, Cooldown);

    /// <summary>
    /// Asked every frame while the car is in the air (jump <paramref name="jumpNumber"/>). Returns the reason to take a
    /// picture now, or null. A flip wins over a big jump when both are true.
    /// </summary>
    public ShotReason? Air(float now, int jumpNumber, float airTime, float tumbleDegrees, bool jumps, bool flips)
    {
        if (jumpNumber == _shotJump || CoolingDown(now)) return null;
        if (flips && tumbleDegrees >= FlipDegrees) return ShotReason.Flip;
        if (jumps && airTime >= MinAirTime) return ShotReason.BigJump;
        return null;
    }

    /// <summary>A landmark was just discovered: take a picture unless one was taken a moment ago.</summary>
    public bool Landmark(float now, bool enabled) => enabled && !CoolingDown(now);

    /// <summary>Records a picture (for the cooldown and the one-per-jump rule; jump -1 for a landmark).</summary>
    public void Taken(float now, int jumpNumber = -1)
    {
        _lastAt = now;
        if (jumpNumber >= 0) _shotJump = jumpNumber;
    }

    public static string Tag(ShotReason r) => r switch
    {
        ShotReason.BigJump => "jump",
        ShotReason.Flip => "flip",
        _ => "landmark",
    };

    public static string Describe(ShotReason r) => r switch
    {
        ShotReason.BigJump => "big jump",
        ShotReason.Flip => "flip",
        _ => "new landmark",
    };

    /// <summary>File name for a picture: oth_20261016_142501_jump.png (a counter is added when the name is taken).</summary>
    public static string FileName(DateTime at, ShotReason r, int counter = 0) =>
        $"oth_{at:yyyyMMdd_HHmmss}_{Tag(r)}" + (counter > 0 ? "_" + counter : "") + ".png";
}
