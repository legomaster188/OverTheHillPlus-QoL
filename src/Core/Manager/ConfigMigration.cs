using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace OverTheHillPlus.Core.Manager;

/// <summary>
/// Moves the settings of the old single config file (BepInEx/config/&lt;old GUID&gt;.cfg) into each mod's own
/// oth.&lt;id&gt;.cfg, using <see cref="SectionMap"/>. Never overwrites a mod's file that already exists. Settings of
/// mods that aren't installed wait in oth.migration-pending.cfg (under their old names) until the mod first starts.
/// The old file is renamed to &lt;name&gt;.migrated, so the move happens once and nothing is lost. Pure logic with no
/// BepInEx types, so it can be unit tested.
/// </summary>
internal static class ConfigMigration
{
    public const string PendingFile = "oth.migration-pending.cfg";
    public const string MigratedSuffix = ".migrated";

    private const string MovedHeader = "## Settings moved here from the old OverTheHillPlus config file by the OTH Mod Manager.";
    private const string PendingHeader =
        "## Settings from the old OverTheHillPlus config file for mods that aren't installed yet.\n" +
        "## Each mod takes its part (into its own oth.<id>.cfg) the first time it starts.";

    /// <summary>One setting line with the comment lines above it.</summary>
    private sealed class Entry
    {
        public string Section;
        public string Key;
        public string Value;
        public List<string> Comments;
    }

    /// <summary>An old setting and where it goes.</summary>
    private readonly struct Placed
    {
        public Placed(Entry old, SectionMap.Target to)
        {
            Old = old;
            To = to;
        }

        public Entry Old { get; }
        public SectionMap.Target To { get; }
    }

    // ------------------------------------------------------------------ planning

    /// <summary>Mod id → the text of its new cfg file. Whole sections only; unknown sections go to core.</summary>
    public static Dictionary<string, string> Plan(string oldCfgText, IReadOnlyDictionary<string, string> sectionToMod) =>
        Plan(oldCfgText, new SectionMap(sectionToMod));

