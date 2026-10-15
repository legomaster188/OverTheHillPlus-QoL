using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Configuration;

namespace OverTheHillPlus.Features.Public.GraphicsProfiles;

/// <summary>
/// Generic view over every mod setting that belongs to graphics: all bound config entries whose section name starts
/// with "Graphics" (other features use "Graphics.&lt;Area&gt;"), except this feature's own section and key bindings.
/// Nothing here knows about specific graphics features, so new ones are covered automatically.
/// </summary>
internal static class GraphicsCatalog
{
    public const string Prefix = "Graphics";

    /// <summary>A section/key → serialized value map, the payload of a profile.</summary>
    internal sealed class Values : Dictionary<string, Dictionary<string, string>>
    {
        public Values() : base(StringComparer.Ordinal) { }

        public int Total => this.Sum(s => s.Value?.Count ?? 0);

        public bool TryGet(string section, string key, out string value)
        {
            value = null;
            return TryGetValue(section, out var keys) && keys != null && keys.TryGetValue(key, out value);
        }

        public void Set(string section, string key, string value)
        {
            if (!TryGetValue(section, out var keys) || keys == null) this[section] = keys = new Dictionary<string, string>(StringComparer.Ordinal);
            keys[key] = value;
        }
    }

    internal readonly struct ApplyResult
    {
        public readonly int Changed, Unchanged, Unknown, Failed;
        public ApplyResult(int changed, int unchanged, int unknown, int failed)
        {
            Changed = changed; Unchanged = unchanged; Unknown = unknown; Failed = failed;
        }
        public string Describe()
        {
            string s = $"{Changed} setting{(Changed == 1 ? "" : "s")} changed";
            if (Unknown > 0) s += $", {Unknown} not installed";
            if (Failed > 0) s += $", {Failed} invalid";
            return s;
        }
    }

