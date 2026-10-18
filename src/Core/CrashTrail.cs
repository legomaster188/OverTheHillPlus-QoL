using System;
using System.Collections.Generic;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Text;
using BepInEx;
using UnityEngine;

namespace OverTheHillPlus;

/// <summary>
/// A crash trail: the last steps the mods took, kept in a memory-mapped file (BepInEx/oth.trail.bin) so they survive a
/// hard crash of the game (the operating system still writes the mapped pages out after the process dies). Core marks
/// every mod's per-frame work and overlay drawing before it runs; mods can mark finer steps with <see cref="Mark"/>.
/// On the next start, when the last session didn't close cleanly, the last steps go to the BepInEx log
/// ("[CrashTrail] ..."), so a crash names the mod and step that was running.
/// <para>Several game copies may share one BepInEx folder (multiplayer tests): each takes the first trail file no
/// other copy has open (oth.trail.bin, then oth.trail.2.bin ... oth.trail.4.bin), and the report on start covers every
/// trail file that no running copy holds and that didn't close cleanly.</para>
/// <para><see cref="Mark"/> is for the main thread: a call from another thread (a worker task, Unity's log callback
/// on a background thread) is skipped.</para>
/// <para>The same steps also go into a managed ring (with a Stopwatch timestamp each) that the hang watchdog reads
/// from its own thread (<see cref="Recent"/>), and <see cref="RunningMod"/> says whose hook is running right now.</para>
/// </summary>
public static class CrashTrail
{
    private const int Slots = 512, SlotSize = 128, TextMax = SlotSize - 13, Header = 16;
    private const int Magic = 0x5448544F; // "OTHT"
    private static MemoryMappedFile _file;
    private static MemoryMappedViewAccessor _view;
    private static int _seq;
    private static readonly Dictionary<string, byte[]> Encoded = new();

    // The managed copy of the last steps, for the watchdog thread: the main thread writes a slot, then publishes its
    // number; a reader takes the published number and reads back from it.
    private static readonly string[] RingText = new string[Slots];
    private static readonly int[] RingFrame = new int[Slots];
    private static readonly float[] RingTime = new float[Slots];
    private static readonly long[] RingTicks = new long[Slots];
    private static int _published;
    private static string _running;

    private const int MaxFiles = 4;
    private static int _mainThread;

    /// <summary>The last step's number (any thread).</summary>
    public static int Seq => System.Threading.Volatile.Read(ref _published);

    /// <summary>True on the game's main thread (the thread core loaded on).</summary>
    public static bool OnMainThread => _mainThread == 0 || Environment.CurrentManagedThreadId == _mainThread;

    /// <summary>
    /// The mod whose feature hook, static hook, Guard site or guarded patch is running right now on the main thread
    /// (null between them). Set by core around each hook; readable from any thread.
    /// </summary>
    public static string RunningMod
    {
        get => System.Threading.Volatile.Read(ref _running);
        internal set => System.Threading.Volatile.Write(ref _running, value);
    }

    /// <summary>A trail of a session that did not close cleanly, read on this start (for the crash report).</summary>
    internal sealed class Previous
    {
        public string Path;
        public DateTime EndedUtc;
        public List<Logging.TrailRow> Rows;
    }

    /// <summary>The unclean trails found on this start (empty after a clean end).</summary>
    internal static readonly List<Previous> PreviousTrails = new();

    private static string TrailPath(int i) => Path.Combine(Paths.BepInExRootPath, i == 1 ? "oth.trail.bin" : $"oth.trail.{i}.bin");

    /// <summary>The trail file this game copy writes (null when the trail is off).</summary>
    public static string FileInUse { get; private set; }

    /// <summary>Reports the previous session's trail when it crashed, then starts a new one. Called once by core.</summary>
    internal static void Start(Action<string> log)
    {
        _mainThread = Environment.CurrentManagedThreadId;
        try
        {
            // Every trail no other running copy holds: report it when it didn't close cleanly.
            for (int i = 1; i <= MaxFiles; i++) ReportPrevious(TrailPath(i), log);
            for (int i = 1; i <= MaxFiles && _file == null; i++)
            {
                try
                {
                    _file = MemoryMappedFile.CreateFromFile(TrailPath(i), FileMode.Create, null, Header + Slots * SlotSize,
                        MemoryMappedFileAccess.ReadWrite);
                    FileInUse = TrailPath(i);
                }
                catch (IOException) { _file = null; } // another game copy has this one open
            }
            if (_file == null) { log?.Invoke($"[CrashTrail] off: all {MaxFiles} trail files are in use by other game copies"); return; }
            _view = _file.CreateViewAccessor();
            _view.Write(0, Magic);
            _view.Write(4, 0);      // 0 = running, 1 = closed cleanly, 2 = crash already reported
            Application.quitting += (Action)Closed;
            Mark("session start");
        }
        catch (Exception e) { log?.Invoke("[CrashTrail] off: " + e.Message); _view = null; }
    }

    /// <summary>The last step marked (for error reports: what the mods were doing when it happened).</summary>
    public static string LastStep { get; private set; } = "";

