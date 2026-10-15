using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using BepInEx;
using BepInEx.Configuration;
using OverTheHillPlus.UI;
using UnityEngine;
using UnityEngine.InputSystem;

namespace OverTheHillPlus.Features.Public.Settings;

/// <summary>
/// Settings export / import. "Copy my settings" turns every shareable setting into one line of text (a code) that
/// someone else pastes in with "Paste a code"; the same settings can also go through a text file. Before anything is
/// changed the import is shown as a list of changes to apply or cancel, and the last import can be undone.
///
/// The code is the BepInEx config format ("[Section]" / "Key = value"), deflated and base64-encoded after the
/// <see cref="CodePrefix"/>. Plain config text (a chunk of a .cfg file) is accepted too.
/// Left out: measured / machine-specific values, anything that looks like a secret, and personal text (server name,
/// join message) unless the sharer turns that on.
/// </summary>
internal sealed class SettingsShare : IFeature
{
    private const string CodePrefix = "OTHP1:";
    private const string ConfigSection = "SettingsShare";
    private const int MaxFieldLength = 64 * 1024;
    private const int MaxDecodedBytes = 1024 * 1024;

    private static ConfigEntry<bool> _includePersonal;

    private static string _field = "";
    private static Plan _plan;
    private static List<(ConfigEntryBase entry, string value)> _undo;
    private static bool _fileExists;
    private static float _nextFileCheck;

    private sealed class Plan
    {
        public string Source;
        public readonly List<(ConfigEntryBase entry, string from, string to)> Changes = new();
        public int Same, Unknown, Skipped, Bad;
        public int Recognised => Changes.Count + Same + Skipped;
    }

    private static string FilePath => Path.Combine(Paths.ConfigPath, "OverTheHillPlus.shared-settings.txt");

    public void Register()
    {
        _includePersonal = ModConfig.File.Bind(ConfigSection, "IncludePersonalText", false,
            "Include your server name and join message when you copy or save your settings.");
        Registry.Section(Registry.PlusMode, "QoL", Draw, 1000);
    }

    // ---- which settings travel ----

    private static readonly HashSet<string> NeverShared = new(StringComparer.OrdinalIgnoreCase)
    {
        "Multiplayer.MeasuredCapacity", // measured on this PC
        // State the mod keeps for itself (also caught by the description rule below): a remembered search is often a
        // friend's or a server's name, and importing a fingerprint would break "changed since the profile was applied".
        "BrowserRefresh.SavedSortCriteria", "BrowserRefresh.SavedSortDirection", "BrowserRefresh.SavedSearch",
        "Graphics.Profiles.LastApplied", "Graphics.Profiles.LastFingerprint",
    };

