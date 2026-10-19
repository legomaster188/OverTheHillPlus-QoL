using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using OverTheHillPlus.Features.Public;
using OverTheHillPlus.UI;
using OverTheHillPlus.UI.GameMenu;
using OverTheHillPlus.UI.Native;
using UnityEngine;

namespace OverTheHillPlus;

/// <summary>
/// The menu widget kit: cards, switches, sliders, labels, buttons and chips, drawn in the classic IMGUI look or the
/// game's native look (UI/Native). Pure IMGUI with Rect-based calls (GUILayout's params arrays are awkward under
/// IL2CPP). Every mod draws its settings with these; the window around them (the MODS page: tabs, header, footer)
/// drives the layout state through <see cref="BeginWindow"/>, <see cref="BeginContent"/>, <see cref="BeginGroup"/>
/// and <see cref="EndContent"/>.
/// </summary>
internal static class Menu
{
    internal const float Inset = 14f;

    // ---- state set by the window that hosts the widgets (BeginWindow / BeginContent / BeginGroup) ----

    // Layout cursor inside the content area and its width.
    private static float _y, _cw;
    // The style latched for this frame, and whether the widgets are drawing inside the scrolling content area
    // (keyboard navigation only registers rows there).
    private static bool _native;
    private static bool _inContent;
    private static int _textRows;
    // The group being drawn (card fold keys) and how many cards it has drawn so far (only the first starts open).
    private static int _drawMode;
    private static string _drawTab = "";
    // The settings tab name of the group (the fold key may carry a prefix, e.g. the mod id).
    private static string _drawTabName = "";
    private static int _cardIndex;
    private static readonly Dictionary<string, float> CardH = new();

    /// <summary>Starts a frame of the window: remembers it (<see cref="DrawnFrame"/>) and the style latched for it.</summary>
    internal static void BeginWindow(bool native)
    {
        DrawnFrame = Time.frameCount;
        _native = native;
        _inContent = false;
        _textRows = 0;
    }

    /// <summary>The scrolling content area starts: widgets lay out from y = 0 across <paramref name="width"/>.</summary>
    internal static void BeginContent(float width)
    {
        _y = 0f;
        _cw = width;
        _inContent = true;
        BeginGroup(0, "");
    }

    /// <summary>A number for a text box's control name, unique within this IMGUI pass and the same in every pass.</summary>
    internal static int NextTextId() => _textRows++;

    /// <summary>Ends the content area; returns the height the widgets used.</summary>
    internal static float EndContent()
    {
        _inContent = false;
        return _y;
    }

    /// <summary>
    /// A new group of cards (e.g. one tab's sections on a mod page): cards remember their fold state per mode, group
    /// and title, and only a group's first card starts open. <paramref name="tab"/> is the group's settings tab name
    /// when <paramref name="group"/> is more than that (e.g. "chat/Lobby" for the Lobby tab on Chat's page), so the
    /// menu settings tab stays recognised (its cards all start open).
    /// </summary>
    internal static void BeginGroup(int mode, string group, string tab = null)
    {
        _drawMode = mode;
        _drawTab = group ?? "";
        _drawTabName = tab ?? _drawTab;
        _cardIndex = 0;
    }

    /// <summary>Time.frameCount of the last menu draw; lets features tell whether the menu is open.</summary>
    internal static int DrawnFrame = -100;

    /// <summary>Set by a widget (or a feature) to ask the host to close the menu.</summary>
    public static bool CloseRequested;

    /// <summary>Tab name of the lobby settings (servers you host).</summary>
    public const string LobbyTab = "Lobby";

    /// <summary>The menu's own settings page (style, size, search, keybinds).</summary>
    internal static string SettingsTab => MenuUxFeature.Tab;

    /// <summary>True while the menu draws in the native (game) style this frame.</summary>
    internal static bool NativeActive => _native;

    /// <summary>True while visuals go to the native canvas (native style, Repaint event).</summary>
    internal static bool Emit => NativeCanvas.Emitting;

    // ---- hooks set by the menu window ----

    /// <summary>Jumps the menu to a tab by name (the settings search's Enter). The MODS page has no search box, so
    /// nothing sets it at the moment and <see cref="SelectTab"/> does nothing.</summary>
    internal static Action<string> SelectTabHook;

