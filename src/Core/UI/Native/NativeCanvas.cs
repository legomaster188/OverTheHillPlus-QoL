using System;
using System.Collections.Generic;
using System.Text;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace OverTheHillPlus.UI.Native;

/// <summary>Stacking inside a layer: backgrounds, then highlight bars, then widget parts, then text.</summary>
internal enum Depth
{
    Back = 0,
    Mid = 1,
    Front = 2,
}

/// <summary>How a piece of native text looks. Sizes are menu units (the same units the IMGUI layout uses).</summary>
internal struct TextSpec
{
    public float Size;
    public Color Color;
    public TextAnchor Align;
    public bool Bold;
    public bool Heading;
    public bool Wrap;
    public bool Upper;

    public TextSpec(float size, Color color, TextAnchor align = TextAnchor.MiddleLeft, bool bold = false, bool heading = false, bool wrap = false, bool upper = false)
    {
        Size = size; Color = color; Align = align; Bold = bold; Heading = heading; Wrap = wrap; Upper = upper;
    }

    public TextSpec With(Color c) { var s = this; s.Color = c; return s; }
    public TextSpec With(TextAnchor a) { var s = this; s.Align = a; return s; }

    public bool SameLook(in TextSpec o) =>
        Mathf.Abs(Size - o.Size) < 0.001f && Align == o.Align && Bold == o.Bold && Heading == o.Heading && Wrap == o.Wrap && Upper == o.Upper;
}

/// <summary>
/// An immediate-mode front end over a pool of retained uGUI objects. The menu's normal draw pass (the same widget
/// calls that drive the IMGUI layout and input) reports rectangles and text here during the Repaint event; each call
/// takes the next pooled Image or TextMeshProUGUI of its layer and only touches the properties that changed since
/// the last frame. Unused elements are deactivated at the end of the frame. Nothing here takes input (no raycasts):
/// clicks, sliders and text fields stay IMGUI controls laid over the same rectangles.
///
/// Layers: chrome (window, header, tabs, footer), content (clipped by a RectMask2D to the scroll area) and overlay
/// (tooltips). Canvas: Screen Space Overlay, sort order 32000, CanvasScaler 1920x1080.
/// </summary>
internal static class NativeCanvas
{
    private const int MaxPerPool = 1500;

    private sealed class FillEl
    {
        public GameObject Go;
        public RectTransform Rt;
        public Image Img;
        public FunCore.UI.SkewedImage Skew;
        public bool WantSkew; // asked for a skewed image (Skew stays null if the game type couldn't be added)
        public bool On;
        public Vector4 Pos = new(float.NaN, 0, 0, 0);
        public Color Col = new(-1, -1, -1, -1);
        public SpriteLook Shape;
        public float Mult = -1f, SkewX = float.NaN;
        public int Ver = -1;
    }

    private sealed class TextEl
    {
        public GameObject Go;
        public RectTransform Rt;
        public TextMeshProUGUI Tmp;
        public bool On;
        public Vector4 Pos = new(float.NaN, 0, 0, 0);
        public string Text;
        public Color Col = new(-1, -1, -1, -1);
        public TextSpec Spec;
        public float SizePx = -1f;
        public bool HasSpec;
        public int Ver = -1;
    }

    private sealed class Layer
    {
        public string Name;
        public RectTransform Root;
        public bool Clip;
        public readonly RectTransform[] FillParents = new RectTransform[3];
        public readonly List<FillEl>[] Fills = { new(), new(), new() };
        public readonly int[] FillUsed = new int[3];
        public RectTransform TextParent;
        public readonly List<TextEl> Texts = new();
        public int TextUsed;
    }

    private static GameObject _root;
    private static Canvas _canvas;
    private static Layer _chrome, _content, _overlay, _cur;
    private static TextMeshProUGUI _measure;
    private static bool _shown;
    private static float _k = 1f;
    private static Vector2 _off;
    private static Rect _view;
    private static Vector2 _scroll;
    private static Vector4 _viewPos = new(float.NaN, 0, 0, 0);
    private static bool _overflowLogged;

    /// <summary>True between BeginFrame and EndFrame (inside the menu's Repaint pass in the native style).</summary>
    internal static bool Emitting { get; private set; }

    internal static bool Built => _root != null && (bool)_root;