    /// <summary>Mod id → the text of its new cfg file, split (and renamed) by <paramref name="map"/>.</summary>
    public static Dictionary<string, string> Plan(string oldCfgText, SectionMap map)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in Group(Parse(oldCfgText), map ?? SectionMap.Default))
            result[kv.Key] = RenderMoved(kv.Value);
        return result;
    }

    // ------------------------------------------------------------------ files

    /// <summary>
    /// Migrates every old config file found in <paramref name="configDir"/>: writes oth.&lt;id&gt;.cfg for each
    /// installed mod that has none yet, keeps the rest in <see cref="PendingFile"/>, then renames the old file.
    /// Does nothing when no old file is there. With both old files present (private and public edition), the first
    /// one read decides: a key found only in the second one doesn't reach a mod whose file the first one wrote.
    /// </summary>
    public static void Run(string configDir, Func<string, bool> isModInstalled, SectionMap map = null)
    {
        map ??= SectionMap.Default;
        var pending = new List<Entry>();
        var pendingKeys = new HashSet<(string, string)>();
        foreach (var name in LegacyDetector.OldConfigFiles)
        {
            string path = Path.Combine(configDir, name);
            if (!File.Exists(path)) continue;
            foreach (var kv in Group(Parse(File.ReadAllText(path)), map))
            {
                if (isModInstalled == null || isModInstalled(kv.Key))
                {
                    WriteIfMissing(configDir, kv.Key, kv.Value);
                    continue;
                }
                foreach (var p in kv.Value)
                    if (pendingKeys.Add((p.Old.Section, p.Old.Key))) pending.Add(p.Old);
            }
            if (pending.Count > 0) MergePending(configDir, pending);
            Archive(path);
        }
    }

    /// <summary>
    /// Writes only <paramref name="modId"/>'s file from the old config (when it has none yet) and leaves the old file
    /// where it is. For core while the old plugin is still loaded: core binds its own file now, the rest waits.
    /// </summary>
    public static void RunFor(string configDir, string modId, SectionMap map = null)
    {
        if (string.IsNullOrEmpty(modId)) return;
        map ??= SectionMap.Default;
        foreach (var name in LegacyDetector.OldConfigFiles)
        {
            string path = Path.Combine(configDir, name);
            if (!File.Exists(path)) continue;
            if (Group(Parse(File.ReadAllText(path)), map).TryGetValue(modId, out var mine)) WriteIfMissing(configDir, modId, mine);
        }
    }

    /// <summary>
    /// Hands a mod its waiting settings from <see cref="PendingFile"/> (when it has no oth.&lt;id&gt;.cfg yet) and
    /// removes them from there; the pending file goes once it's empty. Called by ModManager.Register.
    /// </summary>
    public static void ApplyPending(string configDir, string modId, SectionMap map = null)
    {
        if (string.IsNullOrEmpty(modId)) return;
        string path = Path.Combine(configDir, PendingFile);
        if (!File.Exists(path)) return;
        var groups = Group(Parse(File.ReadAllText(path)), map ?? SectionMap.Default);
        if (!groups.TryGetValue(modId, out var mine)) return;
        WriteIfMissing(configDir, modId, mine);

        var rest = new List<Entry>();
        foreach (var kv in groups)
            if (!string.Equals(kv.Key, modId, StringComparison.OrdinalIgnoreCase))
                foreach (var p in kv.Value) rest.Add(p.Old);
        if (rest.Count == 0) File.Delete(path);
        else WriteAtomic(path, Render(PendingHeader, rest));
    }

    private static string ModFile(string configDir, string modId) => Path.Combine(configDir, $"oth.{modId}.cfg");

    private static void WriteIfMissing(string configDir, string modId, List<Placed> items)
    {
        string path = ModFile(configDir, modId);
        if (File.Exists(path)) return; // never overwrite: the mod's own file wins
        Directory.CreateDirectory(configDir);
        WriteAtomic(path, RenderMoved(items));
    }

    /// <summary>
    /// Adds settings to the pending file. A setting already waiting there keeps its value (never overwrite: an old file
    /// that comes back, say after reinstalling the old plugin, holds defaults rather than the player's values).
    /// </summary>
    private static void MergePending(string configDir, List<Entry> fresh)
    {
        string path = Path.Combine(configDir, PendingFile);
        var keys = new HashSet<(string, string)>();
        var all = new List<Entry>();
        if (File.Exists(path))
            foreach (var e in Parse(File.ReadAllText(path)))
                if (keys.Add((e.Section, e.Key))) all.Add(e);
        foreach (var e in fresh)
            if (keys.Add((e.Section, e.Key))) all.Add(e);
        Directory.CreateDirectory(configDir);
        WriteAtomic(path, Render(PendingHeader, all));
    }

    /// <summary>
    /// Writes path.tmp and then moves it over path, so a crash mid-write never leaves a cut-off file that a later run
    /// would take for the player's own.
    /// </summary>
    private static void WriteAtomic(string path, string text)
    {
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, text);
        if (File.Exists(path)) File.Replace(tmp, path, null);
        else File.Move(tmp, path);
    }

    /// <summary>Renames the old file to name.migrated (name.migrated.2, ... when an earlier one is kept).</summary>
    private static void Archive(string path)
    {
        string target = path + MigratedSuffix;
        for (int n = 2; File.Exists(target); n++) target = path + MigratedSuffix + "." + n;
        File.Move(path, target);
    }

    // ------------------------------------------------------------------ cfg text

    /// <summary>Reads a BepInEx cfg the way BepInEx does ('#' lines are comments, [Section], Key = Value).</summary>
    private static List<Entry> Parse(string text)
    {
        var list = new List<Entry>();
        if (string.IsNullOrEmpty(text)) return list;
        string section = null;
        var comments = new List<string>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith("#"))
            {
                comments.Add(line);
                continue;
            }
            if (line.StartsWith("[") && line.EndsWith("]"))
            {
                section = line.Substring(1, line.Length - 2);
                comments = new List<string>(); // the file header and stray lines stay behind
                continue;
            }
            var split = line.Split(new[] { '=' }, 2);
            if (split.Length != 2 || section == null) { comments = new List<string>(); continue; }
            list.Add(new Entry { Section = section, Key = split[0].Trim(), Value = split[1].Trim(), Comments = comments });
            comments = new List<string>();
        }
        return list;
    }

    /// <summary>Mod id → its settings in file order. A target setting that comes twice keeps its first value.</summary>
    private static Dictionary<string, List<Placed>> Group(List<Entry> entries, SectionMap map)
    {
        var groups = new Dictionary<string, List<Placed>>(StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<(string, string, string)>();
        foreach (var e in entries)
        {
            var to = map.Resolve(e.Section, e.Key);
            string mod = string.IsNullOrEmpty(to.Mod) ? SectionMap.Fallback : to.Mod.ToLowerInvariant();
            if (!seen.Add((mod, to.Section, to.Key))) continue;
            if (!groups.TryGetValue(mod, out var list)) groups[mod] = list = new List<Placed>();
            list.Add(new Placed(e, to));
        }
        return groups;
    }

    private static string RenderMoved(List<Placed> items)
    {
        var entries = new List<Entry>(items.Count);
        foreach (var p in items)
            entries.Add(new Entry { Section = p.To.Section, Key = p.To.Key, Value = p.Old.Value, Comments = p.Old.Comments });
        return Render(MovedHeader, entries);
    }

    /// <summary>BepInEx's layout: sections in order of first use, each entry with its comments above it.</summary>
    private static string Render(string header, List<Entry> entries)
    {
        var order = new List<string>();
        var bySection = new Dictionary<string, List<Entry>>(StringComparer.Ordinal);
        foreach (var e in entries)
        {
            if (!bySection.TryGetValue(e.Section, out var list))
            {
                bySection[e.Section] = list = new List<Entry>();
                order.Add(e.Section);
            }
            list.Add(e);
        }
        var sb = new StringBuilder();
        sb.Append(header).Append('\n');
        foreach (var section in order)
        {
            sb.Append('\n').Append('[').Append(section).Append("]\n");
            foreach (var e in bySection[section])
            {
                sb.Append('\n');
                if (e.Comments != null)
                    foreach (var c in e.Comments) sb.Append(c).Append('\n');
                sb.Append(e.Key).Append(" = ").Append(e.Value).Append('\n');
            }
        }
        return sb.ToString();
    }
}
