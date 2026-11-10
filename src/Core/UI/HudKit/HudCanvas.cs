using System;
using System.Collections.Generic;
#if MELON
using Il2CppTMPro;
#else
using TMPro;
#endif
using OverTheHillPlus.UI.Native;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace OverTheHillPlus.UI.HudKit;

/// <summary>Which canvas a HUD group belongs to.</summary>
internal enum HudLayer
{
    /// <summary>In the world: follows the game's HUD, so it is gone in menus, pause, the map, photo mode and cutscenes.</summary>
    Driving,
    /// <summary>Panels and notes that must also show over menus (the server browser, the lobby).</summary>
    Screen,
}

/// <summary>How a piece of HUD text looks. Sizes are the units of whatever draws it (see <see cref="HudCanvas.Sync"/>).</summary>
internal struct HudText
{
    public float Size;
    public Color Color;
    public TextAnchor Align;
    public HudFont Font;
    public bool Wrap, Rich, Italic, Upper, Shadow;

    public HudText(float size, Color color, TextAnchor align = TextAnchor.MiddleLeft, HudFont font = HudFont.Regular)
    {
        Size = size; Color = color; Align = align; Font = font;
        Wrap = Rich = Italic = Upper = Shadow = false;
    }

    public bool SameLook(in HudText o) =>
        Align == o.Align && Font == o.Font && Wrap == o.Wrap && Rich == o.Rich && Italic == o.Italic && Upper == o.Upper;
}

/// <summary>
/// The mods' HUD as uGUI: two screen-space canvases with the game's scaling (1920 wide, like GameUI) whose elements are
/// pooled per mod. It is immediate mode, the same idea as the native menu's <see cref="NativeCanvas"/>: a mod's normal
/// draw code runs in the Repaint event between <see cref="HudDraw.Begin"/> and <see cref="HudDraw.End"/>; each fill and
/// text takes the next pooled Image or TextMeshProUGUI of that mod's group and only touches what changed since the last
/// frame, and what a group didn't use this frame is switched off when the frame ends. Nothing here takes input (no
/// raycasters): buttons stay invisible IMGUI controls over the same rectangles.
///
/// The driving layer copies the opacity of the game's HUD, so it fades with it and is gone wherever the game hides its
/// own HUD. Any exception switches the whole kit off for the session (<see cref="Fail"/>): the mods' draw calls then go
/// straight to their old IMGUI drawing, so a mistake here can't take a HUD away.
/// </summary>
internal static class HudCanvas
{
    private const int MaxPerGroup = 900, MaxClips = 8;

    private sealed class FillEl
    {
        public GameObject Go;
        public RectTransform Rt;
        public Image Img;
        public bool On;
        public Vector4 Pos = new(float.NaN, 0, 0, 0);
        public Color Col = new(-1, -1, -1, -1);
        public SpriteLook Shape;
        public Sprite Spr;
        public float Mult = -1f, Degrees;
    }

    // A texture shown through a window: a RectMask2D with a RawImage under it, so a map can be bigger than its panel.
    private sealed class ClipEl
    {
        public GameObject Go;
        public RectTransform Rt, ImageRt;
        public RawImage Raw;
        public bool On;
        public Vector4 Window = new(float.NaN, 0, 0, 0), Full = new(float.NaN, 0, 0, 0);
        public Color Col = new(-1, -1, -1, -1);
        public Texture Tex;
    }

    private sealed class FillPool
    {
        public RectTransform Parent;
        public readonly List<FillEl> Items = new();
        public int Used, High;
    }

    private sealed class TextEl
    {
        public GameObject Go;
        public RectTransform Rt;
        public TextMeshProUGUI Tmp;
        public bool On, HasSpec;
        public Vector4 Pos = new(float.NaN, 0, 0, 0);
        public string Text;
        public Color Col = new(-1, -1, -1, -1);
        public HudText Spec;
        public float SizePx = -1f;
        public int Ver = -1;
    }

