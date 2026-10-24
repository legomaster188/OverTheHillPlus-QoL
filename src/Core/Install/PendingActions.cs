using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace OverTheHillPlus.Core.Install;

internal enum PendingKind
{
    /// <summary>Put the staged download (.pending/&lt;file&gt;.new) in place of the file.</summary>
    Replace,
    /// <summary>Remove the file.</summary>
    Delete,
    /// <summary>Another creator's plugin: rename &lt;file&gt; to &lt;file&gt;.disabled (BepInEx only loads *.dll).</summary>
    Disable,
    /// <summary>Another creator's plugin: rename &lt;file&gt;.disabled back to &lt;file&gt;.</summary>
    Enable,
}

/// <summary>A change to a mod DLL that waits for the next start (a loaded DLL can't be replaced or deleted on Windows).</summary>
internal sealed class PendingAction
{
    public PendingKind Kind;
    /// <summary>Plain file name in the plugin folder, e.g. "OTH.Chat.dll". For <see cref="IsRename"/> actions: a path
    /// relative to BepInEx/plugins with '/' between folders, e.g. "SomeMod/SomeMod.dll" (always the .dll name).</summary>
    public string File;

    /// <summary>A rename of another creator's plugin (<see cref="PendingKind.Disable"/> / <see cref="PendingKind.Enable"/>),
    /// as opposed to the installer's actions on our own mods' files.</summary>
    public bool IsRename => IsRenameKind(Kind);

    public PendingAction(PendingKind kind, string file)
    {
        Kind = kind;
        File = IsRenameKind(kind) ? PendingActions.NormalizeRelative(file) : file;
    }

    internal static bool IsRenameKind(PendingKind kind) => kind == PendingKind.Disable || kind == PendingKind.Enable;
}

/// <summary>What <see cref="PendingActions.ApplyAll"/> did at startup, for the log and the AVAILABLE tab.</summary>
internal sealed class ApplyReport
{
    /// <summary>Files replaced or deleted.</summary>
    public readonly List<string> Applied = new();
    /// <summary>Files that were in use and replaced by moving the running copy aside: the new file runs from the
    /// next start (how core updates itself: "restart the game twice").</summary>
    public readonly List<string> Swapped = new();
    /// <summary>Files still locked: their action stays queued for the next start.</summary>
    public readonly List<string> StillQueued = new();
    /// <summary>Mod updates kept queued because core's own update was applied (or tried) this start: the core running
    /// now is still the old one, so they are applied at the next start, by the new core.</summary>
    public readonly List<string> Held = new();
    /// <summary>Actions that were dropped (unsafe name, staged download missing, disk error), as messages.</summary>
    public readonly List<string> Errors = new();
    /// <summary>Where a damaged actions.json was moved (actions.json.bad), or null.</summary>
    public string Quarantined;
}

/// <summary>
/// The queue of changes to apply at the next start, in OverTheHillMods/.pending/actions.json. Core runs
/// <see cref="ApplyAll"/> first in its Load, before BepInEx loads the other mods (they depend on core, so they load
/// after it), which is when their DLLs are free. Staged downloads sit next to it as &lt;file&gt;.new: never ".dll",
/// because BepInEx loads every .dll anywhere under the plugins folder.
/// Renames of other creators' plugins (<see cref="PendingKind.Disable"/> / <see cref="PendingKind.Enable"/>) share the
/// queue; their paths are relative to BepInEx/plugins, the plugin folder's parent.
/// Pure logic (no BepInEx or Unity types) so it can be unit tested.
/// </summary>
internal static class PendingActions
{
    public const string PendingFolder = ".pending";
    /// <summary>The Mod Manager's own folder in BepInEx/plugins: renames never touch it.</summary>
    public const string OurFolder = "OverTheHillMods";
    /// <summary>What a switched-off plugin of another creator is renamed to: &lt;file&gt;.dll.disabled.</summary>
    public const string DisabledSuffix = ".disabled";
    private const string QueueName = "actions.json";
    private const string StagedSuffix = ".new";
    private const string OldSuffix = ".old";

    private static readonly object Gate = new();

    /// <summary>The plugin folder used by <see cref="Add(PendingAction)"/>; set by <see cref="ApplyAll"/>.</summary>
    public static string PluginDir;

    /// <summary>What the last <see cref="ApplyAll"/> did (null before it ran).</summary>
    public static ApplyReport LastReport;

