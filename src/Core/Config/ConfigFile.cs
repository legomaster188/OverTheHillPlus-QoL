using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace OverTheHillPlus.Config;

/// <summary>
/// A settings file in BepInEx's .cfg format, with the API the mods always used (Bind, Value, SettingChanged,
/// descriptions, acceptable values). The same code runs under BepInEx and MelonLoader, and a file BepInEx wrote reads
/// and saves unchanged: sections sorted by name, "## description", "# Setting type", "# Default value" and
/// "# Acceptable values" lines, then "Key = value"; lines this file doesn't bind are kept as they are.
/// </summary>
public class ConfigFile : IDictionary<ConfigDefinition, ConfigEntryBase>
{
    /// <summary>Where a value that can't be read (or a SettingChanged handler that throws) is reported. Core points it
    /// at its log; until then it goes nowhere.</summary>
    public static Action<string> Warn;

    private readonly object _ioLock = new();
    private readonly string _ownerName, _ownerVersion, _ownerGuid;

    /// <summary>The entries bound so far, in the order they were bound.</summary>
    protected Dictionary<ConfigDefinition, ConfigEntryBase> Entries { get; } = new();

    /// <summary>Values read from the file that nothing has bound yet. Saved back as they are.</summary>
    protected Dictionary<ConfigDefinition, string> OrphanedEntries { get; } = new();

    /// <summary>Full path of the file.</summary>
    public string ConfigFilePath { get; }

    /// <summary>Save the file after every Bind and every changed value (on by default, as in BepInEx).</summary>
    public bool SaveOnConfigSet { get; set; } = true;

    /// <summary>Raised after <see cref="Reload"/>.</summary>
    public event EventHandler ConfigReloaded;

    /// <summary>Raised when a bound value changes (sender: the entry that changed).</summary>
    public event EventHandler<SettingChangedEventArgs> SettingChanged;

    public ConfigFile(string configPath, bool saveOnInit) : this(configPath, saveOnInit, null, null, null) { }

    /// <summary>With an owner, the file starts with BepInEx's "created by plugin" header lines.</summary>
    public ConfigFile(string configPath, bool saveOnInit, string ownerName, string ownerVersion, string ownerGuid)
    {
        if (configPath == null) throw new ArgumentNullException(nameof(configPath));
        ConfigFilePath = Path.GetFullPath(configPath);
        _ownerName = ownerName;
        _ownerVersion = ownerVersion;
        _ownerGuid = ownerGuid;
        if (File.Exists(ConfigFilePath)) Reload();
        else if (saveOnInit) Save();
    }

    // ---------------------------------------------------------------- read and write

    /// <summary>Reads the file again: bound entries take its values, the rest are kept as orphaned lines.</summary>
    public void Reload()
    {
        lock (_ioLock)
        {
            OrphanedEntries.Clear();
            string currentSection = string.Empty;
            foreach (string rawLine in File.ReadAllLines(ConfigFilePath))
            {
                string line = rawLine.Trim();
                if (line.StartsWith("#", StringComparison.Ordinal)) continue;
                if (line.StartsWith("[", StringComparison.Ordinal) && line.EndsWith("]", StringComparison.Ordinal))
                {
                    currentSection = line.Substring(1, line.Length - 2);
                    continue;
                }
                string[] split = line.Split(new[] { '=' }, 2);
                if (split.Length != 2) continue;
                string currentKey = split[0].Trim();
                string currentValue = split[1].Trim();
                var definition = new ConfigDefinition(currentSection, currentKey);
                if (Entries.TryGetValue(definition, out var entry)) entry.SetSerializedValue(currentValue);
                else OrphanedEntries[definition] = currentValue;
            }
        }
        OnConfigReloaded();
    }

    /// <summary>Writes every bound entry (with its description) and every orphaned line, by section. Does nothing when the
    /// text is what the file holds already: every start would otherwise write all the mods' files, each one flushed to the
    /// disk. Written through a temp file, so a crash mid-write never cuts the settings.</summary>
    public void Save()
    {
        lock (_ioLock)
        {
            string text = ToText();
            if (text == _lastWritten && File.Exists(ConfigFilePath)) return;
            if (_lastWritten == null && SameOnDisk(text))
            {
                _lastWritten = text;
                return;
            }
            string dir = Path.GetDirectoryName(ConfigFilePath);
            if (dir != null) Directory.CreateDirectory(dir);
            OverTheHillPlus.Core.Shared.AtomicFile.WriteAllText(ConfigFilePath, text);
            _lastWritten = text;
        }
    }

