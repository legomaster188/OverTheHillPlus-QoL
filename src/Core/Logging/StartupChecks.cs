using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Reflection.Metadata;
using System.Threading;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Unity.IL2CPP;

namespace OverTheHillPlus.Logging;

/// <summary>
/// The start-up self-checks, written to the debug log on the first frame (Info for what is fine, Error for what is not,
/// so a problem shows at the default level): each mod's patches (applied vs failed, from PatchReport), types of a
/// mod that could not load, config files BepInEx could not make sense of (read before the mods bind and save them),
/// and game types and members a mod's code refers to that the game no longer has (<see cref="MissingMembers"/>, on a
/// worker thread). Never throws.
/// </summary>
internal static class StartupChecks
{
    private sealed class FileScan
    {
        public string Mod;
        public string Path;
        public ConfigFileScan.Result Scan;
        public string Problem;
    }

    private static readonly List<FileScan> Scans = new();

    /// <summary>Reads a config file's text before the mods bind (and save) it. Called by ModManager.ConfigFor.</summary>
    internal static void CaptureConfig(string path)
    {
        try
        {
            var scan = new FileScan { Path = path, Mod = ConfigLine.ModOfFile(path) };
            if (File.Exists(path))
            {
                try { scan.Scan = ConfigFileScan.Parse(File.ReadAllLines(path)); }
                catch (Exception e) { scan.Problem = "could not be read: " + e.Message; }
            }
            lock (Scans) Scans.Add(scan);
        }
        catch (Exception e) { DebugLog.Error("core", e, "StartupChecks.CaptureConfig"); }
    }

    /// <summary>Runs every check (first frame, main thread).</summary>
    internal static void Run()
    {
        int problems = 0;
        try { problems += Patches(); } catch (Exception e) { DebugLog.Error("core", e, "StartupChecks.Patches"); }
        try { problems += Types(); } catch (Exception e) { DebugLog.Error("core", e, "StartupChecks.Types"); }
        try { problems += Configs(); } catch (Exception e) { DebugLog.Error("core", e, "StartupChecks.Configs"); }
        DebugLog.Info("core", problems == 0 ? "start-up self-checks: patches, types and settings files are fine; the game API check follows" :
                                              $"start-up self-checks: {problems} problem(s), see the lines above");
        try { StartApiCheck(); } catch (Exception e) { DebugLog.Error("core", e, "StartupChecks.StartApiCheck"); }
    }

    // ---- patches ----

    private static int Patches()
    {
        // Each mod's own "patches: ..." line was written when it patched (PatchReport); here the one-line total, and an
        // error for each mod with a failed patch so it shows at the default level.
        int problems = 0, mods = 0, ok = 0, failed = 0;
        foreach (var id in PatchReport.Mods())
        {
            mods++;
            ok += PatchReport.Applied(id);
            failed += PatchReport.Failed(id);
            if (PatchReport.Failed(id) == 0) continue;
            problems++;
            DebugLog.Error(id, "patches: " + PatchReport.SummaryLine(id) + " (the features that need them are off this session)");
        }
        DebugLog.Info("core", $"patch self-check: {ok} patch classes applied, {failed} failed, over {mods} mod(s)");
        return problems;
    }

    // ---- types that did not load ----

    private static int Types()
    {
        int problems = 0;
        foreach (var p in ModMap.TypeLoadProblems)
        {
            problems++;
            DebugLog.Error(p.Mod, StartupText.TypeLoadLine(p.Mod, p.Failed, p.Total, p.Messages));
        }
        return problems;
    }

    // ---- settings files ----

    private static int Configs()
    {
        int problems = 0;
        List<FileScan> scans;
        lock (Scans) scans = new List<FileScan>(Scans);
        foreach (var s in scans)
        {
            string file = System.IO.Path.GetFileName(s.Path);
            if (s.Problem != null)
            {
                problems++;
                DebugLog.Error(s.Mod, StartupText.ConfigLine(file, s.Problem));
                continue;
            }
            if (s.Scan == null) continue; // a new file: nothing to read
            string bad = ConfigFileScan.BadLinesText(s.Scan);
            if (bad != null)
            {
                problems++;
                DebugLog.Error(s.Mod, StartupText.ConfigLine(file, bad + "; BepInEx ignores them and the next save drops them"));
            }
            problems += BadValues(s, file);
        }
        return problems;
    }