    // ---------- lifecycle ----------

    /// <summary>Builds the canvas (and reads the game's look) if needed. Throws on failure; the caller falls back.</summary>
    internal static void Ensure()
    {
        NativeLook.Ensure();
        if (Built) return;
        Build();
    }

    private static void Build()
    {
        DestroyObjects();
        _root = new GameObject("OverTheHillPlus.NativeMenu");
        Object.DontDestroyOnLoad(_root);
        _root.layer = 5; // UI
        _canvas = _root.AddComponent<Canvas>();
        _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        _canvas.sortingOrder = 32000;
        _canvas.pixelPerfect = false;
        try
        {
            _canvas.additionalShaderChannels |= AdditionalCanvasShaderChannels.TexCoord1 | AdditionalCanvasShaderChannels.Normal | AdditionalCanvasShaderChannels.Tangent;
        }
        catch (Exception ex) { DebugLog.Error("core", ex, "NativeCanvas.Build"); /* TMP sets these itself when it can */ }
        var scaler = _root.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
        scaler.matchWidthOrHeight = 1f;

        var rootRt = _root.GetComponent<RectTransform>();
        _chrome = MakeLayer("Chrome", rootRt, false);
        _content = MakeLayer("Content", rootRt, true);
        _overlay = MakeLayer("Overlay", rootRt, false);

        var mgo = new GameObject("Measure");
        mgo.layer = 5;
        var mrt = mgo.AddComponent<RectTransform>();
        mrt.SetParent(rootRt, false);
        TopLeft(mrt);
        mrt.anchoredPosition = new Vector2(-20000f, 20000f);
        mrt.sizeDelta = new Vector2(100f, 20f);
        _measure = mgo.AddComponent<TextMeshProUGUI>();
        _measure.raycastTarget = false;
        _measure.richText = true;
        _measure.color = new Color(1f, 1f, 1f, 0f);
        _measure.font = NativeLook.Body;

        _root.SetActive(false);
        _shown = false;
        MeasureCache.Clear();
        GlyphCache.Clear();
        SanitizeCache.Clear();
    }

    private static Layer MakeLayer(string name, RectTransform parent, bool clip)
    {
        var l = new Layer { Name = name, Clip = clip };
        var go = new GameObject(name);
        go.layer = 5;
        l.Root = go.AddComponent<RectTransform>();
        l.Root.SetParent(parent, false);
        if (clip)
        {
            TopLeft(l.Root);
            go.AddComponent<RectMask2D>();
        }
        else Stretch(l.Root);
        for (int d = 0; d < 3; d++) l.FillParents[d] = Child(l.Root, ((Depth)d).ToString());
        l.TextParent = Child(l.Root, "Text");
        return l;
    }

    private static RectTransform Child(RectTransform parent, string name)
    {
        var go = new GameObject(name);
        go.layer = 5;
        var rt = go.AddComponent<RectTransform>();
        rt.SetParent(parent, false);
        Stretch(rt);
        return rt;
    }

    private static void Stretch(RectTransform rt)
    {
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.pivot = new Vector2(0f, 1f);
        rt.offsetMin = Vector2.zero;
        rt.offsetMax = Vector2.zero;
    }

    private static void TopLeft(RectTransform rt)
    {
        rt.anchorMin = new Vector2(0f, 1f);
        rt.anchorMax = new Vector2(0f, 1f);
        rt.pivot = new Vector2(0f, 1f);
    }

    internal static void Hide()
    {
        Emitting = false;
        if (!_shown) return;
        _shown = false;
        try { if (Built) _root.SetActive(false); }
        catch (Exception ex) { DebugLog.Error("core", ex, "NativeCanvas.Hide"); /* destroyed */ }
    }

    internal static void Destroy()
    {
        Emitting = false;
        DestroyObjects();
    }

    private static void DestroyObjects()
    {
        try { if (_root != null && (bool)_root) Object.Destroy(_root); }
        catch (Exception ex) { DebugLog.Error("core", ex, "NativeCanvas.DestroyObjects"); }
        _root = null;
        _canvas = null;
        _chrome = _content = _overlay = _cur = null;
        _measure = null;
        _shown = false;
        _viewPos = new Vector4(float.NaN, 0, 0, 0);
    }

    // ---------- frame ----------

