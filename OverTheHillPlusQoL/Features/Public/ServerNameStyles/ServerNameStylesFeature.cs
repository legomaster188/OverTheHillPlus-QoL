using System;
using System.Collections.Generic;
using OverTheHillPlus.UI;
using UnityEngine;

namespace OverTheHillPlus.Features.Public.ServerNameStyles;

/// <summary>
/// Extra looks for your server's name (highlight, size, italic, underline, letter spacing, quick presets) and a live
/// preview. The controls are drawn inside the Lobby tab's "Server name" card (see Menu.DrawLobby), next to the name
/// and colour they style.
/// </summary>
internal sealed class ServerNameStylesFeature : IFeature
{
    public void Register() => NameStyle.Bind();
}

/// <summary>Style controls and the preview for the "Server name" card.</summary>
internal static class ServerNameStyleMenu
{
    private const string PreviewSample = "Player's room";

    private static readonly (string Name, Action Apply)[] Looks =
    {
        ("Plain", () => Set("None", false, false, false, "None", 100, "Normal")),
        ("Marker", () => Set("None", true, false, false, "Amber", 100, "Normal")),
        ("Neon", () => Set("Cyan", true, false, false, "Violet", 100, "Wide")),
        ("Sunset sign", () => Set("Sunset", true, false, false, "None", 110, "Normal")),
        ("Ocean wave", () => Set("Ocean", false, true, false, "None", 100, "Wide")),
        ("Headline", () => Set("None", true, false, true, "None", 120, "Normal")),
        ("Whisper", () => Set("Purple", false, true, false, "None", 90, "Wider")),
        ("Party", () => Set("Rainbow", true, false, false, "Smoke", 110, "Normal")),
    };

    private static void Set(string colour, bool bold, bool italic, bool underline, string highlight, int size, string spacing)
    {
        ModConfig.RoomNameStyle.Value = colour;
        ModConfig.RoomNameBold.Value = bold;
        if (NameStyle.Italic == null) return; // settings not bound: only colour and bold exist
        NameStyle.Italic.Value = italic;
        NameStyle.Underline.Value = underline;
        NameStyle.Highlight.Value = highlight;
        NameStyle.SizePercent.Value = size;
        NameStyle.Spacing.Value = spacing;
    }

    private static string Mark(string label, bool on) => on ? $"[{label}]" : label;

    /// <summary>Draws the style controls, the preview and the markup budget (call inside a card).</summary>
    public static void Draw()
    {
        if (NameStyle.Italic == null)
        {
            // The feature failed to register: keep the old controls working.
            Menu.Switch(ModConfig.RoomNameBold, "Bold", null, true);
            DrawPreview(NameStyle.Compose(Multiplayer.RoomNames.DisplayName(PreviewSample), NameStyle.Current()));
            return;
        }

        Menu.Small("Style");
        Menu.Chips(3,
            (Mark("Bold", ModConfig.RoomNameBold.Value), () => ModConfig.RoomNameBold.Value = !ModConfig.RoomNameBold.Value, true),
            (Mark("Italic", NameStyle.Italic.Value), () => NameStyle.Italic.Value = !NameStyle.Italic.Value, true),
            (Mark("Underline", NameStyle.Underline.Value), () => NameStyle.Underline.Value = !NameStyle.Underline.Value, true));

        Menu.Small("Highlight (background)");
        var marks = new (string, Action, bool)[NameStyle.Highlights.Length];
        for (int i = 0; i < marks.Length; i++)
        {
            string h = NameStyle.Highlights[i].Name;
            marks[i] = (Mark(h, NameStyle.Highlight.Value == h), () => NameStyle.Highlight.Value = h, true);
        }
        Menu.Chips(4, marks);

        Menu.Small("Size");
        int size = NameStyle.SnapSize(NameStyle.SizePercent.Value);
        var sizes = new (string, Action, bool)[NameStyle.Sizes.Length];
        for (int i = 0; i < sizes.Length; i++)
        {
            int s = NameStyle.Sizes[i];
            sizes[i] = (Mark(s + "%", size == s), () => NameStyle.SizePercent.Value = s, true);
        }
        Menu.Chips(5, sizes);

        Menu.Small("Letter spacing");
        var spacings = new (string, Action, bool)[NameStyle.Spacings.Length];
        for (int i = 0; i < spacings.Length; i++)
        {
            string s = NameStyle.Spacings[i].Name;
            spacings[i] = (Mark(s, NameStyle.Spacing.Value == s), () => NameStyle.Spacing.Value = s, true);
        }
        Menu.Chips(4, spacings);

        Menu.Small("Quick looks (set colour and all styles at once)");
        var looks = new (string, Action, bool)[Looks.Length];
        for (int i = 0; i < looks.Length; i++) looks[i] = (Looks[i].Name, Looks[i].Apply, true);
        Menu.Chips(4, looks);

        var styled = NameStyle.Compose(Multiplayer.RoomNames.DisplayName(PreviewSample), NameStyle.Current());
        DrawPreview(styled);

        float used = styled.Markup / (float)NameStyle.MarkupBudget;
        Menu.Bar("Markup budget", used, used > 0.85f ? Theme.Warn : Theme.Good, $"{styled.Markup}/{NameStyle.MarkupBudget}");
        if (!string.IsNullOrEmpty(styled.Dropped))
            Menu.Small($"<color=#F2B33D>Trimmed to fit the markup budget: {styled.Dropped}.</color> Fewer effects at once keep everything.");
        Menu.Small("Preview is approximate: the menu cannot draw the server browser's highlight, underline and letter spacing exactly, so they are imitated here.");
    }