    private static readonly HashSet<string> PersonalKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "Multiplayer.CustomRoomName", "Multiplayer.JoinMessage",
    };

    private static readonly string[] SecretWords = { "password", "token", "secret", "apikey", "api_key", "steamid", "webhook" };
    private static readonly string[] PersonalWords = { "name", "message", "text", "note", "friend", "player", "path", "folder", "file", "url", "search" };
    private static readonly string[] StyleWords = { "style", "color", "colour" };

    /// <summary>Settings that are never exported or imported, whatever the switches say.</summary>
    private static bool NeverTravels(ConfigDefinition d)
    {
        if (d.Section == ConfigSection) return true;
        string id = d.Section + "." + d.Key;
        if (NeverShared.Contains(id)) return true;
        string lower = id.ToLowerInvariant();
        return SecretWords.Any(w => lower.Contains(w));
    }

    /// <summary>The entry-level rule: also never shares state the mod sets itself ("Set by the mod" / "Internal:").</summary>
    private static bool NeverTravels(ConfigEntryBase e)
    {
        if (NeverTravels(e.Definition)) return true;
        string desc = e.Description?.Description ?? "";
        return desc.StartsWith("Internal:", StringComparison.OrdinalIgnoreCase) ||
               desc.IndexOf("Set by the mod", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static bool IsPersonal(ConfigEntryBase e)
    {
        var d = e.Definition;
        if (PersonalKeys.Contains(d.Section + "." + d.Key)) return true;
        if (e.SettingType != typeof(string)) return false;
        string key = d.Key.ToLowerInvariant();
        if (StyleWords.Any(w => key.Contains(w))) return false; // colours and styles are fine to share
        return PersonalWords.Any(w => key.Contains(w));
    }

    private static bool Exported(ConfigEntryBase e)
    {
        var d = e.Definition;
        if (NeverTravels(e)) return false;
        return _includePersonal.Value || !IsPersonal(e);
    }

    // ---- export ----

    private static string BuildText(out int count)
    {
        count = 0;
        var sb = new StringBuilder();
        sb.Append("## ").Append(Plugin.Name).Append(' ').Append(Plugin.Version).Append(" settings\n");
        var entries = new List<ConfigEntryBase>();
        foreach (var kv in ModConfig.File)
            if (kv.Value != null && Exported(kv.Value)) entries.Add(kv.Value);
        foreach (var group in entries.GroupBy(e => e.Definition.Section).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            sb.Append('\n').Append('[').Append(group.Key).Append("]\n");
            foreach (var e in group.OrderBy(e => e.Definition.Key, StringComparer.Ordinal))
            {
                string value;
                try { value = e.GetSerializedValue(); }
                catch { continue; }
                sb.Append(e.Definition.Key).Append(" = ").Append(value).Append('\n');
                count++;
            }
        }
        return sb.ToString();
    }

    private static string Encode(string text)
    {
        var raw = Encoding.UTF8.GetBytes(text);
        using var ms = new MemoryStream();
        using (var deflate = new DeflateStream(ms, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true)) deflate.Write(raw, 0, raw.Length);
        return CodePrefix + Convert.ToBase64String(ms.ToArray());
    }

    // ---- import ----

    /// <summary>Turns a code (possibly inside a longer message) or plain config text into config text.</summary>
    private static string Decode(string input)
    {
        input ??= "";
        int at = input.IndexOf(CodePrefix, StringComparison.Ordinal);
        if (at < 0)
        {
            if (input.Contains('[') && input.Contains('=')) return input; // plain config text
            throw new FormatException("not a settings code");
        }
        var b64 = new StringBuilder();
        foreach (char c in input.Substring(at + CodePrefix.Length))
        {
            if (char.IsWhiteSpace(c)) { if (b64.Length > 0 && c != '\r' && c != '\n') break; continue; }
            if (char.IsLetterOrDigit(c) || c == '+' || c == '/' || c == '=') b64.Append(c);
            else break;
        }
        var packed = Convert.FromBase64String(b64.ToString());
        using var input2 = new DeflateStream(new MemoryStream(packed), CompressionMode.Decompress);
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        int read;
        while ((read = input2.Read(buffer, 0, buffer.Length)) > 0)
        {
            output.Write(buffer, 0, read);
            if (output.Length > MaxDecodedBytes) throw new FormatException("code too large");
        }
        return Encoding.UTF8.GetString(output.ToArray());
    }

    private static Plan Parse(string text, string source)
    {
        var plan = new Plan { Source = source };
        var byEntry = new Dictionary<ConfigEntryBase, string>();
        string section = null;
        foreach (var rawLine in text.Split('\n'))
        {
            string line = rawLine.Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            if (line[0] == '[' && line[line.Length - 1] == ']') { section = line.Substring(1, line.Length - 2).Trim(); continue; }
            int eq = line.IndexOf('=');
            if (eq <= 0 || section == null) { plan.Bad++; continue; }
            string key = line.Substring(0, eq).Trim(), value = line.Substring(eq + 1).Trim();

            ConfigDefinition def;
            try { def = new ConfigDefinition(section, key); }
            catch { plan.Bad++; continue; }
            if (!ModConfig.File.ContainsKey(def)) { plan.Unknown++; continue; }
            var entry = ModConfig.File[def];
            if (entry == null) { plan.Unknown++; continue; }
            if (NeverTravels(entry)) { plan.Skipped++; continue; }

            string normalised;
            try
            {
                object v = TomlTypeConverter.ConvertToValue(value, entry.SettingType);
                var acceptable = entry.Description?.AcceptableValues;
                if (acceptable != null) v = acceptable.Clamp(v);
                normalised = TomlTypeConverter.ConvertToString(v, entry.SettingType);
            }
            catch { plan.Bad++; continue; }
            byEntry[entry] = normalised; // a repeated key: the last one wins, as in BepInEx
        }

        foreach (var kv in byEntry)
        {
            string current;
            try { current = kv.Key.GetSerializedValue(); }
            catch { plan.Bad++; continue; }
            if (current == kv.Value) plan.Same++;
            else plan.Changes.Add((kv.Key, current, kv.Value));
        }
        plan.Changes.Sort((a, b) => string.CompareOrdinal(a.entry.Definition.Section + a.entry.Definition.Key,
                                                          b.entry.Definition.Section + b.entry.Definition.Key));
        return plan;
    }

    private static void Review(string input, string source)
    {
        _plan = null;
        Plan plan;
        try { plan = Parse(Decode(input), source); }
        catch (Exception e)
        {
            Menu.SetStatus($"Failed: {source} doesn't hold a settings code ({e.Message})");
            return;
        }
        if (plan.Recognised == 0)
        {
            Menu.SetStatus($"Failed: no settings for this mod found in {source}");
            return;
        }
        _plan = plan;
        Menu.SetStatus(plan.Changes.Count == 0 ? "You already have these settings" : $"Read {plan.Changes.Count} changes: check them and press Apply");
    }

    private static void Apply()
    {
        var plan = _plan;
        _plan = null;
        if (plan == null || plan.Changes.Count == 0) return;
        var undo = new List<(ConfigEntryBase, string)>();
        int applied = 0;
        Key menuKeyBefore = ModConfig.MenuKey.Value;
        foreach (var (entry, from, to) in plan.Changes)
        {
            try
            {
                entry.SetSerializedValue(to);
                if (entry.GetSerializedValue() != from) { undo.Add((entry, from)); applied++; }
            }
            catch (Exception e) { Plugin.Logger.LogWarning($"Settings import: {entry.Definition.Key}: {e.Message}"); }
        }
        // Never leave the menu without a working key.
        if (ModConfig.MenuKey.Value == Key.None) ModConfig.MenuKey.Value = menuKeyBefore == Key.None ? Key.F7 : menuKeyBefore;
        _undo = undo.Count > 0 ? undo : _undo;
        Save();
        Menu.SetStatus($"Imported {applied} settings from {plan.Source}");
    }

    private static void Undo()
    {
        var undo = _undo;
        _undo = null;
        if (undo == null) return;
        foreach (var (entry, value) in undo)
        {
            try { entry.SetSerializedValue(value); }
            catch { /* entry gone or value no longer valid: leave it */ }
        }
        Save();
        Menu.SetStatus($"Restored {undo.Count} settings");
    }

    private static void Save()
    {
        Menu.ForgetTypedText();
        try { ModConfig.File.Save(); }
        catch (Exception e) { Plugin.Logger.LogWarning("Settings import: could not save the config: " + e.Message); }
    }

    // ---- clipboard and file ----

    private static bool TryCopy(string text)
    {
        try
        {
            GUIUtility.systemCopyBuffer = text;
            return GUIUtility.systemCopyBuffer == text;
        }
        catch { return false; }
    }

    private static string TryPaste()
    {
        try { return GUIUtility.systemCopyBuffer ?? ""; }
        catch { return ""; }
    }

    private static void CopyMine()
    {
        try
        {
            string code = Encode(BuildText(out int n));
            _field = code;
            Menu.SetStatus(TryCopy(code)
                ? $"Copied {n} settings to the clipboard: paste the code anywhere to share it"
                : $"The code for {n} settings is in the box below: select it and press Ctrl+C");
        }
        catch (Exception e) { Menu.SetStatus("Failed: " + e.Message); }
    }

    private static void PasteCode()
    {
        string clip = TryPaste();
        if (string.IsNullOrWhiteSpace(clip)) { Menu.SetStatus("Failed: the clipboard is empty (or paste into the box with Ctrl+V)"); return; }
        if (clip.Length <= MaxFieldLength) _field = clip.Trim();
        Review(clip, "the clipboard");
    }

    private static void SaveFile()
    {
        try
        {
            string text = BuildText(out int n);
            File.WriteAllText(FilePath,
                "## Load this in the mod menu (QoL tab > Share settings > Load file), or copy the [sections] below into\n" +
                "## the code box. Lines starting with # are ignored.\n" + text);
            _nextFileCheck = 0f;
            Menu.SetStatus($"Saved {n} settings to BepInEx/config/{Path.GetFileName(FilePath)}");
        }
        catch (Exception e) { Menu.SetStatus("Failed: could not save the file: " + e.Message); }
    }

    private static void LoadFile()
    {
        try
        {
            var info = new FileInfo(FilePath);
            if (!info.Exists) { Menu.SetStatus("Failed: no shared settings file yet"); return; }
            if (info.Length > MaxDecodedBytes) { Menu.SetStatus("Failed: the file is too large"); return; }
            Review(File.ReadAllText(FilePath), "the file");
        }
        catch (Exception e) { Menu.SetStatus("Failed: could not read the file: " + e.Message); }
    }

    // ---- menu ----

    private static string Short(string s)
    {
        s ??= "";
        if (s.Length == 0) return "(empty)";
        s = s.Replace("<", "<​"); // show rich-text tags as text
        return s.Length > 26 ? s.Substring(0, 24) + "…" : s;
    }

    private static void Draw()
    {
        if (Time.unscaledTime >= _nextFileCheck)
        {
            _nextFileCheck = Time.unscaledTime + 2f;
            try { _fileExists = File.Exists(FilePath); } catch { _fileExists = false; }
        }

        Menu.Card("Share settings", "Copy your settings as a code anyone can paste in, or use someone else's. " +
                                    "Nothing changes until you press Apply.", () =>
        {
            Menu.Chips(2, ("Copy my settings", CopyMine, true), ("Paste a code", PasteCode, true));
            Menu.Chips(2, ("Save to file", SaveFile, true), ("Load file", LoadFile, _fileExists));
            Menu.Switch(_includePersonal, "Include server name and join message", "Off: those stay yours when you share", true);

            float y = Menu.Y;
            GUI.Label(new Rect(Menu.Left + 4f, y, 60f, 28f), "Code", Theme.Label);
            _field = GUI.TextField(new Rect(Menu.Left + 64f, y, Menu.Width - 64f - 98f, 28f), _field ?? "", MaxFieldLength, Theme.TextField);
            if (Menu.ChipButton(new Rect(Menu.Left + Menu.Width - 92f, y, 92f, 28f), "Read", !string.IsNullOrWhiteSpace(_field)))
                Review(_field, "the code box");
            Menu.Y = y + 34f;
            Menu.Small("You can also paste a code into the box (Ctrl+V) and press Read.");

            if (_plan != null) DrawPlan(_plan);
            if (_undo != null) Menu.Chips(1, ($"Undo last import ({_undo.Count} settings)", Undo, true));
        });
    }

    private static void DrawPlan(Plan p)
    {
        var notes = new List<string>();
        if (p.Same > 0) notes.Add($"{p.Same} already match");
        if (p.Unknown > 0) notes.Add($"{p.Unknown} not in your version");
        if (p.Skipped > 0) notes.Add($"{p.Skipped} kept private");
        if (p.Bad > 0) notes.Add($"{p.Bad} unreadable");
        string head = p.Changes.Count == 0 ? "nothing to change" : $"{p.Changes.Count} setting{(p.Changes.Count == 1 ? "" : "s")} will change";
        Menu.Small($"<color=#F2B33D>From {p.Source}:</color> {head}" + (notes.Count > 0 ? "  (" + string.Join(", ", notes) + ")" : ""));

        const int shown = 8;
        foreach (var (entry, from, to) in p.Changes.Take(shown))
            Menu.KeyValue(entry.Definition.Key, $"{Short(from)}  →  <color=#4CC38A>{Short(to)}</color>");
        if (p.Changes.Count > shown) Menu.Small($"…and {p.Changes.Count - shown} more.");

        Menu.Chips(2,
            (p.Changes.Count > 0 ? $"Apply {p.Changes.Count}" : "Apply", Apply, p.Changes.Count > 0),
            ("Cancel", () => { _plan = null; Menu.SetStatus("Import cancelled"); }, true));
    }
}
