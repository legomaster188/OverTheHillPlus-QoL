using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using BepInEx;
using BepInEx.Unity.IL2CPP;
using UnityEngine;

namespace OverTheHillPlus.Logging;

/// <summary>
/// On core's first frame (every plugin has loaded): builds the mod map, writes the session header (game, Unity,
/// BepInEx, OS, GPU, each mod with its version and state, the other plugins) to the debug log and one line of it to
/// the BepInEx log, and writes a crash report for each trail of a session that did not close cleanly.
/// </summary>
internal static class SessionReport
{
    private static bool _done;
    private static string _header = "";

    /// <summary>The session header (empty before the first frame).</summary>
    public static string Header => _header;

    public static void FirstFrame()
    {
        if (_done) return;
        _done = true;
        try { ModMap.Build(); } catch { }
        try
        {
            _header = BuildHeader(out string summary);
            DebugLog.Write("HEAD", "core", "=== session header ===\n" + _header + "\n=== end of header ===");
            Core.CorePlugin.Logger?.LogInfo("[Debug] " + summary + $"; debug log: {DebugLog.FilePath ?? "off"} (level {DebugLog.Level}, " +
                                            "[Debug] Level in BepInEx/config/oth.core.cfg)");
        }
        catch (Exception e) { Core.CorePlugin.Logger?.LogWarning("[Debug] session header: " + e.Message); }
        try { ConfigLog.SnapshotAll(); } catch (Exception e) { DebugLog.Error("core", e, "SessionReport.FirstFrame config"); }
        StartupChecks.Run();
        try { CrashReports.WriteAll(); }
        catch (Exception e) { Core.CorePlugin.Logger?.LogWarning("[Debug] crash report: " + e.Message); }
    }

    /// <summary>"on", "off (switched off)", "off (blocked by the old plugin)" or "FAILED: ..." for a mod.</summary>
    internal static string StateOf(Core.Manager.ModInfo m, out bool on)
    {
        on = false;
        string failed = Core.Manager.ModManager.FailedReason(m.Id);
        if (failed != null) return "FAILED: " + failed;
        if (Core.Manager.ModManager.Blocked && m.Id != "core") return "off (blocked by the old plugin)";
        if (Core.Manager.ModManager.IsLoaded(m.Id)) { on = true; return "on"; }
        return "off (switched off)";
    }

    private static string Safe(Func<string> f)
    {
        try { return f() ?? "?"; } catch { return "?"; }
    }

    private static string BuildHeader(out string summary)
    {
        var sb = new StringBuilder(2048);
        string game = Safe(() => Application.productName) + " " + Safe(() => Application.version);
        string unity = Safe(() => Application.unityVersion);
        string bepinex = Safe(() => typeof(Paths).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                                    ?? typeof(Paths).Assembly.GetName().Version?.ToString());
        string gpu = Safe(() => $"{SystemInfo.graphicsDeviceName} ({SystemInfo.graphicsDeviceType}, {SystemInfo.graphicsMemorySize} MB, {SystemInfo.graphicsDeviceVersion})");
        sb.Append("started: ").Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")).Append(" local, ").Append(DateTime.UtcNow.ToString("HH:mm:ss")).Append(" UTC\n");
        sb.Append("game: ").Append(game).Append(", Unity ").Append(unity).Append('\n');
        sb.Append("BepInEx: ").Append(bepinex).Append(", .NET ").Append(Environment.Version).Append('\n');
        sb.Append("OS: ").Append(Safe(() => RuntimeInformation.OSDescription)).Append(" (").Append(Safe(() => SystemInfo.operatingSystem)).Append(")\n");
        sb.Append("CPU: ").Append(Safe(() => $"{SystemInfo.processorType}, {SystemInfo.processorCount} threads; RAM {SystemInfo.systemMemorySize} MB")).Append('\n');
        sb.Append("GPU: ").Append(gpu).Append('\n');
        sb.Append("screen: ").Append(Safe(() => $"{Screen.width}x{Screen.height}, {Screen.fullScreenMode}, quality {QualitySettings.GetQualityLevel()}")).Append('\n');
        sb.Append("process: ").Append(Safe(() => Process.GetCurrentProcess().ProcessName)).Append(", trail ")
          .Append(CrashTrail.FileInUse == null ? "off" : Path.GetFileName(CrashTrail.FileInUse)).Append(", debug level ").Append(DebugLog.Level)
          .Append(", patch guards ").Append(PatchGuard.Enabled ? "on" : "off").Append('\n');
        sb.Append("mods:\n");
        int on = 0, total = 0;
        foreach (var m in Core.Manager.ModManager.All)
        {
            if (m == null) continue;
            total++;
            string state = StateOf(m, out bool isOn);
            if (isOn) on++;
            int patchFails = PatchReport.Failed(m.Id);
            if (patchFails > 0) state += $", {patchFails} patch classes failed";
            sb.Append("  ").Append(m.Id).Append(' ').Append(m.Version ?? "?").Append(": ").Append(state).Append('\n');
        }
        sb.Append("other plugins:\n");
        int others = 0;
        try
        {
            foreach (var kv in IL2CPPChainloader.Instance.Plugins)
            {
                var meta = kv.Value?.Metadata;
                if (meta == null || meta.GUID.StartsWith("com.oth.", StringComparison.OrdinalIgnoreCase)) continue;
                others++;
                string version = Safe(() => meta.GetType().GetProperty("Version")?.GetValue(meta)?.ToString());
                sb.Append("  ").Append(meta.GUID).Append(" (").Append(meta.Name).Append(") ").Append(version).Append('\n');
            }
        }
        catch { }
        if (others == 0) sb.Append("  (none)\n");
        summary = $"session: {game}, Unity {unity}, BepInEx {bepinex}, GPU {Safe(() => SystemInfo.graphicsDeviceName)}; {on} of {total} mods on, {others} other plugins";
        return sb.ToString().TrimEnd('\n');
    }
}