    /// <summary>
    /// Starts a frame. <paramref name="guiScale"/> is the IMGUI matrix scale (menu unit -> screen pixel);
    /// <paramref name="offset"/> is added to chrome and overlay rectangles (menu units).
    /// </summary>
    internal static void BeginFrame(float guiScale, Vector2 offset)
    {
        if (!Built) return;
        if (!_shown)
        {
            _root.SetActive(true);
            _shown = true;
        }
        float sf = 1f;
        try { sf = _canvas.scaleFactor; } catch (Exception ex) { DebugLog.Error("core", ex, "NativeCanvas.BeginFrame"); }
        if (sf < 0.01f) sf = Mathf.Max(0.01f, Screen.height / 1080f);
        _k = guiScale / sf;
        _off = offset;
        foreach (var l in new[] { _chrome, _content, _overlay })
        {
            for (int d = 0; d < 3; d++) l.FillUsed[d] = 0;
            l.TextUsed = 0;
        }
        _cur = _chrome;
        Emitting = true;
    }

    /// <summary>Following calls draw into the clipped content layer: <paramref name="area"/> is the scroll view in
    /// menu units (chrome space), and rectangles are content coordinates scrolled by <paramref name="scroll"/>.</summary>
    internal static void BeginContent(Rect area, Vector2 scroll)
    {
        if (!Emitting) return;
        _view = area;
        _scroll = scroll;
        var p = new Vector4((area.x + _off.x) * _k, (area.y + _off.y) * _k, area.width * _k, area.height * _k);
        if (!Same(p, _viewPos))
        {
            _viewPos = p;
            _content.Root.anchoredPosition = new Vector2(p.x, -p.y);
            _content.Root.sizeDelta = new Vector2(p.z, p.w);
        }
        _cur = _content;
    }

    internal static void EndContent()
    {
        if (Emitting) _cur = _chrome;
    }

    internal static void BeginOverlay()
    {
        if (Emitting) _cur = _overlay;
    }

    internal static void EndFrame()
    {
        if (!Emitting) return;
        Emitting = false;
        foreach (var l in new[] { _chrome, _content, _overlay })
        {
            for (int d = 0; d < 3; d++)
            {
                var list = l.Fills[d];
                for (int i = l.FillUsed[d]; i < list.Count; i++)
                    if (list[i].On) { list[i].On = false; list[i].Go.SetActive(false); }
            }
            for (int i = l.TextUsed; i < l.Texts.Count; i++)
                if (l.Texts[i].On) { l.Texts[i].On = false; l.Texts[i].Go.SetActive(false); }
        }
    }

    // ---------- primitives ----------

    private static bool Map(Rect r, out Vector4 p)
    {
        if (_cur.Clip)
        {
            if (r.yMax < _scroll.y || r.y > _scroll.y + _view.height || r.xMax < _scroll.x || r.x > _scroll.x + _view.width)
            {
                p = default;
                return false;
            }
            p = new Vector4((r.x - _scroll.x) * _k, (r.y - _scroll.y) * _k, r.width * _k, r.height * _k);
        }
        else p = new Vector4((r.x + _off.x) * _k, (r.y + _off.y) * _k, r.width * _k, r.height * _k);
        return true;
    }

    private static bool Same(Vector4 a, Vector4 b) =>
        Mathf.Abs(a.x - b.x) < 0.01f && Mathf.Abs(a.y - b.y) < 0.01f && Mathf.Abs(a.z - b.z) < 0.01f && Mathf.Abs(a.w - b.w) < 0.01f;

    private static bool Same(Color a, Color b) =>
        Mathf.Abs(a.r - b.r) < 0.002f && Mathf.Abs(a.g - b.g) < 0.002f && Mathf.Abs(a.b - b.b) < 0.002f && Mathf.Abs(a.a - b.a) < 0.002f;

    private static Color Tint(Color c)
    {
        var g = GUI.color;
        return new Color(c.r * g.r, c.g * g.g, c.b * g.b, c.a * g.a);
    }

