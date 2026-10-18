using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using BepInEx.Configuration;
using OverTheHillPlus.UI;
using UnityEngine;
using UnityEngine.InputSystem;

namespace OverTheHillPlus.Features.Public;

/// <summary>
/// Rebinds every keyboard hotkey of the mods (every <c>ConfigEntry&lt;Key&gt;</c> in the tracked config files, including
/// ones other features add later): click the key, press the new one. Escape cancels.
/// </summary>
internal static class KeybindEditor
{
    private static ConfigEntry<Key> _capturing;
    private static float _captureStarted;
    // The frame a capture ended: the Enter / A that set the key is also a press of its row, which must not start
    // another capture.
    private static int _endedFrame = -100;
    private static readonly List<ConfigEntry<Key>> Entries = new();
    private static int _entriesForCount = -1;
    private static float _entriesAt = -100f;
    private static Key[] _validKeys;
    private const float CaptureTimeout = 10f;

    internal static bool Capturing => _capturing != null;

    internal static List<ConfigEntry<Key>> All
    {
        get
        {
            var files = CoreConfig.Files;
            int count = 0;
            foreach (var f in files) count += f.Count;
            if (count != _entriesForCount || Time.unscaledTime - _entriesAt > 5f)
            {
                _entriesForCount = count;
                _entriesAt = Time.unscaledTime;
                Entries.Clear();
                try
                {
                    foreach (var file in files)
                        foreach (var kv in file)
                            if (kv.Value is ConfigEntry<Key> k) Entries.Add(k);
                    Entries.Sort((a, b) => string.CompareOrdinal(a.Definition.Section + a.Definition.Key, b.Definition.Section + b.Definition.Key));
                    // The MODS page key first, it's the one everybody looks for.
                    int menu = Entries.IndexOf(CoreConfig.OpenKey);
                    if (menu > 0) { Entries.RemoveAt(menu); Entries.Insert(0, CoreConfig.OpenKey); }
                }
                catch (Exception e) { DebugLog.Error("core", e, "KeybindEditor.All"); MenuUx.LogOnce("keybind list", e); }
            }
            return Entries;
        }
    }

    internal static void StartCapture(ConfigEntry<Key> entry)
    {
        _capturing = entry;
        _captureStarted = Time.unscaledTime;
        Menu.SetStatus($"Press a key for \"{Pretty(entry)}\" (Esc cancels)");
    }

    internal static void CancelCapture(string why = null)
    {
        if (_capturing != null) _endedFrame = Time.frameCount;
        _capturing = null;
        if (why != null) Menu.SetStatus(why);
    }

    /// <summary>Per frame, before the mod reads its hotkeys.</summary>
    internal static void Tick()
    {
        if (_capturing == null) return;
        MenuUx.SuppressHotkeysThisFrame();
        if (Time.unscaledTime - _captureStarted > CaptureTimeout) { CancelCapture("Blocked: no key pressed, rebind cancelled"); return; }
        // Menu closed or not drawn any more: drop the capture.
        if (!MenuUx.MenuVisible) { CancelCapture(); return; }

        var kb = Keyboard.current;
        if (kb == null) return;
        if (kb.escapeKey.wasPressedThisFrame) { CancelCapture("Blocked: rebind cancelled"); return; }
        _validKeys ??= BuildValidKeys(kb);
        foreach (var k in _validKeys)
        {
            bool down;
            try { down = kb[k].wasPressedThisFrame; }
            catch (Exception ex) { DebugLog.Error("core", ex, "KeybindEditor.Tick"); continue; }
            if (!down) continue;
            var entry = _capturing;
            _capturing = null;
            _endedFrame = Time.frameCount;
            entry.Value = k;
            var clash = Clashes(entry);
            Menu.SetStatus(clash.Count == 0
                ? $"{Pretty(entry)} is now {KeyName(k)}"
                : $"Blocked: {KeyName(k)} set for {Pretty(entry)}, but {string.Join(", ", clash.Select(Pretty))} also use{(clash.Count == 1 ? "s" : "")} it");
            return;
        }
    }

    private static Key[] BuildValidKeys(Keyboard kb)
    {
        var list = new List<Key>();
        foreach (Key k in Enum.GetValues(typeof(Key)))
        {
            if (k == Key.None || k == Key.Escape || k == Key.IMESelected) continue;
            try { _ = kb[k]; list.Add(k); }
            catch (Exception ex) { DebugLog.Error("core", ex, "KeybindEditor.BuildValidKeys"); /* not a real key on this keyboard layout / input system version */ }
        }
        return list.Distinct().ToArray();
    }