    /// <summary>Jumps to a tab by name (an Advanced tab, a registered tab, or the menu settings).</summary>
    public static void SelectTab(string name) => SelectTabHook?.Invoke(name);

    // ---- footer status and hint ----

    private static string _status = "";
    private static float _statusAt = -100f;
    // The selected Simple row's explanation, shown in the footer for the frame it was set in.
    private static string _hint;
    private static int _hintFrame = -1;

    /// <summary>The footer's status message while it is fresh (8 s after <see cref="SetStatus"/>).</summary>
    internal static bool TryStatus(out string status)
    {
        status = _status;
        return _status.Length > 0 && Time.unscaledTime - _statusAt < 8f;
    }

    /// <summary>The selected row's explanation, when one was set this frame (<see cref="ShowHint"/>).</summary>
    internal static bool TryHint(out string hint)
    {
        hint = _hint;
        return _hintFrame == Time.frameCount && !string.IsNullOrEmpty(_hint);
    }

    /// <summary>Shows a line of help in the footer this frame (the selected Simple row's explanation).</summary>
    internal static void ShowHint(string hint)
    {
        _hint = hint;
        _hintFrame = Time.frameCount;
    }

    internal static void SetStatus(string s)
    {
        if (string.IsNullOrEmpty(s)) return;
        _status = s;
        _statusAt = Time.unscaledTime;
    }

    // ---- widgets ----

    internal static float IW => _cw - Inset * 2;

    // ---- layout access for feature files (Features/*) ----

    /// <summary>Current vertical position inside the page's scroll area.</summary>
    internal static float Y { get => _y; set => _y = value; }
    /// <summary>Left edge for content inside a card.</summary>
    internal static float Left => Inset;
    /// <summary>Usable content width inside a card.</summary>
    internal static float Width => IW;
    /// <summary>Full width of the page's content area (cards and Simple rows span it).</summary>
    internal static float ContentWidth => _cw;

    /// <summary>Reserves a full-width row of the given height inside a card and returns its rect.</summary>
    internal static Rect NextRow(float height)
    {
        var r = new Rect(Inset, _y, IW, height);
        _y += height + 6f;
        if (RowRecorder.Recording) Recorded.CustomRow(r);
        return r;
    }

    // ---- text and buttons that follow the menu style (use these instead of GUI.Label / GUI.Button) ----

    private static TextSpec SpecLabel => new(14f, NativeLook.Text);
    private static TextSpec SpecMuted => new(12.5f, NativeLook.Muted, wrap: true);
    private static TextSpec SpecSmall => new(11.5f, NativeLook.Muted);
    private static TextSpec SpecValue => new(14f, NativeLook.Highlight, TextAnchor.MiddleRight, bold: true);

    private static TextSpec SpecFor(GUIStyle style)
    {
        if (ReferenceEquals(style, Theme.Label)) return SpecLabel;
        if (ReferenceEquals(style, Theme.Muted_)) return SpecMuted;
        if (ReferenceEquals(style, Theme.Small)) return SpecSmall;
        if (ReferenceEquals(style, Theme.Value)) return SpecValue;
        if (ReferenceEquals(style, Theme.Section)) return new TextSpec(13f, NativeLook.Text, bold: true, heading: true, upper: true);
        if (ReferenceEquals(style, Theme.Title)) return new TextSpec(22f, NativeLook.Text, heading: true, upper: true);
        if (ReferenceEquals(style, Theme.Status)) return new TextSpec(12.5f, NativeLook.Text);
        if (ReferenceEquals(style, Theme.Badge)) return new TextSpec(11f, Theme.Hex(0x0E1116), TextAnchor.MiddleCenter, bold: true);
        if (ReferenceEquals(style, Theme.Chip) || ReferenceEquals(style, Theme.Button) || ReferenceEquals(style, Theme.ChipDanger)
            || ReferenceEquals(style, Theme.Tab) || ReferenceEquals(style, Theme.TabActive))
            return new TextSpec(13f, NativeLook.ButtonText, TextAnchor.MiddleCenter);
        return SpecLabel;
    }

