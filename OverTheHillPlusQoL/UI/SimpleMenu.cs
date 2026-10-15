using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using OverTheHillPlus.Features;
using OverTheHillPlus.UI.Native;
using UnityEngine;

namespace OverTheHillPlus.UI;

/// <summary>
/// The menu's Simple view: a few short pages of big option rows drawn like the game's own option screens (Server
/// Browser > Create): a flat translucent row per setting, label on the left, "&lt; value &gt;" on the right, the
/// selected row in the game's yellow with dark text. Rows read and write the same ConfigEntries as the Advanced view,
/// so nothing is duplicated. Works in both menu styles; in the native style the rows are keyboard / gamepad
/// selectable (MenuNav: up/down select, left/right change, Enter / A cycles or presses).
///
/// Pages: <see cref="Page"/> adds rows to a Simple tab (built-in pages: UI/SimplePages.cs). Rows are drawn with the
/// helpers below inside the menu's content area (the same coordinate system as <see cref="Menu"/>'s cards).
/// </summary>
internal static class SimpleMenu
{
    internal static readonly Color Yellow = Theme.Hex(0xFFD865);
    internal static readonly Color Ink = Theme.Hex(0x16181C);
    private static readonly Color RowBack = new(1f, 1f, 1f, 0.07f);
    private static readonly Color Disabled = new(1f, 1f, 1f, 0.42f);

    private const float RowH = 42f, Gap = 5f, Side = 16f, ValueW = 200f, Arrow = 26f;

    // ---------------------------------------------------------------- pages

    private sealed class PageEntry
    {
        public int Order;
        public Action Draw;
        public string Owner;
    }

    private static readonly Dictionary<int, List<string>> TabLists = new();
    private static readonly Dictionary<(int, string), List<PageEntry>> Pages = new();
    private static readonly Dictionary<(int, string), string> Aliases = new();
    private static readonly List<string> NoTabs = new();
    private static bool _builtIns;

    private static void EnsureBuiltIns()
    {
        if (_builtIns) return;
        _builtIns = true;
        try { SimplePages.Register(); }
        catch (Exception e) { Plugin.Logger.LogError("Simple menu pages failed to register: " + e); }
    }

    /// <summary>
    /// Adds rows to a Simple-view tab (a new name adds a tab after the existing ones). Pages of a tab draw by
    /// <paramref name="order"/>. Use the row helpers of this class (Toggle, Choice, Button, Text, Heading).
    /// </summary>
    internal static void Page(int mode, string tab, Action draw, int order = 0)
    {
        EnsureBuiltIns();
        if (!TabLists.TryGetValue(mode, out var tabs)) TabLists[mode] = tabs = new List<string>();
        if (!tabs.Contains(tab)) tabs.Add(tab);
        if (!Pages.TryGetValue((mode, tab), out var list)) Pages[(mode, tab)] = list = new List<PageEntry>();
        // After every page with the same or a lower order: equal orders keep registration order.
        int at = list.Count;
        while (at > 0 && list[at - 1].Order > order) at--;
        list.Insert(at, new PageEntry { Order = order, Draw = draw, Owner = draw?.Method?.DeclaringType?.Name ?? "?" });
    }

    /// <summary>Opening the menu on Advanced tab <paramref name="source"/> (e.g. "Lobby") shows this Simple tab instead.</summary>
    internal static void Alias(int mode, string source, string tab) => Aliases[(mode, source)] = tab;

    internal static List<string> TabsFor(int mode)
    {
        EnsureBuiltIns();
        return TabLists.TryGetValue(mode, out var tabs) ? tabs : NoTabs;
    }

    /// <summary>Index of a Simple tab by its name or an alias (-1 if none).</summary>
    internal static int IndexFor(int mode, string name)
    {
        var tabs = TabsFor(mode);
        int i = tabs.IndexOf(name);
        if (i < 0 && name != null && Aliases.TryGetValue((mode, name), out var alias)) i = tabs.IndexOf(alias);
        return i;
    }

    // ---------------------------------------------------------------- drawing a page

    private static int _texts;

    internal static void DrawPage(int mode, string tab)
    {
        _texts = 0;
        Menu.Y += 2f;
        if (!Pages.TryGetValue((mode, tab), out var list) || list.Count == 0)
        {
            Note("Nothing here yet. Everything is in the Advanced view (switch at the top).");
            return;
        }
        foreach (var p in list)
        {
            try { p.Draw(); }
            catch (Exception e) when (!IsNativeError(e)) { Note($"<color=#E5534B>{p.Owner} failed: {e.Message}</color>"); }
        }
        Menu.Y += 4f;
        Note("More settings: switch to Advanced at the top.");
    }

