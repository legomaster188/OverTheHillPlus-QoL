using System;
using System.Collections.Generic;

namespace OverTheHillPlus.Shared;

internal static class ThrottleCaps
{
    /// <summary>A cap older than this (seconds) is ignored: the limiting mod was switched off or the car went.</summary>
    public const float StaleAfter = 0.5f;

    private static readonly Dictionary<IntPtr, (float Cap, float At)> Caps = new();

    /// <summary>The car's throttle may be at most <paramref name="cap"/> (0 to 1) of what is asked, as of <paramref name="now"/>.</summary>
    public static void Set(IntPtr car, float cap, float now)
    {
        if (car == IntPtr.Zero || !float.IsFinite(now)) return;
        Caps[car] = (float.IsFinite(cap) ? Math.Max(0f, Math.Min(1f, cap)) : 1f, now);
    }

    /// <summary>The cap that is fresh at <paramref name="now"/>, or 1 (no limit).</summary>
    public static float Of(IntPtr car, float now)
    {
        if (!Caps.TryGetValue(car, out var c)) return 1f;
        return now - c.At <= StaleAfter && now >= c.At - 1f ? c.Cap : 1f;
    }

    public static void Clear() => Caps.Clear();
}