    /// <summary>GUI.Label that renders with the game's font and colours in the native style.</summary>
    internal static void Label(Rect r, string text, GUIStyle style)
    {
        if (RowRecorder.Recording) { Recorded.PlacedLabel(r, text); return; }
        if (!Emit) { GUI.Label(r, text, style); return; }
        NativeCanvas.Text(r, text, SpecFor(style));
    }

    /// <summary>Label with an alignment override (instead of changing the shared style).</summary>
    internal static void Label(Rect r, string text, GUIStyle style, TextAnchor align)
    {
        if (RowRecorder.Recording) { Recorded.PlacedLabel(r, text); return; }
        if (Emit) { NativeCanvas.Text(r, text, SpecFor(style).With(align)); return; }
        var prev = style.alignment;
        style.alignment = align;
        GUI.Label(r, text, style);
        style.alignment = prev;
    }

    /// <summary>Label with a colour override (instead of changing the shared style).</summary>
    internal static void Label(Rect r, string text, GUIStyle style, Color color)
    {
        if (RowRecorder.Recording) { Recorded.PlacedLabel(r, Tinted(text, color)); return; }
        if (Emit) { NativeCanvas.Text(r, text, SpecFor(style).With(color)); return; }
        var prev = style.normal.textColor;
        style.normal.textColor = color;
        GUI.Label(r, text, style);
        style.normal.textColor = prev;
    }

    /// <summary>Label with alignment and colour overrides.</summary>
    internal static void Label(Rect r, string text, GUIStyle style, TextAnchor align, Color color)
    {
        if (RowRecorder.Recording) { Recorded.PlacedLabel(r, Tinted(text, color)); return; }
        if (Emit) { NativeCanvas.Text(r, text, SpecFor(style).With(align).With(color)); return; }
        var prevA = style.alignment;
        var prevC = style.normal.textColor;
        style.alignment = align;
        style.normal.textColor = color;
        GUI.Label(r, text, style);
        style.alignment = prevA;
        style.normal.textColor = prevC;
    }

    /// <summary>A placed label's colour kept on the game rows (rich text).</summary>
    private static string Tinted(string text, Color c) =>
        string.IsNullOrEmpty(text) ? text : $"<color=#{ColorText.Hex(c.r, c.g, c.b)}>{text}</color>";

    /// <summary>GUI.Button that follows the menu style (and is keyboard-selectable in the native style).
    /// Theme.TabActive draws as selected, Theme.ChipDanger as a destructive action.</summary>
    internal static bool Button(Rect r, string label, GUIStyle style)
    {
        if (RowRecorder.Recording) return Recorded.PlacedButton(r, label, true);
        if (!_native) return GUI.Button(r, label, style);
        return NativeButton(r, label, true, ReferenceEquals(style, Theme.TabActive), ReferenceEquals(style, Theme.ChipDanger));
    }

    /// <summary>Registers a selectable rect with keyboard navigation (native style, inside the tab content).
    /// <paramref name="activated"/> is true once when the user presses Enter / A on it.</summary>
    internal static void NavItem(Rect r, NavKind kind, out bool focused, out bool activated) => NavItem(r, kind, out focused, out activated, out _);

    /// <summary><see cref="NavItem(Rect, NavKind, out bool, out bool)"/> with the left/right step (-1 / +1) for
    /// switches and sliders.</summary>
    internal static void NavItem(Rect r, NavKind kind, out bool focused, out bool activated, out int side)
    {
        focused = activated = false;
        side = 0;
        if (_native && _inContent) MenuNav.Register(r, kind, out focused, out activated, out side);
    }

    /// <summary>The game's selection bar behind a row (native style only).</summary>
    internal static void SelectionBar(Rect r)
    {
        if (Emit) NativeCanvas.Fill(r, NativeLook.Highlight, NativeLook.HighlightShape, Depth.Mid);
    }