    /// <summary>A filled shape (game sprite or our rounded rectangle), tinted by <paramref name="color"/> and GUI.color.</summary>
    internal static void Fill(Rect r, Color color, SpriteLook shape, Depth depth = Depth.Front)
    {
        if (!Emitting || r.width <= 0.5f || r.height <= 0.5f || shape == null) return;
        var c = Tint(color);
        if (c.a <= 0.004f) return;
        if (!Map(r, out var p)) return;
        var el = NextFill(depth, shape.Skewed && depth == Depth.Mid);
        if (el == null) return;

        int ver = NativeLook.Version;
        if (!ReferenceEquals(el.Shape, shape) || el.Ver != ver)
        {
            el.Shape = shape;
            el.Ver = ver;
            el.Img.sprite = shape.Sprite;
            el.Img.type = shape.Sliced ? Image.Type.Sliced : Image.Type.Simple;
            el.Mult = -1f;
        }
        if (shape.Sliced && shape.BorderPx > 0f)
        {
            // Corner size in canvas units: what the shape asks for, never more than half the element.
            float want = Mathf.Max(0.5f, Mathf.Min(shape.CornerUnits * _k, Mathf.Min(p.z, p.w) * 0.5f));
            float mult = shape.BorderPx * (100f / Mathf.Max(1f, shape.Ppu)) / want;
            if (Mathf.Abs(mult - el.Mult) > 0.01f)
            {
                el.Mult = mult;
                el.Img.pixelsPerUnitMultiplier = mult;
            }
        }
        if (el.Skew != null)
        {
            float sx = shape.SkewPerHeight * p.w;
            if (!(Mathf.Abs(sx - el.SkewX) < 0.01f))
            {
                el.SkewX = sx;
                try { el.Skew.skewX = sx; el.Skew.SetVerticesDirty(); } catch (Exception ex) { DebugLog.Error("core", ex, "NativeCanvas.Fill"); }
            }
        }
        if (!Same(c, el.Col)) { el.Col = c; el.Img.color = c; }
        if (!Same(p, el.Pos))
        {
            el.Pos = p;
            el.Rt.anchoredPosition = new Vector2(p.x, -p.y);
            el.Rt.sizeDelta = new Vector2(p.z, p.w);
        }
    }

    // Shapes for Theme.Fill radii, made once per radius.
    private static readonly Dictionary<int, SpriteLook> RadiusShapes = new();

    /// <summary>Theme.Fill's rounded fills, drawn natively while the native menu is being emitted.</summary>
    internal static void FillRounded(Rect r, Color color, int radius)
    {
        if (!Emitting) return;
        if (!RadiusShapes.TryGetValue(radius, out var shape) || shape.Sprite == null || !(bool)shape.Sprite)
            RadiusShapes[radius] = shape = radius <= 0 ? NativeLook.Square : NativeLook.Rounded(radius);
        Fill(r, color, shape, Depth.Front);
    }

    internal static void Text(Rect r, string text, in TextSpec spec)
    {
        if (!Emitting || string.IsNullOrEmpty(text) || r.width <= 0.5f || r.height <= 0.5f) return;
        if (!Map(r, out var p)) return;
        var el = NextText();
        if (el == null) return;
        var tmp = el.Tmp;
        int ver = NativeLook.Version;
        var font = spec.Heading ? NativeLook.Heading : NativeLook.Body;
        if (el.Ver != ver || !el.HasSpec || el.Spec.Heading != spec.Heading)
        {
            tmp.font = font;
            el.Ver = ver;
            el.Text = null; // re-check glyphs against the new font
        }
        if (!el.HasSpec || !el.Spec.SameLook(spec))
        {
            el.HasSpec = true;
            el.Spec = spec;
            tmp.alignment = Map(spec.Align, spec.Wrap);
            tmp.textWrappingMode = spec.Wrap ? TextWrappingModes.Normal : TextWrappingModes.NoWrap;
            tmp.overflowMode = spec.Wrap ? TextOverflowModes.Overflow : TextOverflowModes.Ellipsis;
            var fs = FontStyles.Normal;
            if (spec.Bold) fs |= FontStyles.Bold;
            if (spec.Upper) fs |= FontStyles.UpperCase;
            tmp.fontStyle = fs;
            el.SizePx = -1f;
        }
        float size = spec.Size * _k;
        if (Mathf.Abs(size - el.SizePx) > 0.01f)
        {
            el.SizePx = size;
            tmp.fontSize = size;
            // Single lines shrink a little before they get an ellipsis: the game's fonts are wider than IMGUI's.
            tmp.enableAutoSizing = !spec.Wrap;
            if (!spec.Wrap)
            {
                tmp.fontSizeMax = size;
                tmp.fontSizeMin = size * 0.72f;
            }
        }
        if (!ReferenceEquals(text, el.Text) && text != el.Text)
        {
            el.Text = text;
            tmp.text = Sanitize(text, font);
        }
        var c = Tint(spec.Color);
        if (!Same(c, el.Col)) { el.Col = c; tmp.color = c; }
        if (!Same(p, el.Pos))
        {
            el.Pos = p;
            el.Rt.anchoredPosition = new Vector2(p.x, -p.y);
            el.Rt.sizeDelta = new Vector2(p.z, p.w);
        }
    }

