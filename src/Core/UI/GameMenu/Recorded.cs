using System;
using BepInEx.Configuration;
using UnityEngine;

namespace OverTheHillPlus.UI.GameMenu;

/// <summary>
/// The page widgets while a recording pass runs (<see cref="RowRecorder.Recording"/>): each describes its row to
/// the recorder and applies the player's input for it, exactly where it would have drawn and handled a click. The
/// layout cursor (Menu.Y) moves as on the drawn page, so draw code that places things by it stays in step.
/// Called from the top of the widgets in Widgets.cs and SimpleMenu.cs.
/// </summary>
internal static class Recorded
{
    // Heights as on the drawn page (SimpleMenu rows are 42 + a gap of 5, list rows 52 + 5).
    internal const float RowH = 47f, ListH = 57f, LineH = 24f, HeadingH = 32f, SwitchH = 32f, SliderH = 30f, BannerH = 50f, TextRowH = 34f;

    private static RowRecorder R => RowRecorder.Current;

    private static RowInput? Row(RowModel row, float height)
    {
        float y = Menu.Y;
        var input = R.Take(row, y, height);
        Menu.Y = y + height;
        return input;
    }

    internal static void Heading(string text) => Row(new RowModel { Kind = RowKind.Heading, Label = text ?? "" }, HeadingH);

    internal static void Note(string text) => Row(new RowModel { Kind = RowKind.Note, Label = text ?? "" }, LineH);

    internal static void Banner(string text, Color color) =>
        Row(new RowModel { Kind = RowKind.Banner, Label = text ?? "", Color = ToRgb(color) }, BannerH);

    internal static void KeyValue(string key, string value) =>
        Row(new RowModel { Kind = RowKind.KeyValue, Label = key ?? "", Value = value ?? "" }, LineH);

    /// <summary>A group title on a mod page (ModPageView.GroupTitle), with the spacing above it.</summary>
    internal static void GroupTitle(string title, bool first)
    {
        if (!first) Menu.Y += 8f;
        Heading(title);
    }

    internal static void Switch(ConfigEntry<bool> entry, string label, string hint, bool enabled)
    {
        if (entry == null) return;
        var input = Row(new RowModel
        {
            Kind = RowKind.Toggle, Label = label ?? "", Hint = Hint(hint, entry.Description?.Description), On = entry.Value, Enabled = enabled,
        }, hint == null ? SwitchH : SwitchH + 12f);
        if (input == null || !enabled) return;
        var i = input.Value;
        bool want = i.Kind == RowInputKind.Step ? i.Step > 0 : i.Kind == RowInputKind.Press ? !entry.Value : entry.Value;
        if (want != entry.Value) entry.Value = want;
    }

    internal static void Choice(string label, string hint, int index, string[] labels, Action<int> set, bool enabled, string shown, bool wrap, int from)
    {
        var input = Row(new RowModel
        {
            Kind = RowKind.Choice, Label = label ?? "", Hint = hint, Options = labels, Index = index, Value = shown, Wrap = wrap, Enabled = enabled,
        }, RowH);
        if (input == null || !enabled || labels == null || labels.Length == 0) return;
        var i = input.Value;
        bool press = i.Kind == RowInputKind.Press;
        int step = press ? 1 : i.Kind == RowInputKind.Step ? i.Step : 0;
        int next = ChoiceStep.Next(index, labels.Length, step, press, wrap, from);
        if (next != index && next >= 0) set(next);
    }

    internal static bool Button(string label, string value, string hint, Action action, bool enabled, string id = null)
    {
        var input = Row(new RowModel { Kind = RowKind.Button, Label = label ?? "", Value = value, Hint = hint, Enabled = enabled, Id = id }, RowH);
        if (input == null || !enabled || input.Value.Kind != RowInputKind.Press) return false;
        action?.Invoke();
        return true;
    }

    /// <summary>A text box: returns the text as the player typed it (the box on the page holds it while they type).</summary>
    internal static string Text(string label, string value, int maxLength, string hint, float height, bool enabled = true)
    {
        var input = Row(new RowModel
        {
            Kind = RowKind.Text, Label = label ?? "", Value = value ?? "", Hint = hint, MaxLength = Math.Max(1, maxLength), Enabled = enabled,
        }, height);
        if (input == null || !enabled || input.Value.Kind != RowInputKind.SetText) return value;
        string text = input.Value.Text ?? "";
        return text.Length > maxLength && maxLength > 0 ? text.Substring(0, maxLength) : text;
    }

    internal static bool ListRow(string label, string detail, string badge, string value, bool on, string hint, bool canStep, out int step,
        CardInfo card = null)
    {
        step = 0;
        var input = Row(new RowModel
        {
            Kind = RowKind.ListItem, Label = label ?? "", Detail = detail, Badge = badge, Value = value, On = on, Hint = hint, CanStep = canStep,
            Id = label ?? "", // the entry's name, digits and all
            Card = card,
        }, ListH);
        if (input == null) return false;
        var i = input.Value;
        if (i.Kind == RowInputKind.Step && canStep) step = i.Step;
        return i.Kind == RowInputKind.Press;
    }

    internal static float Slider(string label, float value, float min, float max, string shown, bool enabled, string hint)
    {
        var input = Row(new RowModel
        {
            Kind = RowKind.Slider, Label = label ?? "", Value = shown, Min = min, Max = max, Number = value, Enabled = enabled, Hint = hint,
        }, SliderH);
        if (input == null || !enabled || input.Value.Kind != RowInputKind.SetValue) return value;
        return Mathf.Clamp(input.Value.Value, Mathf.Min(min, max), Mathf.Max(min, max));
    }

    /// <summary>A card's title row; returns whether its body shows (pressing the row folds it open or shut).</summary>
    internal static bool Fold(string title, bool open)
    {
        var input = Row(new RowModel { Kind = RowKind.Fold, Label = title ?? "", On = open, Hint = open ? "Folds this group shut." : "Shows this group." }, HeadingH);
        return input != null && input.Value.Kind == RowInputKind.Press;
    }

    /// <summary>A button placed at a rect (a chip, Menu.Button): true when pressed.</summary>
    internal static bool PlacedButton(Rect r, string label, bool enabled)
    {
        var input = R.Piece(new RowModel { Kind = RowKind.Button, Label = label ?? "", Enabled = enabled }, r.y, r.height);
        return input != null && enabled && input.Value.Kind == RowInputKind.Press;
    }

    /// <summary>A label placed at a rect (Menu.Label).</summary>
    internal static void PlacedLabel(Rect r, string text) => R.Label(text, r.y, r.height);

    /// <summary>A free-form row (Menu.NextRow).</summary>
    internal static void CustomRow(Rect r) => R.CustomRow(r.y, r.yMax + 6f);

    private static string Hint(string hint, string description) =>
        !string.IsNullOrEmpty(hint) ? hint : string.IsNullOrEmpty(description) ? null : description;

    private static int ToRgb(Color c) =>
        (Mathf.Clamp(Mathf.RoundToInt(c.r * 255f), 0, 255) << 16) | (Mathf.Clamp(Mathf.RoundToInt(c.g * 255f), 0, 255) << 8) |
        Mathf.Clamp(Mathf.RoundToInt(c.b * 255f), 0, 255);
}