    public static bool IsGraphicsSection(string section) =>
        section != null
        && section.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)
        && !string.Equals(section, GraphicsProfilesFeature.Section, StringComparison.OrdinalIgnoreCase);

    /// <summary>Key bindings are personal, not a look: profiles never carry them.</summary>
    public static bool IsProfileable(ConfigEntryBase e)
    {
        if (e?.Definition == null || !IsGraphicsSection(e.Definition.Section)) return false;
        var t = e.SettingType;
        if (t.Name == "KeyboardShortcut" || t == typeof(UnityEngine.InputSystem.Key) || t == typeof(UnityEngine.KeyCode)) return false;
        string k = e.Definition.Key ?? "";
        if (k.EndsWith("Key", StringComparison.OrdinalIgnoreCase) || k.EndsWith("Hotkey", StringComparison.OrdinalIgnoreCase)
            || k.EndsWith("Keybind", StringComparison.OrdinalIgnoreCase) || k.EndsWith("Shortcut", StringComparison.OrdinalIgnoreCase))
            return false;
        return true;
    }

    /// <summary>All graphics entries currently bound, sorted by section then key.</summary>
    public static List<ConfigEntryBase> Entries()
    {
        var list = new List<ConfigEntryBase>();
        var file = ModConfig.File;
        if (file == null) return list;
        try
        {
            foreach (var kv in file)
                if (IsProfileable(kv.Value)) list.Add(kv.Value);
        }
        catch (Exception e) { LogOnce("enumerate", "Could not list graphics settings: " + e.Message); }
        list.Sort((a, b) =>
        {
            int c = string.Compare(a.Definition.Section, b.Definition.Section, StringComparison.OrdinalIgnoreCase);
            return c != 0 ? c : string.Compare(a.Definition.Key, b.Definition.Key, StringComparison.OrdinalIgnoreCase);
        });
        return list;
    }

    public static ConfigEntryBase Find(string section, string key)
    {
        var file = ModConfig.File;
        if (file == null || section == null || key == null) return null;
        try
        {
            var def = new ConfigDefinition(section, key);
            return ((IDictionary<ConfigDefinition, ConfigEntryBase>)file).TryGetValue(def, out var e) && IsProfileable(e) ? e : null;
        }
        catch { return null; }
    }

    public static Values Snapshot()
    {
        var v = new Values();
        foreach (var e in Entries())
        {
            try { v.Set(e.Definition.Section, e.Definition.Key, e.GetSerializedValue()); }
            catch { /* unserializable entry: leave it out */ }
        }
        return v;
    }

    /// <summary>The defaults of every graphics entry, as a profile.</summary>
    public static Values Defaults()
    {
        var v = new Values();
        foreach (var e in Entries())
        {
            try { v.Set(e.Definition.Section, e.Definition.Key, TomlTypeConverter.ConvertToString(e.DefaultValue, e.SettingType)); }
            catch { }
        }
        return v;
    }

    /// <summary>
    /// Writes the given values into the bound entries. Only graphics sections are ever touched, whatever the
    /// profile contains (a shared profile cannot switch on anything else). Entries missing from the profile stay.
    /// </summary>
    public static ApplyResult Apply(Values values)
    {
        int changed = 0, same = 0, unknown = 0, failed = 0;
        if (values == null) return default;
        var file = ModConfig.File;
        bool saveOnSet = file != null && file.SaveOnConfigSet;
        try
        {
            if (file != null) file.SaveOnConfigSet = false;
            foreach (var section in values)
            {
                if (section.Value == null) continue;
                if (!IsGraphicsSection(section.Key)) { unknown += section.Value.Count; continue; }
                foreach (var kv in section.Value)
                {
                    var e = Find(section.Key, kv.Key);
                    if (e == null) { unknown++; continue; }
                    try
                    {
                        string before = e.GetSerializedValue();
                        if (string.Equals(before, kv.Value, StringComparison.Ordinal)) { same++; continue; }
                        e.SetSerializedValue(kv.Value ?? "");
                        if (string.Equals(e.GetSerializedValue(), before, StringComparison.Ordinal)) same++;
                        else changed++;
                    }
                    catch (Exception ex)
                    {
                        failed++;
                        Plugin.Logger.LogDebug($"Graphics profile: {section.Key}/{kv.Key} = '{kv.Value}' rejected: {ex.Message}");
                    }
                }
            }
        }
        finally
        {
            if (file != null)
            {
                file.SaveOnConfigSet = saveOnSet;
                try { file.Save(); } catch (Exception e) { LogOnce("save", "Could not save the config: " + e.Message); }
            }
        }
        return new ApplyResult(changed, same, unknown, failed);
    }

    /// <summary>A short stable fingerprint of the values (to show "modified since applied").</summary>
    public static string Fingerprint(Values v)
    {
        if (v == null) return "";
        unchecked
        {
            ulong h = 1469598103934665603UL;
            foreach (var s in v.OrderBy(x => x.Key, StringComparer.Ordinal))
            {
                if (s.Value == null) continue;
                foreach (var kv in s.Value.OrderBy(x => x.Key, StringComparer.Ordinal))
                {
                    foreach (char c in s.Key) h = (h ^ c) * 1099511628211UL;
                    h = (h ^ '/') * 1099511628211UL;
                    foreach (char c in kv.Key) h = (h ^ c) * 1099511628211UL;
                    h = (h ^ '=') * 1099511628211UL;
                    foreach (char c in kv.Value ?? "") h = (h ^ c) * 1099511628211UL;
                    h = (h ^ '\n') * 1099511628211UL;
                }
            }
            return h.ToString("x16");
        }
    }

    /// <summary>"Graphics.Sky" → "Sky"; plain "Graphics" stays.</summary>
    public static string AreaOf(string section)
    {
        if (section == null) return "";
        if (section.Length > Prefix.Length && (section[Prefix.Length] == '.' || section[Prefix.Length] == '_' || section[Prefix.Length] == ' '))
            return section.Substring(Prefix.Length + 1);
        return section.Length > Prefix.Length ? section.Substring(Prefix.Length) : section;
    }

    private static readonly HashSet<string> Logged = new();
    internal static void LogOnce(string id, string message)
    {
        if (Logged.Add(id)) Plugin.Logger.LogWarning(message);
    }
}
