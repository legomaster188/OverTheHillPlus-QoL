using System;
using System.Collections.Generic;
using BepInEx.Configuration;

namespace OverTheHillPlus.Logging;

/// <summary>
/// One hook on every config file of the mods (core's and each mod's, through <see cref="CoreConfig.Files"/>): a changed
/// setting is written at Info as "[Section] Key old -> new" under its mod's id. BepInEx only reports the new value, so
/// the old one is the last value this saw (all values are read once on the first frame and when the level goes up to
/// Info). A slider dragged for a second gives one line once it has been quiet for a moment. Main thread only.
/// </summary>
internal static class ConfigLog
{
    private const double QuietSeconds = 1.0;

    private static readonly ConfigChangeBook Book = new();
    private static readonly Dictionary<ConfigEntryBase, string> Known = new();
    private static readonly Dictionary<ConfigFile, string> Mods = new();
    private static readonly HashSet<ConfigFile> Hooked = new();

    /// <summary>Hooks every tracked file and the ones tracked later (once, from core's Load).</summary>
    internal static void Install()
    {
        foreach (var f in CoreConfig.Files) Hook(f);
        CoreConfig.FileTracked += Hook;
    }

    private static void Hook(ConfigFile file)
    {
        if (file == null || !Hooked.Add(file)) return;
        try
        {
            Mods[file] = ConfigLine.ModOfFile(file.ConfigFilePath);
            file.SettingChanged += OnChanged;
        }
        catch (Exception e) { DebugLog.Error("core", e, "ConfigLog.Hook"); }
    }

    /// <summary>Reads every setting's value, so the first change of each has a real "old" value. Cheap enough to run once.</summary>
    internal static void SnapshotAll()
    {
        try
        {
            foreach (var f in CoreConfig.Files)
            {
                if (f == null) continue;
                foreach (var key in f.Keys)
                {
                    var entry = f[key];
                    if (entry != null && !Known.ContainsKey(entry)) Known[entry] = Serialize(entry);
                }
            }
        }
        catch (Exception e) { DebugLog.Error("core", e, "ConfigLog.SnapshotAll"); }
    }

    private static string Serialize(ConfigEntryBase entry)
    {
        try { return entry.GetSerializedValue(); }
        catch { return entry.BoxedValue?.ToString(); }
    }

    private static void OnChanged(object sender, SettingChangedEventArgs args)
    {
        if (!DebugLog.IsInfo) return;
        try
        {
            var entry = args?.ChangedSetting;
            if (entry == null) return;
            Known.TryGetValue(entry, out string old);
            string now = Serialize(entry);
            Known[entry] = now;
            string mod = sender is ConfigFile f && Mods.TryGetValue(f, out var m) ? m : "?";
            Book.Note(mod, entry.Definition.Section, entry.Definition.Key, old, now, DebugLog.Now);
        }
        catch (Exception e) { DebugLog.Error("core", e, "ConfigLog.OnChanged"); }
    }

    /// <summary>Once a second: writes the settings that have been quiet for a moment.</summary>
    internal static void Flush(double now)
    {
        if (Book.PendingCount == 0) return;
        if (!DebugLog.IsInfo) { Book.TakeSettled(double.MaxValue, 0); return; }
        foreach (var (mod, line) in Book.TakeSettled(now, QuietSeconds))
            DebugLog.Info(mod, "setting " + line);
    }
}
