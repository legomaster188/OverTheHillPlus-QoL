using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using OverTheHillPlus.UI;
using OverTheHillPlus.UI.Native;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace OverTheHillPlus.Features.Public.ServerBrowserPlus;

/// <summary>
/// The PLUS quick bar built from real uGUI Images and TextMeshPro texts inside the game's own server list
/// (a child of ContextUIServerList), so it scales, fades and layers exactly like the browser around it and
/// disappears with the list tab. It copies the look of the game's browser screens: no panel, flat translucent
/// rows with white text in the game font, a cream fill under the pointer, yellow while pressed, and keycap
/// prompts for the hotkeys.
///
/// Immediate mode, driven from Update: <see cref="Begin"/>, then one call per widget (each returns true when it
/// was clicked), then <see cref="End"/>. Positions are 1080p units from the top-left of the screen / bar. None of
/// the elements take raycasts: clicks are read from the Input System here, and while the pointer is over the bar
/// the feature pauses the game's EventSystem (as the IMGUI bar did) so the click doesn't reach the list too.
/// Any exception disables this for the session and the feature falls back to the IMGUI bar.
/// </summary>
internal static class QuickBarNative
{
    internal enum Mode
    {
        /// <summary>Native bar not usable: draw the IMGUI bar.</summary>
        Fallback,
        /// <summary>Native bar in charge but not visible now (other browser tab): draw nothing.</summary>
        Hidden,
        /// <summary>Native bar drawn this frame.</summary>
        Native,
    }

    // ---- look, from the game's Mod Browser / server browser ----
    private static readonly Color RowFill = new(1f, 1f, 1f, 0.2f);
    private static readonly Color RowEdge = new(0f, 0f, 0f, 0.16f);
    private static readonly Color Cream = Theme.Hex(0xFFF6E0);
    private static readonly Color Yellow = Theme.Hex(0xFFD865);
    private static readonly Color Ink = Theme.Hex(0x22252A);
    private static readonly Color White = Color.white;
    private static readonly Color Faded = new(1f, 1f, 1f, 0.42f);
    private static readonly Color MarkOff = new(1f, 1f, 1f, 0.32f);
    private static readonly Color MarkOffHot = new(0.13f, 0.15f, 0.16f, 0.22f);

    private const float RowText = 16f, TitleText = 30f, SubText = 17f, KeyText = 12f, PromptText = 13f;

    private sealed class FillEl
    {
        public GameObject Go;
        public RectTransform Rt;
        public Image Img;
        public bool On;
        public Vector4 Pos = new(float.NaN, 0, 0, 0);
        public Color Col = new(-1, -1, -1, -1);
        public Sprite Spr;
    }

    private sealed class TextEl
    {
        public GameObject Go;
        public RectTransform Rt;
        public TextMeshProUGUI Tmp;
        public bool On;
        public Vector4 Pos = new(float.NaN, 0, 0, 0);
        public Color Col = new(-1, -1, -1, -1);
        public string Text;
        public float Size = -1f;
        public TextAlignmentOptions Align = (TextAlignmentOptions)(-1);
        public FontStyles Style = (FontStyles)(-1);
        public TMP_FontAsset Font;
    }

    private static GameObject _root;
    private static RectTransform _rt, _parent, _fills, _texts;
    private static IntPtr _parentPtr;
    private static Camera _cam;
    private static TMP_FontAsset _font;
    private static TextMeshProUGUI _measure;
    private static readonly List<FillEl> Fills = new();
    private static readonly List<TextEl> Texts = new();
    private static int _fillUsed, _textUsed;
    private static readonly Dictionary<(string, int, bool), float> Widths = new();
    private static Il2CppStructArray<Vector3> _corners;
    private static Sprite _white, _round, _caret;
    private static float _nextScaleCheck;
    private static int _frames;

    // input state for this frame (bar units, relative to the bar's top-left)
    private static Vector2 _m, _mAbs, _origin, _dragOffset;
    private static bool _held, _down, _up, _interactive, _dragging;
    private static int _ix, _hot = -1, _lastHot = -1, _pressed = -1;