    internal static List<ConfigEntry<Key>> Clashes(ConfigEntry<Key> entry) =>
        All.Where(o => !ReferenceEquals(o, entry) && o.Value == entry.Value).ToList();

    /// <summary>"ChatKey" -> "Chat", "HudKey" -> "HUD", core's OpenKey -> "MODS page".</summary>
    internal static string Pretty(ConfigEntry<Key> e)
    {
        if (e != null && ReferenceEquals(e, CoreConfig.OpenKey)) return "MODS page";
        string k = e?.Definition?.Key ?? "?";
        if (k.EndsWith("Key", StringComparison.Ordinal) && k.Length > 3) k = k.Substring(0, k.Length - 3);
        if (k == "Hud") return "HUD";
        var sb = new StringBuilder();
        for (int i = 0; i < k.Length; i++)
        {
            char c = k[i];
            if (i > 0 && char.IsUpper(c) && !char.IsUpper(k[i - 1])) sb.Append(' ').Append(char.ToLowerInvariant(c));
            else sb.Append(c);
        }
        return sb.ToString();
    }

    internal static string KeyName(Key k) => k switch
    {
        Key.Backquote => "` (backquote)",
        Key.LeftCtrl => "Left Ctrl",
        Key.RightCtrl => "Right Ctrl",
        Key.LeftShift => "Left Shift",
        Key.RightShift => "Right Shift",
        Key.LeftAlt => "Left Alt",
        Key.RightAlt => "Right Alt",
        _ => k.ToString(),
    };

    /// <summary>
    /// The Keybinds card body: one row per hotkey, its key on the right ("press a key…" while it waits for the new
    /// one); pressing the row starts the capture, pressing it again (or Esc) cancels. A key that isn't its default
    /// gets a Reset row under it. Plain rows, so they show the same in the MOD BROWSER and in the full view.
    /// </summary>
    internal static void DrawRows()
    {
        var all = All;
        if (all.Count == 0) { Menu.Small("No hotkeys found."); return; }
        foreach (var entry in all)
        {
            string label = Pretty(entry);
            MenuUx.Note(label, entry.Description?.Description, entry.Value.ToString());
            bool capturing = ReferenceEquals(_capturing, entry);
            SimpleMenu.Button(label, KeyText(entry, capturing), KeyHint(entry, capturing), () =>
            {
                if (ReferenceEquals(_capturing, entry)) CancelCapture("Blocked: rebind cancelled");
                else if (Time.frameCount - _endedFrame > 1) StartCapture(entry); // not the key press that just set it
            });
            var def = entry.DefaultValue is Key dk ? dk : entry.Value;
            if (entry.Value != def)
                SimpleMenu.Button("   Reset " + label, "to " + KeyName(def), $"Puts {label} back on {KeyName(def)}.", () =>
                {
                    entry.Value = def;
                    Menu.SetStatus($"{label} reset to {KeyName(def)}");
                });
        }
        SimpleMenu.Button("Reset all keys", null, "Every hotkey back on its default.", () =>
        {
            foreach (var e in All) if (e.DefaultValue is Key d) e.Value = d;
            CancelCapture();
            Menu.SetStatus("All hotkeys reset to defaults");
        }, all.Any(e => e.DefaultValue is Key d && e.Value != d));
        Menu.Small("Hotkeys are ignored while you type in a menu text box. Saved when the menu closes.");
    }

    /// <summary>The key a row shows: the key, "!" when another hotkey uses it too, "press a key…" while capturing.</summary>
    internal static string KeyText(ConfigEntry<Key> entry, bool capturing)
    {
        if (capturing) return "press a key…";
        return Clashes(entry).Count > 0 ? $"<color=#F2B33D>{KeyName(entry.Value)} !</color>" : KeyName(entry.Value);
    }

    private static string KeyHint(ConfigEntry<Key> entry, bool capturing)
    {
        if (capturing) return "Press the new key now. Esc (or this row again) cancels.";
        var clash = Clashes(entry);
        string desc = entry.Description?.Description;
        string what = string.IsNullOrEmpty(desc) ? "" : desc + " ";
        return clash.Count > 0
            ? what + "Also used by: " + string.Join(", ", clash.Select(Pretty)) + ". Press to change it."
            : what + "Press, then press the new key. Esc cancels.";
    }
}