/// <summary>
/// BepInEx/oth-reports/crash-&lt;time&gt;.txt for each session that did not close cleanly: how it most likely ended,
/// its header and errors / hangs / spikes from its debug log, its last 100 trail steps by frame, and (best effort, on a
/// worker thread with a timeout) the matching Windows Application events. One line in the BepInEx log points to it.
/// </summary>
internal static class CrashReports
{
    public static void WriteAll()
    {
        if (CrashTrail.PreviousTrails.Count == 0) return;
        string exe = "";
        try { exe = Process.GetCurrentProcess().ProcessName + ".exe"; } catch { }
        bool events = DebugConfig.Get(DebugConfig.WindowsEvents, true);
        int keep = Math.Max(2, DebugConfig.Get(DebugConfig.KeepFiles, 10) * 2);
        foreach (var prev in CrashTrail.PreviousTrails)
        {
            var parts = new CrashReportParts
            {
                TrailFile = Path.GetFileName(prev.Path),
                EndedUtc = prev.EndedUtc,
                Trail = prev.Rows,
                CurrentHeader = SessionReport.Header,
                ModOfStep = ModMap.ModOfStep,
            };
            // Reading the old debug log (up to a few MB) and the event log happen on a worker thread, off the frame.
            var t = new Thread(() => Finish(parts, exe, events, keep)) { IsBackground = true, Name = "OTH crash report" };
            t.Start();
        }
    }