    /// <summary>Set after an error: the IMGUI bar is used for the rest of the session.</summary>
    internal static bool Failed { get; private set; }

    /// <summary>The pointer is over the bar (or dragging it) and the browser takes input.</summary>
    internal static bool Hover { get; private set; }

    // ---------------------------------------------------------------- frame

    /// <summary>
    /// Starts a frame with the bar's top-left at (<paramref name="x"/>, <paramref name="y"/>) and the given size, in
    /// 1080p screen units. Only draw widgets when this returns <see cref="Mode.Native"/>, and call End() after.
    /// </summary>
    internal static Mode Begin(float x, float y, float w, float h)
    {
        Hover = false;
        if (Failed) return Mode.Fallback;
        ContextUIServerList list;
        try { list = BrowserPlus.Browser?.ui?.ContextUIServerList; }
        catch { list = null; }
        if (list == null) { Hide(); return Mode.Fallback; }

        bool active;
        try { active = list.gameObject.activeInHierarchy; }
        catch { active = false; } // being destroyed with the browser
        if (!active)
        {
            Hide(); // another tab of the browser (create server, join by code)
            _pressed = -1;
            _dragging = false;
            return Mode.Hidden;
        }
        if (!Build(list)) return Mode.Fallback;
        if (!_root.activeSelf) _root.SetActive(true);

        float s = Mathf.Max(0.01f, Screen.height / 1080f);
        Place(x, y, w, h, s);
        _origin = new Vector2(x, y);

        var mouse = Mouse.current;
        if (mouse != null)
        {
            var p = mouse.position.ReadValue();
            _mAbs = new Vector2(p.x / s, (Screen.height - p.y) / s);
            _m = _mAbs - _origin;
            _held = mouse.leftButton.isPressed;
            _down = mouse.leftButton.wasPressedThisFrame;
            _up = mouse.leftButton.wasReleasedThisFrame;
            if (_held || _up || mouse.delta.ReadValue().sqrMagnitude > 0.01f) _lastMouse = Time.unscaledTime;
        }
        else
        {
            _m = _mAbs = new Vector2(-99999f, -99999f);
            _held = _down = _up = false;
        }
        if (KeyboardOrPadUsed()) _lastMouse = -100f;
        // A pointer left parked over the bar must not keep the game's EventSystem paused for a keyboard or
        // controller player: the bar only takes the pointer while the mouse is actually in use.
        bool mouseInUse = Time.unscaledTime - _lastMouse < 8f;
        _interactive = mouseInUse && BrowserFocused(list);
        if (!_interactive) { _pressed = -1; _dragging = false; }
        Hover = _interactive && (_dragging || new Rect(0f, 0f, w, h).Contains(_m));

        _ix = 0;
        _hot = -1;
        _fillUsed = _textUsed = 0;
        return Mode.Native;
    }

    internal static void End()
    {
        for (int i = _fillUsed; i < Fills.Count; i++)
            if (Fills[i].On) { Fills[i].On = false; Fills[i].Go.SetActive(false); }
        for (int i = _textUsed; i < Texts.Count; i++)
            if (Texts[i].On) { Texts[i].On = false; Texts[i].Go.SetActive(false); }
        _lastHot = _hot;
        if (_up || !_held) _pressed = -1;
        if (_dragging && !_held) _dragging = false;
    }

    /// <summary>Hides the bar (kept for reuse).</summary>
    internal static void Hide()
    {
        Hover = false;
        try { if (_root != null && (bool)_root && _root.activeSelf) _root.SetActive(false); }
        catch { /* destroyed with the browser */ }
    }

    /// <summary>Switches to the IMGUI bar for this session (logged once).</summary>
    internal static void Fail(string where, Exception e)
    {
        if (Failed) return;
        Failed = true;
        Hover = false;
        Plugin.Logger.LogError($"[Browser+] game-styled quick bar failed ({where}), using the overlay bar this session: {e}");
        DestroyObjects();
    }

    private static float _lastMouse = -100f;

