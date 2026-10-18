using System;

namespace OverTheHillPlus;

/// <summary>
/// Turns per-frame samples of a car (wheels on the ground or not, position, the car's up vector) into jumps: when it
/// took off, how long it was in the air, how far it went and how many times it flipped. Pure logic (no game types), so
/// it is unit tested; <see cref="CarAir"/> feeds it the local car once per frame for the mods that use it.
///
/// - A jump starts on the first sample with no wheel on the ground and ends once the wheels have touched the ground
///   for <see cref="LandGrace"/> seconds (a wheel brushing a rock mid-air does not end it).
/// - Only jumps of at least <see cref="MinAirTime"/> seconds count: driving over a bump is not a jump.
/// - Flips: the angle the car's up vector turns through while in the air, summed frame by frame (a yaw spin keeps the
///   up vector still, so it is not a flip). 360 degrees is one flip; a landing up to 60 degrees short still counts.
/// - A sample that jumps further than <see cref="MaxStep"/> metres (respawn, reset, teleport) cancels the jump.
/// </summary>
internal sealed class JumpDetector
{
    /// <summary>One finished jump.</summary>
    internal readonly struct Landing
    {
        public readonly float AirTime;
        /// <summary>Horizontal distance from take-off to landing, in metres.</summary>
        public readonly float Distance;
        /// <summary>Highest point above the take-off point, in metres.</summary>
        public readonly float Height;
        public readonly float TumbleDegrees;
        public readonly int Flips;

        public Landing(float airTime, float distance, float height, float tumbleDegrees)
        {
            AirTime = airTime;
            Distance = distance;
            Height = height;
            TumbleDegrees = tumbleDegrees;
            Flips = FlipsFor(tumbleDegrees);
        }
    }

    public float MinAirTime = 0.35f;
    public float LandGrace = 0.08f;
    public float MaxStep = 60f;

    public bool InAir { get; private set; }
    /// <summary>Seconds in the air so far (0 on the ground).</summary>
    public float AirTime { get; private set; }
    /// <summary>Degrees the car has turned over in the air so far.</summary>
    public float TumbleDegrees { get; private set; }
    /// <summary>Counts take-offs, so a caller can do something once per jump.</summary>
    public int JumpNumber { get; private set; }

    private float _groundedFor;
    private float _x0, _y0, _z0, _maxY;
    private float _px, _py, _pz;
    private float _ux, _uy, _uz;
    private bool _hasLast;

    /// <summary>Whole flips for a tumble angle (a landing up to 60 degrees short of a full turn still counts).</summary>
    public static int FlipsFor(float degrees) => degrees <= 0f ? 0 : (int)((degrees + 60f) / 360f);

    /// <summary>Forgets any jump in progress (car changed, session ended).</summary>
    public void Reset()
    {
        InAir = false;
        AirTime = 0f;
        TumbleDegrees = 0f;
        _groundedFor = 0f;
        _hasLast = false;
    }

    /// <summary>
    /// Feeds one sample. Returns true when a jump (at least <see cref="MinAirTime"/> long) has just ended, with its
    /// numbers in <paramref name="landing"/>.
    /// </summary>
    public bool Step(float dt, bool grounded, float x, float y, float z, float ux, float uy, float uz, out Landing landing)
    {
        landing = default;
        if (dt < 0f || float.IsNaN(dt) || !Finite(x) || !Finite(y) || !Finite(z)) return false;

        if (_hasLast)
        {
            float dx = x - _px, dy = y - _py, dz = z - _pz;
            if (dx * dx + dy * dy + dz * dz > MaxStep * MaxStep)
            {
                // Respawned or teleported: whatever was going on is not a jump.
                Reset();
            }
        }
        float lastUx = _ux, lastUy = _uy, lastUz = _uz;
        bool hadLast = _hasLast;
        _px = x; _py = y; _pz = z;
        _ux = ux; _uy = uy; _uz = uz;
        _hasLast = true;

        if (!InAir)
        {
            if (grounded) return false;
            InAir = true;
            JumpNumber++;
            // The car left the ground somewhere during the last frame: that frame counts as air time already.
            AirTime = dt;
            TumbleDegrees = hadLast ? AngleBetween(lastUx, lastUy, lastUz, ux, uy, uz) : 0f;
            _groundedFor = 0f;
            _x0 = x; _y0 = y; _z0 = z;
            _maxY = y;
            return false;
        }

        if (hadLast) TumbleDegrees += AngleBetween(lastUx, lastUy, lastUz, ux, uy, uz);
        if (y > _maxY) _maxY = y;

        if (!grounded)
        {
            _groundedFor = 0f;
            AirTime += dt;
            return false;
        }

        _groundedFor += dt;
        if (_groundedFor < LandGrace) return false;

        float air = AirTime;
        float hx = x - _x0, hz = z - _z0;
        var result = new Landing(air, (float)Math.Sqrt(hx * hx + hz * hz), Math.Max(0f, _maxY - _y0), TumbleDegrees);
        InAir = false;
        AirTime = 0f;
        TumbleDegrees = 0f;
        _groundedFor = 0f;
        if (air < MinAirTime) return false;
        landing = result;
        return true;
    }

    /// <summary>Angle in degrees between two direction vectors (0 when either is zero).</summary>
    public static float AngleBetween(float ax, float ay, float az, float bx, float by, float bz)
    {
        double la = Math.Sqrt(ax * ax + ay * ay + az * az), lb = Math.Sqrt(bx * bx + by * by + bz * bz);
        if (la < 1e-6 || lb < 1e-6) return 0f;
        double dot = (ax * bx + ay * by + az * bz) / (la * lb);
        if (dot > 1.0) dot = 1.0;
        if (dot < -1.0) dot = -1.0;
        return (float)(Math.Acos(dot) * 180.0 / Math.PI);
    }

    private static bool Finite(float v) => !float.IsNaN(v) && !float.IsInfinity(v);
}