    private string _lastWritten;

    private bool SameOnDisk(string text)
    {
        try { return File.Exists(ConfigFilePath) && File.ReadAllText(ConfigFilePath) == text; }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    /// <summary>The file's text as <see cref="Save"/> writes it.</summary>
    public string ToText()
    {
        lock (_ioLock)
        {
            var sb = new StringBuilder();
            if (_ownerName != null || _ownerGuid != null)
            {
                Line(sb, $"## Settings file was created by plugin {_ownerName} v{_ownerVersion}");
                Line(sb, $"## Plugin GUID: {_ownerGuid}");
                Line(sb, "");
            }
            var all = Entries.Select(x => (key: x.Key, entry: x.Value, value: x.Value.GetSerializedValue()))
                             .Concat(OrphanedEntries.Select(x => (key: x.Key, entry: (ConfigEntryBase)null, value: x.Value)));
            // Sections in the order BepInEx writes them (its OrderBy on the section name, the default string comparer).
            foreach (var section in all.GroupBy(x => x.key.Section).OrderBy(x => x.Key))
            {
                Line(sb, $"[{section.Key}]");
                foreach (var e in section)
                {
                    Line(sb, "");
                    e.entry?.WriteDescription(sb);
                    Line(sb, $"{e.key.Key} = {e.value}");
                }
                Line(sb, "");
            }
            return sb.ToString();
        }
    }

    // StreamWriter.WriteLine on Windows: CRLF, as BepInEx writes it.
    internal static void Line(StringBuilder sb, string text) => sb.Append(text).Append(Environment.NewLine);

    // ---------------------------------------------------------------- bind

    public ConfigEntry<T> Bind<T>(ConfigDefinition configDefinition, T defaultValue, ConfigDescription configDescription = null)
    {
        if (configDefinition == null) throw new ArgumentNullException(nameof(configDefinition));
        if (!TomlTypeConverter.CanConvert(typeof(T)))
            throw new ArgumentException($"Type {typeof(T)} is not supported by the config system. Supported types: " +
                                        string.Join(", ", TomlTypeConverter.GetSupportedTypes().Select(x => x.Name).ToArray()));
        lock (_ioLock)
        {
            if (Entries.TryGetValue(configDefinition, out var rawEntry)) return (ConfigEntry<T>)rawEntry;
            var entry = new ConfigEntry<T>(this, configDefinition, defaultValue, configDescription);
            Entries[configDefinition] = entry;
            if (OrphanedEntries.TryGetValue(configDefinition, out string homeless))
            {
                entry.SetSerializedValue(homeless);
                OrphanedEntries.Remove(configDefinition);
            }
            if (SaveOnConfigSet) Save();
            return entry;
        }
    }

    public ConfigEntry<T> Bind<T>(string section, string key, T defaultValue, ConfigDescription configDescription = null) =>
        Bind(new ConfigDefinition(section, key), defaultValue, configDescription);

    public ConfigEntry<T> Bind<T>(string section, string key, T defaultValue, string description) =>
        Bind(new ConfigDefinition(section, key), defaultValue, new ConfigDescription(description, null));

    public bool TryGetEntry<T>(ConfigDefinition configDefinition, out ConfigEntry<T> entry)
    {
        lock (_ioLock)
        {
            if (Entries.TryGetValue(configDefinition, out var raw) && raw is ConfigEntry<T> typed)
            {
                entry = typed;
                return true;
            }
            entry = null;
            return false;
        }
    }

    public bool TryGetEntry<T>(string section, string key, out ConfigEntry<T> entry) =>
        TryGetEntry(new ConfigDefinition(section, key), out entry);

    /// <summary>
    /// Takes a line of the file that no entry binds (a setting that moved to another file): true with its value, and the
    /// line is gone from this file at its next save. False when there is no such line (or an entry binds it).
    /// </summary>
    public bool TakeOrphan(string section, string key, out string value)
    {
        var def = new ConfigDefinition(section, key);
        lock (_ioLock)
        {
            if (!OrphanedEntries.TryGetValue(def, out value)) return false;
            OrphanedEntries.Remove(def);
        }
        if (SaveOnConfigSet) Save();
        return true;
    }

    // ---------------------------------------------------------------- events

    internal void OnSettingChanged(object sender, ConfigEntryBase changedEntryBase)
    {
        if (changedEntryBase == null) throw new ArgumentNullException(nameof(changedEntryBase));
        if (SaveOnConfigSet) Save();
        var handlers = SettingChanged;
        if (handlers == null) return;
        var args = new SettingChangedEventArgs(changedEntryBase);
        foreach (var callback in handlers.GetInvocationList().Cast<EventHandler<SettingChangedEventArgs>>())
        {
            try { callback(sender, args); }
            catch (Exception e) { Warn?.Invoke(e.ToString()); }
        }
    }

    private void OnConfigReloaded()
    {
        var handlers = ConfigReloaded;
        if (handlers == null) return;
        foreach (var callback in handlers.GetInvocationList().Cast<EventHandler>())
        {
            try { callback(this, EventArgs.Empty); }
            catch (Exception e) { Warn?.Invoke(e.ToString()); }
        }
    }

    // ---------------------------------------------------------------- dictionary

    public ConfigEntryBase this[ConfigDefinition key]
    {
        get { lock (_ioLock) return Entries[key]; }
        set => throw new InvalidOperationException("Directly setting a config entry is not supported");
    }

    public ConfigEntryBase this[string section, string key] => this[new ConfigDefinition(section, key)];

    public ICollection<ConfigDefinition> Keys { get { lock (_ioLock) return Entries.Keys.ToArray(); } }

    public ICollection<ConfigEntryBase> Values { get { lock (_ioLock) return Entries.Values.ToArray(); } }

    public int Count { get { lock (_ioLock) return Entries.Count; } }

    public bool IsReadOnly => false;

    public bool ContainsKey(ConfigDefinition key) { lock (_ioLock) return Entries.ContainsKey(key); }

    public bool TryGetValue(ConfigDefinition key, out ConfigEntryBase value) { lock (_ioLock) return Entries.TryGetValue(key, out value); }

    public bool Remove(ConfigDefinition key) { lock (_ioLock) return Entries.Remove(key); }

    public void Clear() { lock (_ioLock) Entries.Clear(); }

    public void Add(ConfigDefinition key, ConfigEntryBase value) =>
        throw new InvalidOperationException("Directly adding a config entry is not supported");

    void ICollection<KeyValuePair<ConfigDefinition, ConfigEntryBase>>.Add(KeyValuePair<ConfigDefinition, ConfigEntryBase> item) =>
        throw new InvalidOperationException("Directly adding a config entry is not supported");

    bool ICollection<KeyValuePair<ConfigDefinition, ConfigEntryBase>>.Contains(KeyValuePair<ConfigDefinition, ConfigEntryBase> item)
    {
        lock (_ioLock) return ((ICollection<KeyValuePair<ConfigDefinition, ConfigEntryBase>>)Entries).Contains(item);
    }

    void ICollection<KeyValuePair<ConfigDefinition, ConfigEntryBase>>.CopyTo(KeyValuePair<ConfigDefinition, ConfigEntryBase>[] array, int arrayIndex)
    {
        lock (_ioLock) ((ICollection<KeyValuePair<ConfigDefinition, ConfigEntryBase>>)Entries).CopyTo(array, arrayIndex);
    }

    bool ICollection<KeyValuePair<ConfigDefinition, ConfigEntryBase>>.Remove(KeyValuePair<ConfigDefinition, ConfigEntryBase> item)
    {
        lock (_ioLock) return Entries.Remove(item.Key);
    }

    public IEnumerator<KeyValuePair<ConfigDefinition, ConfigEntryBase>> GetEnumerator() => Entries.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>Arguments of <see cref="ConfigFile.SettingChanged"/>.</summary>
public sealed class SettingChangedEventArgs : EventArgs
{
    public SettingChangedEventArgs(ConfigEntryBase changedSetting) { ChangedSetting = changedSetting; }

    /// <summary>The entry whose value changed.</summary>
    public ConfigEntryBase ChangedSetting { get; }
}
