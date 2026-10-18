using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using BepInEx;
using OverTheHillPlus.Features.Public.ModNet;
using UnityEngine;
using UnityEngine.InputSystem;

namespace OverTheHillPlus.Logging;

/// <summary>
/// The diagnostics snapshot: BepInEx/oth-reports/snapshot-&lt;time&gt;.txt with the session header, every mod's state and
/// last error, the hook timing totals, recent errors, the scene / session / players / car, the mod network, memory,
/// the frame rate of the last few seconds and the last 200 trail steps (text: <see cref="SnapshotText"/>). Written
/// by the MODS page's button or the [Debug] SnapshotKey hotkey (Ctrl+Shift+F12), on the main thread, from values
/// the game already holds. Works at every debug level. Never throws.
/// </summary>
internal static class Snapshot
{
    private const double FpsWindowSeconds = 5;

    /// <summary>The newest snapshot file this session, or null.</summary>
    internal static string LastPath { get; private set; }

    /// <summary>Why the newest attempt failed, or null.</summary>
    internal static string LastProblem { get; private set; }

    /// <summary>The key press that writes a snapshot (called from core's hotkeys).</summary>
    internal static void Hotkey(Keyboard kb)
    {
        var key = DebugConfig.SnapshotKey?.Value ?? Key.None;
        if (key == Key.None || kb == null) return;
        bool needMods = DebugConfig.Get(DebugConfig.SnapshotCtrlShift, true);
        try
        {
            if (!kb[key].wasPressedThisFrame) return;
            if (needMods && !(kb.ctrlKey.isPressed && kb.shiftKey.isPressed)) return;
        }
        catch (Exception e) { DebugLog.Error("core", e, "Snapshot.Hotkey"); return; }
        string result = Write("hotkey");
        Core.CorePlugin.Logger?.LogInfo("[Debug] " + result);
        try { Menu.SetStatus(result); } catch (Exception e) { DebugLog.Error("core", e, "Snapshot.Hotkey status"); }
    }

    /// <summary>Writes a snapshot now. Returns one line for the player ("Snapshot written: ...", or why it failed).</summary>
    internal static string Write(string reason)
    {
        try
        {
            var model = Gather(reason);
            string text = SnapshotText.Build(model);
            string dir = Path.Combine(Paths.BepInExRootPath, "oth-reports");
            Directory.CreateDirectory(dir);
            var names = new List<string>();
            foreach (var f in Directory.GetFiles(dir, SnapshotText.FilePrefix + "*" + SnapshotText.FileExt)) names.Add(Path.GetFileName(f));
            int keep = Math.Max(2, DebugConfig.Get(DebugConfig.KeepFiles, 10));
            foreach (var old in LogFiles.ToDelete(names, keep - 1, SnapshotText.FilePrefix, SnapshotText.FileExt))
            {
                try { File.Delete(Path.Combine(dir, old)); } catch (Exception e) { DebugLog.Error("core", e, "Snapshot.Write cleanup"); }
            }
            string path = null;
            var now = DateTime.Now;
            for (int copy = 1; copy <= 9; copy++)
            {
                path = Path.Combine(dir, SnapshotText.FileName(now, copy));
                if (!File.Exists(path)) break;
            }
            File.WriteAllText(path, text.Replace("\n", Environment.NewLine), new UTF8Encoding(false));
            LastPath = path;
            LastProblem = null;
            DebugLog.Info("core", $"diagnostics snapshot written ({reason}): {path}");
            return "Snapshot written: " + path;
        }
        catch (Exception e)
        {
            LastProblem = e.Message;
            DebugLog.Error("core", e, "Snapshot.Write");
            return "Could not write the snapshot: " + e.Message;
        }
    }