    private static TextAlignmentOptions Map(TextAnchor a, bool wrap)
    {
        switch (a)
        {
            case TextAnchor.UpperLeft: return TextAlignmentOptions.TopLeft;
            case TextAnchor.UpperCenter: return TextAlignmentOptions.Top;
            case TextAnchor.UpperRight: return TextAlignmentOptions.TopRight;
            case TextAnchor.MiddleCenter: return wrap ? TextAlignmentOptions.Top : TextAlignmentOptions.Center;
            case TextAnchor.MiddleRight: return wrap ? TextAlignmentOptions.TopRight : TextAlignmentOptions.Right;
            case TextAnchor.LowerLeft: return TextAlignmentOptions.BottomLeft;
            case TextAnchor.LowerCenter: return TextAlignmentOptions.Bottom;
            case TextAnchor.LowerRight: return TextAlignmentOptions.BottomRight;
            default: return wrap ? TextAlignmentOptions.TopLeft : TextAlignmentOptions.Left;
        }
    }

    // ---------- pools ----------

    private static FillEl NextFill(Depth depth, bool skewed)
    {
        int d = (int)depth;
        var list = _cur.Fills[d];
        int i = _cur.FillUsed[d];
        if (i >= MaxPerPool) { Overflow(); return null; }
        _cur.FillUsed[d] = i + 1;
        if (i < list.Count && list[i].WantSkew != skewed)
        {
            // The highlight look changed between skewed and plain: replace this element in place.
            var old = list[i];
            var fresh = NewFill(_cur.FillParents[d], skewed);
            fresh.Go.transform.SetSiblingIndex(old.Go.transform.GetSiblingIndex());
            Object.Destroy(old.Go);
            list[i] = fresh;
        }
        if (i >= list.Count) list.Add(NewFill(_cur.FillParents[d], skewed));
        var el = list[i];
        if (!el.On) { el.On = true; el.Go.SetActive(true); }
        return el;
    }

    private static FillEl NewFill(RectTransform parent, bool skewed)
    {
        var go = new GameObject("f");
        go.layer = 5;
        var rt = go.AddComponent<RectTransform>();
        rt.SetParent(parent, false);
        TopLeft(rt);
        var el = new FillEl { Go = go, Rt = rt, On = true, WantSkew = skewed };
        if (skewed)
        {
            try
            {
                el.Skew = go.AddComponent<FunCore.UI.SkewedImage>();
                el.Img = el.Skew;
            }
            catch (Exception ex) { DebugLog.Error("core", ex, "NativeCanvas.NewFill"); el.Skew = null; }
        }
        if (el.Img == null) el.Img = go.AddComponent<Image>();
        el.Img.raycastTarget = false;
        return el;
    }

    private static TextEl NextText()
    {
        int i = _cur.TextUsed;
        if (i >= MaxPerPool) { Overflow(); return null; }
        _cur.TextUsed = i + 1;
        var list = _cur.Texts;
        if (i >= list.Count)
        {
            var go = new GameObject("t");
            go.layer = 5;
            var rt = go.AddComponent<RectTransform>();
            rt.SetParent(_cur.TextParent, false);
            TopLeft(rt);
            var tmp = go.AddComponent<TextMeshProUGUI>();
            tmp.raycastTarget = false;
            tmp.richText = true;
            tmp.margin = Vector4.zero;
            tmp.extraPadding = false;
            list.Add(new TextEl { Go = go, Rt = rt, Tmp = tmp, On = true });
        }
        var el = list[i];
        if (!el.On) { el.On = true; el.Go.SetActive(true); }
        return el;
    }