    private static bool IsNativeError(Exception e)
    {
        if (!Menu.NativeActive) return false;
        string st = e.StackTrace ?? "";
        return st.Contains("UI.Native.") || st.Contains("NativeCanvas");
    }

    // ---------------------------------------------------------------- row helpers

    /// <summary>On / off row for a bool setting.</summary>
    internal static void Toggle(ConfigEntry<bool> entry, string label, string hint, bool enabled = true)
    {
        if (entry == null) return;
        Choice(label, hint, entry.Value ? 1 : 0, OffOn, i => entry.Value = i == 1, enabled);
    }

    private static readonly string[] OffOn = { "Off", "On" };

    /// <summary>
    /// "&lt; value &gt;" row. <paramref name="index"/> is the current choice (-1 = something else, shown as
    /// <paramref name="shown"/> or "Custom"); <paramref name="set"/> gets the new index. The arrows stop at the ends
    /// unless <paramref name="wrap"/>; Enter / a click on the row always goes round. From -1, stepping starts at
    /// <paramref name="from"/> (pressing it there re-applies that choice), or at the first / last choice.
    /// </summary>
    internal static void Choice(string label, string hint, int index, string[] labels, Action<int> set, bool enabled = true,
        string shown = null, bool wrap = true, int from = -1)
    {
        var row = NextRow();
        bool lit = Select(row, NavKind.Switch, enabled, hint, out bool act, out int side);
        var prev = GUI.color;
        if (!enabled) GUI.color = Mul(prev, Disabled);
        try
        {
            Back(row, lit);
            Label(LabelRect(row), label, lit);
            var box = ValueRect(row);
            string value = shown ?? (index >= 0 && index < labels.Length ? labels[index] : "Custom");
            Value(new Rect(box.x + Arrow, box.y, box.width - Arrow * 2, box.height), value, lit, true);
            bool canLeft = wrap || index != 0, canRight = wrap || index != labels.Length - 1;
            ArrowText(new Rect(box.x, box.y, Arrow, box.height), "<", lit, canLeft);
            ArrowText(new Rect(box.xMax - Arrow, box.y, Arrow, box.height), ">", lit, canRight);

            // Arrows first: IMGUI hands a click to the first button that takes it.
            bool left = GUI.Button(new Rect(box.x - 4f, row.y, Arrow + 8f, row.height), GUIContent.none, Theme.Invisible);
            bool right = GUI.Button(new Rect(box.xMax - Arrow - 4f, row.y, Arrow + 8f, row.height), GUIContent.none, Theme.Invisible);
            bool click = GUI.Button(row, GUIContent.none, Theme.Invisible);
            bool cycle = !left && !right && (click || act); // Enter / click: always goes round
            int step = left ? -1 : right ? 1 : cycle ? 1 : side;
            if (!enabled || step == 0 || labels.Length == 0) return;
            int n = labels.Length;
            int at = index >= 0 ? index : from >= 0 && from < n ? from : -1;
            int next = at < 0 ? (step > 0 ? 0 : n - 1)
                : wrap || cycle ? ((at + step) % n + n) % n
                : Mathf.Clamp(at + step, 0, n - 1);
            if (next == index) return;
            set(next);
            if (Menu.NativeActive) NativeLook.PlayOption();
        }
        finally { GUI.color = prev; }
    }

    /// <summary>Choice over a list of whole numbers; a value between two steps moves to its neighbour.</summary>
    internal static void IntChoice(string label, string hint, int current, int[] values, Func<int, string> format, Action<int> set,
        bool enabled = true)
    {
        if (!IntSteps.TryGetValue(values, out var steps))
        {
            steps = new float[values.Length];
            for (int i = 0; i < values.Length; i++) steps[i] = values[i];
            IntSteps[values] = steps;
        }
        FloatChoice(label, hint, current, steps, v => format(Mathf.RoundToInt(v)), v => set(Mathf.RoundToInt(v)), enabled);
    }

    private static readonly Dictionary<int[], float[]> IntSteps = new();
    private static readonly Dictionary<(float[], string), string[]> LabelCache = new();

