using System;

namespace OverTheHillPlus.Shared;

/// <summary>
/// Extra texture other mods want the gamepad to feel (Rumble+ plays it with the surface): Tyre Pressure's snow chains
/// clattering on the road. The mod that owns the car's tyres sets the level every frame; Rumble+ reads it. The level fades
/// out by itself when nobody refreshes it (the mod was switched off, the car is gone). Mods never reference each other, so
/// this is the one place they meet. Main thread only; no game types (unit-tested).
/// </summary>
internal static class RumbleHints
{
    /// <summary>A level not refreshed for this long (seconds) reads as zero.</summary>
    public const float StaleAfter = 0.35f;

    private static float _rattle, _at = float.NegativeInfinity;

    /// <summary>Sets the rattle of the local car's tyres (0 none, 1 the loudest), at <paramref name="now"/> (unscaled seconds).</summary>
    public static void SetRattle(float level, float now)
    {
        _rattle = float.IsNaN(level) ? 0f : Math.Max(0f, Math.Min(1f, level));
        _at = now;
    }

    /// <summary>The rattle now: the last level set, or 0 when it was not refreshed lately.</summary>
    public static float Rattle(float now) => now - _at <= StaleAfter && now >= _at ? _rattle : 0f;

    public static void Clear()
    {
        _rattle = 0f;
        _at = float.NegativeInfinity;
    }
}