    private static bool NativeButton(Rect r, string label, bool enabled, bool selected, bool danger)
    {
        bool focused = false, act = false;
        if (_inContent) MenuNav.Register(r, NavKind.Button, out focused, out act, out _);
        if (Emit)
        {
            bool lit = focused || selected;
            if (lit) NativeCanvas.Fill(r, selected && !focused ? Dim(NativeLook.Highlight, 0.8f) : NativeLook.Highlight, NativeLook.HighlightShape, Depth.Mid);
            else NativeCanvas.Fill(r, danger ? new Color(NativeLook.Danger.r, NativeLook.Danger.g, NativeLook.Danger.b, 0.28f) : NativeLook.Button, NativeLook.ButtonShape, Depth.Front);
            NativeCanvas.Text(new Rect(r.x + 4f, r.y, r.width - 8f, r.height), label,
                new TextSpec(13f, lit ? NativeLook.HighlightText : danger ? Theme.Hex(0xFFB4AE) : NativeLook.ButtonText, TextAnchor.MiddleCenter, bold: lit));
        }
        bool clicked = (GUI.Button(r, GUIContent.none, Theme.Invisible) || act) && enabled;
        if (clicked) NativeLook.PlayPress();
        return clicked;
    }

    private static Color Dim(Color c, float k) => new(c.r * k, c.g * k, c.b * k, c.a);

    /// <summary>Text height for wrapped text, measured the way the active style will draw it.</summary>
    private static float WrappedHeight(string text, float width)
    {
        if (_native)
        {
            try
            {
                var h = NativeCanvas.MeasureHeight(text, width, SpecMuted);
                if (h.HasValue) return h.Value;
            }
            catch (Exception e) { DebugLog.Error("core", e, "Menu.WrappedHeight"); NativeStyle.Fail("measure", e); }
        }
        return Theme.Muted_.CalcHeight(new GUIContent(text), width);
    }

    /// <summary>A labelled single-line text field row.</summary>
    internal static string TextRow(string label, string value, int maxLength = 64)
    {
        if (RowRecorder.Recording) return Recorded.Text(label, value, maxLength, null, Recorded.TextRowH);
        var row = new Rect(Inset - 6f, _y - 2f, IW + 12f, 32f);
        bool hit = MenuUx.Note(label);
        if (!_native)
        {
            if (hit) MenuUx.Highlight(row);
            GUI.Label(new Rect(Inset + 4f, _y, 150f, 28f), label, Theme.Label);
            string v0 = GUI.TextField(new Rect(Inset + 156f, _y, IW - 156f, 28f), value ?? "", maxLength, Theme.TextField);
            _y += 34f;
            return v0;
        }
        NavItem(row, NavKind.Text, out bool focused, out bool act);
        if (focused) SelectionBar(row);
        else if (hit) MenuUx.Highlight(row);
        Label(new Rect(Inset + 4f, _y, 150f, 28f), label, Theme.Label, focused ? NativeLook.HighlightText : NativeLook.Text);
        string name = "otp.textrow." + NextTextId();
        GUI.SetNextControlName(name);
        string v = GUI.TextField(new Rect(Inset + 156f, _y, IW - 156f, 28f), value ?? "", maxLength, Theme.TextField);
        if (act) GUI.FocusControl(name);
        _y += 34f;
        return v;
    }

    internal static void Card(string title, string subtitle, Action body)
    {
        if (RowRecorder.Recording)
        {
            RecordedCard(title, subtitle, body);
            return;
        }
        float top = _y;
        string key = MenuUx.CardKey(_drawMode, _drawTab, title);
        float h = CardH.TryGetValue(key, out var cached) ? cached : 80f;
        if (!_native) GUI.Box(new Rect(0f, top, _cw, h), GUIContent.none, Theme.CardBox);
        else if (Emit) NativeCanvas.Fill(new Rect(0f, top, _cw, h), NativeLook.Card, NativeLook.RoundMedium, Depth.Back);
        // A group starts with only its first card open (the menu settings group stays open).
        bool defaultOpen = _cardIndex++ == 0 || _drawTabName == SettingsTab || !MenuUx.FoldByDefault;
        bool open = MenuUx.CardHeader(key, title, new Rect(0f, top, _cw, h), Inset, IW, defaultOpen);
        _y += 32f;
        if (open)
        {
            var prev = GUI.color;
            GUI.color = MenuUx.CardTint(key, prev);
            try
            {
                if (!string.IsNullOrEmpty(subtitle)) Small(subtitle);
                body();
            }
            finally { GUI.color = prev; }
            _y += 8f;
        }
        else _y = top + 38f; // folded: title row only
        if (!MenuUx.Indexing) CardH[key] = _y - top;
        _y += 10f;
    }

