using System;
using System.Collections.Generic;
using OverTheHillPlus.ServerCreator;
using OverTheHillPlus.UI;
using UnityEngine;

namespace OverTheHillPlus.Features.Public.ServerNameStyles;

/// <summary>
/// Extra looks for your server's name (highlight, size, italic, underline, letter spacing, quick presets) and a live
/// preview. The controls are drawn inside the Lobby tab's "Server name" card (see ServerCreator.ServerCreatorFeature),
/// next to the name and colour they style.
/// </summary>
internal sealed class ServerNameStylesFeature : IFeature
{
    public void Register() => NameStyle.Bind();
}

/// <summary>Style controls and the preview for the "Server name" card.</summary>
internal static class ServerNameStyleMenu
{
    private const string PreviewSample = "Player's room";

    private sealed class LookDef
    {
        public string Name, Colour, Highlight, Spacing, Font = "Default", Caps = "Normal";
        public bool Bold, Italic, Underline;
        public int Size;

        public LookDef(string name, string colour, bool bold, bool italic, bool underline, string highlight, int size, string spacing)
        {
            Name = name; Colour = colour; Bold = bold; Italic = italic; Underline = underline; Highlight = highlight; Size = size; Spacing = spacing;
        }
    }

    private static readonly LookDef[] Looks =
    {
        new("Plain", "None", false, false, false, "None", 100, "Normal"),
        new("Marker", "None", true, false, false, "Amber", 100, "Normal"),
        new("Neon", "Cyan", true, false, false, "Violet", 100, "Wide"),
        new("Sunset sign", "Sunset", true, false, false, "None", 100, "Normal"),
        new("Ocean wave", "Ocean", false, true, false, "None", 100, "Wide"),
        new("Headline", "None", true, false, true, "None", 100, "Normal") { Caps = "ALL CAPS" },
        new("Whisper", "Purple", false, true, false, "None", 90, "Wider"),
        new("Party", "Rainbow", true, false, false, "Smoke", 100, "Normal"),
    };

    private static string[] _lookNames, _highlightNames, _sizeLabels, _spacingNames, _fontNames, _caseNames;

    private static float _fontCheckAt = -1f;
    private static bool _anyFont;

    private static bool AnyFontRenders()
    {
        if (Time.unscaledTime - _fontCheckAt < 5f && _fontCheckAt >= 0f) return _anyFont; // the menu draws every frame
        _fontCheckAt = Time.unscaledTime;
        _anyFont = false;
        foreach (var f in NameStyle.Fonts)
            if (f.Asset != null && Core.Shared.ServerListCaps.FontRenders(f.Asset)) { _anyFont = true; break; }
        return _anyFont;
    }

    private static void Apply(LookDef l)
    {
        ServerCreatorConfig.RoomNameStyle.Value = l.Colour;
        ServerCreatorConfig.RoomNameBold.Value = l.Bold;
        if (NameStyle.Italic == null) return; // settings not bound: only colour and bold exist
        NameStyle.Italic.Value = l.Italic;
        NameStyle.Underline.Value = l.Underline;
        NameStyle.Highlight.Value = l.Highlight;
        NameStyle.SizePercent.Value = l.Size;
        NameStyle.Spacing.Value = l.Spacing;
        NameStyle.Font.Value = l.Font;
        NameStyle.Caps.Value = l.Caps;
    }

    /// <summary>The quick look the current settings are (-1: a mix of their own).</summary>
    private static int CurrentLook()
    {
        for (int i = 0; i < Looks.Length; i++)
        {
            var l = Looks[i];
            if (ServerCreatorConfig.RoomNameStyle.Value == l.Colour && ServerCreatorConfig.RoomNameBold.Value == l.Bold &&
                NameStyle.Italic.Value == l.Italic && NameStyle.Underline.Value == l.Underline && NameStyle.Highlight.Value == l.Highlight &&
                NameStyle.SnapSize(NameStyle.SizePercent.Value) == l.Size && NameStyle.Spacing.Value == l.Spacing &&
                NameStyle.Font.Value == l.Font && NameStyle.Caps.Value == l.Caps) return i;
        }
        return -1;
    }