    private sealed class Group
    {
        public string Id;
        public GameObject Go;
        public RectTransform Clips, Texts;
        public readonly FillPool Back = new(), Front = new();
        public readonly List<ClipEl> ClipPool = new();
        public readonly List<TextEl> TextPool = new();
        public int ClipUsed, ClipHigh, TextUsed, TextHigh;
        public int Serial = -1;
        public bool On;
    }

    private sealed class Root
    {
        public GameObject Go;
        public Canvas Canvas;
        public CanvasGroup Alpha;
        public RectTransform Rt;
        public float Applied = -1f;
        public bool On;
    }

    private static Root _driving, _screen;
    private static readonly Dictionary<string, Group> Groups = new();
    private static readonly List<Group> GroupList = new();
    private static Group _cur;
    private static int _serial;
    private static float _scaleFactor = 1f, _k = 1f, _ox, _oy, _nextBuild;
    private static bool _overflowLogged, _builtLogged;

    private static CanvasGroup _hudGroup;
    private static GameObject _hudObject;
    private static bool _hudSeen;
    private static float _nextHudLook;

    /// <summary>True in this OnGUI pass's Repaint event (asked once a pass by core, not per mod).</summary>
    internal static bool Repaint { get; private set; }

    /// <summary>A group is open and draws natively (in any event; it only emits in Repaint).</summary>
    internal static bool Native { get; private set; }

    /// <summary>A group is open and this is the Repaint event: fills and texts become canvas elements.</summary>
    internal static bool Emitting { get; private set; }

    /// <summary>While true, fills go under the group's textures (a map's panel) instead of over them.</summary>
    internal static bool Behind { get; set; }

    /// <summary>Set when something failed: the old drawing is used for the rest of the session.</summary>
    internal static bool Failed { get; private set; }

    internal static string FailReason { get; private set; } = "";

    internal static bool Built => _driving != null && _driving.Go != null && (bool)_driving.Go;

    private static bool Wanted => NativeStyle.HudWanted && !Failed;

    // ---------- lifecycle ----------

    private static bool Ensure()
    {
        if (!Built)
        {
            if (Time.unscaledTime < _nextBuild) return false;
            _nextBuild = Time.unscaledTime + 3f;
            if (!HudLook.Ensure()) return false;
            CrashTrail.Mark("hud: building the layer");
            NativeLook.EnsureGeneratedShapes();
            Build();
            ReadGame();
            ReadScale(); // switched on just now: the scaler has only worked out its scale since
            return true;
        }
        return HudLook.Ensure();
    }

    private static void Build()
    {
        DestroyObjects();
        _driving = MakeRoot("OverTheHillPlus.Hud", HudRules.DrivingSort);
        _screen = MakeRoot("OverTheHillPlus.HudScreen", HudRules.ScreenSort);
        if (_builtLogged) return;
        _builtLogged = true;
        Core.CorePlugin.Logger?.LogInfo($"[HUD] game-style layer built: {HudLook.Summary}; canvases at order {HudRules.DrivingSort} and {HudRules.ScreenSort}, screen {Screen.width}x{Screen.height}");
    }