    /// <summary>Choice over a list of numbers for a float setting (matched within 0.005); a value between two
    /// steps (set in the Advanced view) moves to its neighbour.</summary>
    internal static void FloatChoice(string label, string hint, float current, float[] values, Func<float, string> format, Action<float> set,
        bool enabled = true)
    {
        // Labels are pure functions of the steps: build them once per row, not on every IMGUI event.
        if (!LabelCache.TryGetValue((values, label), out var labels))
        {
            labels = new string[values.Length];
            for (int i = 0; i < values.Length; i++) labels[i] = format(values[i]);
            LabelCache[(values, label)] = labels;
        }
        int index = -1;
        for (int i = 0; i < values.Length; i++)
            if (Mathf.Abs(values[i] - current) < 0.005f) { index = i; break; }
        if (index >= 0)
        {
            Choice(label, hint, index, labels, i => set(values[i]), enabled, wrap: false);
            return;
        }
        int below = -1, above = -1;
        for (int i = 0; i < values.Length; i++)
        {
            if (values[i] < current) below = i;
            if (values[i] > current && above < 0) above = i;
        }
        // From "custom" Choice steps right to index 0 and left to the last index: map those to the neighbours.
        Choice(label, hint, -1, labels, i => set(values[i == 0 ? (above >= 0 ? above : values.Length - 1) : (below >= 0 ? below : 0)]),
            enabled, format(current), wrap: false);
    }

    /// <summary>A row that does something when pressed. <paramref name="value"/> shows on the right.</summary>
    internal static bool Button(string label, string value, string hint, Action action, bool enabled = true)
    {
        var row = NextRow();
        bool lit = Select(row, NavKind.Button, enabled, hint, out bool act, out _);
        var prev = GUI.color;
        if (!enabled) GUI.color = Mul(prev, Disabled);
        try
        {
            Back(row, lit);
            Label(LabelRect(row), label, lit);
            var box = ValueRect(row);
            if (!string.IsNullOrEmpty(value)) Value(new Rect(box.x, box.y, box.width - Arrow, box.height), value, lit, false, TextAnchor.MiddleRight);
            ArrowText(new Rect(box.xMax - Arrow, box.y, Arrow, box.height), ">", lit, enabled);
            bool click = GUI.Button(row, GUIContent.none, Theme.Invisible);
            if (!enabled || !(click || act)) return false;
            if (Menu.NativeActive) NativeLook.PlayPress();
            action?.Invoke();
            return true;
        }
        finally { GUI.color = prev; }
    }

    /// <summary>A row with a text box on the right. Returns the (possibly edited) text.</summary>
    internal static string Text(string label, string value, int maxLength, string hint)
    {
        var row = NextRow();
        bool lit = Select(row, NavKind.Text, true, hint, out bool act, out _);
        Back(row, lit);
        Label(LabelRect(row), label, lit);
        var box = ValueRect(row);
        var field = new Rect(box.x - 20f, row.y + 7f, box.width + 20f, row.height - 14f);
        string name = "otp.simple.text." + _texts++;
        GUI.SetNextControlName(name);
        string v = GUI.TextField(field, value ?? "", maxLength, Theme.TextField);
        if (act) GUI.FocusControl(name);
        return v;
    }

    /// <summary>
    /// A row with a label and small buttons on the right (e.g. per-player actions).
    /// </summary>
    internal static void Actions(string label, params (string text, Action action, bool enabled)[] items)
    {
        var row = NextRow();
        Back(row, false);
        const float bw = 92f, gap = 6f;
        float buttons = items.Length * bw + Math.Max(0, items.Length - 1) * gap;
        Label(new Rect(row.x + Side, row.y, row.width - Side * 2f - buttons - 10f, row.height), label, false);
        float x = row.xMax - Side + 4f - buttons;
        foreach (var it in items)
        {
            if (Menu.ChipButton(new Rect(x, row.y + 6f, bw, row.height - 12f), it.text, it.enabled)) it.action?.Invoke();
            x += bw + gap;
        }
    }

    /// <summary>A small upper-case group title above a few rows.</summary>
    internal static void Heading(string text)
    {
        Menu.Y += 6f;
        var r = new Rect(Side - 4f, Menu.Y, Menu.ContentWidth - Side * 2f, 22f);
        if (Menu.NativeActive)
        {
            if (NativeCanvas.Emitting)
                NativeCanvas.Text(r, text, new TextSpec(12.5f, NativeLook.Muted, TextAnchor.MiddleLeft, bold: true, heading: true, upper: true));
        }
        else Draw(r, text.ToUpperInvariant(), 11f, Theme.Muted, TextAnchor.MiddleLeft, true);
        Menu.Y += 26f;
    }

    /// <summary>A short muted line of explanation (wraps).</summary>
    internal static void Note(string text)
    {
        Menu.Y += 2f;
        Menu.Small(text);
    }

    // ---------------------------------------------------------------- row parts

    private static Rect NextRow()
    {
        var r = new Rect(0f, Menu.Y, Menu.ContentWidth, RowH);
        Menu.Y += RowH + Gap;
        return r;
    }

    private static Rect LabelRect(Rect row) => new(row.x + Side, row.y, row.width - Side * 2f - ValueW - 12f, row.height);

    private static Rect ValueRect(Rect row) => new(row.xMax - Side - ValueW, row.y, ValueW, row.height);