    private static bool KeyboardOrPadUsed()
    {
        try
        {
            var kb = Keyboard.current;
            if (kb != null && kb.anyKey.wasPressedThisFrame) return true;
            var gp = Gamepad.current;
            if (gp != null && (gp.buttonSouth.wasPressedThisFrame || gp.buttonEast.wasPressedThisFrame ||
                               gp.dpad.ReadValue().sqrMagnitude > 0.25f || gp.leftStick.ReadValue().sqrMagnitude > 0.25f))
                return true;
        }
        catch { }
        return false;
    }

    /// <summary>Whether the bar may take the pointer: not while a pop-up, the region list or a keyboard is up.</summary>
    private static bool BrowserFocused(ContextUIServerList list)
    {
        if (!Application.isFocused) return false;
        try
        {
            var b = BrowserPlus.Browser;
            if (b != null && (!b.isFocused || b.IsVirtualKeyboardOpen)) return false; // filters / join pop-up on top
        }
        catch { /* assume focused */ }
        try
        {
            if (list.IsVirtualKeyboardOpen) return false;
            var dd = list.RegionSelectionDropdown;
            if (dd != null && dd.IsExpanded) return false; // its list may lie over the bar
        }
        catch { }
        return true;
    }

    // ---------------------------------------------------------------- widgets

    /// <summary>A flat row: translucent white, cream under the pointer, yellow while pressed.</summary>
    internal static bool Button(Rect r, string label, bool enabled) => Row(r, label, enabled, false, false);

    /// <summary>A row with a small square that is yellow when <paramref name="on"/>.</summary>
    internal static bool Toggle(Rect r, string label, bool on) => Row(r, label, true, true, on);

    private static bool Row(Rect r, string label, bool enabled, bool toggle, bool on)
    {
        bool hot = Hot(r, enabled, out bool pressing, out bool clicked);
        bool lit = hot || pressing;
        Fill(r, pressing ? Yellow : hot ? Cream : RowFill, _white);
        if (!lit) Fill(new Rect(r.x, r.yMax - 1.5f, r.width, 1.5f), RowEdge, _white);
        float tx = r.x + 10f;
        if (toggle)
        {
            float m = 9f;
            Fill(new Rect(r.x + 9f, r.y + (r.height - m) / 2f, m, m), on ? (lit ? Ink : Yellow) : (lit ? MarkOffHot : MarkOff), _white);
            tx = r.x + 25f;
        }
        Text(new Rect(tx, r.y, r.xMax - tx - 6f, r.height), label, RowText, !enabled ? Faded : lit ? Ink : White,
            TextAlignmentOptions.Left, FontStyles.Normal);
        return clicked;
    }

    /// <summary>Large thin all-caps title, like the game's screen titles.</summary>
    internal static void Title(Rect r, string text) =>
        Text(r, text.ToUpperInvariant(), TitleText, White, TextAlignmentOptions.Left, FontStyles.Normal);

    /// <summary>The small label under a title, with the game's tiny yellow caret before it.</summary>
    internal static void SubLabel(Rect r, string text)
    {
        Fill(new Rect(r.x + 1f, r.y + r.height / 2f - 2.5f, 9f, 5.5f), Yellow, _caret);
        Text(new Rect(r.x + 14f, r.y, r.width - 14f, r.height), text, SubText, White, TextAlignmentOptions.Left, FontStyles.Normal);
    }

    /// <summary>
    /// A key prompt (white keycap with the key, then an upper-case label) ending at <paramref name="right"/>;
    /// moves <paramref name="right"/> left past it. Greyed when disabled. Clickable.
    /// </summary>
    internal static bool Prompt(ref float right, float y, float h, string key, string label, bool enabled)
    {
        label = label.ToUpperInvariant();
        float kw = Mathf.Max(h, Measure(key, KeyText, true) + 10f);
        float lw = Measure(label, PromptText, true) + 2f;
        float w = kw + 6f + lw;
        var r = new Rect(right - w, y, w, h);
        right = r.x - 16f;

        bool hot = Hot(r, enabled, out bool pressing, out bool clicked);
        var cap = new Rect(r.x, r.y, kw, h);
        Fill(cap, enabled ? (pressing ? Yellow : White) : Faded, _round);
        Text(cap, key, KeyText, Ink, TextAlignmentOptions.Center, FontStyles.Bold);
        Text(new Rect(cap.xMax + 6f, r.y, lw + 4f, h), label, PromptText, !enabled ? Faded : hot ? Yellow : White,
            TextAlignmentOptions.Left, FontStyles.Bold);
        return clicked;
    }

