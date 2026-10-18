using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using BepInEx;
using BepInEx.Configuration;
using OverTheHillPlus.Logging;

namespace OverTheHillPlus;

/// <summary>
/// The mods' debug log: BepInEx/oth-debug/&lt;yyyyMMdd_HHmmss&gt;.log (the last 10 are kept), one line per entry with
/// the time, frame, thread and mod id. How much goes in is [Debug] Level in BepInEx/config/oth.core.cfg:
/// Off, Errors (the default: hangs, frame spikes, slow hooks, each distinct error once with repeat counts, crash
/// reports), Info (plus <see cref="Info"/>), Verbose (plus <see cref="Verbose(string, Func{string})"/> and per-hook
/// timing tables every 60 s). Every call is cheap when its level is off; any thread may call. Never throws.
/// </summary>
public static class DebugLog
{
    private static volatile int _level;
    private static readonly object Lock = new();
    private static StreamWriter _writer;
    private static bool _fileFailed;
    private static int _mainThread;

    internal static readonly ErrorBook Book = new(600);
    internal static readonly ErrorSites Sites = new();

    // The last exception DebugLog.Error saw on this thread: the log listener skips the same exception when its
    // caller also writes it to the BepInEx log.
    [ThreadStatic] private static string _lastType;
    [ThreadStatic] private static string _lastMessage;

    /// <summary>The current level.</summary>
    public static DebugLevel Level => (DebugLevel)_level;

    /// <summary>True unless the level is Off.</summary>
    public static bool On => _level > 0;

    /// <summary>True at Info and Verbose.</summary>
    public static bool IsInfo => _level >= (int)DebugLevel.Info;

    /// <summary>True at Verbose: check it before building an expensive text.</summary>
    public static bool IsVerbose => _level >= (int)DebugLevel.Verbose;

    /// <summary>This session's debug log file (null while the level is Off or the file could not be made).</summary>
    public static string FilePath { get; private set; }

    /// <summary>Seconds on a monotonic clock (any thread).</summary>
    internal static double Now => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

    internal static ConfigEntry<int> KeepFiles;

    /// <summary>Called once by core's Load, after its config is bound.</summary>
    internal static void Start(ConfigEntry<DebugLevel> level, ConfigEntry<int> keepFiles)
    {
        _mainThread = Environment.CurrentManagedThreadId;
        KeepFiles = keepFiles;
        if (level == null) return;
        Apply(level.Value);
        _levelEntry = level;
        level.SettingChanged += LevelChanged;
    }

    private static ConfigEntry<DebugLevel> _levelEntry;

    private static void LevelChanged(object sender, EventArgs e)
    {
        var before = Level;
        Apply(_levelEntry.Value);
        if (Level != before) Info("core", $"debug level {before} -> {Level}");
        // Going up to Info: read every setting once, so the next change of each shows its real old value.
        if (before < DebugLevel.Info && Level >= DebugLevel.Info) ConfigLog.SnapshotAll();
    }

    private static void Apply(DebugLevel level)
    {
        if (level < DebugLevel.Off || level > DebugLevel.Verbose) level = DebugLevel.Errors;
        _level = (int)level;
        if (level != DebugLevel.Off) EnsureFile();
    }

    /// <summary>A line from the mod at Info and Verbose.</summary>
    public static void Info(string modId, string text)
    {
        if (_level >= (int)DebugLevel.Info) Write("INFO", modId, text);
    }

    /// <summary>
    /// A line at Verbose only; <paramref name="text"/> runs only then. A lambda that captures variables still allocates
    /// at the call site: in per-frame code check <see cref="IsVerbose"/> first.
    /// </summary>
    public static void Verbose(string modId, Func<string> text)
    {
        if (_level < (int)DebugLevel.Verbose || text == null) return;
        string s;
        try { s = text(); }
        catch (Exception e) { s = "(the text failed: " + e.GetType().Name + ": " + e.Message + ")"; }
        Write("VERB", modId, s);
    }

    /// <summary>A fixed line at Verbose only.</summary>
    public static void Verbose(string modId, string text)
    {
        if (_level >= (int)DebugLevel.Verbose) Write("VERB", modId, text);
    }

