using System;

namespace OverTheHillPlus.Cockpit;

/// <summary>
/// The head on a spring: it lags behind the car's acceleration (back under power, forward under braking, sideways in
/// corners, down over a drop) and settles again (no game types, unit tested). Metres in the car's space: x right,
/// y up, z forward. The acceleration in is the car's, in the car's space, in m/s^2.
/// </summary>
internal sealed class HeadSway
{
    public float X { get; private set; }
    public float Y { get; private set; }
    public float Z { get; private set; }

    /// <summary>Metres of head movement per m/s^2 of acceleration.</summary>
    public float Gain = 0.006f;
    /// <summary>The most the head moves from rest on each axis (metres).</summary>
    public float MaxOffset = 0.09f;

    private float _vx, _vy, _vz;
    private float _ax, _ay, _az;

    private const float Spring = 140f, Damping = 15f, MaxAccel = 35f, AccelSharpness = 14f;

    public void Reset()
    {
        X = Y = Z = _vx = _vy = _vz = _ax = _ay = _az = 0f;
    }

    public void Step(float ax, float ay, float az, float dt)
    {
        if (!(dt > 0f)) return;
        dt = Math.Min(dt, 0.05f);
        // Physics steps and frames don't line up, so the raw acceleration jumps between zero and double: smooth it.
        float k = 1f - (float)Math.Exp(-AccelSharpness * dt);
        _ax += (Limit(ax) - _ax) * k;
        _ay += (Limit(ay) - _ay) * k;
        _az += (Limit(az) - _az) * k;
        // Fixed small steps keep the spring stable at a low frame rate.
        int n = Math.Max(1, (int)Math.Ceiling(dt / 0.008f));
        float h = dt / n;
        for (int i = 0; i < n; i++)
        {
            _vx += (Spring * (-_ax * Gain - X) - Damping * _vx) * h;
            _vy += (Spring * (-_ay * Gain - Y) - Damping * _vy) * h;
            _vz += (Spring * (-_az * Gain - Z) - Damping * _vz) * h;
            X += _vx * h;
            Y += _vy * h;
            Z += _vz * h;
        }
        X = Clamp(X);
        Y = Clamp(Y);
        Z = Clamp(Z);
    }

    private float Clamp(float v) => v < -MaxOffset ? -MaxOffset : v > MaxOffset ? MaxOffset : v;

    private static float Limit(float a) => float.IsNaN(a) ? 0f : a < -MaxAccel ? -MaxAccel : a > MaxAccel ? MaxAccel : a;
}
