using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using UnityEngine;

namespace OverTheHillPlus.Logging;

/// <summary>
/// The frame clock and the hang watchdog. The main thread beats once per frame (<see cref="Beat"/>, first thing in
/// core's Update): a frame longer than [Debug] SpikeMs is noted with the steps of ours in it. A background thread
/// (<see cref="Watch"/>) checks the last beat every 200 ms: when the main thread has not finished a frame for
/// [Debug] HangSeconds it writes (flushed) how long, the step the main thread is still in, whose hook is running and
/// the last 40 steps; again at 10 s and 30 s, and "main thread back after X s" when it recovers. The watchdog thread
/// never calls Unity: it reads only the plain values the main thread publishes (Volatile) and the trail's ring.
/// </summary>
internal static class FrameWatch
{
    private static long _beat;
    private static int _frame;
    private static int _seqAtBeat;
    private static double _nextSecond, _nextMinute, _nextLogSummary;
    private static readonly RateLimiter Spikes = new(2.0, 400);
    /// <summary>The lengths of the last frames (the diagnostics snapshot's frame rate).</summary>
    internal static readonly FrameHistory Frames = new(2048);
    private static readonly List<TrailRow> SpikeRows = new(64);
    private static Thread _thread;
    private static double _hangSeconds = 3;
    private static double _spikeMs = 250;
    private static long _slowTicks = long.MaxValue;
    private static string _scene = "?", _session = "MENU";
    private static readonly double Freq = Stopwatch.Frequency;

    /// <summary>The frame of the last beat (any thread).</summary>
    public static int Frame => Volatile.Read(ref _frame);

    /// <summary>The active scene's name, refreshed once a second (any thread).</summary>
    public static string Scene => Volatile.Read(ref _scene);

    /// <summary>MENU, SOLO, HOST, FRIENDS or PUBLIC, refreshed once a second (any thread).</summary>
    public static string Session => Volatile.Read(ref _session);

    /// <summary>A hook longer than this (Stopwatch ticks) is a slow hook (long.MaxValue: off).</summary>
    public static long SlowTicks => Volatile.Read(ref _slowTicks);

    /// <summary>Main thread, once per frame, first thing in core's Update.</summary>
    internal static void Beat()
    {
        long now = Stopwatch.GetTimestamp();
        int frame;
        try { frame = Time.frameCount; } catch { frame = 0; }
        int seq = CrashTrail.Seq;
        long prev = _beat;
        if (prev != 0)
        {
            double ms = (now - prev) * 1000.0 / Freq;
            Frames.Add(ms / 1000.0);
            Lifecycle.OnBeat(ms);
            if (DebugLog.On && _spikeMs > 0 && ms >= _spikeMs && frame > 30) Spike(ms, prev, now, _seqAtBeat, seq, frame - 1);
        }
        _seqAtBeat = seq;
        Volatile.Write(ref _frame, frame);
        Volatile.Write(ref _beat, now);
        double t = now / Freq;
        if (t >= _nextSecond)
        {
            _nextSecond = t + 1;
            EverySecond(t);
        }
    }