    /// <summary>A card while recording: its title row folds it (same fold state as the drawn card), then its rows.</summary>
    private static void RecordedCard(string title, string subtitle, Action body)
    {
        string key = MenuUx.CardKey(_drawMode, _drawTab, title);
        bool defaultOpen = _cardIndex++ == 0 || _drawTabName == SettingsTab || !MenuUx.FoldByDefault;
        bool open = MenuUx.CardOpen(key, defaultOpen);
        if (Recorded.Fold(title, open))
        {
            open = !open;
            MenuUx.SetCardOpen(key, open);
        }
        if (!open) return;
        if (!string.IsNullOrEmpty(subtitle)) Small(subtitle);
        body();
        _y += 8f;
    }

    internal static void Banner(string text, Color color)
    {
        if (RowRecorder.Recording) { Recorded.Banner(text, color); return; }
        MenuUx.Note(text);
        float th = WrappedHeight(text, _cw - 36f);
        var r = new Rect(0f, _y, _cw, th + 16f);
        if (_native)
        {
            if (Emit)
            {
                NativeCanvas.Fill(r, new Color(color.r, color.g, color.b, 0.18f), NativeLook.RoundMedium, Depth.Back);
                NativeCanvas.Fill(new Rect(r.x, r.y, 4f, r.height), color, NativeLook.Square, Depth.Front);
                NativeCanvas.Text(new Rect(16f, r.y + 8f, _cw - 32f, th), text, SpecMuted.With(NativeLook.Text));
            }
            _y += r.height + 10f;
            return;
        }
        Theme.Fill(r, new Color(color.r, color.g, color.b, 0.16f), 8);
        Theme.Fill(new Rect(r.x, r.y, 4f, r.height), color, 2);
        var prev = Theme.Muted_.normal.textColor;
        Theme.Muted_.normal.textColor = Theme.Text;
        GUI.Label(new Rect(16f, r.y + 8f, _cw - 32f, th), text, Theme.Muted_);
        Theme.Muted_.normal.textColor = prev;
        _y += r.height + 10f;
    }

    internal static void Small(string text)
    {
        if (RowRecorder.Recording) { Recorded.Note(text); return; }
        MenuUx.Note(text);
        float th = WrappedHeight(text, IW);
        Label(new Rect(Inset, _y, IW, th), text, Theme.Muted_);
        _y += th + 6f;
    }

    internal static void Switch(ConfigEntry<bool> entry, string label, string hint, bool enabled)
    {
        if (RowRecorder.Recording) { Recorded.Switch(entry, label, hint, enabled); return; }
        float rh = hint == null ? 30f : 42f;
        var row = new Rect(Inset - 6f, _y, IW + 12f, rh);
        var prev = GUI.color;
        if (!enabled) GUI.color = new Color(1f, 1f, 1f, 0.45f);
        bool hit = MenuUx.Note(label, hint, entry?.Description?.Description);
        if (_native)
        {
            NativeSwitch(entry, label, hint, row, rh, hit, enabled);
            GUI.color = prev;
            _y += rh + 2f;
            return;
        }
        if (hit) MenuUx.Highlight(row);
        else if (row.Contains(Event.current.mousePosition)) Theme.Fill(row, Theme.CardHover, 6);

        GUI.Label(new Rect(Inset + 4f, _y + (hint == null ? 4f : 2f), IW - 70f, 22f), label, Theme.Label);
        if (hint != null) GUI.Label(new Rect(Inset + 4f, _y + 21f, IW - 70f, 18f), hint, Theme.Small);

        var track = new Rect(Inset + IW - 44f, _y + (rh - 22f) / 2f, 42f, 22f);
        Theme.Fill(track, entry.Value ? Theme.Accent : Theme.Off, 11);
        Theme.Fill(new Rect(entry.Value ? track.xMax - 20f : track.x + 2f, track.y + 2f, 18f, 18f), Theme.Text, 9);

        // A greyed-out switch (host only, physics only, parent off) can't be flipped.
        if (GUI.Button(row, MenuUx.TipContent(entry, hint), Theme.Invisible) && enabled) entry.Value = !entry.Value;
        GUI.color = prev;
        _y += rh + 2f;
    }