    /// <summary>
    /// Lets the bar be dragged by <paramref name="handle"/>; while dragging, returns true with the new top-left
    /// (1080p screen units).
    /// </summary>
    internal static bool DragHandle(Rect handle, out float x, out float y)
    {
        x = y = 0f;
        if (_interactive && _down && _pressed < 0 && !_dragging && handle.Contains(_m))
        {
            _dragging = true;
            _dragOffset = _m;
        }
        if (!_dragging || !_held) return false;
        x = Mathf.Round(_mAbs.x - _dragOffset.x);
        y = Mathf.Round(_mAbs.y - _dragOffset.y);
        return true;
    }

    private static bool Hot(Rect r, bool enabled, out bool pressing, out bool clicked)
    {
        int id = _ix++;
        pressing = clicked = false;
        bool hot = _interactive && enabled && !_dragging && r.Contains(_m);
        if (!hot) return false;
        _hot = id;
        if (_lastHot != id) NativeLook.PlayHover();
        if (_down) _pressed = id;
        pressing = _held && _pressed == id;
        if (_up && _pressed == id)
        {
            clicked = true;
            NativeLook.PlayPress();
        }
        return true;
    }

    // ---------------------------------------------------------------- building

    private static bool Alive(Object o)
    {
        try { return o is not null && (bool)o; }
        catch { return false; }
    }

    private static bool Build(ContextUIServerList list)
    {
        // The browser screen's root (ContextUIServerListMenu) rather than the list itself, so nothing that sizes or
        // masks the list part can clip the bar; it is hidden whenever the list tab isn't showing.
        RectTransform parent = null;
        try { parent = BrowserPlus.Browser?.ui?.transform.TryCast<RectTransform>(); }
        catch { parent = null; }
        if (parent == null) parent = list.transform.TryCast<RectTransform>();
        if (parent == null) return false;
        if (Alive(_root) && _parentPtr == parent.Pointer && Alive(_font))
        {
            if (_rt.GetSiblingIndex() != parent.childCount - 1) _rt.SetAsLastSibling(); // stay above the list
            return true;
        }

        DestroyObjects();
        EnsureSprites();
        _font = FindFont(list);
        if (!Alive(_font)) throw new InvalidOperationException("no TextMeshPro font for the quick bar");
        try { NativeLook.Ensure(); } catch { /* sounds are optional */ }

        _parent = parent;
        _parentPtr = parent.Pointer;
        _cam = null;
        try
        {
            var canvas = list.GetComponentInParent<Canvas>();
            var rootCanvas = canvas == null ? null : canvas.rootCanvas ?? canvas;
            if (rootCanvas != null && rootCanvas.renderMode != RenderMode.ScreenSpaceOverlay) _cam = rootCanvas.worldCamera;
        }
        catch { _cam = null; }

        _root = new GameObject("OTHPlus_QuickBar");
        _root.layer = list.gameObject.layer;
        _rt = _root.AddComponent<RectTransform>();
        _rt.SetParent(parent, false);
        TopLeft(_rt);
        _rt.localRotation = Quaternion.identity;
        _rt.localScale = Vector3.one;
        var le = _root.AddComponent<LayoutElement>();
        le.ignoreLayout = true; // never let a layout group on the list move or size us
        _rt.SetAsLastSibling();

        _fills = Child(_rt, "Fills");
        _texts = Child(_rt, "Texts");

        var mgo = new GameObject("Measure");
        mgo.layer = _root.layer;
        var mrt = mgo.AddComponent<RectTransform>();
        mrt.SetParent(_rt, false);
        TopLeft(mrt);
        mrt.anchoredPosition = new Vector2(-20000f, 20000f);
        mrt.sizeDelta = new Vector2(10f, 10f);
        _measure = mgo.AddComponent<TextMeshProUGUI>();
        _measure.font = _font;
        _measure.raycastTarget = false;
        _measure.richText = false;
        _measure.color = new Color(1f, 1f, 1f, 0f);
        _measure.textWrappingMode = TextWrappingModes.NoWrap;

        _corners ??= new Il2CppStructArray<Vector3>(4);
        _nextScaleCheck = 0f;
        _frames = 0;
        Widths.Clear();
        if (!_loggedBuild)
        {
            _loggedBuild = true;
            string mask = "none";
            try { if (_rt.GetComponentInParent<RectMask2D>() != null || _rt.GetComponentInParent<Mask>() != null) mask = "yes"; }
            catch { mask = "?"; }
            Plugin.Logger.LogInfo($"[Browser+] game-styled quick bar built under '{Safe(parent.gameObject)}' " +
                                  $"(camera {(_cam == null ? "overlay" : Safe(_cam.gameObject))}, font {Safe(_font)}, mask above: {mask})");
        }
        return true;
    }