    public static string PendingDir(string pluginDir) => Path.Combine(pluginDir, PendingFolder);
    public static string QueuePath(string pluginDir) => Path.Combine(PendingDir(pluginDir), QueueName);
    public static string StagedPath(string pluginDir, string file) => Path.Combine(PendingDir(pluginDir), file + StagedSuffix);
    private static string OldPath(string pluginDir, string file) => Path.Combine(PendingDir(pluginDir), file + OldSuffix);

    /// <summary>Queues an action in <see cref="PluginDir"/>.</summary>
    public static void Add(PendingAction a)
    {
        if (string.IsNullOrEmpty(PluginDir)) throw new InvalidOperationException("PendingActions.PluginDir isn't set.");
        Add(PluginDir, a);
    }

    /// <summary>
    /// Queues an action; a queued action for the same file is replaced by this one. Installer actions and renames
    /// are kept apart: a rename never replaces an update or uninstall of the same name, nor the other way round.
    /// </summary>
    public static void Add(string pluginDir, PendingAction a)
    {
        if (a == null) throw new ArgumentException("No action");
        if (a.IsRename ? !IsSafeRelativeDll(a.File) : !Manifest.IsSafeFileName(a.File))
            throw new ArgumentException((a.IsRename ? "Not a .dll inside the plugins folder: " : "Not a plain .dll file name: ") + a.File);
        lock (Gate)
        {
            var all = Read(pluginDir);
            all.RemoveAll(x => x.IsRename == a.IsRename && SameFile(x.File, a.File));
            all.Add(new PendingAction(a.Kind, a.File));
            Write(pluginDir, all);
        }
    }

    /// <summary>Drops the queued installer action for this file, if any (e.g. the file was just put in place directly).</summary>
    public static void Remove(string pluginDir, string file) => RemoveWhere(pluginDir, x => !x.IsRename && SameFile(x.File, file));

    /// <summary>Drops the queued rename of another creator's plugin (path relative to BepInEx/plugins), if any.</summary>
    public static void RemoveRename(string pluginDir, string relFile)
    {
        string rel = NormalizeRelative(relFile);
        RemoveWhere(pluginDir, x => x.IsRename && SameFile(x.File, rel));
    }

    private static void RemoveWhere(string pluginDir, Predicate<PendingAction> match)
    {
        lock (Gate)
        {
            var all = Read(pluginDir);
            if (all.RemoveAll(match) > 0) Write(pluginDir, all);
        }
    }

    /// <summary>The queued actions (empty when there are none or the file is unreadable).</summary>
    public static List<PendingAction> Load(string pluginDir)
    {
        lock (Gate)
        {
            try { return Read(pluginDir); }
            catch (Exception ex) { DebugLog.Error("core", ex, "PendingActions.Load"); return new List<PendingAction>(); }
        }
    }

    /// <summary>The queued installer action (update or uninstall) for this file, or null.</summary>
    public static PendingAction Find(string pluginDir, string file)
    {
        foreach (var a in Load(pluginDir))
            if (!a.IsRename && SameFile(a.File, file)) return a;
        return null;
    }

    /// <summary>The queued rename of another creator's plugin (path relative to BepInEx/plugins), or null.</summary>
    public static PendingAction FindRename(string pluginDir, string relFile)
    {
        string rel = NormalizeRelative(relFile);
        foreach (var a in Load(pluginDir))
            if (a.IsRename && SameFile(a.File, rel)) return a;
        return null;
    }

