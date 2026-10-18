using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace OverTheHillPlus.Core.Manager;

/// <summary>
/// Which mods are switched on, kept in a plain "id=true|false" text file. Pure logic with no BepInEx types, so it can
/// be unit tested. A mod is on unless the file says otherwise. A mod's enabled state is only read when the game starts,
/// so <see cref="PendingRestart"/> reports a change made since then.
/// </summary>
internal sealed class EnabledStore
{
    private readonly string _path;
    private readonly Dictionary<string, bool> _current = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, bool> _atStartup = new(StringComparer.OrdinalIgnoreCase);

    public EnabledStore(string path)
    {
        _path = path;
        Load();
        foreach (var kv in _current) _atStartup[kv.Key] = kv.Value;
    }

    public bool Get(string id, bool dflt = true) => _current.TryGetValue(id, out var on) ? on : dflt;

    public void Set(string id, bool on)
    {
        if (_current.TryGetValue(id, out var old) && old == on) return;
        _current[id] = on;
        Save();
    }

    /// <summary>The value the game started with (what the mods read in their Load).</summary>
    public bool AtStartup(string id) => !_atStartup.TryGetValue(id, out var on) || on;

    /// <summary>True when the value now differs from the one the game started with.</summary>
    public bool PendingRestart(string id) => Get(id) != AtStartup(id);

    private void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;
            foreach (var raw in File.ReadAllLines(_path))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line[0] == '#') continue;
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                var id = line.Substring(0, eq).Trim();
                if (!bool.TryParse(line.Substring(eq + 1).Trim(), out var on)) continue;
                _current[id] = on;
            }
        }
        catch (Exception ex)
        {
            DebugLog.Error("core", ex, "EnabledStore.Load");
            // An unreadable file means "everything on"; the next Set rewrites it.
        }
    }

    private void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var sb = new StringBuilder();
            foreach (var kv in _current) sb.Append(kv.Key).Append('=').Append(kv.Value ? "true" : "false").Append('\n');
            File.WriteAllText(_path, sb.ToString());
        }
        catch (Exception ex)
        {
            DebugLog.Error("core", ex, "EnabledStore.Save");
            // The switch still works for this session; it just will not be remembered.
        }
    }
}
