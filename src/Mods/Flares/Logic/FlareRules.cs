using System;
using System.Collections.Generic;

namespace OverTheHillPlus.Flares;

/// <summary>
/// Which flares are allowed: a per-player rate limit (one instance for your own cooldown, one on the host for every
/// player), the host's check that a flare is fired from where the player's car is, and the cap on flares on screen.
/// </summary>
internal static class FlareRules
{
    /// <summary>Flares shown at once; the oldest goes first.</summary>
    public const int MaxActive = 12;
    /// <summary>The host's hard limits for any player, whatever its own cooldown says.</summary>
    public const float HostMinInterval = 3f, HostWindow = 60f;
    public const int HostMaxPerWindow = 6;
    /// <summary>How far from the player's car (metres) the host accepts a flare.</summary>
    public const float MaxAnchorDistance = 60f;

    /// <summary>A flare is accepted when the host knows the sender's car and the flare starts within <see cref="MaxAnchorDistance"/> of it.</summary>
    public static bool NearCar(bool hasCar, float cx, float cy, float cz, float x, float y, float z)
    {
        if (!hasCar || !float.IsFinite(x) || !float.IsFinite(y) || !float.IsFinite(z)) return false;
        float dx = x - cx, dy = y - cy, dz = z - cz;
        return dx * dx + dy * dy + dz * dz <= MaxAnchorDistance * MaxAnchorDistance;
    }

    /// <summary>Keeps a colour from being too dark to see against the sky: the brightest channel is raised to at least 0.6.</summary>
    public static void Lift(ref float r, ref float g, ref float b)
    {
        float max = Math.Max(r, Math.Max(g, b));
        if (max >= 0.6f) return;
        if (max <= 0.001f) { r = g = b = 0.6f; return; }
        float k = 0.6f / max;
        r *= k; g *= k; b *= k;
    }

    /// <summary>
    /// Allows at most one use per <see cref="MinInterval"/> seconds and <see cref="MaxPerWindow"/> per
    /// <see cref="Window"/> for each key (a player id). Times are the caller's clock in seconds.
    /// </summary>
    public sealed class Throttle
    {
        public float MinInterval;
        public int MaxPerWindow;
        public float Window;

        private readonly Dictionary<int, List<float>> _uses = new();

        public Throttle(float minInterval, int maxPerWindow, float window)
        {
            MinInterval = minInterval; MaxPerWindow = maxPerWindow; Window = window;
        }

        private List<float> Recent(int key, float now)
        {
            if (!_uses.TryGetValue(key, out var list)) _uses[key] = list = new List<float>();
            list.RemoveAll(t => now - t > Window || t > now); // old ones, and ones from a clock that went backwards
            return list;
        }

        /// <summary>Records the use and returns true, or returns false (recording nothing) when it is too soon or too many.</summary>
        public bool TryUse(int key, float now)
        {
            var list = Recent(key, now);
            if (list.Count > 0 && now - list[list.Count - 1] < MinInterval) return false;
            if (list.Count >= MaxPerWindow) return false;
            list.Add(now);
            return true;
        }

        /// <summary>Seconds until <paramref name="key"/> may fire again (0 = now).</summary>
        public float Wait(int key, float now)
        {
            var list = Recent(key, now);
            if (list.Count == 0) return 0f;
            float wait = MinInterval - (now - list[list.Count - 1]);
            if (list.Count >= MaxPerWindow) wait = Math.Max(wait, Window - (now - list[0]));
            return Math.Max(0f, wait);
        }

        public void Clear() => _uses.Clear();
    }
}