    /// <summary>A switch drawn like the game's option rows: label on the left, "&lt; ON &gt;" carousel on the right.</summary>
    private static void NativeSwitch(ConfigEntry<bool> entry, string label, string hint, Rect row, float rh, bool hit, bool enabled)
    {
        int side = 0;
        bool focused = false, act = false;
        if (_inContent) MenuNav.Register(row, NavKind.Switch, out focused, out act, out side);
        if (Emit)
        {
            if (focused) SelectionBar(row);
            else if (hit) MenuUx.Highlight(row);
            var text = focused ? NativeLook.HighlightText : NativeLook.Text;
            NativeCanvas.Text(new Rect(Inset + 4f, _y + (hint == null ? 4f : 2f), IW - 120f, 22f), label, SpecLabel.With(text));
            if (hint != null)
                NativeCanvas.Text(new Rect(Inset + 4f, _y + 21f, IW - 120f, 18f), hint, SpecSmall.With(focused ? Dim(NativeLook.HighlightText, 1f) : NativeLook.Muted));
            var box = new Rect(Inset + IW - 100f, _y + (rh - 24f) / 2f, 100f, 24f);
            bool on = entry.Value;
            var arrows = focused ? NativeLook.HighlightText : NativeLook.Muted;
            var value = focused ? NativeLook.HighlightText : on ? NativeLook.Highlight : NativeLook.Muted;
            NativeCanvas.Text(new Rect(box.x, box.y, 16f, box.height), "<", new TextSpec(14f, arrows, TextAnchor.MiddleCenter, bold: true, heading: true));
            NativeCanvas.Text(new Rect(box.xMax - 16f, box.y, 16f, box.height), ">", new TextSpec(14f, arrows, TextAnchor.MiddleCenter, bold: true, heading: true));
            NativeCanvas.Text(new Rect(box.x + 16f, box.y, box.width - 32f, box.height), on ? "ON" : "OFF",
                new TextSpec(13.5f, value, TextAnchor.MiddleCenter, bold: true, heading: true));
        }
        bool clicked = GUI.Button(row, MenuUx.TipContent(entry, hint), Theme.Invisible) || act;
        if (!enabled) { clicked = false; side = 0; } // greyed out: unavailable, not just dimmed
        bool want = clicked ? !entry.Value : side != 0 ? side > 0 : entry.Value;
        if (want != entry.Value)
        {
            entry.Value = want;
            NativeLook.PlayOption();
        }
    }

    internal static void SliderRow(ConfigEntry<float> entry, string label, float min, float max, string fmt, string suffix, bool enabled, bool percent = false)
    {
        string shown = percent ? (entry.Value * 100f).ToString("0") + "%" : entry.Value.ToString(fmt) + suffix;
        float v = RawSlider(label, entry.Value, min, max, shown, enabled, MenuUx.TipFor(entry, null), entry.Description?.Description);
        if (Mathf.Abs(v - entry.Value) > 0.0001f) entry.Value = Mathf.Round(v * 100f) / 100f;
    }

    private static GUIStyle _thumbHit;

    internal static float RawSlider(string label, float value, float min, float max, string shown, bool enabled, string tooltip = null, string searchText = null)
    {
        if (RowRecorder.Recording) return Recorded.Slider(label, value, min, max, shown, enabled, tooltip ?? searchText);
        bool hit = MenuUx.Note(label, searchText);
        if (_native) return NativeSlider(label, value, min, max, shown, enabled, tooltip, hit);
        if (hit) MenuUx.Highlight(new Rect(Inset - 6f, _y - 1f, IW + 12f, 30f));
        var prev = GUI.color;
        if (!enabled) GUI.color = new Color(1f, 1f, 1f, 0.45f);
        GUI.Label(new Rect(Inset + 4f, _y, 190f, 28f), label, Theme.Label);
        MenuUx.Tooltip(new Rect(Inset, _y, 196f, 28f), tooltip);
        GUI.Label(new Rect(Inset + IW - 80f, _y, 80f, 28f), shown, Theme.Value);

        var track = new Rect(Inset + 200f, _y + 11f, IW - 290f, 6f);
        float t = Mathf.InverseLerp(min, max, value);
        Theme.Fill(track, Theme.Off, 3);
        Theme.Fill(new Rect(track.x, track.y, track.width * t, track.height), Theme.AccentDim, 3);
        float v = GUI.HorizontalSlider(new Rect(track.x - 7f, _y + 7f, track.width + 14f, 14f), value, min, max, Theme.Slider, Theme.Thumb);
        GUI.color = prev;
        _y += 30f;
        return enabled ? v : value;
    }