    private static Root MakeRoot(string name, int sort)
    {
        var go = new GameObject(name);
        Object.DontDestroyOnLoad(go);
        go.layer = 5; // UI
        var canvas = go.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = sort;
        canvas.pixelPerfect = false;
        try
        {
            canvas.additionalShaderChannels |= AdditionalCanvasShaderChannels.TexCoord1 | AdditionalCanvasShaderChannels.Normal | AdditionalCanvasShaderChannels.Tangent;
        }
        catch (Exception ex) { DebugLog.Error("core", ex, "HudCanvas.MakeRoot"); /* TMP sets these itself when it can */ }
        var scaler = go.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
        scaler.matchWidthOrHeight = 0f; // by width, like the game's GameUI canvas
        var group = go.AddComponent<CanvasGroup>();
        var root = new Root { Go = go, Canvas = canvas, Alpha = group, Rt = go.GetComponent<RectTransform>() };
        go.SetActive(false);
        return root;
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

    private static void TopLeft(RectTransform rt, Vector2 pivot)
    {
        rt.anchorMin = new Vector2(0f, 1f);
        rt.anchorMax = new Vector2(0f, 1f);
        rt.pivot = pivot;
    }

    private static void DestroyObjects()
    {
        foreach (var r in new[] { _driving, _screen })
        {
            try { if (r?.Go != null && (bool)r.Go) Object.Destroy(r.Go); }
            catch (Exception ex) { DebugLog.Error("core", ex, "HudCanvas.DestroyObjects"); }
        }
        _driving = _screen = null;
        Groups.Clear();
        GroupList.Clear();
        _cur = null;
    }

    /// <summary>Switches both canvases off now (a screenshot, the style changed, a failure).</summary>
    internal static void Hide()
    {
        Native = Emitting = false;
        _cur = null;
        Show(_driving, 0f);
        Show(_screen, 0f);
    }

    /// <summary>Back to the old drawing for this session, logged once with the reason.</summary>
    internal static void Fail(string where, Exception e)
    {
        Native = Emitting = false;
        _cur = null;
        if (Failed) return;
        Failed = true;
        FailReason = where + ": " + (e?.Message ?? "unknown error");
        Core.CorePlugin.Logger?.LogError($"[HUD] the game-style HUD failed ({where}), the old overlays draw for this session: {e}");
        DebugLog.Error("core", e, "HudCanvas." + where);
        try { Hide(); } catch (Exception ex) { DebugLog.Error("core", ex, "HudCanvas.Fail"); /* already broken */ }
    }

    /// <summary>Clears a failure and builds the layer again on the next draw.</summary>
    internal static void Retry()
    {
        Failed = false;
        FailReason = "";
        _nextBuild = 0f;
        try { DestroyObjects(); }
        catch (Exception e) { DebugLog.Error("core", e, "HudCanvas.Retry"); }
        HudLook.Reset();
    }

    // ---------- following the game ----------

    /// <summary>Update, after the mods' ticks: shows, hides and fades the layers before the frame renders.</summary>
    internal static void Tick()
    {
        if (_driving == null) return;
        if (!Wanted)
        {
            if (_driving.On || _screen?.On == true) Hide();
            return;
        }
        try { ReadGame(); }
        catch (Exception e) { Fail("following the game's HUD", e); }
    }

    /// <summary>The game's HUD exists right now (in the world, not the main menu or a lobby).</summary>
    internal static bool GameHudUp { get; private set; }

    private static void ReadGame()
    {
        var view = new HudView
        {
            GuiHidden = CoreBehaviour.GuiHidden,
            CleanView = CoreBehaviour.CleanView,
            PhotoOrFreeCamera = CameraView.PhotoOrFreeCamera,
            Cutscene = CameraView.Cutscene,
        };
        view.GameHudAlpha = GameHudAlpha(out view.GameHudUp);
        GameHudUp = view.GameHudUp;
        Show(_driving, HudRules.DrivingAlpha(view));
        Show(_screen, HudRules.ScreenAlpha(view));
    }

    /// <summary>The opacity of the game's HUD: its CanvasGroup, looked for at most twice a second while there is none.</summary>
    private static float GameHudAlpha(out bool up)
    {
        if (_hudGroup == null)
        {
            float now = Time.unscaledTime;
            if (now >= _nextHudLook)
            {
                _nextHudLook = now + 0.5f;
                _hudSeen = false;
                try
                {
                    var hud = HUDManager.Instance;
                    if (hud != null)
                    {
                        _hudSeen = true;
                        _hudGroup = hud.canvasGroup;
                        _hudObject = hud.gameObject;
                    }
                }
                catch (Exception e) { DebugLog.Error("core", e, "HudCanvas.GameHudAlpha"); }
            }
            if (_hudGroup == null)
            {
                up = _hudSeen;
                return -1f;
            }
        }
        up = true;
        // Switched off is as hidden as faded out (the game has a switch for it, and a screen may do the same).
        if (_hudObject != null && !_hudObject.activeInHierarchy) return 0f;
        return _hudGroup.alpha;
    }

    private static void Show(Root r, float alpha)
    {
        if (r == null || r.Go == null) return;
        bool on = alpha > HudRules.Invisible;
        if (on != r.On)
        {
            r.On = on;
            r.Go.SetActive(on);
        }
        if (on && Mathf.Abs(alpha - r.Applied) > 0.002f)
        {
            r.Applied = alpha;
            r.Alpha.alpha = alpha;
        }
    }

    private static void ReadScale()
    {
        float sf = 0f;
        try { sf = _screen.Canvas.scaleFactor; }
        catch (Exception ex) { DebugLog.Error("core", ex, "HudCanvas.ReadScale"); }
        _scaleFactor = sf >= 0.01f ? sf : Mathf.Max(0.01f, Screen.width / 1920f);
    }

    // ---------- frame ----------

    /// <summary>Start of an OnGUI pass of the overlays (core, once per event).</summary>
    internal static void FrameStart(bool repaint)
    {
        Repaint = repaint;
        Native = Emitting = false;
        _cur = null;
        if (!repaint) return;
        _serial++;
        if (_screen != null && _screen.On) ReadScale();
    }

    /// <summary>End of the pass: what wasn't drawn this frame goes off.</summary>
    internal static void FrameEnd(bool repaint)
    {
        Native = Emitting = false;
        _cur = null;
        Repaint = false;
        if (!repaint || GroupList.Count == 0) return;
        try
        {
            for (int i = 0; i < GroupList.Count; i++)
            {
                var g = GroupList[i];
                if (g.Serial == _serial)
                {
                    Trim(g);
                    if (!g.On) { g.On = true; g.Go.SetActive(true); }
                }
                else if (g.On) { g.On = false; g.Go.SetActive(false); }
            }
        }
        catch (Exception e) { Fail("end of frame", e); }
    }

    private static void Trim(Group g)
    {
        Trim(g.Back);
        Trim(g.Front);
        for (int i = g.ClipUsed; i < g.ClipHigh; i++)
        {
            var el = g.ClipPool[i];
            if (el.On) { el.On = false; el.Go.SetActive(false); }
        }
        g.ClipHigh = g.ClipUsed;
        for (int i = g.TextUsed; i < g.TextHigh; i++)
        {
            var el = g.TextPool[i];
            if (el.On) { el.On = false; el.Go.SetActive(false); }
        }
        g.TextHigh = g.TextUsed;
    }

    private static void Trim(FillPool p)
    {
        for (int i = p.Used; i < p.High; i++)
        {
            var el = p.Items[i];
            if (el.On) { el.On = false; el.Go.SetActive(false); }
        }
        p.High = p.Used;
    }

    /// <summary>
    /// Opens a mod's group. False: the layer is hidden now, draw nothing. True: draw; <see cref="Native"/> says whether
    /// it goes to the canvas or (classic style, a failure, no font yet) through the old IMGUI calls.
    /// </summary>
    internal static bool Open(string id, HudLayer layer)
    {
        Native = Emitting = false;
        _cur = null;
        if (!Wanted) return true;
        try
        {
            if (!Ensure()) return true;
            var root = layer == HudLayer.Driving ? _driving : _screen;
            if (!root.On) return false;
            Native = true;
            if (!Repaint) return true;
            if (!Groups.TryGetValue(id, out var g)) g = NewGroup(id, root);
            if (g.Serial != _serial)
            {
                g.Serial = _serial;
                g.Back.Used = g.Front.Used = g.ClipUsed = g.TextUsed = 0;
            }
            _cur = g;
            Behind = false;
            Emitting = true;
            Sync();
            return true;
        }
        catch (Exception e)
        {
            Fail("opening " + id, e);
            return true;
        }
    }

    /// <summary>Closes the open group.</summary>
    internal static void Close()
    {
        Native = Emitting = false;
        Behind = false;
        _cur = null;
    }

    private static Group NewGroup(string id, Root root)
    {
        CrashTrail.Mark("hud: new group " + id);
        var go = new GameObject("g_" + id);
        go.layer = 5;
        var rt = go.AddComponent<RectTransform>();
        rt.SetParent(root.Rt, false);
        Stretch(rt);
        var g = new Group { Id = id, Go = go, On = true };
        g.Back.Parent = Child(rt, "Back");
        g.Clips = Child(rt, "Clips");
        g.Front.Parent = Child(rt, "Fills");
        g.Texts = Child(rt, "Texts");
        Groups[id] = g;
        GroupList.Add(g);
        DebugLog.Info("core", "HUD: " + id + " is drawn on the game-style layer");
        return g;
    }

    /// <summary>
    /// Takes the draw code's units from GUI.matrix (scale and offset), so a mod in HUD units (HudLayout.Begin), in raw
    /// pixels or with its own scale lands where its IMGUI drawing did. Call after changing GUI.matrix.
    /// </summary>
    internal static void Sync()
    {
        if (!Emitting) return;
        var m = GUI.matrix;
        _k = m.m00 / _scaleFactor;
        _ox = m.m03 / _scaleFactor;
        _oy = m.m13 / _scaleFactor;
    }

    // ---------- primitives (Repaint, a group open; false: the caller draws the old way) ----------

    private static bool Same(Vector4 a, Vector4 b) =>
        Mathf.Abs(a.x - b.x) < 0.01f && Mathf.Abs(a.y - b.y) < 0.01f && Mathf.Abs(a.z - b.z) < 0.01f && Mathf.Abs(a.w - b.w) < 0.01f;

    private static bool Same(Color a, Color b) =>
        Mathf.Abs(a.r - b.r) < 0.002f && Mathf.Abs(a.g - b.g) < 0.002f && Mathf.Abs(a.b - b.b) < 0.002f && Mathf.Abs(a.a - b.a) < 0.002f;

    private static Color Tint(Color color)
    {
        var c = HudLook.Game(color);
        var g = GUI.color;
        return new Color(c.r * g.r, c.g * g.g, c.b * g.b, c.a * g.a);
    }

    // Shapes for Theme.Fill radii, made once per radius.
    private static readonly Dictionary<int, SpriteLook> RadiusShapes = new();

    private static SpriteLook Shape(int radius)
    {
        // The generated sprites are HideAndDontSave: no need to ask if they are alive on every fill.
        if (!RadiusShapes.TryGetValue(radius, out var shape))
            RadiusShapes[radius] = shape = radius <= 0 ? NativeLook.Square : NativeLook.Rounded(radius);
        return shape;
    }

    /// <summary>Theme.Fill's rounded fills while a group is open.</summary>
    internal static bool FillRounded(Rect r, Color color, int radius, float degrees = 0f)
    {
        if (!Emitting) return false;
        if (r.width <= 0f || r.height <= 0f) return true;
        try { Fill(r, color, Shape(radius), null, degrees); return true; }
        catch (Exception e) { Fail("a fill", e); return false; }
    }

    /// <summary>A sprite in a rectangle, tinted.</summary>
    internal static bool Sprite(Rect r, Sprite sprite, Color color, float degrees = 0f)
    {
        if (!Emitting || sprite == null) return false;
        try { Fill(r, color, null, sprite, degrees); return true; }
        catch (Exception e) { Fail("a sprite", e); return false; }
    }

    /// <summary>
    /// A texture of size <paramref name="full"/> seen through <paramref name="window"/> (both in the group's units): a map
    /// bigger than its panel. Under the group's fills and over its <see cref="Behind"/> fills.
    /// </summary>
    internal static bool Texture(Rect window, Rect full, Texture tex, Color color)
    {
        if (!Emitting || tex == null) return false;
        if (window.width <= 0f || window.height <= 0f) return true;
        try
        {
            var el = NextClip();
            if (el == null) return true;
            if (el.Tex != tex)
            {
                el.Tex = tex;
                el.Raw.texture = tex;
            }
            var c = Tint(color);
            if (!Same(c, el.Col)) { el.Col = c; el.Raw.color = c; }
            var w = new Vector4(window.x * _k + _ox, window.y * _k + _oy, window.width * _k, window.height * _k);
            if (!Same(w, el.Window))
            {
                el.Window = w;
                el.Rt.anchoredPosition = new Vector2(w.x, -w.y);
                el.Rt.sizeDelta = new Vector2(w.z, w.w);
            }
            var f = new Vector4((full.x - window.x) * _k, (full.y - window.y) * _k, full.width * _k, full.height * _k);
            if (!Same(f, el.Full))
            {
                el.Full = f;
                el.ImageRt.anchoredPosition = new Vector2(f.x, -f.y);
                el.ImageRt.sizeDelta = new Vector2(f.z, f.w);
            }
            return true;
        }
        catch (Exception e)
        {
            Fail("a texture", e);
            return false;
        }
    }

    /// <summary>A line of any angle between two points, as one turned fill.</summary>
    internal static bool Line(Vector2 a, Vector2 b, Color color, float width)
    {
        if (!Emitting) return false;
        var (cx, cy, len, deg) = HudRules.Line(a.x, a.y, b.x, b.y);
        if (len < 0.5f || float.IsNaN(len)) return true;
        if (len > 20000f) len = 20000f;
        return FillRounded(new Rect(cx - len * 0.5f, cy - width * 0.5f, len, width), color, 0, deg);
    }

    private static bool Bad(Rect r)
    {
        float sum = r.x + r.y + r.width + r.height;
        return float.IsNaN(sum) || float.IsInfinity(sum);
    }

    private static void Fill(Rect r, Color color, SpriteLook shape, Sprite sprite, float degrees)
    {
        if (Bad(r)) return;
        var c = Tint(color);
        if (c.a <= 0.004f) return;
        var el = NextFill();
        if (el == null) return;

        if (!ReferenceEquals(el.Shape, shape) || (sprite != null && el.Spr != sprite))
        {
            el.Shape = shape;
            el.Spr = sprite;
            el.Img.sprite = shape != null ? shape.Sprite : sprite;
            el.Img.type = shape != null && shape.Sliced ? Image.Type.Sliced : Image.Type.Simple;
            el.Mult = -1f;
        }
        float w = r.width * _k, h = r.height * _k;
        if (shape != null && shape.Sliced && shape.BorderPx > 0f)
        {
            float mult = HudRules.CornerMultiplier(shape.BorderPx, shape.Ppu, shape.CornerUnits, _k, w, h);
            if (Mathf.Abs(mult - el.Mult) > 0.01f)
            {
                el.Mult = mult;
                el.Img.pixelsPerUnitMultiplier = mult;
            }
        }
        if (!Same(c, el.Col)) { el.Col = c; el.Img.color = c; }
        var p = new Vector4(r.x * _k + _ox + w * 0.5f, r.y * _k + _oy + h * 0.5f, w, h);
        if (!Same(p, el.Pos))
        {
            el.Pos = p;
            el.Rt.anchoredPosition = new Vector2(p.x, -p.y);
            el.Rt.sizeDelta = new Vector2(p.z, p.w);
        }
        if (Mathf.Abs(degrees - el.Degrees) > 0.01f)
        {
            el.Degrees = degrees;
            el.Rt.localRotation = Quaternion.Euler(0f, 0f, -degrees);
        }
    }

    /// <summary>Text in a rectangle (rich text only when the spec says so: names are untrusted).</summary>
    internal static bool Text(Rect r, string text, in HudText spec)
    {
        if (!Emitting) return false;
        if (string.IsNullOrEmpty(text) || r.width <= 0.5f || r.height <= 0.5f) return true;
        try
        {
            if (spec.Shadow)
            {
                var s = spec;
                s.Shadow = false;
                s.Color = new Color(0f, 0f, 0f, spec.Color.a * 0.7f);
                Emit(new Rect(r.x + 1f, r.y + 1f, r.width, r.height), text, s);
            }
            Emit(r, text, spec);
            return true;
        }
        catch (Exception e)
        {
            Fail("a text", e);
            return false;
        }
    }

    private static void Emit(Rect r, string text, in HudText spec)
    {
        if (Bad(r)) return;
        var el = NextText();
        if (el == null) return;
        var tmp = el.Tmp;
        int ver = HudLook.Version;
        if (el.Ver != ver || !el.HasSpec || el.Spec.Font != spec.Font)
        {
            tmp.font = HudLook.Font(spec.Font);
            el.Ver = ver;
            el.Text = null; // check the glyphs against the new font
        }
        if (!el.HasSpec || !el.Spec.SameLook(spec))
        {
            el.HasSpec = true;
            el.Spec = spec;
            tmp.alignment = Map(spec.Align);
            tmp.textWrappingMode = spec.Wrap ? TextWrappingModes.Normal : TextWrappingModes.NoWrap;
            tmp.overflowMode = spec.Wrap ? TextOverflowModes.Overflow : TextOverflowModes.Ellipsis;
            tmp.richText = spec.Rich;
            var fs = FontStyles.Normal;
            if (spec.Italic) fs |= FontStyles.Italic;
            if (spec.Upper) fs |= FontStyles.UpperCase;
            tmp.fontStyle = fs;
            el.SizePx = -1f;
        }
        float size = spec.Size * _k;
        if (Mathf.Abs(size - el.SizePx) > 0.01f)
        {
            el.SizePx = size;
            // Off first: while auto sizing is on TMP keeps its old base size and the element would stay at its last one.
            tmp.enableAutoSizing = false;
            tmp.fontSize = size;
            // Single lines shrink a little before they get an ellipsis: the game's fonts are wider than IMGUI's.
            if (!spec.Wrap)
            {
                tmp.fontSizeMax = size;
                tmp.fontSizeMin = size * 0.72f;
                tmp.enableAutoSizing = true;
            }
        }
        if (!ReferenceEquals(text, el.Text) && text != el.Text)
        {
            el.Text = text;
            string shown = NativeCanvas.Sanitize(text, HudLook.Font(spec.Font));
            // Sanitize guards a '<' it puts in with a tag; without rich text that tag would show as words.
            if (!spec.Rich && !ReferenceEquals(shown, text)) shown = shown.Replace("<noparse><</noparse>", "<");
            tmp.text = shown;
        }
        var c = Tint(spec.Color);
        if (!Same(c, el.Col)) { el.Col = c; tmp.color = c; }
        float top = r.y, height = r.height;
        if (!spec.Wrap) (top, height) = HudRules.FitLine(r.y, r.height, spec.Size, Vertical(spec.Align));
        var p = new Vector4(r.x * _k + _ox, top * _k + _oy, r.width * _k, height * _k);
        if (!Same(p, el.Pos))
        {
            el.Pos = p;
            el.Rt.anchoredPosition = new Vector2(p.x, -p.y);
            el.Rt.sizeDelta = new Vector2(p.z, p.w);
        }
    }

    private static int Vertical(TextAnchor a) => a switch
    {
        TextAnchor.UpperLeft or TextAnchor.UpperCenter or TextAnchor.UpperRight => -1,
        TextAnchor.LowerLeft or TextAnchor.LowerCenter or TextAnchor.LowerRight => 1,
        _ => 0,
    };

    private static TextAlignmentOptions Map(TextAnchor a)
    {
        switch (a)
        {
            case TextAnchor.UpperLeft: return TextAlignmentOptions.TopLeft;
            case TextAnchor.UpperCenter: return TextAlignmentOptions.Top;
            case TextAnchor.UpperRight: return TextAlignmentOptions.TopRight;
            case TextAnchor.MiddleCenter: return TextAlignmentOptions.Center;
            case TextAnchor.MiddleRight: return TextAlignmentOptions.Right;
            case TextAnchor.LowerLeft: return TextAlignmentOptions.BottomLeft;
            case TextAnchor.LowerCenter: return TextAlignmentOptions.Bottom;
            case TextAnchor.LowerRight: return TextAlignmentOptions.BottomRight;
            default: return TextAlignmentOptions.Left;
        }
    }

    // ---------- pools ----------

    private static FillEl NextFill()
    {
        var g = _cur;
        var pool = Behind ? g.Back : g.Front;
        int i = pool.Used;
        if (i >= MaxPerGroup) { Overflow(g); return null; }
        pool.Used = i + 1;
        if (i >= pool.High) pool.High = i + 1;
        var list = pool.Items;
        if (i >= list.Count)
        {
            var go = new GameObject("f");
            go.layer = 5;
            var rt = go.AddComponent<RectTransform>();
            rt.SetParent(pool.Parent, false);
            TopLeft(rt, new Vector2(0.5f, 0.5f));
            var img = go.AddComponent<Image>();
            img.raycastTarget = false;
            list.Add(new FillEl { Go = go, Rt = rt, Img = img, On = true });
        }
        var el = list[i];
        if (!el.On) { el.On = true; el.Go.SetActive(true); }
        return el;
    }

    private static ClipEl NextClip()
    {
        var g = _cur;
        int i = g.ClipUsed;
        if (i >= MaxClips) { Overflow(g); return null; }
        g.ClipUsed = i + 1;
        if (i >= g.ClipHigh) g.ClipHigh = i + 1;
        var list = g.ClipPool;
        if (i >= list.Count)
        {
            var go = new GameObject("c");
            go.layer = 5;
            var rt = go.AddComponent<RectTransform>();
            rt.SetParent(g.Clips, false);
            TopLeft(rt, new Vector2(0f, 1f));
            go.AddComponent<RectMask2D>();
            var image = new GameObject("i");
            image.layer = 5;
            var irt = image.AddComponent<RectTransform>();
            irt.SetParent(rt, false);
            TopLeft(irt, new Vector2(0f, 1f));
            var raw = image.AddComponent<RawImage>();
            raw.raycastTarget = false;
            list.Add(new ClipEl { Go = go, Rt = rt, ImageRt = irt, Raw = raw, On = true });
        }
        var el = list[i];
        if (!el.On) { el.On = true; el.Go.SetActive(true); }
        return el;
    }

    private static TextEl NextText()
    {
        var g = _cur;
        int i = g.TextUsed;
        if (i >= MaxPerGroup) { Overflow(g); return null; }
        g.TextUsed = i + 1;
        if (i >= g.TextHigh) g.TextHigh = i + 1;
        var list = g.TextPool;
        if (i >= list.Count)
        {
            var go = new GameObject("t");
            go.layer = 5;
            var rt = go.AddComponent<RectTransform>();
            rt.SetParent(g.Texts, false);
            TopLeft(rt, new Vector2(0f, 1f));
            var tmp = go.AddComponent<TextMeshProUGUI>();
            tmp.raycastTarget = false;
            tmp.richText = false;
            tmp.margin = Vector4.zero;
            tmp.extraPadding = false;
            list.Add(new TextEl { Go = go, Rt = rt, Tmp = tmp, On = true });
        }
        var el = list[i];
        if (!el.On) { el.On = true; el.Go.SetActive(true); }
        return el;
    }

    private static void Overflow(Group g)
    {
        if (_overflowLogged) return;
        _overflowLogged = true;
        Core.CorePlugin.Logger?.LogWarning($"[HUD] {g.Id}: more than {MaxPerGroup} elements in one frame; the rest is not drawn.");
    }
}