    private static bool _loggedBuild;

    private static string Safe(Object o)
    {
        try { return o == null ? "-" : o.name; }
        catch { return "?"; }
    }

    private static TMP_FontAsset FindFont(ContextUIServerList list)
    {
        // The game's own filter row label (NunitoSans in the browser), then the search box, then the menu's font.
        try
        {
            var f = list.HideFullServersFilter?.label?.GetComponent<TMP_Text>()?.font;
            if (Alive(f)) return f;
        }
        catch { }
        try
        {
            var f = list.SearchBarInputField?.textComponent?.font;
            if (Alive(f)) return f;
        }
        catch { }
        try
        {
            NativeLook.Ensure();
            if (Alive(NativeLook.Body)) return NativeLook.Body;
        }
        catch { }
        try { return TMP_Settings.defaultFontAsset; }
        catch { return null; }
    }

    private static RectTransform Child(RectTransform parent, string name)
    {
        var go = new GameObject(name);
        go.layer = parent.gameObject.layer;
        var rt = go.AddComponent<RectTransform>();
        rt.SetParent(parent, false);
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.pivot = new Vector2(0f, 1f);
        rt.offsetMin = Vector2.zero;
        rt.offsetMax = Vector2.zero;
        return rt;
    }

    private static void TopLeft(RectTransform rt)
    {
        rt.anchorMin = new Vector2(0f, 1f);
        rt.anchorMax = new Vector2(0f, 1f);
        rt.pivot = new Vector2(0f, 1f);
    }

    /// <summary>Scales the bar so one unit is a 1080p pixel on screen and puts its top-left at the given point.</summary>
    private static void Place(float x, float y, float w, float h, float s)
    {
        var size = new Vector2(w, h);
        if ((_rt.sizeDelta - size).sqrMagnitude > 0.01f) _rt.sizeDelta = size;

        float now = Time.unscaledTime;
        if (_frames < 3 || now >= _nextScaleCheck)
        {
            _frames++;
            _nextScaleCheck = now + 0.5f;
            _rt.GetWorldCorners(_corners);
            var a = RectTransformUtility.WorldToScreenPoint(_cam, _corners[0]);
            var b = RectTransformUtility.WorldToScreenPoint(_cam, _corners[3]);
            float px = Vector2.Distance(a, b), want = w * s;
            if (px > 1f && want > 1f)
            {
                float k = want / px;
                if (Mathf.Abs(k - 1f) > 0.002f && k > 0.01f && k < 100f) _rt.localScale = _rt.localScale * k;
            }
        }

        // Re-placed every frame: the screen point stays put even while the browser animates in.
        var screen = new Vector2(x * s, Screen.height - y * s);
        if (RectTransformUtility.ScreenPointToWorldPointInRectangle(_parent, screen, _cam, out var world))
        {
            if ((_rt.position - world).sqrMagnitude > 1e-6f) _rt.position = world;
        }
    }