    /// <summary>Draws the style controls, the preview and the markup budget (call inside a card).</summary>
    public static void Draw()
    {
        if (NameStyle.Italic == null)
        {
            // The feature failed to register: keep the old controls working.
            SimpleMenu.Toggle(ServerCreatorConfig.RoomNameBold, "Bold", null);
            DrawPreview(NameStyle.Compose(Multiplayer.RoomNames.DisplayName(PreviewSample), NameStyle.Current()));
            return;
        }

        _lookNames ??= Array.ConvertAll(Looks, l => l.Name);
        _highlightNames ??= Array.ConvertAll(NameStyle.Highlights, h => h.Name);
        _sizeLabels ??= Array.ConvertAll(NameStyle.Sizes, v => v + "%");
        _spacingNames ??= Array.ConvertAll(NameStyle.Spacings, v => v.Name);
        _fontNames ??= Array.ConvertAll(NameStyle.Fonts, v => v.Name);
        _caseNames ??= Array.ConvertAll(NameStyle.CaseStyles, v => v.Name);

        SimpleMenu.Choice("Quick look", "Sets the colour and every style below at once.", CurrentLook(), _lookNames, i => Apply(Looks[i]), true, "Your own");
        SimpleMenu.Toggle(ServerCreatorConfig.RoomNameBold, "Bold", null);
        SimpleMenu.Toggle(NameStyle.Italic, "Italic", null);
        SimpleMenu.Toggle(NameStyle.Underline, "Underline", null);
        SimpleMenu.Choice("Highlight", "A background behind the name.", Array.IndexOf(_highlightNames, NameStyle.Highlight.Value), _highlightNames,
            i => NameStyle.Highlight.Value = _highlightNames[i], true, NameStyle.Highlight.Value);
        int size = NameStyle.SnapSize(NameStyle.SizePercent.Value);
        SimpleMenu.Choice("Size", null, Array.IndexOf(NameStyle.Sizes, size), _sizeLabels, i => NameStyle.SizePercent.Value = NameStyle.Sizes[i], true, wrap: false);
        SimpleMenu.Choice("Letter spacing", null, Array.IndexOf(_spacingNames, NameStyle.Spacing.Value), _spacingNames,
            i => NameStyle.Spacing.Value = _spacingNames[i], true, NameStyle.Spacing.Value, wrap: false);
        // Only offered once Server Browser+ saw a font tag render in the list (in the 10-16 build none does: the game's
        // fonts aren't findable by name, so a font tag would show as text).
        if (AnyFontRenders())
            SimpleMenu.Choice("Font", "The game's own fonts. The preview can't draw them; the server browser does.", Array.IndexOf(_fontNames, NameStyle.Font.Value), _fontNames,
                i => NameStyle.Font.Value = _fontNames[i], true, NameStyle.Font.Value);
        SimpleMenu.Choice("Letter case", "Capitals look bigger in the same row height.", Array.IndexOf(_caseNames, NameStyle.Caps.Value), _caseNames,
            i => NameStyle.Caps.Value = _caseNames[i], true, NameStyle.Caps.Value, wrap: false);

        var styled = NameStyle.Compose(Multiplayer.RoomNames.DisplayName(PreviewSample), NameStyle.Current());
        DrawPreview(styled);

        float used = styled.Markup / (float)NameStyle.MarkupBudget;
        Menu.Bar("Markup budget", used, used > 0.85f ? Theme.Warn : Theme.Good, $"{styled.Markup}/{NameStyle.MarkupBudget}");
        if (!string.IsNullOrEmpty(styled.Dropped))
            Menu.Small($"<color=#F2B33D>Trimmed to fit the markup budget: {styled.Dropped}.</color> Fewer effects at once keep everything.");
        if (!UI.GameMenu.RowRecorder.Recording)
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
        if (a.Caps != "Normal") text = text.ToUpperInvariant(); // small caps imitated as capitals
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
        if (UI.GameMenu.RowRecorder.Recording)
        {
            // The MOD BROWSER's rows are TextMeshPro, like the server browser: the name shows exactly as it will there.
            Menu.Small("Preview:   " + (styled.Text ?? styled.Visible ?? ""));
            return;
        }
        var row = Menu.NextRow(36f);
        Theme.Fill(new Rect(row.x - 6f, row.y, row.width + 12f, row.height), Theme.Hex(0x0E1218), 6);
        Menu.Label(new Rect(row.x + 4f, row.y, 70f, row.height), "Preview:", Theme.Muted_);
        try { if (!_failed) Layout(styled); }
        catch (Exception e)
        {
            DebugLog.Error("servercreator", e, "ServerNameStyleMenu.DrawPreview");
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
        ServerCreatorPlugin.Logger.LogWarning("[Server name style] preview failed (shown plain from now on): " + e.Message);
    }
}