    // ---- preview (IMGUI has no mark/u/cspace/size%: letters are laid out one by one and the effects drawn by hand) ----

    private sealed class Glyph
    {
        public string Text;
        public float X, W;
        public Color Color;
    }

    private static GUIStyle _glyphStyle;
    private static NameStyle.Styled _laidOut;
    private static readonly List<Glyph> Glyphs = new();
    private static float _width, _lineHeight, _spacing;
    private static bool _failed;
    private static int _fontSize;

    private static void Layout(NameStyle.Styled styled)
    {
        if (ReferenceEquals(_laidOut, styled) && _glyphStyle != null) return;
        _laidOut = styled;
        Glyphs.Clear();
        _width = 0f;

        var a = styled.Applied;
        _fontSize = Mathf.RoundToInt(13f * a.Size / 100f);
        if (_glyphStyle == null)
        {
            _glyphStyle = new GUIStyle();
            _glyphStyle.richText = false;
            _glyphStyle.alignment = TextAnchor.MiddleLeft;
            _glyphStyle.clipping = TextClipping.Overflow;
            _glyphStyle.wordWrap = false;
        }
        _glyphStyle.fontSize = _fontSize;
        _glyphStyle.fontStyle = a.Bold && a.Italic ? FontStyle.BoldAndItalic : a.Bold ? FontStyle.Bold : a.Italic ? FontStyle.Italic : FontStyle.Normal;

        float spacing = 0f;
        string em = NameStyle.SpacingEm(a.Spacing);
        if (em != null && float.TryParse(em.Replace("em", ""), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float e))
            spacing = e * _fontSize;

        string text = styled.Visible ?? "";
        float x = 0f;
        for (int i = 0; i < text.Length;)
        {
            int n = i + 1 < text.Length && char.IsSurrogatePair(text[i], text[i + 1]) ? 2 : 1;
            string ch = text.Substring(i, n);
            float w = _glyphStyle.CalcSize(new GUIContent(ch)).x;
            Glyphs.Add(new Glyph { Text = ch, X = x, W = w, Color = styled.CharColors != null && i < styled.CharColors.Length && styled.CharColors[i].HasValue ? styled.CharColors[i].Value : Theme.Text });
            x += w + spacing;
            i += n;
        }
        _spacing = spacing;
        _width = Math.Max(0f, x - spacing);
        _lineHeight = _fontSize + 4f;
    }

    private static void DrawPreview(NameStyle.Styled styled)
    {
        var row = Menu.NextRow(36f);
        Theme.Fill(new Rect(row.x - 6f, row.y, row.width + 12f, row.height), Theme.Hex(0x0E1218), 6);
        Menu.Label(new Rect(row.x + 4f, row.y, 70f, row.height), "Preview:", Theme.Muted_);
        try { if (!_failed) Layout(styled); }
        catch (Exception e)
        {
            _failed = true;
            LogOnce(e);
        }
        if (_failed)
        {
            Menu.Label(new Rect(row.x + 74f, row.y, row.width - 78f, row.height), styled.Visible, Theme.Label);
            return;
        }

        float x0 = row.x + 74f, mid = row.y + row.height / 2f;
        var clip = new Rect(x0 - 4f, row.y, row.width - 74f, row.height);
        bool repaint = Event.current != null && Event.current.type == EventType.Repaint;
        var a = styled.Applied;
        var prevColor = GUI.color;
        GUI.BeginGroup(clip);
        try
        {
            float ox = 4f, top = mid - row.y - _lineHeight / 2f;
            string rgba = NameStyle.HighlightRgba(a.Highlight);
            if (repaint && rgba != null)
            {
                GUI.color = prevColor * NameStyle.ParseHex(rgba); // keeps the card's fade tint
                GUI.DrawTexture(new Rect(ox - 2f, top, _width + 4f, _lineHeight), Texture2D.whiteTexture);
            }
            for (int i = 0; i < Glyphs.Count; i++)
            {
                var g = Glyphs[i];
                if (ox + g.X > clip.width) break;
                GUI.color = prevColor;
                _glyphStyle.normal.textColor = g.Color;
                GUI.Label(new Rect(ox + g.X, top, g.W + 4f, _lineHeight), g.Text, _glyphStyle);
                if (repaint && a.Underline)
                {
                    GUI.color = prevColor * g.Color;
                    float w = i + 1 < Glyphs.Count ? g.W + _spacing : g.W; // TMP underlines the spacing between letters too
                    GUI.DrawTexture(new Rect(ox + g.X, top + _lineHeight - 2f, Math.Max(0f, w), 1f), Texture2D.whiteTexture);
                }
            }
        }
        finally
        {
            GUI.EndGroup();
            GUI.color = prevColor;
        }
    }

    private static bool _logged;

    private static void LogOnce(Exception e)
    {
        if (_logged) return;
        _logged = true;
        Plugin.Logger.LogWarning("[Server name style] preview failed (shown plain from now on): " + e.Message);
    }
}