    /// <summary>Records one step (cheap: a copy into mapped memory). Main thread only: a call from any other thread is
    /// skipped. Never throws.</summary>
    public static void Mark(string step)
    {
        if (step == null || (_mainThread != 0 && Environment.CurrentManagedThreadId != _mainThread)) return;
        LastStep = step;
        int seq = ++_seq;
        int frame;
        float time;
        try
        {
            frame = Time.frameCount;
            time = Time.realtimeSinceStartup;
        }
        catch { frame = 0; time = 0f; }
        int slot = seq & (Slots - 1);
        RingText[slot] = step;
        RingFrame[slot] = frame;
        RingTime[slot] = time;
        RingTicks[slot] = System.Diagnostics.Stopwatch.GetTimestamp();
        System.Threading.Volatile.Write(ref _published, seq);
        var v = _view;
        if (v == null) return;
        try
        {
            if (!Encoded.TryGetValue(step, out var bytes))
            {
                bytes = Encoding.UTF8.GetBytes(step.Length > TextMax ? step.Substring(0, TextMax) : step);
                if (Encoded.Count < 4096) Encoded[step] = bytes;
            }
            long at = Header + (long)(seq % Slots) * SlotSize;
            v.Write(at, seq);
            v.Write(at + 4, frame);
            v.Write(at + 8, time);
            int n = Math.Min(bytes.Length, TextMax);
            v.Write(at + 12, (byte)n);
            v.WriteArray(at + 13, bytes, 0, n);
            v.Write(8, seq);
        }
        catch { /* the trail must never break the game */ }
    }

    /// <summary>
    /// Copies the steps after <paramref name="afterSeq"/> up to <paramref name="toSeq"/> (at most the last
    /// <paramref name="max"/> of them, oldest first) into <paramref name="into"/>, and returns how many were left out
    /// at the start. Safe from any thread: a step overwritten while it is read may show the newer text.
    /// </summary>
    internal static int Recent(int afterSeq, int toSeq, int max, List<Logging.TrailRow> into)
    {
        into.Clear();
        if (toSeq <= afterSeq) return 0;
        int from = Math.Max(afterSeq + 1, Math.Max(1, toSeq - Math.Min(max, Slots - 1) + 1));
        for (int s = from; s <= toSeq; s++)
        {
            int slot = s & (Slots - 1);
            into.Add(new Logging.TrailRow(s, RingFrame[slot], RingTime[slot], RingText[slot], RingTicks[slot]));
        }
        return from - (afterSeq + 1);
    }

    /// <summary>The last <paramref name="count"/> steps, oldest first (any thread).</summary>
    internal static void Recent(int count, List<Logging.TrailRow> into)
    {
        int to = Seq;
        Recent(Math.Max(0, to - count), to, count, into);
    }

    /// <summary>The last few steps as one line, newest last: "a &gt; b &gt; c" (any thread).</summary>
    internal static string RecentLine(int count)
    {
        var rows = new List<Logging.TrailRow>(count);
        Recent(count, rows);
        var sb = new StringBuilder();
        foreach (var r in rows)
        {
            if (sb.Length > 0) sb.Append(" > ");
            sb.Append(r.Text);
        }
        return sb.ToString();
    }

    /// <summary>Writes the wall clock into the trail's header (once a second, from the frame clock): a crash report then
    /// knows when the crashed session ended, for matching Windows' event log.</summary>
    internal static void StampClock()
    {
        try { _view?.Write(12, (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds()); } catch { }
    }

    private static void Closed()
    {
        try { Mark("session end (clean)"); _view?.Write(4, 1); _view?.Flush(); } catch { }
    }

    private static void ReportPrevious(string path, Action<string> log)
    {
        if (log == null || !File.Exists(path)) return;
        byte[] all;
        try
        {
            // Exclusive: a trail another running game copy writes can't be opened, and is not a crash.
            using var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
            all = new byte[f.Length];
            int got = 0;
            while (got < all.Length)
            {
                int n = f.Read(all, got, all.Length - got);
                if (n <= 0) break;
                got += n;
            }
        }
        catch { return; }
        if (all.Length < Header + Slots * SlotSize || BitConverter.ToInt32(all, 0) != Magic) return;
        if (BitConverter.ToInt32(all, 4) != 0) return; // 1 = closed cleanly, 2 = already reported
        var rows = new List<(int seq, int frame, float time, string text)>();
        for (int i = 0; i < Slots; i++)
        {
            int at = Header + i * SlotSize;
            int seq = BitConverter.ToInt32(all, at);
            if (seq <= 0) continue;
            int n = Math.Min((int)all[at + 12], TextMax);
            rows.Add((seq, BitConverter.ToInt32(all, at + 4), BitConverter.ToSingle(all, at + 8),
                Encoding.UTF8.GetString(all, at + 13, n)));
        }
        if (rows.Count == 0) return;
        rows.Sort((a, b) => a.seq.CompareTo(b.seq));
        try
        {
            int stamp = BitConverter.ToInt32(all, 12);
            var prev = new Previous
            {
                Path = path,
                EndedUtc = stamp > 0 ? DateTimeOffset.FromUnixTimeSeconds(stamp).UtcDateTime : File.GetLastWriteTimeUtc(path),
                Rows = new List<Logging.TrailRow>(rows.Count),
            };
            foreach (var r in rows) prev.Rows.Add(new Logging.TrailRow(r.seq, r.frame, r.time, r.text));
            PreviousTrails.Add(prev);
        }
        catch { /* the report below still goes to the log */ }
        log($"[CrashTrail] a previous session ({Path.GetFileName(path)}) did not close cleanly. Its last steps (oldest first):");
        for (int i = Math.Max(0, rows.Count - 40); i < rows.Count; i++)
        {
            var r = rows[i];
            log($"[CrashTrail]   #{r.seq} frame {r.frame} t={r.time:0.00}s  {r.text}");
        }
        // Report each crash once: a trail file no later copy reuses would otherwise show up on every start.
        try
        {
            using var w = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None);
            w.Position = 4;
            w.Write(BitConverter.GetBytes(2), 0, 4);
        }
        catch { /* reported again next time: harmless */ }
    }
}
