using UnityEngine;

namespace OverTheHillPlus.UI;

/// <summary>
/// Immediate-mode drawing primitives for on-screen overlays: boxes, lines, markers and outlined labels (screen pixels,
/// call only during Repaint). In core because several mods draw overlays with them.
/// </summary>
internal static class OverlayDraw
{
    private static GUIStyle _text;
    private static int _textSize = -1;

    private static Texture White
    {
        get
        {
            var t = Texture2D.whiteTexture;
            return t;
        }
    }

    public static void Rect(Rect r, Color c)
    {
        if (r.width <= 0f || r.height <= 0f) return;
        var prev = GUI.color;
        GUI.color = c;
        GUI.DrawTexture(r, White);
        GUI.color = prev;
    }

    public static void Outline(Rect r, Color c, float w)
    {
        Rect(new Rect(r.x, r.y, r.width, w), c);
        Rect(new Rect(r.x, r.yMax - w, r.width, w), c);
        Rect(new Rect(r.x, r.y + w, w, r.height - 2f * w), c);
        Rect(new Rect(r.xMax - w, r.y + w, w, r.height - 2f * w), c);
    }

    /// <summary>Outline with a dark border on both sides so it reads on bright snow and dark forest alike.</summary>
    public static void OutlineShadowed(Rect r, Color c, float w)
    {
        var sh = new Color(0f, 0f, 0f, c.a * 0.6f);
        Outline(new Rect(r.x - 1f, r.y - 1f, r.width + 2f, r.height + 2f), sh, w + 2f);
        Outline(r, c, w);
    }

    public static void Corners(Rect r, Color c, float w)
    {
        float lx = Mathf.Max(4f, r.width * 0.25f), ly = Mathf.Max(4f, r.height * 0.25f);
        var sh = new Color(0f, 0f, 0f, c.a * 0.6f);
        for (int pass = 0; pass < 2; pass++)
        {
            var col = pass == 0 ? sh : c;
            float g = pass == 0 ? 1f : 0f;
            float ww = w + g * 2f;
            // top-left
            Rect(new Rect(r.x - g, r.y - g, lx + g, ww), col);
            Rect(new Rect(r.x - g, r.y - g, ww, ly + g), col);
            // top-right
            Rect(new Rect(r.xMax - lx, r.y - g, lx + g, ww), col);
            Rect(new Rect(r.xMax - w - g, r.y - g, ww, ly + g), col);
            // bottom-left
            Rect(new Rect(r.x - g, r.yMax - w - g, lx + g, ww), col);
            Rect(new Rect(r.x - g, r.yMax - ly, ww, ly + g), col);
            // bottom-right
            Rect(new Rect(r.xMax - lx, r.yMax - w - g, lx + g, ww), col);
            Rect(new Rect(r.xMax - w - g, r.yMax - ly, ww, ly + g), col);
        }
    }

    /// <summary>Any-angle line, drawn as a rotated quad.</summary>
    public static void Line(Vector2 a, Vector2 b, Color c, float w)
    {
        var d = b - a;
        float len = d.magnitude;
        if (len < 0.5f || float.IsNaN(len)) return;
        if (len > 20000f) { d = d / len * 20000f; len = 20000f; }
        float ang = Mathf.Atan2(d.y, d.x) * Mathf.Rad2Deg;
        var saved = GUI.matrix;
        var prev = GUI.color;
        GUI.matrix = saved * Matrix4x4.TRS(new Vector3(a.x, a.y, 0f), Quaternion.Euler(0f, 0f, ang), Vector3.one);
        GUI.color = c;
        GUI.DrawTexture(new Rect(0f, -w * 0.5f, len, w), White);
        GUI.matrix = saved;
        GUI.color = prev;
    }

    public static void LineShadowed(Vector2 a, Vector2 b, Color c, float w)
    {
        Line(a, b, new Color(0f, 0f, 0f, c.a * 0.55f), w + 2f);
        Line(a, b, c, w);
    }

    /// <summary>Diamond marker centred on <paramref name="p"/>.</summary>
    public static void Diamond(Vector2 p, float size, Color c, float w)
    {
        var top = new Vector2(p.x, p.y - size);
        var right = new Vector2(p.x + size, p.y);
        var bottom = new Vector2(p.x, p.y + size);
        var left = new Vector2(p.x - size, p.y);
        LineShadowed(top, right, c, w);
        LineShadowed(right, bottom, c, w);
        LineShadowed(bottom, left, c, w);
        LineShadowed(left, top, c, w);
        Rect(new Rect(p.x - 1.5f, p.y - 1.5f, 3f, 3f), c);
    }

    /// <summary>Arrow head pointing along <paramref name="dir"/> (unit vector) with its tip at <paramref name="tip"/>.</summary>
    public static void Arrow(Vector2 tip, Vector2 dir, float size, Color c, float w)
    {
        var back = tip - dir * size;
        var side = new Vector2(-dir.y, dir.x) * (size * 0.6f);
        var l = back + side;
        var r = back - side;
        LineShadowed(tip, l, c, w);
        LineShadowed(tip, r, c, w);
        LineShadowed(l, r, c, w);
        // Fill-ish: a few inner strokes.
        for (int i = 1; i <= 3; i++)
        {
            float f = i / 4f;
            Line(Vector2.Lerp(tip, l, f), Vector2.Lerp(tip, r, f), c, w);
        }
    }

    private static GUIStyle Text(int size)
    {
        if (_text == null)
        {
            _text = new GUIStyle();
            _text.alignment = TextAnchor.MiddleCenter;
            _text.richText = false; // player names are untrusted
            _text.clipping = TextClipping.Overflow;
            _text.wordWrap = false;
            _text.fontStyle = FontStyle.Bold;
        }
        if (_textSize != size) { _text.fontSize = size; _textSize = size; }
        return _text;
    }

    public static Vector2 Measure(string s, int size) => Text(size).CalcSize(new GUIContent(s));

    /// <summary>Text with a 1px dark shadow, centred horizontally on <paramref name="cx"/>; returns its height.</summary>
    public static float Label(float cx, float y, string s, int size, Color c)
    {
        var st = Text(size);
        var sz = st.CalcSize(new GUIContent(s));
        var r = new Rect(cx - sz.x * 0.5f, y, sz.x, sz.y);
        st.normal.textColor = new Color(0f, 0f, 0f, c.a * 0.85f);
        GUI.Label(new Rect(r.x + 1f, r.y + 1f, r.width, r.height), s, st);
        st.normal.textColor = c;
        GUI.Label(r, s, st);
        return sz.y;
    }

    /// <summary>Left-aligned shadowed text.</summary>
    public static void LabelLeft(Rect r, string s, int size, Color c)
    {
        var st = Text(size);
        var prevAlign = st.alignment;
        st.alignment = TextAnchor.MiddleLeft;
        st.normal.textColor = new Color(0f, 0f, 0f, c.a * 0.85f);
        GUI.Label(new Rect(r.x + 1f, r.y + 1f, r.width, r.height), s, st);
        st.normal.textColor = c;
        GUI.Label(r, s, st);
        st.alignment = prevAlign;
    }

    public static Color HealthColor(float h)
    {
        if (h < 0f) return new Color(0.55f, 0.59f, 0.65f);
        return h > 0.5f
            ? Color.Lerp(new Color(0.95f, 0.70f, 0.24f), new Color(0.30f, 0.76f, 0.54f), (h - 0.5f) * 2f)
            : Color.Lerp(new Color(0.90f, 0.33f, 0.29f), new Color(0.95f, 0.70f, 0.24f), h * 2f);
    }
}
