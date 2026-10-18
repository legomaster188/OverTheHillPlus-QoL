using System;

namespace OverTheHillPlus.CameraPlus;

/// <summary>The cameras Camera+ offers. Game = the game's own chase camera, untouched.</summary>
internal enum CamMode
{
    Game,
    ChaseFar,
    Hood,
    Bumper,
    Drone,
}

/// <summary>
/// Camera+'s pure maths (no game types, unit tested): which camera comes next, where the drone sits on its orbit, where
/// the hood and bumper cameras sit on a car of a given size, and frame-rate independent smoothing.
/// </summary>
internal static class CameraMath
{
    public const int ModeCount = 5;
    public const float MinPitch = -10f, MaxPitch = 85f;
    public const float MinDroneDistance = 2f, MaxDroneDistance = 60f;

    public static readonly string[] ModeNames = { "Game", "Chase far", "Hood", "Bumper", "Drone" };

    public static string Name(CamMode m) => (int)m >= 0 && (int)m < ModeNames.Length ? ModeNames[(int)m] : "?";

    /// <summary>
    /// The camera after <paramref name="current"/> in the cycle: Game, then each mode that
    /// <paramref name="inCycle"/> says is in it (index = (int)mode), then back to Game.
    /// </summary>
    public static CamMode Next(CamMode current, Func<CamMode, bool> inCycle)
    {
        int start = (int)current;
        if (start < 0 || start >= ModeCount) start = 0;
        for (int i = 1; i <= ModeCount; i++)
        {
            var m = (CamMode)((start + i) % ModeCount);
            if (m == CamMode.Game || inCycle(m)) return m;
        }
        return CamMode.Game;
    }

    /// <summary>Same, backwards.</summary>
    public static CamMode Previous(CamMode current, Func<CamMode, bool> inCycle)
    {
        int start = (int)current;
        if (start < 0 || start >= ModeCount) start = 0;
        for (int i = 1; i <= ModeCount; i++)
        {
            var m = (CamMode)(((start - i) % ModeCount + ModeCount) % ModeCount);
            if (m == CamMode.Game || inCycle(m)) return m;
        }
        return CamMode.Game;
    }

    public static float ClampPitch(float pitch) => pitch < MinPitch ? MinPitch : pitch > MaxPitch ? MaxPitch : pitch;

    public static float ClampDistance(float d) => float.IsNaN(d) ? 10f : d < MinDroneDistance ? MinDroneDistance : d > MaxDroneDistance ? MaxDroneDistance : d;

    /// <summary>Keeps an angle in [0, 360).</summary>
    public static float WrapYaw(float yaw)
    {
        if (float.IsNaN(yaw) || float.IsInfinity(yaw)) return 0f;
        yaw %= 360f;
        return yaw < 0f ? yaw + 360f : yaw;
    }

    /// <summary>
    /// Offset from the orbited point to the drone, for a yaw (degrees, 0 = behind a target facing +z, i.e. the drone
    /// sits at -z) and pitch (degrees above the horizon) at <paramref name="distance"/>. Y is up.
    /// </summary>
    public static (float x, float y, float z) Orbit(float yawDeg, float pitchDeg, float distance)
    {
        double yaw = yawDeg * Math.PI / 180.0, pitch = ClampPitch(pitchDeg) * Math.PI / 180.0;
        double flat = Math.Cos(pitch) * distance;
        return ((float)(-Math.Sin(yaw) * flat), (float)(Math.Sin(pitch) * distance), (float)(-Math.Cos(yaw) * flat));
    }

    /// <summary>Fraction to move toward a target this frame for exponential smoothing that does not depend on frame rate.</summary>
    public static float Damp(float sharpness, float dt)
    {
        if (sharpness <= 0f) return 1f;
        if (!(dt > 0f)) return 0f;
        return 1f - (float)Math.Exp(-sharpness * dt);
    }

    /// <summary>The car's size in its own space (x right, y up, z forward), from its renderers.</summary>
    internal readonly struct CarBox
    {
        public readonly float MinX, MaxX, MinY, MaxY, MinZ, MaxZ;

        public CarBox(float minX, float maxX, float minY, float maxY, float minZ, float maxZ)
        {
            MinX = minX; MaxX = maxX; MinY = minY; MaxY = maxY; MinZ = minZ; MaxZ = maxZ;
        }

        public float CenterX => (MinX + MaxX) * 0.5f;
        public float CenterZ => (MinZ + MaxZ) * 0.5f;
        public float Height => MaxY - MinY;
        public float Length => MaxZ - MinZ;

        /// <summary>A plausible car: between 1 and 15 m long, 0.5 and 6 m high.</summary>
        public bool Plausible => Length is > 1f and < 15f && Height is > 0.5f and < 6f && MaxX > MinX;

        /// <summary>Used when the car's size can't be read: a mid-size truck around its origin.</summary>
        public static CarBox Default => new(-1f, 1f, 0f, 2f, -2.4f, 2.4f);
    }

    /// <summary>Hood camera: on the centre line, a little above the hood and just in front of the windscreen.</summary>
    public static (float x, float y, float z) HoodPoint(CarBox b, float up, float forward) =>
        (b.CenterX, b.MinY + b.Height * 0.72f + up, b.CenterZ + b.Length * 0.18f + forward);

    /// <summary>Bumper camera: on the centre line, low, just ahead of the front of the car.</summary>
    public static (float x, float y, float z) BumperPoint(CarBox b, float up, float forward) =>
        (b.CenterX, b.MinY + b.Height * 0.3f + up, b.MaxZ + 0.12f + forward);

    /// <summary>
    /// Chase far: the game's camera pulled back from the point it looks at. <paramref name="camera"/> minus
    /// <paramref name="target"/> is scaled by <paramref name="scale"/> and lifted by <paramref name="lift"/>.
    /// </summary>
    public static (float x, float y, float z) PullBack((float x, float y, float z) target, (float x, float y, float z) camera,
        float scale, float lift)
    {
        if (float.IsNaN(scale) || scale < 1f) scale = 1f;
        return (target.x + (camera.x - target.x) * scale,
            target.y + (camera.y - target.y) * scale + lift,
            target.z + (camera.z - target.z) * scale);
    }
}
