using System;
using BepInEx.Configuration;

namespace OverTheHillPlus.Api;

/// <summary>
/// Drawing your mod's MODS page in the game's style. Use these only while the page draws: inside your
/// ModInfo.DrawSimple (the everyday settings, first on your page), ModInfo.DrawAdvanced, or a block added with
/// <see cref="Section"/> (in the page's Advanced fold). Rows are laid out top to bottom and are keyboard and gamepad
/// selectable; a row's hint shows at the bottom of the page while it is selected.
/// </summary>
public static class Page
{
    /// <summary>
    /// Adds a block of settings to your mod's page, in its Advanced fold under the heading <paramref name="title"/>
    /// (blocks with the same title share it; within it they draw by <paramref name="order"/>). Usually
    /// <paramref name="draw"/> draws one or more <see cref="Card"/>s. Call it once, from your Load. Throws
    /// ArgumentException for an invalid id or one of ours, ArgumentNullException without <paramref name="draw"/>.
    /// </summary>
    public static void Section(string modId, string title, Action draw, int order = 0)
    {
        ApiGuard.Require(modId, "Page.Section");
        if (draw == null) throw new ArgumentNullException(nameof(draw));
        Features.Registry.Section(Features.Registry.PlusMode, string.IsNullOrEmpty(title) ? modId : title, draw, order, null, modId);
    }

    /// <summary>A card that folds open and shut, with a title, an optional muted subtitle and its rows.</summary>
    public static void Card(string title, string subtitle, Action body)
    {
        if (body == null) return;
        OverTheHillPlus.Menu.Card(title ?? "", subtitle, body);
    }

    /// <summary>A small upper-case title above a few rows.</summary>
    public static void Heading(string text) => UI.SimpleMenu.Heading(text ?? "");

    /// <summary>A short muted line of explanation (wraps).</summary>
    public static void Note(string text) => UI.SimpleMenu.Note(text ?? "");

    /// <summary>One row each: label on the left, the value on the right.</summary>
    public static class Row
    {
        /// <summary>An Off / On row for a bool setting.</summary>
        public static void Toggle(ConfigEntry<bool> entry, string label, string hint = null, bool enabled = true)
        {
            if (entry == null) return;
            UI.SimpleMenu.Toggle(entry, label, hint, enabled);
        }

        /// <summary>
        /// A "&lt; value &gt;" row: <paramref name="index"/> is the current one of <paramref name="labels"/> (-1 shows
        /// "Custom"); <paramref name="set"/> gets the index the player picked.
        /// </summary>
        public static void Choice(string label, string hint, int index, string[] labels, Action<int> set, bool enabled = true)
        {
            if (labels == null || labels.Length == 0 || set == null) return;
            UI.SimpleMenu.Choice(label, hint, index, labels, set, enabled);
        }

        /// <summary>
        /// A slider for a float setting from <paramref name="min"/> to <paramref name="max"/>, its value shown with
        /// <paramref name="format"/> (a .NET number format) and <paramref name="suffix"/>.
        /// </summary>
        public static void Slider(ConfigEntry<float> entry, string label, float min, float max, string format = "0.00", string suffix = "",
            bool enabled = true)
        {
            if (entry == null) return;
            OverTheHillPlus.Menu.SliderRow(entry, label, min, max, format ?? "0.00", suffix ?? "", enabled);
        }

        /// <summary>A row that runs <paramref name="action"/> when pressed; <paramref name="value"/> shows on the right.
        /// True on the frame it was pressed.</summary>
        public static bool Button(string label, string value, string hint, Action action, bool enabled = true) =>
            UI.SimpleMenu.Button(label, value, hint, action, enabled);

        /// <summary>A row with a text box on the right. Returns the text, as the player may have edited it.</summary>
        public static string Text(string label, string value, int maxLength, string hint = null) =>
            UI.SimpleMenu.Text(label, value, Math.Clamp(maxLength, 1, 256), hint);
    }
}