    private static SnapshotModel Gather(string reason)
    {
        var m = new SnapshotModel
        {
            Time = DateTime.Now,
            Reason = reason,
            Header = SessionReport.Header,
            DebugLevel = DebugLog.Level.ToString(),
            DebugFile = DebugLog.FilePath == null ? null : Path.GetFileName(DebugLog.FilePath),
            FpsWindow = FpsWindowSeconds,
            ModOfStep = ModMap.ModOfStep,
        };
        Part(() => { m.Frame = FrameWatch.Frame; m.Uptime = Time.realtimeSinceStartup; m.Scene = FrameWatch.Scene; }, "frame");
        Part(() => GatherMods(m), "mods");
        Part(() => m.HookTable = HookTiming.SessionTable(), "hook table");
        Part(() => GatherErrors(m), "errors");
        Part(() => GatherState(m), "state");
        Part(() => m.ModNet = ModNet.Snapshot(), "mod network");
        Part(() => GatherMemory(m), "memory");
        Part(() => m.Fps = FrameWatch.Frames.Stats(FpsWindowSeconds), "frame rate");
        Part(() => CrashTrail.Recent(200, m.Trail), "trail");
        return m;
    }

    private static void Part(Action gather, string what)
    {
        try { gather(); }
        catch (Exception e) { DebugLog.Error("core", e, "Snapshot " + what); }
    }

    private static void GatherMods(SnapshotModel m)
    {
        var lastByMod = new Dictionary<string, string>();
        var kinds = new Dictionary<string, int>();
        var counts = new Dictionary<string, int>();
        // The book lists the newest first: the first one seen for a mod is its latest error.
        foreach (var e in DebugLog.Book.Recent(DebugLog.Book.Count))
        {
            if (!lastByMod.ContainsKey(e.ModId)) lastByMod[e.ModId] = e.Title;
            kinds[e.ModId] = kinds.TryGetValue(e.ModId, out int k) ? k + 1 : 1;
            counts[e.ModId] = counts.TryGetValue(e.ModId, out int c) ? c + e.Count : e.Count;
        }
        foreach (var info in Core.Manager.ModManager.All)
        {
            if (info == null) continue;
            m.Mods.Add(new SnapshotMod
            {
                Id = info.Id,
                Version = info.Version,
                State = SessionReport.StateOf(info, out _),
                PatchFailed = PatchReport.Failed(info.Id),
                ErrorKinds = kinds.TryGetValue(info.Id, out int kk) ? kk : 0,
                ErrorCount = counts.TryGetValue(info.Id, out int cc) ? cc : 0,
                LastError = lastByMod.TryGetValue(info.Id, out var le) ? le : null,
            });
        }
    }

    private static void GatherErrors(SnapshotModel m)
    {
        foreach (var e in DebugLog.Book.Recent(60))
            m.Errors.Add(new SnapshotError { Mod = e.ModId, Title = e.Title, Count = e.Count, First = FrameWatch.Clock(e.FirstAt), Last = FrameWatch.Clock(e.LastAt) });
        m.ErrorsOverflow = DebugLog.Book.Overflow;
    }

    private static void GatherState(SnapshotModel m)
    {
        var s = Lifecycle.Sample();
        m.Session = s.Session;
        m.SessionText = s.SessionText;
        m.Map = s.Map;
        m.Car = StateTracker.CarText(s);
        m.GameState = s.GameState;
        foreach (var kv in s.Players) m.Players.Add("#" + kv.Key + " " + kv.Value);
    }

    private static void GatherMemory(SnapshotModel m)
    {
        m.GcHeapBytes = GC.GetTotalMemory(false);
        m.GcTotalAllocated = GC.GetTotalAllocatedBytes(false);
        m.Gen0 = GC.CollectionCount(0);
        m.Gen1 = GC.CollectionCount(1);
        m.Gen2 = GC.CollectionCount(2);
        m.UnityAllocated = UnityEngine.Profiling.Profiler.GetTotalAllocatedMemoryLong();
        m.UnityReserved = UnityEngine.Profiling.Profiler.GetTotalReservedMemoryLong();
        m.UnityMonoUsed = -1;
        try { m.UnityMonoUsed = UnityEngine.Profiling.Profiler.GetMonoUsedSizeLong(); } catch (Exception e) { DebugLog.Error("core", e, "Snapshot mono heap"); }
        m.WorkingSet = -1;
        try { m.WorkingSet = Process.GetCurrentProcess().WorkingSet64; } catch (Exception e) { DebugLog.Error("core", e, "Snapshot working set"); }
    }
}