    private static void EnsureSprites()
    {
        if (Alive(_white) && Alive(_round) && Alive(_caret)) return;

        var wt = new Texture2D(4, 4, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave };
        for (int y = 0; y < 4; y++) for (int x = 0; x < 4; x++) wt.SetPixel(x, y, Color.white);
        wt.Apply();
        _white = Sprite.Create(wt, new Rect(0, 0, 4, 4), new Vector2(0.5f, 0.5f), 100f);
        _white.hideFlags = HideFlags.HideAndDontSave;

        // Keycap: rounded corners (6 px border, drawn at half size: 3 units).
        const int r = 6, n = r * 2 + 2;
        var rt = new Texture2D(n, n, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave, wrapMode = TextureWrapMode.Clamp };
        for (int y = 0; y < n; y++)
        for (int x = 0; x < n; x++)
        {
            float px = x + 0.5f, py = y + 0.5f;
            float cx = Mathf.Clamp(px, r, n - r), cy = Mathf.Clamp(py, r, n - r);
            float d = Mathf.Sqrt((px - cx) * (px - cx) + (py - cy) * (py - cy));
            rt.SetPixel(x, y, new Color(1f, 1f, 1f, Mathf.Clamp01(r - d + 0.5f)));
        }
        rt.Apply();
        _round = Sprite.Create(rt, new Rect(0, 0, n, n), new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect, new Vector4(r, r, r, r));
        _round.hideFlags = HideFlags.HideAndDontSave;

        // Caret: a small downward triangle.
        const int cw = 18, ch = 11;
        var ct = new Texture2D(cw, ch, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave, wrapMode = TextureWrapMode.Clamp };
        for (int y = 0; y < ch; y++)
        for (int x = 0; x < cw; x++)
        {
            // y = 0 is the bottom row (the tip).
            float half = (y + 0.5f) / ch * (cw / 2f);
            float dx = Mathf.Abs(x + 0.5f - cw / 2f);
            ct.SetPixel(x, y, new Color(1f, 1f, 1f, Mathf.Clamp01(half - dx + 0.5f)));
        }
        ct.Apply();
        _caret = Sprite.Create(ct, new Rect(0, 0, cw, ch), new Vector2(0.5f, 0.5f), 100f);
        _caret.hideFlags = HideFlags.HideAndDontSave;
    }

    private static void DestroyObjects()
    {
        try { if (Alive(_root)) Object.Destroy(_root); }
        catch { }
        _root = null;
        _rt = _parent = _fills = _texts = null;
        _parentPtr = IntPtr.Zero;
        _measure = null;
        Fills.Clear();
        Texts.Clear();
        _fillUsed = _textUsed = 0;
        _pressed = _hot = _lastHot = -1;
        _dragging = false;
        Hover = false;
    }

    // ---------------------------------------------------------------- primitives

    private static bool Same(Vector4 a, Vector4 b) =>
        Mathf.Abs(a.x - b.x) < 0.01f && Mathf.Abs(a.y - b.y) < 0.01f && Mathf.Abs(a.z - b.z) < 0.01f && Mathf.Abs(a.w - b.w) < 0.01f;

    private static bool Same(Color a, Color b) =>
        Mathf.Abs(a.r - b.r) < 0.002f && Mathf.Abs(a.g - b.g) < 0.002f && Mathf.Abs(a.b - b.b) < 0.002f && Mathf.Abs(a.a - b.a) < 0.002f;