    /// <summary>The crashed session's debug log: the newest earlier one that wrote to the same trail file.</summary>
    private static void ReadDebugLog(CrashReportParts parts)
    {
        try
        {
            string dir = Path.Combine(Paths.BepInExRootPath, "oth-debug");
            if (!Directory.Exists(dir)) { parts.NotesMissing = "(no debug log: [Debug] Level was Off)"; return; }
            var files = new List<string>(Directory.GetFiles(dir, "*" + LogFiles.DebugExt));
            files.Sort((a, b) => string.CompareOrdinal(Path.GetFileName(b), Path.GetFileName(a)));
            foreach (var f in files)
            {
                if (DebugLog.FilePath != null && string.Equals(Path.GetFullPath(f), Path.GetFullPath(DebugLog.FilePath), StringComparison.OrdinalIgnoreCase)) continue;
                string first;
                using (var r = new StreamReader(new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))) first = r.ReadLine() ?? "";
                if (!first.Contains("trail " + parts.TrailFile + ",")) continue;
                if (File.GetLastWriteTimeUtc(f) < parts.EndedUtc.AddDays(-2)) break; // far older than the crash: not that session's
                ReportText.Extract(ReadTail(f, 4 << 20), 150, out var header, out var notes);
                parts.Header = header;
                parts.Notes = notes;
                parts.NotesMissing = $"(none in {Path.GetFileName(f)})";
                return;
            }
            parts.NotesMissing = "(no debug log of that session was found: [Debug] Level was Off, or it was deleted)";
        }
        catch (Exception e) { parts.NotesMissing = "(its debug log could not be read: " + e.Message + ")"; }
    }

    /// <summary>The file's lines; for a big file the first 200 lines (the header) and the last <paramref name="tailBytes"/>.</summary>
    private static List<string> ReadTail(string path, long tailBytes)
    {
        var lines = new List<string>();
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var r = new StreamReader(fs);
        if (fs.Length <= tailBytes)
        {
            string l;
            while ((l = r.ReadLine()) != null) lines.Add(l);
            return lines;
        }
        for (int i = 0; i < 200; i++)
        {
            string l = r.ReadLine();
            if (l == null) return lines;
            lines.Add(l);
        }
        fs.Position = fs.Length - tailBytes;
        r.DiscardBufferedData();
        r.ReadLine(); // a partial line
        string line;
        while ((line = r.ReadLine()) != null) lines.Add(line);
        return lines;
    }

    private static void Finish(CrashReportParts parts, string exe, bool events, int keep)
    {
        try
        {
            ReadDebugLog(parts);
            parts.WindowsEvents = events ? WindowsEvents(parts.EndedUtc, exe) : "(not read: [Debug] WindowsEvents = false)";
            string dir = Path.Combine(Paths.BepInExRootPath, "oth-reports");
            Directory.CreateDirectory(dir);
            var names = new List<string>();
            foreach (var f in Directory.GetFiles(dir, LogFiles.CrashPrefix + "*" + LogFiles.CrashExt)) names.Add(Path.GetFileName(f));
            foreach (var old in LogFiles.ToDelete(names, keep - 1, LogFiles.CrashPrefix, LogFiles.CrashExt))
            {
                try { File.Delete(Path.Combine(dir, old)); } catch { }
            }
            var when = parts.EndedUtc == default ? DateTime.Now : parts.EndedUtc.ToLocalTime();
            string path = null;
            for (int copy = 1; copy <= 9; copy++)
            {
                path = Path.Combine(dir, LogFiles.CrashName(when, copy));
                if (!File.Exists(path)) break;
            }
            File.WriteAllText(path, ReportText.CrashReport(parts).Replace("\n", Environment.NewLine), new UTF8Encoding(false));
            string verdict = ReportText.Verdict(parts.Notes, parts.Trail);
            DebugLog.Write("CRASH", "-", $"the session of {parts.TrailFile} did not close cleanly: {verdict}. Report: {path}");
            string p = path;
            Core.Install.MainThread.Post(() => Core.CorePlugin.Logger?.LogWarning($"[CrashTrail] crash report of the last session: {p}"));
        }
        catch (Exception e)
        {
            string msg = e.Message;
            Core.Install.MainThread.Post(() => Core.CorePlugin.Logger?.LogWarning("[CrashTrail] the crash report could not be written: " + msg));
        }
    }

    /// <summary>
    /// Windows' Application Error / Application Hang / Windows Error Reporting events from 3 minutes before to 15 minutes
    /// after the trail ended that mention this game's exe, read with wevtutil (8 s timeout). Never throws.
    /// </summary>
    private static string WindowsEvents(DateTime endedUtc, string exe)
    {
        try
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return "(not Windows)";
            string tool = Path.Combine(Environment.SystemDirectory, "wevtutil.exe");
            if (!File.Exists(tool)) return "(wevtutil.exe is not available)";
            if (endedUtc == default) endedUtc = DateTime.UtcNow;
            var from = endedUtc.AddMinutes(-3);
            var to = endedUtc.AddMinutes(15);
            if (to > DateTime.UtcNow) to = DateTime.UtcNow;
            var psi = new ProcessStartInfo(tool)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("qe");
            psi.ArgumentList.Add("Application");
            psi.ArgumentList.Add("/q:" + ReportText.EventLogQuery(from, to));
            psi.ArgumentList.Add("/f:text");
            psi.ArgumentList.Add("/c:30");
            psi.ArgumentList.Add("/rd:true");
            using var p = Process.Start(psi);
            if (p == null) return "(wevtutil did not start)";
            var output = p.StandardOutput.ReadToEndAsync();
            var error = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(8000))
            {
                try { p.Kill(); } catch { }
                return "(wevtutil took too long)";
            }
            if (!output.Wait(2000)) return "(wevtutil's output could not be read)";
            string text = ReportText.FilterEvents(output.Result, exe);
            if (text.Length > 0) return text;
            string err = error.Wait(500) ? error.Result?.Trim() : null;
            return p.ExitCode != 0 && !string.IsNullOrEmpty(err) ? "(wevtutil: " + err + ")" : $"(no crash or hang event of {exe} in that time)";
        }
        catch (Exception e) { return "(the event log could not be read: " + e.Message + ")"; }
    }
}