    private static float NativeSlider(string label, float value, float min, float max, string shown, bool enabled, string tooltip, bool hit)
    {
        var row = new Rect(Inset - 6f, _y - 1f, IW + 12f, 30f);
        var prev = GUI.color;
        if (!enabled) GUI.color = new Color(1f, 1f, 1f, 0.45f);
        int side = 0;
        bool focused = false;
        if (_inContent) MenuNav.Register(row, NavKind.Slider, out focused, out _, out side);
        var track = new Rect(Inset + 200f, _y + 11f, IW - 290f, 6f);
        float t = Mathf.InverseLerp(min, max, value);
        if (Emit)
        {
            if (focused) SelectionBar(row);
            else if (hit) MenuUx.Highlight(row);
            var text = focused ? NativeLook.HighlightText : NativeLook.Text;
            NativeCanvas.Text(new Rect(Inset + 4f, _y, 190f, 28f), label, SpecLabel.With(text));
            NativeCanvas.Text(new Rect(Inset + IW - 80f, _y, 80f, 28f), shown, SpecValue.With(focused ? NativeLook.HighlightText : NativeLook.Highlight));
            var fill = focused ? NativeLook.HighlightText : NativeLook.Highlight;
            NativeCanvas.Fill(track, focused ? new Color(0f, 0f, 0f, 0.25f) : NativeLook.Track, NativeLook.TrackShape, Depth.Front);
            if (t > 0.001f) NativeCanvas.Fill(new Rect(track.x, track.y, Mathf.Max(track.height, track.width * t), track.height), fill, NativeLook.FillShape, Depth.Front);
            NativeCanvas.Fill(new Rect(track.x + track.width * t - 7f, _y + 7f, 14f, 14f), focused ? NativeLook.HighlightText : NativeLook.Text, NativeLook.KnobShape, Depth.Front);
        }
        MenuUx.Tooltip(new Rect(Inset, _y, 196f, 28f), tooltip);
        _thumbHit ??= new GUIStyle { fixedWidth = 14, fixedHeight = 14 };
        float v = GUI.HorizontalSlider(new Rect(track.x - 7f, _y + 7f, track.width + 14f, 14f), value, min, max, Theme.Slider, _thumbHit);
        if (side != 0 && enabled)
        {
            float step = (max - min) / (ShiftHeld() ? 100f : 20f);
            v = Mathf.Clamp(value + side * step, min, max);
            NativeLook.PlaySlider();
        }
        GUI.color = prev;
        _y += 30f;
        return enabled ? v : value;
    }

    private static bool ShiftHeld()
    {
        try
        {
            var kb = UnityEngine.InputSystem.Keyboard.current;
            return kb != null && kb.shiftKey.isPressed;
        }
        catch (Exception ex) { DebugLog.Error("core", ex, "Menu.ShiftHeld"); return false; }
    }

    internal static void KeyValue(string key, string value)
    {
        if (RowRecorder.Recording) { Recorded.KeyValue(key, value); return; }
        if (MenuUx.Note(key)) MenuUx.Highlight(new Rect(Inset - 6f, _y, IW + 12f, 22f));
        Label(new Rect(Inset + 4f, _y, 176f, 22f), key, Theme.Muted_);
        Label(new Rect(Inset + 180f, _y, IW - 184f, 22f), value, Theme.Label);
        _y += 24f;
    }

    // ---- colours: swatches the game's rows can show too ----

    /// <summary>
    /// A colour swatch to put in a row's text: on the MOD BROWSER's rows a small block of the colour (followed by a
    /// space); "" in the full view, which draws its swatches as fills (<see cref="SwatchRow"/>, <see cref="Swatches"/>).
    /// </summary>
    internal static string Swatch(Color c) => RowRecorder.Recording ? ColorText.Swatch(ColorText.Hex(c.r, c.g, c.b)) + " " : "";