    /// <summary>BepInEx/plugins, where renames apply: the plugin folder's parent.</summary>
    public static string PluginsRoot(string pluginDir) =>
        Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(pluginDir)));

    /// <summary>'/' between folders, no surrounding spaces.</summary>
    internal static string NormalizeRelative(string rel) => rel?.Trim().Replace('\\', '/');

    /// <summary>
    /// A .dll inside BepInEx/plugins, given relative to it: plain folder and file names separated by '/' or '\', no
    /// "..", no drive or leading slash, not inside the queue's own .pending folder. Never one of ours: nothing in
    /// OverTheHillMods/ and no OTH.*.dll is ever renamed (the installer manages those files).
    /// </summary>
    internal static bool IsSafeRelativeDll(string rel)
    {
        if (rel == null || rel.Trim() != rel) return false; // surrounding spaces: refused, not silently trimmed
        rel = NormalizeRelative(rel);
        if (string.IsNullOrEmpty(rel) || rel.Length > 240) return false;
        if (!rel.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) return false;
        if (rel.StartsWith("/") || rel.IndexOf(':') >= 0 || Path.IsPathRooted(rel)) return false;
        var parts = rel.Split('/');
        foreach (var part in parts)
        {
            if (part.Length == 0 || part == "." || part == ".." || part.Trim() != part) return false;
            // Windows drops a trailing '.' or ' ' from a name, so "Folder." would be another way to write "Folder".
            if (part.EndsWith(".") || part.EndsWith(" ")) return false;
            if (part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || part.IndexOfAny(Unsafe) >= 0) return false;
            if (string.Equals(part, PendingFolder, StringComparison.OrdinalIgnoreCase)) return false;
        }
        if (parts.Length > 1 && string.Equals(parts[0], OurFolder, StringComparison.OrdinalIgnoreCase)) return false;
        if (parts[parts.Length - 1].StartsWith("OTH.", StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }

    private static readonly char[] Unsafe = { '<', '>', '"', '|', '?', '*' };

    /// <summary>The full path of a relative plugin file, or null when it would land outside <paramref name="pluginsRoot"/>.</summary>
    internal static string ResolveInside(string pluginsRoot, string relFile)
    {
        if (!IsSafeRelativeDll(relFile) || string.IsNullOrEmpty(pluginsRoot)) return null;
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(pluginsRoot)) + Path.DirectorySeparatorChar;
        string full = Path.GetFullPath(Path.Combine(root, NormalizeRelative(relFile).Replace('/', Path.DirectorySeparatorChar)));
        return full.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? full : null;
    }

    /// <summary>True when <paramref name="path"/> is inside <paramref name="folder"/> (at any depth).</summary>
    private static bool IsInFolder(string path, string folder)
    {
        if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(folder)) return false;
        try
        {
            string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)) + Path.DirectorySeparatorChar;
            return Path.GetFullPath(path).StartsWith(root, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) { DebugLog.Error("core", ex, "PendingActions.IsInFolder"); return true; } // can't tell: treat it as ours (never renamed)
    }

    /// <summary>
    /// Renames a plugin that isn't loaded this session right away, instead of at the next start: Disable renames
    /// &lt;file&gt; to &lt;file&gt;.disabled, Enable the other way. True when it is done (or there was nothing to do).
    /// </summary>
    public static bool TryRenameNow(string pluginsRoot, PendingKind kind, string relFile)
    {
        if (!PendingAction.IsRenameKind(kind)) return false;
        try { return ApplyRename(pluginsRoot, kind, relFile) == Outcome.Done; }
        catch (Exception ex) { DebugLog.Error("core", ex, "PendingActions.TryRenameNow"); return false; }
    }

    /// <summary>
    /// Runs every queued action. A file that is still locked keeps its action queued; a damaged actions.json is
    /// renamed actions.json.bad and nothing is applied. Core's own action runs first. When it is an update, the other
    /// mods' updates stay queued (<see cref="ApplyReport.Held"/>): the core applying the queue is the old one, which
    /// keeps running this session whatever happens to its file, and the mods' new versions may need the new core.
    /// They are applied at the next start, by the new core ("restart the game twice"). Deletes are never held, and
    /// neither are renames of other creators' plugins, which apply in <paramref name="pluginsRoot"/> (default: the
    /// plugin folder's parent, BepInEx/plugins).
    /// Never throws.
    /// </summary>
    public static ApplyReport ApplyAll(string pluginDir, string pluginsRoot = null)
    {
        PluginDir = pluginDir;
        var report = new ApplyReport();
        lock (Gate)
        {
            CleanOldCopies(pluginDir);
            string queue = QueuePath(pluginDir);
            List<PendingAction> all;
            try
            {
                if (!File.Exists(queue)) return LastReport = report;
                all = Read(pluginDir);
            }
            catch (Exception e)
            {
                DebugLog.Error("core", e, "PendingActions.ApplyAll");
                report.Quarantined = Quarantine(queue);
                report.Errors.Add("The list of pending changes was damaged and was set aside: " + e.Message);
                return LastReport = report;
            }

            var left = new List<PendingAction>();
            var ordered = new List<PendingAction>();
            foreach (var a in all) if (IsCoreFile(a.File)) ordered.Add(a);
            foreach (var a in all) if (!IsCoreFile(a.File)) ordered.Add(a);
            bool coreUpdate = false;
            foreach (var a in ordered)
            {
                if (a.IsRename)
                {
                    // Another creator's plugin: never held for a core update (its file doesn't depend on core).
                    try
                    {
                        pluginsRoot ??= PluginsRoot(pluginDir);
                        // A hand-edited queue may name anything: never rename a file in the plugin folder itself.
                        if (IsInFolder(ResolveInside(pluginsRoot, a.File), pluginDir))
                        {
                            report.Errors.Add($"Skipped renaming a file of the Mod Manager's own folder: {a.File}");
                            continue;
                        }
                        switch (ApplyRename(pluginsRoot, a.Kind, a.File))
                        {
                            case Outcome.Done: report.Applied.Add(a.File); break;
                            case Outcome.Locked: report.StillQueued.Add(a.File); left.Add(a); break;
                            default: report.Errors.Add($"Skipped an unsafe plugin rename: {a.File}"); break;
                        }
                    }
                    catch (Exception e) { DebugLog.Error("core", e, "PendingActions.ApplyAll"); report.Errors.Add($"{a.File}: {e.Message}"); }
                    continue;
                }
                if (!Manifest.IsSafeFileName(a.File))
                {
                    report.Errors.Add($"Skipped a pending change with an unsafe file name: {a.File}");
                    continue;
                }
                bool core = IsCoreFile(a.File);
                if (!core && a.Kind == PendingKind.Replace && coreUpdate)
                {
                    report.Held.Add(a.File);
                    left.Add(a);
                    continue;
                }
                if (core && a.Kind == PendingKind.Replace) coreUpdate = true;
                try
                {
                    var outcome = a.Kind == PendingKind.Replace ? ApplyReplace(pluginDir, a.File) : ApplyDelete(pluginDir, a.File);
                    switch (outcome)
                    {
                        case Outcome.Done: report.Applied.Add(a.File); break;
                        case Outcome.Swapped: report.Applied.Add(a.File); report.Swapped.Add(a.File); break;
                        case Outcome.Locked: report.StillQueued.Add(a.File); left.Add(a); break;
                        case Outcome.Missing: report.Errors.Add($"{a.File}: the downloaded update is missing; download it again."); break;
                    }
                }
                catch (Exception e)
                {
                    DebugLog.Error("core", e, "PendingActions.ApplyAll");
                    report.Errors.Add($"{a.File}: {e.Message}");
                }
            }
            try { Write(pluginDir, left); }
            catch (Exception e) { DebugLog.Error("core", e, "PendingActions.ApplyAll"); report.Errors.Add("Couldn't save the pending changes: " + e.Message); }
        }
        return LastReport = report;
    }

    private static bool IsCoreFile(string file) => SameFile(file, Installer.CoreFile);

    private enum Outcome { Done, Swapped, Locked, Missing }

    private static Outcome ApplyReplace(string pluginDir, string file)
    {
        string staged = StagedPath(pluginDir, file), target = Path.Combine(pluginDir, file);
        if (!File.Exists(staged)) return Outcome.Missing;
        try
        {
            File.Move(staged, target, true);
            return Outcome.Done;
        }
        catch (Exception e) when (IsLocked(e))
        {
            DebugLog.Error("core", e, "PendingActions.ApplyReplace");
            // A DLL that is running (core updating itself) can't be overwritten, but Windows lets it be renamed:
            // move it aside and put the new one in its place; the new one runs from the next start.
            if (!File.Exists(target) || !TryMoveAside(pluginDir, file)) return Outcome.Locked;
            try
            {
                File.Move(staged, target, false);
                return Outcome.Swapped;
            }
            catch (Exception ex)
            {
                DebugLog.Error("core", ex, "PendingActions.ApplyReplace");
                // Put the running copy back so the mod isn't left missing.
                try { File.Move(OldPath(pluginDir, file), target, false); } catch (Exception ex2) { DebugLog.Error("core", ex2, "PendingActions.ApplyReplace"); /* the next start retries */ }
                return Outcome.Locked;
            }
        }
    }

    /// <summary>Disable: &lt;file&gt; to &lt;file&gt;.disabled; Enable: back. Never overwrites a live .dll; nothing to
    /// rename counts as done. <see cref="Outcome.Missing"/>: the path is not inside the plugins folder.</summary>
    private static Outcome ApplyRename(string pluginsRoot, PendingKind kind, string relFile)
    {
        string dll = ResolveInside(pluginsRoot, relFile);
        if (dll == null) return Outcome.Missing;
        string off = dll + DisabledSuffix;
        try
        {
            if (kind == PendingKind.Disable)
            {
                if (!File.Exists(dll)) return Outcome.Done; // already off (or gone)
                File.Move(dll, off, true);
            }
            else
            {
                if (File.Exists(dll) || !File.Exists(off)) return Outcome.Done; // already on, or nothing to switch on
                File.Move(off, dll, false);
            }
            return Outcome.Done;
        }
        catch (Exception e) when (IsLocked(e)) { DebugLog.Error("core", e, "PendingActions.ApplyRename"); return Outcome.Locked; }
    }

    private static Outcome ApplyDelete(string pluginDir, string file)
    {
        string target = Path.Combine(pluginDir, file);
        TryDelete(StagedPath(pluginDir, file)); // an update that was downloaded before the uninstall
        if (!File.Exists(target)) return Outcome.Done;
        try
        {
            File.Delete(target);
            return Outcome.Done;
        }
        catch (Exception e) when (IsLocked(e))
        {
            DebugLog.Error("core", e, "PendingActions.ApplyDelete");
            // Moved out of the plugin folder's .dll files, it no longer loads; the copy goes at the next start.
            return TryMoveAside(pluginDir, file) ? Outcome.Swapped : Outcome.Locked;
        }
    }

    private static bool TryMoveAside(string pluginDir, string file)
    {
        string old = OldPath(pluginDir, file);
        try
        {
            TryDelete(old);
            File.Move(Path.Combine(pluginDir, file), old, false);
            return true;
        }
        catch (Exception ex) { DebugLog.Error("core", ex, "PendingActions.TryMoveAside"); return false; }
    }

    private static bool IsLocked(Exception e) => e is IOException || e is UnauthorizedAccessException;

    /// <summary>Copies moved aside at the last start are free now.</summary>
    private static void CleanOldCopies(string pluginDir)
    {
        try
        {
            string dir = PendingDir(pluginDir);
            if (!Directory.Exists(dir)) return;
            foreach (var f in Directory.GetFiles(dir, "*" + OldSuffix)) TryDelete(f);
        }
        catch (Exception ex) { DebugLog.Error("core", ex, "PendingActions.CleanOldCopies"); /* best effort */ }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch (Exception ex) { DebugLog.Error("core", ex, "PendingActions.TryDelete"); /* best effort */ }
    }

    private static string Quarantine(string queue)
    {
        string bad = queue + ".bad";
        try
        {
            if (File.Exists(bad)) File.Delete(bad);
            File.Move(queue, bad);
            return bad;
        }
        catch (Exception ex)
        {
            DebugLog.Error("core", ex, "PendingActions.Quarantine");
            // Can't rename it: at least don't apply it, and don't trip over it at every start.
            TryDelete(queue);
            return File.Exists(queue) ? null : bad;
        }
    }

    internal static bool SameFile(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    // ---------------------------------------------------------------- the file

    // { "actions": [ { "kind": "replace", "file": "OTH.Chat.dll" }, { "kind": "disable", "file": "SomeMod/SomeMod.dll" } ] }

    private static string KindName(PendingKind k) => k switch
    {
        PendingKind.Replace => "replace",
        PendingKind.Delete => "delete",
        PendingKind.Disable => "disable",
        _ => "enable",
    };

    /// <summary>Throws on a damaged file; a missing file is an empty queue.</summary>
    private static List<PendingAction> Read(string pluginDir)
    {
        var list = new List<PendingAction>();
        string path = QueuePath(pluginDir);
        if (!File.Exists(path)) return list;
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        if (!doc.RootElement.TryGetProperty("actions", out var actions) || actions.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("no \"actions\" list");
        foreach (var a in actions.EnumerateArray())
        {
            string kind = a.GetProperty("kind").GetString();
            string file = a.GetProperty("file").GetString();
            var k = string.Equals(kind, "replace", StringComparison.OrdinalIgnoreCase) ? PendingKind.Replace
                : string.Equals(kind, "delete", StringComparison.OrdinalIgnoreCase) ? PendingKind.Delete
                : string.Equals(kind, "disable", StringComparison.OrdinalIgnoreCase) ? PendingKind.Disable
                : string.Equals(kind, "enable", StringComparison.OrdinalIgnoreCase) ? PendingKind.Enable
                : throw new InvalidDataException("unknown action \"" + kind + "\"");
            list.Add(new PendingAction(k, file));
        }
        return list;
    }

    /// <summary>Writes the queue (through a temporary file, so a crash never leaves half a file); no actions: no file.</summary>
    private static void Write(string pluginDir, List<PendingAction> all)
    {
        string path = QueuePath(pluginDir);
        if (all.Count == 0)
        {
            if (File.Exists(path)) File.Delete(path);
            return;
        }
        Directory.CreateDirectory(PendingDir(pluginDir));
        string tmp = path + ".tmp";
        using (var stream = File.Create(tmp))
        using (var w = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteStartArray("actions");
            foreach (var a in all)
            {
                w.WriteStartObject();
                w.WriteString("kind", KindName(a.Kind));
                w.WriteString("file", a.File);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        File.Move(tmp, path, true);
    }
}