    /// <summary>Selection: keyboard / gamepad / hover in the native style, mouse hover in the classic one.</summary>
    private static bool Select(Rect row, NavKind kind, bool enabled, string hint, out bool act, out int side)
    {
        Menu.NavItem(row, kind, out bool focused, out act, out side);
        if (!Menu.NativeActive) focused = row.Contains(Event.current.mousePosition);
        if (!enabled) { act = false; side = 0; }
        if (focused && !string.IsNullOrEmpty(hint)) Menu.ShowHint(hint);
        return focused;
    }

    private static void Back(Rect row, bool lit)
    {
        if (Menu.NativeActive)
        {
            if (!NativeCanvas.Emitting) return;
            if (lit) NativeCanvas.Fill(row, Yellow, NativeLook.HighlightShape ?? NativeLook.Square, Depth.Mid);
            else NativeCanvas.Fill(row, RowBack, NativeLook.Square, Depth.Back);
            return;
        }
        Theme.Fill(row, lit ? Yellow : RowBack, 3);
    }

    private static void Label(Rect r, string text, bool lit)
    {
        var color = lit ? Ink : NativeLook.Text;
        if (Menu.NativeActive)
        {
            if (NativeCanvas.Emitting) NativeCanvas.Text(r, text, new TextSpec(15f, color, TextAnchor.MiddleLeft, heading: true, upper: true));
            return;
        }
        Draw(r, text, 14f, lit ? Ink : Theme.Text, TextAnchor.MiddleLeft, false);
    }

    private static void Value(Rect r, string text, bool lit, bool strong, TextAnchor align = TextAnchor.MiddleCenter)
    {
        var color = lit ? Ink : strong ? NativeLook.Text : NativeLook.Muted;
        if (Menu.NativeActive)
        {
            if (NativeCanvas.Emitting) NativeCanvas.Text(r, text, new TextSpec(14.5f, color, align, bold: strong, heading: true, upper: strong));
            return;
        }
        Draw(r, strong ? text.ToUpperInvariant() : text, 13f, lit ? Ink : strong ? Theme.Text : Theme.Muted, align, strong);
    }

    private static void ArrowText(Rect r, string arrow, bool lit, bool on)
    {
        var color = lit ? Ink : on ? Yellow : new Color(1f, 1f, 1f, 0.2f);
        if (!on && lit) color = new Color(Ink.r, Ink.g, Ink.b, 0.3f);
        if (Menu.NativeActive)
        {
            if (NativeCanvas.Emitting) NativeCanvas.Text(r, arrow, new TextSpec(17f, color, TextAnchor.MiddleCenter, bold: true, heading: true));
            return;
        }
        Draw(r, arrow, 15f, color, TextAnchor.MiddleCenter, true);
    }

    // ---------------------------------------------------------------- keycaps (also used by the menu's tab bar and footer)

    /// <summary>Width of a keycap for <paramref name="key"/>.</summary>
    internal static float KeycapWidth(string key) => Mathf.Max(20f, 10f + key.Length * 7.5f);

    /// <summary>A key hint drawn like the game's: a white box with the key name in dark letters.</summary>
    internal static void Keycap(Rect r, string key)
    {
        if (Menu.NativeActive)
        {
            if (!NativeCanvas.Emitting) return;
            NativeCanvas.Fill(r, new Color(1f, 1f, 1f, 0.92f), NativeLook.RoundSmall ?? NativeLook.Square, Depth.Front);
            NativeCanvas.Text(r, key, new TextSpec(11f, Ink, TextAnchor.MiddleCenter, bold: true, heading: true, upper: true));
            return;
        }
        Theme.Fill(r, new Color(1f, 1f, 1f, 0.9f), 3);
        Draw(r, key.ToUpperInvariant(), 10f, Ink, TextAnchor.MiddleCenter, true);
    }

    // ---------------------------------------------------------------- classic text

    private static GUIStyle _classic;

    private static void Draw(Rect r, string text, float size, Color color, TextAnchor align, bool bold)
    {
        if (Event.current == null || Event.current.type != EventType.Repaint || string.IsNullOrEmpty(text)) return;
        _classic ??= new GUIStyle { richText = true, clipping = TextClipping.Clip, wordWrap = false };
        _classic.fontSize = Mathf.RoundToInt(size);
        _classic.fontStyle = bold ? FontStyle.Bold : FontStyle.Normal;
        _classic.alignment = align;
        _classic.normal.textColor = color;
        GUI.Label(r, text, _classic);
    }

    private static Color Mul(Color a, Color b) => new(a.r * b.r, a.g * b.g, a.b * b.b, a.a * b.a);
}