    private static void EverySecond(double now)
    {
        CrashTrail.StampClock();
        try { Volatile.Write(ref _scene, UnityEngine.SceneManagement.SceneManager.GetActiveScene().name ?? "?"); } catch { }
        try { Volatile.Write(ref _session, GameState.Badge); } catch { }
        _hangSeconds = DebugConfig.Get(DebugConfig.HangSeconds, 3f);
        _spikeMs = DebugConfig.Get(DebugConfig.SpikeMs, 250);
        float slow = DebugConfig.Get(DebugConfig.SlowHookMs, 20f);
        Volatile.Write(ref _slowTicks, slow > 0 && DebugLog.On ? (long)(slow / 1000.0 * Freq) : long.MaxValue);
        try { ErrorTap.Tick(now); } catch { }
        try { ConfigLog.Flush(now); Lifecycle.Tick(); } catch (Exception e) { DebugLog.Error("core", e, "FrameWatch.EverySecond"); }
        if (DebugLog.On && _hangSeconds > 0 && _thread == null) StartWatchdog();
        if (_nextMinute == 0) _nextMinute = now + 60;
        if (now < _nextMinute) return;
        _nextMinute = now + 60;
        if (!DebugLog.On) return;
        try
        {
            var lines = DebugLog.Book.TakeSummary(now, 60, Clock);
            if (lines.Count > 0)
            {
                DebugLog.Write("SUMRY", "-", "errors that repeated in the last 60 s:\n" + string.Join("\n", lines));
                // The BepInEx log gets the summary too, at most every 5 minutes.
                if (now >= _nextLogSummary)
                {
                    _nextLogSummary = now + 300;
                    Core.CorePlugin.Logger?.LogWarning($"[Debug] errors that keep repeating (each was logged once in full): {string.Join("; ", lines)}");
                }
            }
            if (DebugLog.IsInfo) DebugLog.Write("STATS", "-", HookTiming.Table(60)); // once a minute: which mods cost frame time
        }
        catch { }
    }

    /// <summary>A label for a time on <see cref="DebugLog.Now"/>'s clock: the local wall-clock time.</summary>
    internal static string Clock(double at) => DateTime.Now.AddSeconds(at - DebugLog.Now).ToString("HH:mm:ss");

    private static void Spike(double ms, long frameStart, long frameEnd, int seqFrom, int seqTo, int frame)
    {
        if (!Spikes.Allow(frameEnd / Freq)) return;
        try
        {
            int skipped = CrashTrail.Recent(seqFrom, seqTo, 60, SpikeRows);
            string text = ReportText.SpikeText(ms, _spikeMs, frame, SpikeRows, skipped, frameStart, frameEnd, Freq, ModMap.ModOfStep,
                                               Spikes.TakeSuppressed());
            string mod = null;
            if (SpikeRows.Count > 0) mod = ModMap.ModOfStep(SpikeRows[SpikeRows.Count - 1].Text);
            DebugLog.Write("SPIKE", mod ?? "-", text);
        }
        catch { }
    }

    private static void StartWatchdog()
    {
        try
        {
            _thread = new Thread(Watch) { IsBackground = true, Name = "OTH hang watchdog", Priority = System.Threading.ThreadPriority.AboveNormal };
            _thread.Start();
        }
        catch (Exception e)
        {
            Core.CorePlugin.Logger?.LogWarning("[Debug] the hang watchdog could not start: " + e.Message);
            _thread = new Thread(() => { }); // don't try again
        }
    }

    private static void Watch()
    {
        var watch = new HangWatch(_hangSeconds);
        var rows = new List<TrailRow>(48);
        while (true)
        {
            try
            {
                Thread.Sleep(200);
                if (!DebugLog.On || _hangSeconds <= 0) continue;
                watch.First = _hangSeconds;
                long beat = Volatile.Read(ref _beat);
                long now = Stopwatch.GetTimestamp();
                var ev = watch.Check(beat, now, Freq, out double seconds);
                if (ev == HangEvent.Stalled)
                {
                    CrashTrail.Recent(40, rows);
                    string step = CrashTrail.LastStep;
                    string running = CrashTrail.RunningMod;
                    string text = ReportText.HangText(seconds, Frame, running, step, rows, now, Freq, ModMap.ModOfStep);
                    DebugLog.Write("HANG", running ?? ModMap.ModOfStep(step) ?? "-", text);
                }
                else if (ev == HangEvent.Recovered)
                {
                    DebugLog.Write("HANG", "-", $"main thread back after {seconds:0.0} s (now at frame {Frame})");
                    // The BepInEx log is written on the main thread only: it gets the line once the game runs again.
                    double s = seconds;
                    Core.Install.MainThread.Post(() => Core.CorePlugin.Logger?.LogWarning(
                        $"[Debug] the game was stuck for {s:0.0} s; the debug log ({DebugLog.FilePath}) has the steps the mods took before it."));
                }
            }
            catch { /* keep watching */ }
        }
    }
}
