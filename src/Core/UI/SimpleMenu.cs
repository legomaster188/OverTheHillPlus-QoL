using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using OverTheHillPlus.Features;
using OverTheHillPlus.UI.GameMenu;
using OverTheHillPlus.UI.Native;
using UnityEngine;

namespace OverTheHillPlus.UI;

/// <summary>
/// Game-style option rows, drawn like the game's own option screens (Server Browser > Create): a flat translucent
/// row per setting, label on the left, "&lt; value &gt;" on the right, the selected row in the game's yellow with dark
/// text. Mods draw their everyday settings with these on their MODS page (ModInfo.DrawSimple); the MODS list uses
/// <see cref="ListRow"/>. Rows read and write ConfigEntries directly, so nothing is duplicated. Works in both menu
/// styles; in the native style the rows are keyboard / gamepad selectable (MenuNav: up/down select, left/right
/// change, Enter / A cycles or presses).
///
/// Pages: <see cref="Page"/> adds a named page of rows that a mod's DrawSimple can draw with <see cref="DrawPage"/>;
/// a DrawSimple with several pages of its own draws each under a title with <see cref="TitledPage"/>. Rows are drawn with the helpers below inside the
/// page's content area (the same coordinate system as <see cref="Menu"/>'s cards).
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
        public bool BuiltIn;
        public Action Draw;
        public string Owner;
    }

    private static readonly Dictionary<int, List<string>> TabLists = new();
    private static readonly Dictionary<int, int> BuiltInTabCount = new();
    private static readonly Dictionary<(int, string), List<PageEntry>> Pages = new();
    private static readonly Dictionary<(int, string), string> Aliases = new();
    private static readonly List<string> NoTabs = new();
    private static bool _inBuiltIn;

    /// <summary>
    /// Runs <paramref name="register"/> now; the pages it adds are built-in ones: their tabs go before every tab added
    /// by other pages (whenever those were added, e.g. by core before another mod loaded), and in a shared tab they
    /// draw before other pages of the same order.
    /// </summary>
    internal static void BuiltIn(Action register)
    {
        if (register == null) return;
        _inBuiltIn = true;
        try { register(); }
        catch (Exception e) { Core.CorePlugin.Logger?.LogError("Simple menu pages failed to register: " + e); }
        finally { _inBuiltIn = false; }
    }

    /// <summary>
    /// Adds rows to a page (a new name adds a page after the existing ones). Pages of a tab draw by
    /// <paramref name="order"/>. Use the row helpers of this class (Toggle, Choice, Button, Text, Heading).
    /// </summary>
    internal static void Page(int mode, string tab, Action draw, int order = 0)
    {
        bool builtIn = _inBuiltIn;
        if (!TabLists.TryGetValue(mode, out var tabs)) TabLists[mode] = tabs = new List<string>();
        if (!tabs.Contains(tab))
        {
            BuiltInTabCount.TryGetValue(mode, out int nBuiltIn);
            if (builtIn)
            {
                tabs.Insert(Math.Min(nBuiltIn, tabs.Count), tab);
                BuiltInTabCount[mode] = nBuiltIn + 1;
            }
            else tabs.Add(tab);
        }
        if (!Pages.TryGetValue((mode, tab), out var list)) Pages[(mode, tab)] = list = new List<PageEntry>();
        // After every page that sorts before or with it (by order, built-ins first): equal ones keep registration order.
        int at = list.Count;
        while (at > 0 && After(list[at - 1], order, builtIn)) at--;
        list.Insert(at, new PageEntry { Order = order, BuiltIn = builtIn, Draw = draw, Owner = draw?.Method?.DeclaringType?.Name ?? "?" });
    }

    private static bool After(PageEntry e, int order, bool builtIn) =>
        e.Order > order || (e.Order == order && !e.BuiltIn && builtIn);

    /// <summary>Opening the menu on Advanced tab <paramref name="source"/> (e.g. "Lobby") shows this Simple tab instead.</summary>
    internal static void Alias(int mode, string source, string tab) => Aliases[(mode, source)] = tab;

    internal static List<string> TabsFor(int mode) => TabLists.TryGetValue(mode, out var tabs) ? tabs : NoTabs;

    /// <summary>Index of a Simple tab by its name or an alias (-1 if none).</summary>
    internal static int IndexFor(int mode, string name)
    {
        var tabs = TabsFor(mode);
        int i = tabs.IndexOf(name);
        if (i < 0 && name != null && Aliases.TryGetValue((mode, name), out var alias)) i = tabs.IndexOf(alias);
        return i;
    }

    // ---------------------------------------------------------------- drawing a page

    /// <summary>Draws every page registered for <paramref name="tab"/> (nothing when there are none).</summary>
    internal static void DrawPage(int mode, string tab)
    {
        if (!Pages.TryGetValue((mode, tab), out var list) || list.Count == 0) return;
        Menu.Y += 2f;
        foreach (var p in list)
        {
            try { p.Draw(); }
            catch (Exception e) when (!IsNativeError(e)) { DebugLog.Error("core", e, "SimpleMenu.DrawPage"); Note($"<color=#E5534B>{p.Owner} failed: {e.Message}</color>"); }
        }
        Menu.Y += 4f;
    }

    /// <summary>
    /// Draws a mod's own page of rows under a title (a heading on its MODS page, as for its Advanced groups), for a
    /// DrawSimple that shows several pages. A page that throws shows the error and the next one still draws.
    /// </summary>
    internal static void TitledPage(string title, Action draw, bool first)
    {
        ModPageView.GroupTitle(title, first);
        Menu.Y += 2f;
        try { draw?.Invoke(); }
        catch (Exception e) when (!IsNativeError(e)) { DebugLog.Error("core", e, "SimpleMenu.TitledPage"); Note($"<color=#E5534B>{title} failed: {e.Message}</color>"); }
        Menu.Y += 4f;
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
        if (RowRecorder.Recording)
        {
            Recorded.Choice(label, hint, index, labels, set, enabled, shown, wrap, from);
            return;
        }
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
            int next = ChoiceStep.Next(index, labels.Length, step, cycle, wrap, from);
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

    // ---------------------------------------------------------------- colours

    private static readonly Dictionary<string, (string text, string forHex)> ColorCodes = new();
    private static readonly Dictionary<(Color[], bool), string[]> ColorLabels = new();

    /// <summary>
    /// A colour setting as two rows: "◂ colour ▸" over <paramref name="palette"/> (each entry with a swatch and its
    /// name, <paramref name="names"/> or a plain name from its hue; a colour that is none of them shows its code), then
    /// a text box with its code (#RRGGBB) to type any colour. <paramref name="set"/> gets the colour picked or typed.
    /// </summary>
    internal static void ColorChoice(string label, string hint, Color current, Color[] palette, string[] names, Action<Color> set, bool enabled = true)
    {
        if (palette == null || palette.Length == 0 || set == null) return;
        bool game = RowRecorder.Recording;
        if (!ColorLabels.TryGetValue((palette, game), out var labels))
        {
            labels = new string[palette.Length];
            for (int i = 0; i < palette.Length; i++)
            {
                var c = palette[i];
                string name = names != null && i < names.Length && !string.IsNullOrEmpty(names[i]) ? names[i] : ColorText.Name(c.r, c.g, c.b);
                string hex = ColorText.Hex(c.r, c.g, c.b);
                labels[i] = game ? ColorText.Swatch(hex) + " " + name : $"<color=#{hex}>{name}</color>";
            }
            ColorLabels[(palette, game)] = labels;
        }
        int index = -1;
        for (int i = 0; i < palette.Length && index < 0; i++)
        {
            var p = palette[i];
            if (Mathf.Abs(p.r - current.r) <= 0.02f && Mathf.Abs(p.g - current.g) <= 0.02f && Mathf.Abs(p.b - current.b) <= 0.02f) index = i;
        }
        string currentHex = ColorText.Hex(current.r, current.g, current.b);
        string shown = index >= 0 ? null : (game ? ColorText.Swatch(currentHex) + " " : $"<color=#{currentHex}>") + "#" + currentHex + (game ? "" : "</color>");
        Choice(label, hint, index, labels, i => set(palette[i]), enabled, shown, wrap: true);

        // The code box keeps what is typed until it is a colour (or the colour changes elsewhere).
        if (!ColorCodes.TryGetValue(label, out var code) || code.forHex != currentHex) code = ("#" + currentHex, currentHex);
        string typed = Text(label + " code", code.text, 7, "Type any colour as #RRGGBB.", enabled);
        if (typed != code.text && ColorText.TryParse(typed, out float r, out float g, out float b))
        {
            string hex = ColorText.Hex(r, g, b);
            if (hex != currentHex) set(new Color(r, g, b, current.a));
            code = (typed, hex);
        }
        else code = (typed, code.forHex);
        ColorCodes[label] = code;
    }

    /// <summary>A row that does something when pressed. <paramref name="value"/> shows on the right.</summary>
    internal static bool Button(string label, string value, string hint, Action action, bool enabled = true)
    {
        if (RowRecorder.Recording) return Recorded.Button(label, value, hint, action, enabled);
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

    /// <summary>A row with a text box on the right. Returns the (possibly edited) text; a box that isn't
    /// <paramref name="enabled"/> shows its text and can't be edited.</summary>
    internal static string Text(string label, string value, int maxLength, string hint, bool enabled = true)
    {
        if (RowRecorder.Recording) return Recorded.Text(label, value, maxLength, hint, Recorded.RowH, enabled);
        var row = NextRow();
        bool lit = Select(row, NavKind.Text, enabled, hint, out bool act, out _);
        Back(row, lit);
        Label(LabelRect(row), label, lit);
        var box = ValueRect(row);
        var field = new Rect(box.x - 20f, row.y + 7f, box.width + 20f, row.height - 14f);
        string name = "otp.simple.text." + Menu.NextTextId();
        GUI.SetNextControlName(name);
        bool wasEnabled = GUI.enabled;
        GUI.enabled = wasEnabled && enabled;
        string v = GUI.TextField(field, value ?? "", maxLength, Theme.TextField);
        GUI.enabled = wasEnabled;
        if (act && enabled) GUI.FocusControl(name);
        return enabled ? v : value;
    }

    /// <summary>
    /// A row with a label and small buttons on the right (e.g. per-player actions). The entry is identified by its
    /// label as it is; for a label with live values in it (a distance, a count) use <see cref="ActionsFor"/>.
    /// </summary>
    internal static void Actions(string label, params (string text, Action action, bool enabled)[] items) => ActionsFor(label, label, items);

    /// <summary>
    /// <see cref="Actions"/> for an entry identified by <paramref name="id"/> (its name, a stable key), whatever
    /// <paramref name="label"/> shows: on the game rows a press goes to that entry's action even while the label's
    /// distance or count changes, and never to another entry's ("Spot 1" vs "Spot 2").
    /// </summary>
    internal static void ActionsFor(string id, string label, params (string text, Action action, bool enabled)[] items)
    {
        if (RowRecorder.Recording)
        {
            // One game button row per action, each named after the row ("Player ▸ Kick").
            foreach (var it in items)
                if (Recorded.Button(label, it.text, null, null, it.enabled, (id ?? label ?? "") + "\u0001" + it.text)) it.action?.Invoke();
            return;
        }
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

    private const float ListRowH = 52f, ListValueW = 250f;

    /// <summary>
    /// A row for one entry of a list (e.g. a mod on the MODS page): <paramref name="label"/>, then a small
    /// <paramref name="badge"/> and a muted <paramref name="detail"/> under it; "◂ value ▸" on the right.
    /// When <paramref name="canStep"/>, left / right (or a click on an arrow) give <paramref name="step"/> -1 / +1;
    /// otherwise the value shows without arrows. Returns true when the row itself is pressed (Enter / A, or a click
    /// anywhere else on it).
    /// </summary>
    internal static bool ListRow(string label, string detail, string badge, string value, bool valueOn, string hint, bool canStep, out int step,
        GameMenu.CardInfo card)
    {
        // The MOD BROWSER shows a row with a card as one of its own mod cells; drawn pages show the row.
        if (RowRecorder.Recording) return Recorded.ListRow(label, detail, badge, value, valueOn, hint, canStep, out step, card);
        return ListRow(label, detail, badge, value, valueOn, hint, canStep, out step);
    }

    internal static bool ListRow(string label, string detail, string badge, string value, bool valueOn, string hint, bool canStep, out int step)
    {
        if (RowRecorder.Recording) return Recorded.ListRow(label, detail, badge, value, valueOn, hint, canStep, out step);
        step = 0;
        var row = new Rect(0f, Menu.Y, Menu.ContentWidth, ListRowH);
        Menu.Y += ListRowH + Gap;
        bool lit = Select(row, NavKind.Switch, true, hint, out bool act, out int side);
        Back(row, lit);
        var box = new Rect(row.xMax - Side - ListValueW, row.y, ListValueW, row.height);
        float textW = row.width - Side * 2f - ListValueW - 12f;
        bool sub2 = !string.IsNullOrEmpty(badge) || !string.IsNullOrEmpty(detail);
        Label(sub2 ? new Rect(row.x + Side, row.y + 4f, textW, 26f) : new Rect(row.x + Side, row.y, textW, row.height), label, lit);

        float x = row.x + Side;
        var sub = new Rect(x, row.y + 30f, textW, 16f);
        if (!string.IsNullOrEmpty(badge))
        {
            float bw = Mathf.Max(34f, 12f + badge.Length * 6.6f);
            Badge(new Rect(x, sub.y, bw, sub.height), badge, lit);
            x += bw + 8f;
        }
        if (!string.IsNullOrEmpty(detail)) Small(new Rect(x, sub.y - 1f, row.xMax - Side - ListValueW - 12f - x, sub.height + 2f), detail, lit);

        var prev = GUI.color;
        if (!valueOn && !lit) GUI.color = Mul(prev, new Color(1f, 1f, 1f, 0.7f));
        try
        {
            if (canStep)
            {
                Value(new Rect(box.x + Arrow, box.y, box.width - Arrow * 2, box.height), value, lit, true);
                ArrowText(new Rect(box.x, box.y, Arrow, box.height), "◂", lit, true);
                ArrowText(new Rect(box.xMax - Arrow, box.y, Arrow, box.height), "▸", lit, true);
            }
            else Value(new Rect(box.x, box.y, box.width - Arrow, box.height), value, lit, true, TextAnchor.MiddleRight);
        }
        finally { GUI.color = prev; }

        // Arrows first: IMGUI hands a click to the first button that takes it.
        bool left = canStep && GUI.Button(new Rect(box.x - 4f, row.y, Arrow + 8f, row.height), GUIContent.none, Theme.Invisible);
        bool right = canStep && GUI.Button(new Rect(box.xMax - Arrow - 4f, row.y, Arrow + 8f, row.height), GUIContent.none, Theme.Invisible);
        bool click = GUI.Button(row, GUIContent.none, Theme.Invisible);
        step = left ? -1 : right ? 1 : canStep ? side : 0;
        if (step != 0 && Menu.NativeActive) NativeLook.PlayOption();
        bool pressed = !left && !right && (click || act);
        if (pressed && Menu.NativeActive) NativeLook.PlayPress();
        return pressed;
    }

    private static void Badge(Rect r, string text, bool lit)
    {
        var back = lit ? new Color(Ink.r, Ink.g, Ink.b, 0.85f) : new Color(1f, 1f, 1f, 0.14f);
        var color = lit ? Yellow : NativeLook.Text;
        if (Menu.NativeActive)
        {
            if (!NativeCanvas.Emitting) return;
            NativeCanvas.Fill(r, back, NativeLook.Pill ?? NativeLook.Square, Depth.Front);
            NativeCanvas.Text(r, text, new TextSpec(9.5f, color, TextAnchor.MiddleCenter, bold: true, heading: true, upper: true));
            return;
        }
        Theme.Fill(r, back, 7);
        Draw(r, text.ToUpperInvariant(), 9f, lit ? Yellow : Theme.Text, TextAnchor.MiddleCenter, true);
    }

    private static void Small(Rect r, string text, bool lit)
    {
        var color = lit ? new Color(Ink.r, Ink.g, Ink.b, 0.8f) : NativeLook.Muted;
        if (Menu.NativeActive)
        {
            if (NativeCanvas.Emitting) NativeCanvas.Text(r, text, new TextSpec(11.5f, color, TextAnchor.MiddleLeft));
            return;
        }
        Draw(r, text, 11f, lit ? color : Theme.Muted, TextAnchor.MiddleLeft, false);
    }

    /// <summary>A small upper-case group title above a few rows.</summary>
    internal static void Heading(string text)
    {
        if (RowRecorder.Recording) { Recorded.Heading(text); return; }
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
        if (RowRecorder.Recording) { Recorded.Note(text); return; }
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
