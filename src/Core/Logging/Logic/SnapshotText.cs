using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace OverTheHillPlus.Logging;

/// <summary>One mod's line in the snapshot.</summary>
internal sealed class SnapshotMod
{
    public string Id, Version;
    /// <summary>"on", "off (switched off)", "FAILED: ...", "off (blocked by the old plugin)".</summary>
    public string State;
    public int PatchFailed;
    public int ErrorKinds, ErrorCount;
    /// <summary>The newest distinct error of the mod (one line), or null.</summary>
    public string LastError;
}

/// <summary>One distinct error from the error book.</summary>
internal sealed class SnapshotError
{
    public string Mod, Title;
    public int Count;
    public string First, Last;
}

/// <summary>ModNet's state for the snapshot.</summary>
internal sealed class SnapshotModNet
{
    public bool Enabled, Installed, Faulted;
    public string LastError, Describe, Role;
    public int Sent, Received, Peers;
    public List<string> Drops = new();
    public List<string> PeerLines = new();
}

/// <summary>Everything the snapshot file says, gathered by Snapshot (game side) and written by <see cref="SnapshotText.Build"/>.</summary>
internal sealed class SnapshotModel
{
    public DateTime Time;
    public string Header;
    public string Reason;
    public List<SnapshotMod> Mods = new();
    public string HookTable;
    public List<SnapshotError> Errors = new();
    public int ErrorsOverflow;
    public string Scene, Session, SessionText, Map, Car, GameState;
    public int Frame;
    public float Uptime;
    public List<string> Players = new();
    public SnapshotModNet ModNet;
    public long GcHeapBytes, GcTotalAllocated, UnityAllocated, UnityReserved, UnityMonoUsed, WorkingSet;
    public int Gen0, Gen1, Gen2;
    public FpsStats Fps;
    public double FpsWindow = 5;
    public List<TrailRow> Trail = new();
    public Func<string, string> ModOfStep;
    public string DebugLevel, DebugFile;
}

