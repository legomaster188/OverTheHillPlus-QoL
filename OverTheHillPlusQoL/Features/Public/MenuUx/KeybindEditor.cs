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
/// Rebinds every keyboard hotkey of the mod (every <c>ConfigEntry&lt;Key&gt;</c> in the config file, including
/// ones other features add later): click the key, press the new one. Escape cancels.
/// </summary>
internal static class KeybindEditor
{
    private static ConfigEntry<Key> _capturing;
    private static float _captureStarted;
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
            var file = ModConfig.File;
            if (file == null) return Entries;
            if (file.Count != _entriesForCount || Time.unscaledTime - _entriesAt > 5f)
            {
                _entriesForCount = file.Count;
                _entriesAt = Time.unscaledTime;
                Entries.Clear();
                try
                {
                    foreach (var kv in file)
                        if (kv.Value is ConfigEntry<Key> k) Entries.Add(k);
                    Entries.Sort((a, b) => string.CompareOrdinal(a.Definition.Section + a.Definition.Key, b.Definition.Section + b.Definition.Key));
                    // The menu key first, it's the one everybody looks for.
                    int menu = Entries.IndexOf(ModConfig.MenuKey);
                    if (menu > 0) { Entries.RemoveAt(menu); Entries.Insert(0, ModConfig.MenuKey); }
                }
                catch (Exception e) { MenuUx.LogOnce("keybind list", e); }
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
            catch { continue; }
            if (!down) continue;
            var entry = _capturing;
            _capturing = null;
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
            catch { /* not a real key on this keyboard layout / input system version */ }
        }
        return list.Distinct().ToArray();
    }

    internal static List<ConfigEntry<Key>> Clashes(ConfigEntry<Key> entry) =>
        All.Where(o => !ReferenceEquals(o, entry) && o.Value == entry.Value).ToList();

    /// <summary>"MenuKey" -> "Menu", "HudKey" -> "HUD".</summary>
    internal static string Pretty(ConfigEntry<Key> e)
    {
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

    /// <summary>The Keybinds card body.</summary>
    internal static void DrawRows()
    {
        var all = All;
        if (all.Count == 0) { Menu.Small("No hotkeys found."); return; }
        foreach (var entry in all)
        {
            string label = Pretty(entry);
            string desc = entry.Description?.Description;
            bool hit = MenuUx.Note(label, desc, entry.Value.ToString());
            float rh = string.IsNullOrEmpty(desc) ? 32f : 44f;
            var row = new Rect(Menu.Left - 6f, Menu.Y, Menu.Width + 12f, rh);
            bool capturing = ReferenceEquals(_capturing, entry);
            if (hit) MenuUx.Highlight(row);
            else if (capturing || row.Contains(Event.current.mousePosition)) Theme.Fill(row, Theme.CardHover, 6);

            float textW = Menu.Width - 220f;
            GUI.Label(new Rect(Menu.Left + 4f, Menu.Y + (rh > 32f ? 2f : 5f), textW, 22f), label, Theme.Label);
            if (!string.IsNullOrEmpty(desc))
            {
                GUI.Label(new Rect(Menu.Left + 4f, Menu.Y + 22f, textW, 18f), desc, Theme.Small);
                MenuUx.Tooltip(new Rect(Menu.Left, Menu.Y, textW, rh), desc.Length > 60 ? desc : null);
            }
            else MenuUx.Tooltip(new Rect(Menu.Left, Menu.Y, textW, rh), null);

            var clash = Clashes(entry);
            float by = Menu.Y + (rh - 28f) / 2f;
            var keyRect = new Rect(Menu.Left + Menu.Width - 206f, by, 140f, 28f);
            string keyText = capturing ? "press a key…" : clash.Count > 0 ? $"<color=#F2B33D>{KeyName(entry.Value)} !</color>" : KeyName(entry.Value);
            if (capturing) Theme.Fill(new Rect(keyRect.x - 2f, keyRect.y - 2f, keyRect.width + 4f, keyRect.height + 4f), Theme.Accent, 8);
            if (GUI.Button(keyRect, keyText, capturing ? Theme.TabActive : Theme.Chip))
            {
                if (capturing) CancelCapture("Blocked: rebind cancelled");
                else StartCapture(entry);
            }
            if (clash.Count > 0)
                MenuUx.Tooltip(keyRect, "Also used by: " + string.Join(", ", clash.Select(Pretty)));
            else MenuUx.Tooltip(keyRect, "Click, then press the new key. Esc cancels.");

            var def = entry.DefaultValue is Key dk ? dk : entry.Value;
            if (Menu.ChipButton(new Rect(Menu.Left + Menu.Width - 60f, by, 60f, 28f), "Reset", entry.Value != def))
            {
                entry.Value = def;
                Menu.SetStatus($"{label} reset to {KeyName(def)}");
            }
            Menu.Y += rh + 2f;
        }
        Menu.Y += 4f;
        Menu.Chips(2,
            ("Reset all keys", () =>
            {
                foreach (var e in All) if (e.DefaultValue is Key d) e.Value = d;
                CancelCapture();
                Menu.SetStatus("All hotkeys reset to defaults");
            }, all.Any(e => e.DefaultValue is Key d && e.Value != d)),
            (Capturing ? "Cancel rebind" : "Esc cancels a rebind", () => CancelCapture("Blocked: rebind cancelled"), Capturing));
        Menu.Small("Hotkeys are ignored while you type in a menu text box. Saved when the menu closes.");
    }
}
