using System;

namespace OverTheHillPlus.ControllerFeel;

/// <summary>Small curve helpers for Controller Feel (no game types, so the tests compile this file in).</summary>
internal static class FeelMath
{
    public static float Clamp01(float x) => float.IsNaN(x) ? 0f : x < 0f ? 0f : x > 1f ? 1f : x;

    public static float Lerp(float a, float b, float t) => a + (b - a) * Clamp01(t);

    /// <summary>0 below <paramref name="edge0"/>, 1 above <paramref name="edge1"/>, a smooth S between.</summary>
    public static float Smooth(float x, float edge0, float edge1)
    {
        if (float.IsNaN(x)) return 0f;
        if (edge1 <= edge0) return x >= edge1 ? 1f : 0f;
        float t = Clamp01((x - edge0) / (edge1 - edge0));
        return t * t * (3f - 2f * t);
    }

    /// <summary>Two strengths on the same motor stack without ever passing 1: 0.5 + 0.5 = 0.75.</summary>
    public static float Combine(float a, float b)
    {
        a = Clamp01(a);
        b = Clamp01(b);
        return 1f - (1f - a) * (1f - b);
    }

    /// <summary>Moves <paramref name="current"/> toward <paramref name="target"/> at <paramref name="up"/> or <paramref name="down"/> per second.</summary>
    public static float Approach(float current, float target, float up, float down, float dt)
    {
        if (target > current) return Math.Min(target, current + up * dt);
        return Math.Max(target, current - down * dt);
    }
}

/// <summary>
/// Smooth random values between 0 and 1 that change about <c>Hz</c> times a second (a new random target each period, eased
/// toward). Deterministic for a given seed, so the tests can check texture without chance.
/// </summary>
internal sealed class SmoothNoise
{
    private uint _state;
    private float _from, _to, _phase;

    public SmoothNoise(uint seed)
    {
        _state = seed == 0 ? 0x9E3779B9u : seed;
        _from = Next();
        _to = Next();
    }

    private float Next()
    {
        uint x = _state;
        x ^= x << 13;
        x ^= x >> 17;
        x ^= x << 5;
        _state = x;
        return (x & 0xFFFFFF) / (float)0x1000000;
    }

    public float Step(float dt, float hz)
    {
        if (hz <= 0.01f) return _from;
        _phase += dt * hz;
        while (_phase >= 1f)
        {
            _phase -= 1f;
            _from = _to;
            _to = Next();
        }
        float t = _phase * _phase * (3f - 2f * _phase);
        return _from + (_to - _from) * t;
    }

    public void Reset()
    {
        _phase = 0f;
        _from = Next();
        _to = Next();
    }
}

/// <summary>A hit: jumps to its strength at once and dies away over about <c>Decay</c> seconds (a bigger hit while one is fading takes over).</summary>
internal sealed class Pulse
{
    public float Value { get; private set; }

    public void Fire(float strength)
    {
        strength = FeelMath.Clamp01(strength);
        if (strength > Value) Value = strength;
    }

    public float Step(float dt, float decay)
    {
        if (Value <= 0f) return 0f;
        // Exponential: about 5% left after <decay> seconds.
        Value *= (float)Math.Exp(-3f * dt / Math.Max(0.01f, decay));
        if (Value < 0.004f) Value = 0f;
        return Value;
    }

    public void Reset() => Value = 0f;
}
