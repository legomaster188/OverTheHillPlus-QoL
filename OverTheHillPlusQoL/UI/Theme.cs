using System.Collections.Generic;
using UnityEngine;

namespace OverTheHillPlus.UI;

/// <summary>Colors, generated rounded textures and GUIStyles for the menu. Built lazily inside OnGUI.</summary>
internal static class Theme
{
    public static readonly Color Bg = Hex(0x12161D, 0.97f);
    public static readonly Color Card = Hex(0x1B222C);
    public static readonly Color CardHover = Hex(0x232C38);
    public static readonly Color Line = Hex(0x2C3644);
    public static readonly Color Accent = Hex(0xF2B33D);   // the game's menu yellow
    public static readonly Color AccentDim = Hex(0x8A6A2A);
    public static readonly Color Text = Hex(0xE8ECF2);
    public static readonly Color Muted = Hex(0x8C96A6);
    public static readonly Color Good = Hex(0x4CC38A);
    public static readonly Color Info = Hex(0x4FA8F7);
    public static readonly Color Warn = Hex(0xF2B33D);
    public static readonly Color Bad = Hex(0xE5534B);
    public static readonly Color Off = Hex(0x3A4554);

    public static GUIStyle Window, CardBox, Title, Label, Muted_, Small, Section, Value,
        Button, Chip, ChipDanger, Tab, TabActive, Badge, TextField, Slider, Thumb, Invisible, Status;

    private static readonly Dictionary<(Color, int), Texture2D> Cache = new();
    private static bool _ready;

    public static Color Hex(int rgb, float a = 1f) =>
        new(((rgb >> 16) & 0xFF) / 255f, ((rgb >> 8) & 0xFF) / 255f, (rgb & 0xFF) / 255f, a);

    public static void Ensure()
    {
        // Textures can be destroyed by scene unloads in rare cases; rebuild if so.
        if (_ready && Window?.normal?.background != null) return;
        Cache.Clear();
        FillStyles.Clear();

        Window = Box(Bg, 10);
        CardBox = Box(Card, 8);
        Window.padding = new RectOffset(0, 0, 0, 0);

        Label = Text_(13, Text);
        Muted_ = Text_(12, Muted);
        Muted_.wordWrap = true;
        Small = Text_(11, Muted);
        Title = Text_(17, Accent, FontStyle.Bold);
        Section = Text_(11, Muted, FontStyle.Bold);
        Value = Text_(13, Accent, FontStyle.Bold);
        Value.alignment = TextAnchor.MiddleRight;
        Status = Text_(12, Text);

        Button = Clickable(Card, CardHover, Accent, 13, Text);
        Chip = Clickable(Hex(0x222B36), Hex(0x2E3946), Accent, 12, Text);
        ChipDanger = Clickable(Hex(0x3A2424), Hex(0x4A2C2C), Bad, 12, Text);
        Tab = Clickable(Hex(0x000000, 0f), Hex(0x1F2731), Hex(0x1F2731), 13, Muted);
        TabActive = Clickable(Card, Card, Card, 13, Accent);
        TabActive.fontStyle = FontStyle.Bold;

        Badge = Box(Info, 9);
        Badge.fontSize = 11;
        Badge.fontStyle = FontStyle.Bold;
        Badge.alignment = TextAnchor.MiddleCenter;
        Badge.normal.textColor = Hex(0x0E1116);

        TextField = Box(Hex(0x0E1218), 6);
        TextField.normal.textColor = Text;
        TextField.focused.background = Rounded(Hex(0x0E1218), 6);
        TextField.focused.textColor = Text;
        TextField.fontSize = 13;
        TextField.padding = new RectOffset(8, 8, 5, 5);
        TextField.alignment = TextAnchor.MiddleLeft;
        TextField.clipping = TextClipping.Clip;

        // Track and fill are drawn by the menu; the slider style itself only provides the hit area.
        Slider = new GUIStyle();
        Slider.fixedHeight = 14;
        Slider.margin = new RectOffset(0, 0, 0, 0);
        Thumb = Box(Accent, 7);
        Thumb.fixedWidth = 14;
        Thumb.fixedHeight = 14;
        Thumb.hover.background = Rounded(Hex(0xFFD07A), 7);
        Thumb.active.background = Rounded(Hex(0xFFD07A), 7);

        Invisible = new GUIStyle();

        _ready = true;
    }

    // ---- builders ----

    private static GUIStyle Text_(int size, Color color, FontStyle style = FontStyle.Normal)
    {
        var s = new GUIStyle();
        s.fontSize = size;
        s.fontStyle = style;
        s.normal.textColor = color;
        s.alignment = TextAnchor.MiddleLeft;
        s.richText = true;
        s.clipping = TextClipping.Clip;
        return s;
    }

    private static GUIStyle Box(Color color, int radius)
    {
        var s = new GUIStyle();
        s.normal.background = Rounded(color, radius);
        s.border = new RectOffset(radius, radius, radius, radius);
        s.richText = true;
        return s;
    }

    private static GUIStyle Clickable(Color normal, Color hover, Color active, int size, Color text)
    {
        var s = Box(normal, 6);
        s.hover.background = Rounded(hover, 6);
        s.active.background = Rounded(active, 6);
        s.normal.textColor = text;
        s.hover.textColor = Theme.Text;
        s.active.textColor = Hex(0x0E1116);
        s.fontSize = size;
        s.alignment = TextAnchor.MiddleCenter;
        s.clipping = TextClipping.Clip;
        return s;
    }

    /// <summary>Anti-aliased rounded rectangle, used 9-sliced via GUIStyle.border.</summary>
    public static Texture2D Rounded(Color color, int radius)
    {
        if (Cache.TryGetValue((color, radius), out var cached) && cached != null) return cached;
        int size = radius * 2 + 2;
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
        tex.hideFlags = HideFlags.HideAndDontSave;
        tex.wrapMode = TextureWrapMode.Clamp;
        tex.filterMode = FilterMode.Bilinear;
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            float px = x + 0.5f, py = y + 0.5f;
            float cx = Mathf.Clamp(px, radius, size - radius);
            float cy = Mathf.Clamp(py, radius, size - radius);
            float d = Mathf.Sqrt((px - cx) * (px - cx) + (py - cy) * (py - cy));
            float a = Mathf.Clamp01(radius - d + 0.5f);
            tex.SetPixel(x, y, new Color(color.r, color.g, color.b, color.a * a));
        }
        tex.Apply();
        Cache[(color, radius)] = tex;
        return tex;
    }

    private static readonly Dictionary<(Color, int), GUIStyle> FillStyles = new();

    /// <summary>Solid rounded fill in any color (styles cached per color/radius).</summary>
    public static void Fill(Rect r, Color color, int radius = 4)
    {
        if (Event.current.type != EventType.Repaint || r.width <= 0f || r.height <= 0f) return;
        if (!FillStyles.TryGetValue((color, radius), out var s) || s.normal.background == null)
        {
            s = new GUIStyle();
            s.normal.background = Rounded(color, radius);
            s.border = new RectOffset(radius, radius, radius, radius);
            FillStyles[(color, radius)] = s;
        }
        s.Draw(r, false, false, false, false);
    }
}