    private static void Fill(Rect r, Color c, Sprite sprite)
    {
        if (r.width <= 0.1f || r.height <= 0.1f) return;
        FillEl el;
        if (_fillUsed < Fills.Count) el = Fills[_fillUsed];
        else
        {
            var go = new GameObject("f");
            go.layer = _root.layer;
            var rt = go.AddComponent<RectTransform>();
            rt.SetParent(_fills, false);
            TopLeft(rt);
            var img = go.AddComponent<Image>();
            img.raycastTarget = false;
            el = new FillEl { Go = go, Rt = rt, Img = img, On = true };
            Fills.Add(el);
        }
        _fillUsed++;
        if (!el.On) { el.On = true; el.Go.SetActive(true); }
        if (!ReferenceEquals(el.Spr, sprite))
        {
            el.Spr = sprite;
            el.Img.sprite = sprite;
            bool sliced = ReferenceEquals(sprite, _round);
            el.Img.type = sliced ? Image.Type.Sliced : Image.Type.Simple;
            if (sliced) el.Img.pixelsPerUnitMultiplier = 2f;
        }
        if (!Same(c, el.Col)) { el.Col = c; el.Img.color = c; }
        var p = new Vector4(r.x, r.y, r.width, r.height);
        if (!Same(p, el.Pos))
        {
            el.Pos = p;
            el.Rt.anchoredPosition = new Vector2(p.x, -p.y);
            el.Rt.sizeDelta = new Vector2(p.z, p.w);
        }
    }

    private static void Text(Rect r, string text, float size, Color c, TextAlignmentOptions align, FontStyles style)
    {
        if (string.IsNullOrEmpty(text) || r.width <= 0.5f || r.height <= 0.5f) return;
        TextEl el;
        if (_textUsed < Texts.Count) el = Texts[_textUsed];
        else
        {
            var go = new GameObject("t");
            go.layer = _root.layer;
            var rt = go.AddComponent<RectTransform>();
            rt.SetParent(_texts, false);
            TopLeft(rt);
            var tmp = go.AddComponent<TextMeshProUGUI>();
            tmp.font = _font;
            tmp.raycastTarget = false;
            tmp.richText = false;
            tmp.margin = Vector4.zero;
            tmp.textWrappingMode = TextWrappingModes.NoWrap;
            tmp.overflowMode = TextOverflowModes.Ellipsis;
            el = new TextEl { Go = go, Rt = rt, Tmp = tmp, On = true, Font = _font };
            Texts.Add(el);
        }
        _textUsed++;
        var t = el.Tmp;
        if (!el.On) { el.On = true; el.Go.SetActive(true); }
        if (!ReferenceEquals(el.Font, _font)) { el.Font = _font; t.font = _font; }
        if (Mathf.Abs(el.Size - size) > 0.01f)
        {
            el.Size = size;
            t.fontSize = size;
            t.enableAutoSizing = true; // shrink a little before the ellipsis
            t.fontSizeMax = size;
            t.fontSizeMin = size * 0.75f;
        }
        if (el.Align != align) { el.Align = align; t.alignment = align; }
        if (el.Style != style) { el.Style = style; t.fontStyle = style; }
        if (el.Text != text) { el.Text = text; t.text = text; }
        if (!Same(c, el.Col)) { el.Col = c; t.color = c; }
        var p = new Vector4(r.x, r.y, r.width, r.height);
        if (!Same(p, el.Pos))
        {
            el.Pos = p;
            el.Rt.anchoredPosition = new Vector2(p.x, -p.y);
            el.Rt.sizeDelta = new Vector2(p.z, p.w);
        }
    }

    /// <summary>Width of a single line in bar units (game font), cached.</summary>
    private static float Measure(string text, float size, bool bold)
    {
        var key = (text, Mathf.RoundToInt(size * 10f), bold);
        if (Widths.TryGetValue(key, out var w)) return w;
        w = text.Length * size * 0.62f; // estimate if TMP can't tell
        try
        {
            if (_measure != null)
            {
                _measure.fontSize = size;
                _measure.enableAutoSizing = false;
                _measure.fontStyle = bold ? FontStyles.Bold : FontStyles.Normal;
                var v = _measure.GetPreferredValues(text, 9999f, 100f);
                if (v.x > 0.5f && v.x < 5000f) w = Mathf.Ceil(v.x);
            }
        }
        catch { }
        if (Widths.Count > 200) Widths.Clear();
        Widths[key] = w;
        return w;
    }
}