    /// <summary>
    /// An exception the mod caught (any level but Off). Each distinct one (mod, <paramref name="context"/>, type, top
    /// frame) is written once in full with the session state and the last trail steps; repeats are counted and listed
    /// in the summary every 60 s. Safe to call every frame.
    /// </summary>
    public static void Error(string modId, Exception exception, string context = null)
    {
        if (_level == 0 || exception == null) return;
        try
        {
            var clr = exception.GetType();
            // A place that keeps failing (a catch block in a per-frame hook) is only counted after its first calls.
            if (Sites.TryRepeat(Book, modId, context, clr, Now, out var site)) return;
            string type = clr.FullName;
            _lastType = type;
            _lastMessage = exception.Message;
            string trace = exception.StackTrace ?? "";
            string top = StackText.TopFrame(trace);
            string sig = ErrorBook.Signature(modId, context, type, top);
            var entry = Book.Note(sig, Now, out bool first, modId);
            Sites.Remember(site, entry);
            if (entry == null || !first) return;
            entry.Title = $"{type} in {context ?? "?"}: {StackText.Normalize(exception.Message, 120)}";
            Write("ERROR", modId, $"{context ?? "error"}: {exception}\n{Where()}");
        }
        catch { /* the debug log must never break a mod */ }
    }

    /// <summary>An error without an exception (any level but Off), deduplicated by its text like <see cref="Error(string, Exception, string)"/>.</summary>
    public static void Error(string modId, string text)
    {
        if (_level == 0 || string.IsNullOrEmpty(text)) return;
        try
        {
            _lastType = text;
            _lastMessage = null;
            string norm = StackText.Normalize(text, 160);
            var entry = Book.Note(ErrorBook.Signature(modId, "text", norm, ""), Now, out bool first, modId, norm);
            if (entry == null || !first) return;
            Write("ERROR", modId, text + "\n" + Where());
        }
        catch { }
    }

    /// <summary>Where the game is: scene, frame, session and the last trail steps (for error entries).</summary>
    internal static string Where() =>
        $"scene {FrameWatch.Scene}, frame {FrameWatch.Frame}, session {FrameWatch.Session}; running hook: {CrashTrail.RunningMod ?? "none"}; " +
        $"last steps: {CrashTrail.RecentLine(5)}";

    /// <summary>True (once) when the log listener's text is the exception <see cref="Error(string, Exception, string)"/>
    /// just wrote on this thread.</summary>
    internal static bool TakeSameAsLast(string text)
    {
        if (_lastType == null || text == null) return false;
        bool same = text.Contains(_lastType) && (string.IsNullOrEmpty(_lastMessage) || text.Contains(_lastMessage));
        if (same) _lastType = _lastMessage = null;
        return same;
    }

    /// <summary>Writes one entry at any level but Off (core's own lines: HANG, SPIKE, SLOW, CRASH, ...).</summary>
    internal static void Write(string level, string modId, string text)
    {
        if (_level == 0) return;
        try
        {
            int tid = Environment.CurrentManagedThreadId;
            string thread = tid == _mainThread ? "main" : "T" + tid;
            string line = LogFiles.Line(DateTime.Now, FrameWatch.Frame, thread, level, modId, text);
            lock (Lock)
            {
                if (_writer == null) return;
                _writer.WriteLine(line);
            }
        }
        catch { /* a full disk must not break the game */ }
    }

    private static void EnsureFile()
    {
        lock (Lock)
        {
            if (_writer != null || _fileFailed) return;
            try
            {
                string dir = Path.Combine(Paths.BepInExRootPath, "oth-debug");
                Directory.CreateDirectory(dir);
                int keep = Math.Max(1, KeepFiles?.Value ?? 10);
                var names = new System.Collections.Generic.List<string>();
                foreach (var f in Directory.GetFiles(dir, "*" + LogFiles.DebugExt)) names.Add(Path.GetFileName(f));
                foreach (var old in LogFiles.ToDelete(names, keep - 1, "", LogFiles.DebugExt))
                {
                    try { File.Delete(Path.Combine(dir, old)); } catch { /* in use by another game copy */ }
                }
                var start = DateTime.Now;
                for (int copy = 1; copy <= 9 && _writer == null; copy++)
                {
                    string path = Path.Combine(dir, LogFiles.DebugName(start, copy));
                    try
                    {
                        var fs = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
                        // Flushed after every line: a crash or a kill loses nothing written before it.
                        _writer = new StreamWriter(fs, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
                        FilePath = path;
                    }
                    catch (IOException) { /* another game copy started in the same second */ }
                }
                if (_writer == null) throw new IOException("no free file name");
                string trail = CrashTrail.FileInUse == null ? "off" : Path.GetFileName(CrashTrail.FileInUse);
                _writer.WriteLine($"OTH debug log, started {start:yyyy-MM-dd HH:mm:ss}, trail {trail}, level {Level}");
            }
            catch (Exception e)
            {
                _fileFailed = true;
                Core.CorePlugin.Logger?.LogWarning("[Debug] no debug log this session: " + e.Message);
            }
        }
    }
}
