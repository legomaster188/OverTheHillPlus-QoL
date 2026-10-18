using System;

namespace OverTheHillPlus.Ghosts;

/// <summary>
/// Playback between the 20 Hz samples (pure): linear position, wheel steer / spin / suspension, and a normalised lerp
/// of the rotation that takes the short way round.
/// </summary>
internal static class GhostSampler
{
    /// <summary>Writes the drive's look at <paramref name="seconds"/> (clamped to the drive) into <paramref name="into"/>.</summary>
    public static void Sample(GhostRecording rec, float seconds, GhostFrame into)
    {
        var frames = rec.Frames;
        if (frames.Count == 0) return;
        float at = Math.Max(0f, seconds) * rec.Hz;
        int i = (int)Math.Floor(at);
        if (i >= frames.Count - 1) { Copy(frames[frames.Count - 1], into, rec.WheelCount); return; }
        float t = at - i;
        GhostFrame a = frames[i], b = frames[i + 1];
        into.Px = Lerp(a.Px, b.Px, t);
        into.Py = Lerp(a.Py, b.Py, t);
        into.Pz = Lerp(a.Pz, b.Pz, t);
        float sign = a.Qx * b.Qx + a.Qy * b.Qy + a.Qz * b.Qz + a.Qw * b.Qw < 0f ? -1f : 1f;
        GhostCodec.Normalise(Lerp(a.Qx, b.Qx * sign, t), Lerp(a.Qy, b.Qy * sign, t), Lerp(a.Qz, b.Qz * sign, t), Lerp(a.Qw, b.Qw * sign, t),
            out into.Qx, out into.Qy, out into.Qz, out into.Qw);
        int n = Math.Min(rec.WheelCount, into.Wheels.Length);
        for (int w = 0; w < n; w++)
        {
            GhostWheel wa = a.Wheels[w], wb = b.Wheels[w];
            into.Wheels[w] = new GhostWheel
            {
                Steer = Lerp(wa.Steer, wb.Steer, t),
                Spin = Lerp(wa.Spin, wb.Spin, t), // not wrapped: a fast wheel turns more than half a turn a sample
                Suspension = Lerp(wa.Suspension, wb.Suspension, t),
            };
        }
    }

    private static void Copy(GhostFrame f, GhostFrame into, int wheels)
    {
        into.Px = f.Px; into.Py = f.Py; into.Pz = f.Pz;
        into.Qx = f.Qx; into.Qy = f.Qy; into.Qz = f.Qz; into.Qw = f.Qw;
        int n = Math.Min(wheels, into.Wheels.Length);
        for (int w = 0; w < n; w++) into.Wheels[w] = f.Wheels[w];
    }

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;
}

/// <summary>Times as the mod page shows them.</summary>
internal static class GhostTime
{
    /// <summary>A race time: m:ss.cc.</summary>
    public static string Race(int ms)
    {
        if (ms < 0) return "-";
        int cs = ms / 10;
        return $"{cs / 6000}:{cs / 100 % 60:00}.{cs % 100:00}";
    }

    /// <summary>A length: m:ss (whole seconds, rounded down).</summary>
    public static string Clock(float seconds)
    {
        int s = Math.Max(0, (int)seconds);
        return $"{s / 60}:{s % 60:00}";
    }
}