/// <summary>The text of BepInEx/oth-reports/snapshot-&lt;time&gt;.txt (pure).</summary>
internal static class SnapshotText
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public const string FilePrefix = "snapshot-";
    public const string FileExt = ".txt";

    /// <summary>"snapshot-20261017_101500.txt"; a second one in the same second gets "_2".</summary>
    public static string FileName(DateTime when, int copy = 1) =>
        FilePrefix + when.ToString(LogFiles.Stamp, Inv) + (copy > 1 ? "_" + copy.ToString(Inv) : "") + FileExt;

    public static string Mb(long bytes) => bytes < 0 ? "?" : (bytes / 1048576.0).ToString("0.0", Inv) + " MB";

    private static void Title(StringBuilder sb, string title)
    {
        sb.Append('\n').Append("== ").Append(title).Append(" ==\n");
    }

    public static string Build(SnapshotModel m, int maxErrors = 40, int maxTrail = 200)
    {
        var sb = new StringBuilder(8192);
        sb.Append("OTH diagnostics snapshot, ").Append(m.Time.ToString("yyyy-MM-dd HH:mm:ss", Inv)).Append('\n');
        if (!string.IsNullOrEmpty(m.Reason)) sb.Append("asked for: ").Append(m.Reason).Append('\n');
        sb.Append("debug log: level ").Append(m.DebugLevel ?? "?").Append(", ").Append(m.DebugFile ?? "no file").Append('\n');

        Title(sb, "session header");
        sb.Append(string.IsNullOrEmpty(m.Header) ? "(not written yet: the game has not finished its first frame)" : m.Header).Append('\n');

        Title(sb, "game now");
        sb.Append("scene: ").Append(m.Scene ?? "?").Append(", frame ").Append(m.Frame).Append(", running ")
          .Append(m.Uptime.ToString("0.0", Inv)).Append(" s\n");
        sb.Append("session: ").Append(m.Session ?? "?");
        if (!string.IsNullOrEmpty(m.SessionText)) sb.Append(" (").Append(m.SessionText).Append(')');
        sb.Append('\n');
        sb.Append("map: ").Append(string.IsNullOrEmpty(m.Map) ? "?" : m.Map).Append('\n');
        sb.Append("car: ").Append(string.IsNullOrEmpty(m.Car) ? "none" : m.Car).Append('\n');
        if (!string.IsNullOrEmpty(m.GameState)) sb.Append("game state: ").Append(m.GameState).Append('\n');
        sb.Append("players: ").Append(m.Players.Count).Append('\n');
        foreach (var p in m.Players) sb.Append("  ").Append(p).Append('\n');

        Title(sb, "mods");
        int on = 0;
        foreach (var mod in m.Mods)
        {
            if (mod.State != null && mod.State.StartsWith("on", StringComparison.Ordinal)) on++;
            sb.Append("  ").Append(mod.Id).Append(' ').Append(mod.Version ?? "?").Append(": ").Append(mod.State ?? "?");
            if (mod.PatchFailed > 0) sb.Append(", ").Append(mod.PatchFailed).Append(" patch classes failed");
            if (mod.ErrorKinds > 0)
                sb.Append("; ").Append(mod.ErrorKinds).Append(mod.ErrorKinds == 1 ? " error" : " distinct errors").Append(" (x").Append(mod.ErrorCount).Append(')');
            sb.Append('\n');
            if (!string.IsNullOrEmpty(mod.LastError)) sb.Append("      last error: ").Append(mod.LastError).Append('\n');
        }
        sb.Append("  ").Append(on).Append(" of ").Append(m.Mods.Count).Append(" mods on\n");

        Title(sb, "hook timing (session totals)");
        sb.Append(string.IsNullOrEmpty(m.HookTable) ? "(no timing yet: [Debug] Level was Off)" : m.HookTable).Append('\n');

        Title(sb, "recent errors");
        if (m.Errors.Count == 0) sb.Append("(none this session)\n");
        for (int i = 0; i < m.Errors.Count && i < maxErrors; i++)
        {
            var e = m.Errors[i];
            sb.Append("  [").Append(e.Mod).Append("] ").Append(e.Title).Append(" x").Append(e.Count);
            if (!string.IsNullOrEmpty(e.First)) sb.Append(", first ").Append(e.First);
            if (!string.IsNullOrEmpty(e.Last) && e.Last != e.First) sb.Append(", last ").Append(e.Last);
            sb.Append('\n');
        }
        if (m.Errors.Count > maxErrors) sb.Append("  ... and ").Append(m.Errors.Count - maxErrors).Append(" more\n");
        if (m.ErrorsOverflow > 0) sb.Append("  (").Append(m.ErrorsOverflow).Append(" new errors were not kept: the book was full)\n");

        Title(sb, "mod network");
        var n = m.ModNet;
        if (n == null) sb.Append("(not available)\n");
        else
        {
            sb.Append("enabled: ").Append(n.Enabled ? "yes" : "no").Append(", hook installed: ").Append(n.Installed ? "yes" : "no")
              .Append(", faulted: ").Append(n.Faulted ? "YES" : "no").Append('\n');
            sb.Append("status: ").Append(n.Describe ?? "?").Append('\n');
            if (!string.IsNullOrEmpty(n.Role)) sb.Append("role: ").Append(n.Role).Append('\n');
            sb.Append("last error: ").Append(string.IsNullOrEmpty(n.LastError) ? "none" : n.LastError).Append('\n');
            sb.Append("messages: ").Append(n.Sent).Append(" sent, ").Append(n.Received).Append(" received, ").Append(n.Peers).Append(" modded peers\n");
            sb.Append("dropped: ").Append(n.Drops.Count == 0 ? "none" : string.Join(", ", n.Drops)).Append('\n');
            foreach (var l in n.PeerLines) sb.Append("  ").Append(l).Append('\n');
        }

        Title(sb, "memory");
        sb.Append("GC heap ").Append(Mb(m.GcHeapBytes)).Append(", allocated since start ").Append(Mb(m.GcTotalAllocated))
          .Append(", collections gen0/1/2: ").Append(m.Gen0).Append('/').Append(m.Gen1).Append('/').Append(m.Gen2).Append('\n');
        sb.Append("Unity allocated ").Append(Mb(m.UnityAllocated)).Append(" of ").Append(Mb(m.UnityReserved)).Append(" reserved");
        if (m.UnityMonoUsed >= 0) sb.Append(", managed heap ").Append(Mb(m.UnityMonoUsed));
        sb.Append('\n');
        if (m.WorkingSet >= 0) sb.Append("process working set ").Append(Mb(m.WorkingSet)).Append('\n');

        Title(sb, "frame rate (last " + m.FpsWindow.ToString("0", Inv) + " s)");
        sb.Append(m.Fps.ToString()).Append('\n');

        int rows = Math.Min(maxTrail, m.Trail.Count);
        Title(sb, "last " + rows + " trail steps (oldest first)");
        if (rows == 0) sb.Append("(no steps)\n");
        for (int i = m.Trail.Count - rows; i < m.Trail.Count; i++)
        {
            var r = m.Trail[i];
            sb.Append("  #").Append(r.Seq).Append(" f").Append(r.Frame).Append(' ').Append(r.Time.ToString("0.00", Inv)).Append("s  ").Append(r.Text);
            string mod = m.ModOfStep?.Invoke(r.Text);
            if (mod != null && !r.Text.EndsWith("(" + mod + ")", StringComparison.Ordinal)) sb.Append("  [").Append(mod).Append(']');
            sb.Append('\n');
        }
        return sb.ToString().TrimEnd('\n');
    }
}