    private static void Overflow()
    {
        if (_overflowLogged) return;
        _overflowLogged = true;
        Core.CorePlugin.Logger?.LogWarning($"Native menu: more than {MaxPerPool} elements in one layer; the rest of this frame is not drawn.");
    }

    // ---------- text measuring and glyph fallback ----------

    private static readonly Dictionary<(string, int, int, bool, bool), float> MeasureCache = new();

    /// <summary>Height of wrapped text in menu units, measured with the native font (null when not possible).</summary>
    internal static float? MeasureHeight(string text, float width, in TextSpec spec)
    {
        if (_measure == null || string.IsNullOrEmpty(text)) return null;
        var key = (text, Mathf.RoundToInt(width), Mathf.RoundToInt(spec.Size * 10f), spec.Bold, spec.Heading);
        if (MeasureCache.TryGetValue(key, out var h)) return h;
        if (MeasureCache.Count > 3000) MeasureCache.Clear();
        var font = spec.Heading ? NativeLook.Heading : NativeLook.Body;
        if (!ReferenceEquals(_measure.font, font)) _measure.font = font;
        _measure.fontSize = spec.Size;
        _measure.enableAutoSizing = false;
        _measure.textWrappingMode = TextWrappingModes.Normal;
        _measure.fontStyle = spec.Bold ? FontStyles.Bold : FontStyles.Normal;
        var v = _measure.GetPreferredValues(Sanitize(text, font), width, 0f);
        if (!(v.y > 1f) || v.y > 5000f) return null; // nothing sensible: let the caller use the IMGUI measure
        h = Mathf.Ceil(v.y + 1f);
        MeasureCache[key] = h;
        return h;
    }

    private static readonly Dictionary<(IntPtr, char), bool> GlyphCache = new();
    private static readonly Dictionary<string, string> SanitizeCache = new();

    // A '<' a fallback introduces must never open a TMP tag with the text after it (labels render as rich text).
    private const string Lt = "<noparse><</noparse>";

    // Symbols the menu uses that a game font may not have, with plain replacements.
    private static string Fallback(char c, IntPtr fp, TMP_FontAsset font) => c switch
    {
        // Look-alikes HostNotice.Escape / PlayerNamesProperty.Clean use to neutralise markup: never a real bracket.
        '‹' => Has(fp, font, '«') ? "«" : "(",
        '›' => Has(fp, font, '»') ? "»" : ")",
        '×' => "x",
        '·' => "-",
        '•' => "*",
        '—' => "-",
        '–' => "-",
        '…' => "...",
        '≥' => ">=",
        '≤' => Lt + "=",
        '►' => ">",
        '▶' => ">",
        '◄' => Lt,
        '◂' => Has(fp, font, '◄') ? "◄" : Lt,
        '▸' => Has(fp, font, '►') ? "►" : ">",
        '◀' => Lt,
        '▼' => "v",
        '▲' => "^",
        '°' => "",
        '²' => "2",
        '³' => "3",
        '✓' => "OK",
        '←' => Lt + "-",
        '→' => "->",
        '↑' => "^",
        '↓' => "v",
        _ => "?",
    };

    private static string Sanitize(string s, TMP_FontAsset font)
    {
        if (s == null || font == null) return s;
        bool any = false;
        for (int i = 0; i < s.Length; i++) if (s[i] > 127) { any = true; break; }
        if (!any) return s;
        if (SanitizeCache.TryGetValue(s, out var done)) return done;
        if (SanitizeCache.Count > 1000) SanitizeCache.Clear();
        StringBuilder sb = null;
        var fp = font.Pointer;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c <= 127 || char.IsSurrogate(c) || Has(fp, font, c)) { sb?.Append(c); continue; }
            sb ??= new StringBuilder(s, 0, i, s.Length + 8);
            sb.Append(Fallback(c, fp, font));
        }
        string result = sb == null ? s : sb.ToString();
        SanitizeCache[s] = result;
        return result;
    }

    private static bool Has(IntPtr fp, TMP_FontAsset font, char c)
    {
        if (GlyphCache.TryGetValue((fp, c), out var has)) return has;
        try { has = font.HasCharacter(c, true, true); }
        catch (Exception ex) { DebugLog.Error("core", ex, "NativeCanvas.Has"); has = true; }
        GlyphCache[(fp, c)] = has;
        return has;
    }
}