    /// <summary>A row with a colour swatch, a name and a muted value on the right (players' colours, a legend).</summary>
    internal static void SwatchRow(Color c, string left, string right)
    {
        if (RowRecorder.Recording) { Recorded.KeyValue(Swatch(c) + (left ?? ""), right ?? ""); return; }
        var rect = NextRow(22f);
        Theme.Fill(new Rect(rect.x + 4f, rect.y + 3f, 28f, 16f), c, 4);
        Label(new Rect(rect.x + 42f, rect.y, rect.width - 170f, 22f), left ?? "", Theme.Label);
        Label(new Rect(rect.xMax - 120f, rect.y, 120f, 22f), right ?? "", Theme.Muted_);
    }

    /// <summary>A labelled strip of colour swatches (a palette), dimmed when not <paramref name="enabled"/>.</summary>
    internal static void Swatches(string label, IReadOnlyList<Color> colors, bool enabled = true)
    {
        if (colors == null || colors.Count == 0) return;
        if (RowRecorder.Recording)
        {
            var sb = new System.Text.StringBuilder(label ?? "");
            sb.Append("   ");
            foreach (var c in colors) sb.Append(Swatch(c));
            if (!enabled) sb.Append("  (off)");
            Recorded.Note(sb.ToString());
            return;
        }
        Small(label ?? "");
        var row = NextRow(22f);
        const float gap = 4f;
        float w = (row.width - gap * (colors.Count - 1)) / colors.Count;
        var prev = GUI.color;
        if (!enabled) GUI.color = new Color(1f, 1f, 1f, 0.35f);
        for (int i = 0; i < colors.Count; i++)
            Theme.Fill(new Rect(row.x + i * (w + gap), row.y, w, row.height), colors[i], 4);
        GUI.color = prev;
    }

    internal static void Bar(string label, float fraction, Color color, string text)
    {
        if (RowRecorder.Recording) { Recorded.KeyValue(label, text); return; }
        MenuUx.Note(label);
        Label(new Rect(Inset + 4f, _y, 176f, 22f), label, Theme.Muted_);
        var track = new Rect(Inset + 180f, _y + 7f, IW - 250f, 8f);
        if (_native)
        {
            if (Emit)
            {
                NativeCanvas.Fill(track, NativeLook.Track, NativeLook.TrackShape, Depth.Front);
                float f = Mathf.Clamp01(fraction);
                if (f > 0.001f) NativeCanvas.Fill(new Rect(track.x, track.y, Mathf.Max(track.height, track.width * f), track.height), color, NativeLook.FillShape, Depth.Front);
            }
        }
        else
        {
            Theme.Fill(track, Theme.Off, 4);
            Theme.Fill(new Rect(track.x, track.y, track.width * Mathf.Clamp01(fraction), track.height), color, 4);
        }
        Label(new Rect(Inset + IW - 60f, _y, 60f, 22f), text, Theme.Value);
        _y += 24f;
    }

    internal static void Chips(int columns, params (string label, Action action, bool enabled)[] items)
    {
        const float gap = 6f;
        float w = (IW - gap * (columns - 1)) / columns;
        for (int i = 0; i < items.Length; i++)
        {
            int col = i % columns;
            if (i > 0 && col == 0) _y += 36f;
            var r = new Rect(Inset + col * (w + gap), _y, w, 30f);
            bool hit = MenuUx.Note(items[i].label);
            if (ChipButton(r, items[i].label, items[i].enabled)) items[i].action();
            if (hit) Theme.Fill(new Rect(r.x + 6f, r.yMax - 3f, r.width - 12f, 2f), Theme.Accent, 1);
        }
        _y += 36f;
    }

    internal static bool ChipButton(Rect r, string label, bool enabled)
    {
        if (RowRecorder.Recording) return Recorded.PlacedButton(r, label, enabled);
        var prev = GUI.color;
        if (!enabled) GUI.color = new Color(1f, 1f, 1f, 0.35f);
        bool clicked = _native ? NativeButton(r, label, enabled, false, false) : GUI.Button(r, label, Theme.Chip) && enabled;
        GUI.color = prev;
        return clicked;
    }
}