    /// <summary>A value in the file that its setting cannot read (BepInEx then keeps the default).</summary>
    private static int BadValues(FileScan s, string file)
    {
        int problems = 0;
        ConfigFile cfg = null;
        foreach (var f in CoreConfig.Files)
            if (f != null && string.Equals(f.ConfigFilePath, s.Path, StringComparison.OrdinalIgnoreCase)) cfg = f;
        if (cfg == null) return 0;
        foreach (var key in cfg.Keys)
        {
            if (!s.Scan.Values.TryGetValue(key.Section, out var keys) || !keys.TryGetValue(key.Key, out string text)) continue;
            var entry = cfg[key];
            try { TomlTypeConverter.ConvertToValue(text, entry.SettingType); }
            catch (Exception e)
            {
                problems++;
                DebugLog.Error(s.Mod, StartupText.ConfigLine(file, $"[{key.Section}] {key.Key} = '{ConfigLine.Show(text)}' is not a valid {entry.SettingType.Name}: {e.Message}; the default is used"));
            }
        }
        return problems;
    }

    // ---- the game API each mod's code uses ----

    private static void StartApiCheck()
    {
        var assemblies = new List<(string mod, Assembly asm)>();
        foreach (var kv in IL2CPPChainloader.Instance.Plugins)
        {
            var info = kv.Value;
            string id = ModMap.IdOfGuid(info?.Metadata?.GUID);
            Assembly asm = null;
            try { asm = info?.Instance?.GetType().Assembly; } catch (Exception e) { DebugLog.Error("core", e, "StartupChecks.StartApiCheck plugin"); }
            if (asm == null || string.IsNullOrEmpty(id)) continue;
            if (id.StartsWith("com.", StringComparison.OrdinalIgnoreCase)) continue; // another creator's plugin
            assemblies.Add((id, asm));
        }
        if (assemblies.Count == 0) return;
        var thread = new Thread(() => ApiCheck(assemblies)) { IsBackground = true, Name = "OTH api check", Priority = ThreadPriority.BelowNormal };
        thread.Start();
    }

    private static unsafe void ApiCheck(List<(string mod, Assembly asm)> assemblies)
    {
        try
        {
            // The game's own assemblies: the interop DLLs BepInEx generated from its IL2CPP code.
            var game = new HashSet<string>(StringComparer.Ordinal);
            string interop = Path.Combine(Paths.BepInExRootPath, "interop");
            if (Directory.Exists(interop))
                foreach (var f in Directory.GetFiles(interop, "*.dll")) game.Add(Path.GetFileNameWithoutExtension(f));
            if (game.Count == 0)
            {
                DebugLog.Info("core", "game API check skipped: no interop folder");
                return;
            }
            var loaded = new Dictionary<string, Assembly>(StringComparer.Ordinal);
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
            {
                string n = a.GetName().Name;
                if (n != null && !loaded.ContainsKey(n)) loaded[n] = a;
            }
            var started = DateTime.UtcNow;
            int total = 0, mods = 0;
            foreach (var (mod, asm) in assemblies)
            {
                byte* blob;
                int len;
                if (!System.Reflection.Metadata.AssemblyExtensions.TryGetRawMetadata(asm, out blob, out len)) continue;
                var reader = new MetadataReader(blob, len);
                var r = MissingMembers.Find(reader, n => loaded.TryGetValue(n, out var a) ? a : null, game.Contains);
                mods++;
                total += r.MissingTotal;
                if (r.MissingTotal == 0)
                    DebugLog.Info(mod, $"game API check: {r.MembersChecked} members of {r.TypesChecked} game types referenced, none missing");
                else
                    DebugLog.Error(mod, $"game API check: {r.MissingTotal} game type(s)/member(s) this mod refers to are missing" +
                                        " (the game changed; the features using them will fail): " + string.Join(", ", r.Missing) + (r.Truncated ? ", ..." : ""));
            }
            DebugLog.Info("core", $"game API check done for {mods} mods in {(DateTime.UtcNow - started).TotalMilliseconds:0} ms: {total} missing");
        }
        catch (Exception e) { DebugLog.Error("core", e, "StartupChecks.ApiCheck"); }
    }
}
