using System;
using System.Globalization;

namespace OverTheHillPlus.Logging;

/// <summary>Frame rate over a window of recent frames (see <see cref="FrameHistory.Stats"/>).</summary>
internal readonly struct FpsStats
{
    public readonly int Frames;
    public readonly double Seconds;
    public readonly double AvgFps;
    /// <summary>The average of the slowest 1 % of the frames, as frames per second.</summary>
    public readonly double LowFps;
    public readonly double WorstFrameMs;
    public readonly double BestFrameMs;

    public FpsStats(int frames, double seconds, double avgFps, double lowFps, double worstFrameMs, double bestFrameMs)
    {
        Frames = frames;
        Seconds = seconds;
        AvgFps = avgFps;
        LowFps = lowFps;
        WorstFrameMs = worstFrameMs;
        BestFrameMs = bestFrameMs;
    }

    public bool Any => Frames > 0;

    /// <summary>"58.3 fps over the last 5.0 s (1% low 41.2 fps, frames 9.8 to 31.0 ms)", or "no frames yet".</summary>
    public override string ToString()
    {
        if (Frames == 0) return "no frames yet";
        var c = CultureInfo.InvariantCulture;
        return AvgFps.ToString("0.0", c) + " fps over the last " + Seconds.ToString("0.0", c) + " s (" + Frames + " frames; 1% low " +
               LowFps.ToString("0.0", c) + " fps; frames " + BestFrameMs.ToString("0.0", c) + " to " + WorstFrameMs.ToString("0.0", c) + " ms)";
    }
}

/// <summary>
/// The lengths of the last frames in a fixed ring (adding one allocates nothing), for the diagnostics snapshot's frame
/// rate. Main thread writes; a read from the same thread. Pure.
/// </summary>
internal sealed class FrameHistory
{
    private readonly float[] _dt;
    private int _next;
    private int _count;

    public FrameHistory(int capacity = 1024) => _dt = new float[Math.Max(8, capacity)];

    /// <summary>Frames kept.</summary>
    public int Count => _count;

    /// <summary>Adds one frame of <paramref name="seconds"/> (zero, negative and absurd lengths are ignored).</summary>
    public void Add(double seconds)
    {
        if (!(seconds > 0) || seconds > 60) return;
        _dt[_next] = (float)seconds;
        _next = (_next + 1) % _dt.Length;
        if (_count < _dt.Length) _count++;
    }

    /// <summary>The frames of the last <paramref name="windowSeconds"/> (newest first, at most what the ring holds).</summary>
    public FpsStats Stats(double windowSeconds)
    {
        if (_count == 0) return default;
        double total = 0, worst = 0, best = double.MaxValue;
        int frames = 0;
        for (int i = 1; i <= _count; i++)
        {
            double dt = _dt[(_next - i + _dt.Length) % _dt.Length];
            if (total + dt > windowSeconds && frames > 0) break;
            total += dt;
            frames++;
            if (dt > worst) worst = dt;
            if (dt < best) best = dt;
        }
        double avg = total > 0 ? frames / total : 0;
        // 1 % low: the mean of the slowest frames, at least one of them.
        int slow = Math.Max(1, frames / 100);
        double sum = 0;
        var top = new float[slow];
        int used = 0;
        for (int i = 1; i <= frames; i++)
        {
            float dt = _dt[(_next - i + _dt.Length) % _dt.Length];
            if (used < slow) top[used++] = dt;
            else
            {
                int min = 0;
                for (int k = 1; k < slow; k++) if (top[k] < top[min]) min = k;
                if (dt > top[min]) top[min] = dt;
            }
        }
        for (int k = 0; k < used; k++) sum += top[k];
        double low = used > 0 && sum > 0 ? used / sum : 0;
        return new FpsStats(frames, total, avg, low, worst * 1000.0, best == double.MaxValue ? 0 : best * 1000.0);
    }
}
