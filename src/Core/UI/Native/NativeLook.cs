using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using FunCore.Style;
using Il2CppInterop.Runtime;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace OverTheHillPlus.UI.Native;

/// <summary>A shape for a uGUI Image: a game sprite (9-sliced when it has borders) or one of ours.</summary>
internal sealed class SpriteLook
{
    public Sprite Sprite;          // null = plain rectangle (what an Image without a sprite draws)
    public bool Sliced;
    public float BorderPx;         // largest sprite border, in sprite pixels
    public float CornerUnits;      // how big that border is meant to look, in menu units at 100% size
    public float Ppu = 100f;       // the sprite's pixels per unit
    public bool Skewed;            // the game's FunCore SkewedImage (slanted bars)
    public float SkewPerHeight;    // skewX relative to the original element's height
    public string Source = "";
}

/// <summary>
/// Reads the game's own UI look at runtime: its style sheet colours (FunStyleAsset), the fonts of its menu texts,
/// the sprites and highlight colours of its selectors (main menu buttons, settings option rows, sliders, panels)
/// and its UI sounds (UISfxManager). Everything found is optional: anything missing falls back to a built-in
/// look in the same spirit (dark panel, the game's menu yellow), so the native menu always renders.
/// </summary>
internal static class NativeLook
{
    // ---------- results ----------

    public static TMP_FontAsset Body, Heading;

    public static Color Panel = Theme.Hex(0x14181E, 0.96f);
    public static Color HeaderBand = Theme.Hex(0x000000, 0.22f);
    public static Color Card = Theme.Hex(0xFFFFFF, 0.035f);
    public static Color Line = Theme.Hex(0xFFFFFF, 0.10f);
    public static Color Text = Theme.Hex(0xF3EFE6);
    public static Color Muted = Theme.Hex(0xA3A9B1);
    public static Color Highlight = Theme.Accent;
    public static Color HighlightText = Theme.Hex(0x16181C);
    public static Color Button = Theme.Hex(0xFFFFFF, 0.08f);
    public static Color ButtonText = Theme.Hex(0xF3EFE6);
    public static Color Track = Theme.Hex(0xFFFFFF, 0.14f);
    public static Color Danger = Theme.Hex(0xE5534B);

    public static SpriteLook PanelShape, ButtonShape, HighlightShape, TrackShape, FillShape, KnobShape;
    public static SpriteLook RoundSmall, RoundMedium, Pill, Square;

    /// <summary>Bumped whenever fonts or sprites change, so the renderer re-applies them to pooled elements.</summary>
    public static int Version { get; private set; }

    public static bool Ready { get; private set; }
    public static string Summary { get; private set; } = "not read yet";

    // style sheet colours, by lower-case name and by id
    private static readonly Dictionary<string, Color> ColorsByName = new();
    private static readonly Dictionary<string, Color> ColorsById = new();

    private static int _tries;
    private static float _nextTry;
    private static bool _complete;
    private static readonly HashSet<string> Logged = new();

    // Best results so far: a later re-read (e.g. in the world, where the main menu isn't loaded) keeps what an
    // earlier one found instead of falling back to defaults.
    private static SelectorLook _row, _button;
    private static SpriteLook _panel, _track, _fill, _knob;
    private static Color? _panelColor, _trackColor;
    private static float _nextAliveCheck;

    internal static void Reset()
    {
        _row = _button = null;
        _panel = _track = _fill = _knob = null;
        _panelColor = _trackColor = null;
        Ready = false;
        _complete = false;
        _tries = 0;
        _nextTry = 0f;
        Version++;
    }

    /// <summary>
    /// Makes sure a usable look exists (true) and re-reads the game's UI a few times while parts of it are
    /// still missing (menus that weren't loaded yet), at most every 15 s. Throws only on a hard failure.
    /// </summary>
    internal static bool Ensure()
    {
        if (Ready && Time.unscaledTime >= _nextAliveCheck)
        {
            _nextAliveCheck = Time.unscaledTime + 5f;
            if (!SpritesAlive()) Reset(); // an unloaded sprite would draw as a white box
        }
        if (Ready) DropDeadFonts();
        if (Ready && FontsAlive() && (_complete || Time.unscaledTime < _nextTry || _tries >= 5)) return true;
        // Dead fonts with no fallback still wait for the throttle: a full re-read scans thousands of objects.
        if (Ready && Time.unscaledTime < _nextTry) return false;
        _tries++;
        _nextTry = Time.unscaledTime + 15f;
        Discover();
        return Ready;
    }

    private static bool SpritesAlive()
    {
        foreach (var l in new[] { PanelShape, ButtonShape, HighlightShape, TrackShape, FillShape, KnobShape })
        {
            try { if (l?.Sprite != null && !(bool)l.Sprite) return false; }
            catch (Exception ex) { DebugLog.Error("core", ex, "NativeLook.SpritesAlive"); return false; }
        }
        return true;
    }

    private static bool Alive(Object o)
    {
        try { return o is not null && (bool)o; }
        catch (Exception ex) { DebugLog.Error("core", ex, "NativeLook.Alive"); return false; }
    }

    /// <summary>
    /// Forgets fonts that were destroyed (unloaded bundle, scene change) and uses another live one meanwhile, so a
    /// dead font can't keep the look re-reading every frame. Schedules one more re-read soon to find the game font.
    /// </summary>
    private static void DropDeadFonts()
    {
        if (_row?.Font is not null && !Alive(_row.Font)) _row.Font = null;
        if (_button?.Font is not null && !Alive(_button.Font)) _button.Font = null;
        bool bodyDead = Body is not null && !Alive(Body), headingDead = Heading is not null && !Alive(Heading);
        if (!bodyDead && !headingDead) return;
        if (bodyDead) Body = null;
        if (headingDead) Heading = null;
        if (!Alive(Heading)) Heading = Alive(Body) ? Body : null;
        if (!Alive(Body)) Body = Alive(Heading) ? Heading : null;
        if (Body is null)
        {
            try
            {
                var d = TMP_Settings.defaultFontAsset;
                if (Alive(d)) Body = Heading = d;
            }
            catch (Exception ex) { DebugLog.Error("core", ex, "NativeLook.DropDeadFonts"); }
        }
        _complete = false;
        if (_tries >= 5) _tries = 4;
        _nextTry = Mathf.Min(_nextTry, Time.unscaledTime + 3f);
        Version++;
    }

    private static bool FontsAlive()
    {
        try { return Body != null && (bool)Body && Heading != null && (bool)Heading; }
        catch (Exception ex) { DebugLog.Error("core", ex, "NativeLook.FontsAlive"); return false; }
    }

    // ---------- discovery ----------

    private sealed class SelectorLook
    {
        public SpriteLook Bar;
        public Color? BarColor, SelectedText, NormalText, NormalBack;
        public TMP_FontAsset Font;
        public SpriteLook Back;
        public string Source;
    }

    private static void Discover()
    {
        EnsureGeneratedShapes();
        var sb = new StringBuilder();

        Step("style sheet", ReadStyleSheet);

        SelectorLook row = null, button = null;
        Step("option rows", () => row = FirstSelector<UIOptionSelector>(12));
        Step("menu buttons", () => button = MainMenuButton() ?? FirstSelector<UIButtonSelector>(24));
        row = _row = row ?? _row;
        button = _button = button ?? _button;

        SpriteLook panel = null;
        Color? panelColor = null;
        Step("panels", () => (panel, panelColor) = FindPanel());
        if (panel == null) { panel = _panel; panelColor = _panelColor; }
        else { _panel = panel; _panelColor = panelColor; }

        SpriteLook track = null, fill = null, knob = null;
        Color? trackColor = null, fillColor = null;
        Step("sliders", () => (track, trackColor, fill, fillColor, knob) = FindSlider());
        if (track == null && fill == null) { track = _track; trackColor = _trackColor; fill = _fill; knob = _knob; }
        else { _track = track; _trackColor = trackColor; _fill = fill; _knob = knob; }

        TMP_FontAsset body = null, heading = null;
        Step("fonts", () => (body, heading) = FindFonts(row, button));
        Step("sounds", FindSounds);

        // ---- apply, with sanity checks so the menu stays readable whatever the game's colours are ----
        bool sprites = NativeStyle.GameSpritesOn;
        var hl = row ?? button;
        Highlight = Pick(hl?.BarColor, c => c.a > 0.5f && Lum(c) > 0.25f, Theme.Accent);
        HighlightText = Pick(hl?.SelectedText, c => Mathf.Abs(Lum(c) - Lum(Highlight)) > 0.35f,
            Lum(Highlight) > 0.5f ? Theme.Hex(0x16181C) : Theme.Hex(0xF3EFE6));
        Text = Pick(hl?.NormalText, c => Lum(c) > 0.6f && c.a > 0.7f, Theme.Hex(0xF3EFE6));
        // Raw (IMGUI) text from features is light: panels must stay dark to keep it readable.
        Panel = Pick(panelColor, c => Lum(c) < 0.3f && c.a > 0.6f, Theme.Hex(0x14181E, 0.96f));
        Panel.a = Mathf.Max(Panel.a, 0.9f);
        var back = button?.NormalBack;
        Button = back.HasValue && back.Value.a > 0.15f && Lum(back.Value) < 0.35f ? back.Value : Theme.Hex(0xFFFFFF, 0.08f);
        Track = Pick(trackColor, c => c.a > 0.2f && Lum(c) < 0.5f, Theme.Hex(0xFFFFFF, 0.14f));

        PanelShape = sprites && panel != null ? panel : RoundMedium;
        HighlightShape = sprites && hl?.Bar != null ? hl.Bar : RoundSmall;
        ButtonShape = sprites && button?.Back != null && button.Back.Sprite != null ? button.Back : RoundSmall;
        TrackShape = sprites && track != null ? track : Pill;
        FillShape = sprites && fill != null ? fill : Pill;
        KnobShape = sprites && knob != null && knob.Sprite != null ? knob : Pill;
        _ = fillColor; // the fill uses the highlight colour, like the game's selected slider

        // Unity null checks throughout: a destroyed font must never be kept (it would stay "not alive" forever).
        if (Alive(body)) Body = body;
        if (Alive(heading)) Heading = heading;
        if (!Alive(Heading)) Heading = Alive(Body) ? Body : null;
        if (!Alive(Body)) Body = Alive(Heading) ? Heading : null;
        if (Body is null)
        {
            try { Body = Heading = TMP_Settings.defaultFontAsset; } catch (Exception ex) { DebugLog.Error("core", ex, "NativeLook.Discover"); }
        }
        if (!Alive(Body)) throw new InvalidOperationException("no TextMeshPro font found");

        _complete = row != null && button != null && panel != null && body != null && SoundsFound;
        Ready = true;
        Version++;

        sb.Append("fonts ").Append(Name(Body)).Append(" / ").Append(Name(Heading));
        sb.Append("; highlight #").Append(ColorUtility.ToHtmlStringRGB(Highlight)).Append(" from ").Append(hl?.Source ?? "default");
        sb.Append("; panel ").Append(PanelShape?.Source ?? "-");
        sb.Append("; button ").Append(ButtonShape?.Source ?? "-");
        sb.Append("; bar ").Append(HighlightShape?.Source ?? "-");
        sb.Append("; slider ").Append(TrackShape?.Source ?? "-");
        sb.Append("; ").Append(ColorsByName.Count).Append(" style colours");
        sb.Append("; sounds ").Append(SoundsFound ? "yes" : "no");
        Summary = sb.ToString();
        Core.CorePlugin.Logger?.LogInfo("Native menu look: " + Summary);
        if (ColorsByName.Count > 0 && Logged.Add("palette"))
            Core.CorePlugin.Logger?.LogDebug("Game style colours: " + string.Join(", ", ColorsByName.Select(kv => kv.Key + "=#" + ColorUtility.ToHtmlStringRGBA(kv.Value))));
    }

    private static void Step(string what, Action a)
    {
        try { a(); }
        catch (Exception e)
        {
            DebugLog.Error("core", e, "NativeLook.Step");
            if (Logged.Add(what)) Core.CorePlugin.Logger?.LogWarning($"Native menu look: reading {what} failed ({e.Message}); using defaults for it.");
        }
    }

    private static Color Pick(Color? found, Func<Color, bool> ok, Color fallback) =>
        found.HasValue && ok(found.Value) ? found.Value : fallback;

    internal static float Lum(Color c) => 0.2126f * c.r + 0.7152f * c.g + 0.0722f * c.b;

    private static string Name(Object o)
    {
        try { return o == null ? "-" : o.name; }
        catch (Exception ex) { DebugLog.Error("core", ex, "NativeLook.Name"); return "?"; }
    }

    // ---- style sheet ----

    private static void ReadStyleSheet()
    {
        ColorsByName.Clear();
        ColorsById.Clear();
        var asset = FunStyleAsset.Instance;
        if (asset == null)
            foreach (var o in Resources.FindObjectsOfTypeAll(Il2CppType.Of<FunStyleAsset>()))
            {
                asset = o.TryCast<FunStyleAsset>();
                if (asset != null) break;
            }
        if (asset == null) return;
        var sheet = asset.GetActiveStyleSheet() ?? asset.GetStyleSheetDefault();
        if (sheet == null) return;
        for (int type = 0; type < 16; type++)
        {
            Il2CppSystem.Collections.Generic.List<string> ids;
            try { ids = sheet.GetIdListOfType(type); }
            catch (Exception ex) { DebugLog.Error("core", ex, "NativeLook.ReadStyleSheet"); continue; }
            if (ids == null) continue;
            for (int i = 0; i < ids.Count; i++)
            {
                try
                {
                    var el = sheet.GetStyleElement(ids[i]);
                    var c = el?.TryCast<FunStyleColorData>();
                    if (c == null) continue;
                    if (!string.IsNullOrEmpty(c.styleName)) ColorsByName[c.styleName.ToLowerInvariant()] = c.color;
                    if (!string.IsNullOrEmpty(c.Id)) ColorsById[c.Id] = c.color;
                }
                catch (Exception ex) { DebugLog.Error("core", ex, "NativeLook.ReadStyleSheet"); /* one bad element */ }
            }
        }
    }

    private static Color? StyleColor(string nameOrId)
    {
        if (string.IsNullOrEmpty(nameOrId)) return null;
        if (ColorsByName.TryGetValue(nameOrId.ToLowerInvariant(), out var c)) return c;
        if (ColorsById.TryGetValue(nameOrId, out c)) return c;
        return null;
    }

    // ---- selectors (the yellow highlight bar, button look, text colours) ----

    private static SelectorLook MainMenuButton()
    {
        foreach (var o in Resources.FindObjectsOfTypeAll(Il2CppType.Of<ContextUIMainMenu>()))
        {
            var m = o.TryCast<ContextUIMainMenu>();
            if (m == null) continue;
            foreach (var b in new[] { m.SettingsButton, m.MultiplayerButton, m.SinglePlayerButton })
            {
                var look = b == null ? null : ReadSelector(b, "main menu");
                if (look?.Bar != null || look?.Font != null) return look;
            }
        }
        return null;
    }

    private static SelectorLook FirstSelector<T>(int max) where T : UISelector
    {
        int seen = 0;
        SelectorLook partial = null;
        foreach (var o in Resources.FindObjectsOfTypeAll(Il2CppType.Of<T>()))
        {
            if (++seen > max) break;
            var s = o.TryCast<T>();
            if (s == null) continue;
            var look = ReadSelector(s, typeof(T).Name);
            if (look == null) continue;
            if (look.Bar != null && look.BarColor.HasValue) return look;
            partial ??= look;
        }
        return partial;
    }

    private static SelectorLook ReadSelector(UISelector s, string source)
    {
        var look = new SelectorLook { Source = source + " " + Name(s.gameObject) };
        var on = s.transitioningElementsOnSelect;
        if (on != null)
            for (int i = 0; i < on.Count; i++)
            {
                var te = on[i];
                var g = te?.GraphicElement;
                if (g == null) continue;
                var tmp = g.TryCast<TMP_Text>();
                if (tmp != null)
                {
                    look.SelectedText ??= StyleColor(te.FunStyleColorDataName);
                    look.Font ??= tmp.font;
                    continue;
                }
                var img = g.TryCast<Image>();
                if (img != null && look.Bar == null)
                {
                    look.Bar = FromImage(img, source + " highlight");
                    look.BarColor = StyleColor(te.FunStyleColorDataName);
                }
            }
        var off = s.transitioningElementsOnDeselect;
        if (off != null)
            for (int i = 0; i < off.Count; i++)
            {
                var te = off[i];
                var g = te?.GraphicElement;
                if (g == null) continue;
                if (g.TryCast<TMP_Text>() != null) look.NormalText ??= StyleColor(te.FunStyleColorDataName);
                else if (g.TryCast<Image>() != null) look.NormalBack ??= StyleColor(te.FunStyleColorDataName);
            }
        try
        {
            var tg = s.targetGraphic?.TryCast<Image>();
            if (tg != null)
            {
                look.Back = FromImage(tg, source + " background");
                look.NormalBack ??= tg.color;
            }
        }
        catch (Exception ex) { DebugLog.Error("core", ex, "NativeLook.ReadSelector"); /* no target graphic */ }
        if (look.Font == null)
        {
            try { look.Font = s.GetComponentInChildren<TMP_Text>(true)?.font; } catch (Exception ex) { DebugLog.Error("core", ex, "NativeLook.ReadSelector"); }
        }
        return look;
    }

    private static SpriteLook FromImage(Image img, string source)
    {
        var look = new SpriteLook { Source = source };
        try
        {
            var sp = img.sprite;
            float h = 0f;
            try { h = img.rectTransform.rect.height; } catch (Exception ex) { DebugLog.Error("core", ex, "NativeLook.FromImage"); }
            if (sp != null)
            {
                var b = sp.border;
                look.Sprite = sp;
                look.BorderPx = Mathf.Max(Mathf.Max(b.x, b.y), Mathf.Max(b.z, b.w));
                look.Sliced = img.type == Image.Type.Sliced && look.BorderPx > 0f;
                float ppu = sp.pixelsPerUnit > 0f ? sp.pixelsPerUnit : 100f;
                look.Ppu = ppu;
                float mult = img.pixelsPerUnitMultiplier > 0f ? img.pixelsPerUnitMultiplier : 1f;
                look.CornerUnits = look.BorderPx * (100f / ppu) / mult;
                look.Source += " (" + sp.name + ")";
            }
            else
            {
                // A sprite-less Image draws a plain rectangle, unless a rounded-corners component shapes it.
                float r = RoundedCornerRadius(img.gameObject);
                if (r > 0f) { look = Rounded(r); look.Source = source + $" (rounded {r:0})"; }
                else look.Source += " (rect)";
            }
            var sk = img.TryCast<FunCore.UI.SkewedImage>();
            if (sk != null && h > 1f && Mathf.Abs(sk.skewX) > 0.01f)
            {
                look.Skewed = true;
                look.SkewPerHeight = sk.skewX / h;
                look.Source += " skewed";
            }
        }
        catch (Exception e) { DebugLog.Error("core", e, "NativeLook.FromImage"); look.Source += " (" + e.Message + ")"; }
        return look;
    }

    private static float RoundedCornerRadius(GameObject go)
    {
        try
        {
            var c = go.GetComponent(Il2CppType.Of<Nobi.UiRoundedCorners.ImageWithRoundedCorners>())?.TryCast<Nobi.UiRoundedCorners.ImageWithRoundedCorners>();
            if (c != null) return c.radius;
            var ci = go.GetComponent(Il2CppType.Of<Nobi.UiRoundedCorners.ImageWithIndependentRoundedCorners>())?.TryCast<Nobi.UiRoundedCorners.ImageWithIndependentRoundedCorners>();
            if (ci != null) { var r = ci.r; return Mathf.Max(Mathf.Max(r.x, r.y), Mathf.Max(r.z, r.w)); }
        }
        catch (Exception ex) { DebugLog.Error("core", ex, "NativeLook.RoundedCornerRadius"); }
        return 0f;
    }

    // ---- panels: the biggest framed image in the game's settings / server screens ----

    private static (SpriteLook, Color?) FindPanel()
    {
        Image best = null;
        float bestArea = 0f;
        Consider<ContextUICreateServer>(ref best, ref bestArea);
        Consider<ContextUISettingsMenu>(ref best, ref bestArea);
        Consider<ContextUIPauseMenu>(ref best, ref bestArea);
        Consider<ContextUITwoOptionModal>(ref best, ref bestArea);
        if (best == null) return (null, null);
        return (FromImage(best, "panel " + Name(best.gameObject)), best.color);
    }

    private static void Consider<T>(ref Image best, ref float bestArea) where T : Component
    {
        int seen = 0;
        foreach (var o in Resources.FindObjectsOfTypeAll(Il2CppType.Of<T>()))
        {
            if (++seen > 4) break;
            var c = o.TryCast<T>();
            if (c == null) continue;
            var images = c.GetComponentsInChildren<Image>(true);
            if (images == null) continue;
            foreach (var img in images)
            {
                if (img == null) continue;
                var sp = img.sprite;
                bool framed = sp != null && img.type == Image.Type.Sliced || RoundedCornerRadius(img.gameObject) > 0f;
                if (!framed) continue;
                var r = img.rectTransform.rect;
                // Full-screen dimmers/backgrounds aren't panels.
                if (r.width < 240f || r.height < 160f || r.width > 1700f || r.height > 950f) continue;
                float area = r.width * r.height;
                if (area > bestArea) { bestArea = area; best = img; }
            }
        }
    }

    // ---- slider ----

    private static (SpriteLook, Color?, SpriteLook, Color?, SpriteLook) FindSlider()
    {
        int seen = 0;
        foreach (var o in Resources.FindObjectsOfTypeAll(Il2CppType.Of<UISliderSelector>()))
        {
            if (++seen > 8) break;
            var s = o.TryCast<UISliderSelector>();
            var slider = s?.slider;
            if (slider == null) continue;
            Image fill = null, knob = null, back = null;
            try { fill = slider.fillRect?.GetComponent<Image>(); } catch (Exception ex) { DebugLog.Error("core", ex, "NativeLook.static"); }
            try { knob = slider.handleRect?.GetComponent<Image>(); } catch (Exception ex) { DebugLog.Error("core", ex, "NativeLook.static"); }
            var all = slider.GetComponentsInChildren<Image>(true);
            if (all != null)
                foreach (var img in all)
                {
                    if (img == null || fill != null && img.Pointer == fill.Pointer || knob != null && img.Pointer == knob.Pointer) continue;
                    back = img;
                    break;
                }
            if (back == null && fill == null) continue;
            return (back == null ? null : FromImage(back, "slider track"), back?.color,
                fill == null ? null : FromImage(fill, "slider fill"), fill?.color,
                knob == null ? null : FromImage(knob, "slider knob"));
        }
        return (null, null, null, null, null);
    }

    // ---- fonts ----

    private static (TMP_FontAsset body, TMP_FontAsset heading) FindFonts(SelectorLook row, SelectorLook button)
    {
        TMP_FontAsset body = row?.Font, heading = button?.Font;
        if (!Alive(body)) body = null;
        if (!Alive(heading)) heading = null;
        if (body == null || heading == null)
        {
            // Most used font among the game's UI texts (loaded menus, HUD and prefabs).
            var counts = new Dictionary<IntPtr, (TMP_FontAsset font, int n)>();
            int seen = 0;
            foreach (var o in Resources.FindObjectsOfTypeAll(Il2CppType.Of<TextMeshProUGUI>()))
            {
                if (++seen > 3000) break;
                var t = o.TryCast<TextMeshProUGUI>();
                var f = t?.font;
                if (!Alive(f)) continue;
                counts[f.Pointer] = counts.TryGetValue(f.Pointer, out var e) ? (f, e.n + 1) : (f, 1);
            }
            var top = counts.Values.OrderByDescending(v => v.n).Select(v => v.font).FirstOrDefault();
            body ??= top;
            heading ??= top;
        }
        return (body, heading);
    }

    // ---------- sounds ----------

    private static FMODEventRef _hoverSfx, _pressSfx, _tabSfx, _sliderSfx, _optionSfx;
    private static bool SoundsFound => _pressSfx != null;
    private static bool _soundBroken;
    private static float _lastHover, _lastSlider;

    private static void FindSounds()
    {
        UISfxManager best = null;
        foreach (var o in Resources.FindObjectsOfTypeAll(Il2CppType.Of<UISfxManager>()))
        {
            var m = o.TryCast<UISfxManager>();
            if (m == null) continue;
            best ??= m;
            try { if (m.gameObject.scene.IsValid()) { best = m; break; } } catch (Exception ex) { DebugLog.Error("core", ex, "NativeLook.FindSounds"); }
        }
        if (best == null) return;
        _hoverSfx = best.buttonOverlayEventRef;
        _pressSfx = best.buttonPressedEventRef;
        _tabSfx = best.tabSwitchedEventRef;
        _sliderSfx = best.sliderValueChangedEventRef;
        _optionSfx = best.carouselOptionValueChangedEventRef;
    }

    internal static void PlayHover()
    {
        if (Time.unscaledTime - _lastHover < 0.05f) return;
        _lastHover = Time.unscaledTime;
        Play(_hoverSfx);
    }

    internal static void PlayPress() => Play(_pressSfx);
    internal static void PlayTab() => Play(_tabSfx ?? _pressSfx);
    internal static void PlayOption() => Play(_optionSfx ?? _pressSfx);

    internal static void PlaySlider()
    {
        if (Time.unscaledTime - _lastSlider < 0.06f) return;
        _lastSlider = Time.unscaledTime;
        Play(_sliderSfx ?? _optionSfx);
    }

    private static void Play(FMODEventRef r)
    {
        if (_soundBroken || r == null || !NativeStyle.SoundsOn) return;
        try
        {
            if (!(bool)r) return;
            var ev = r.Event;
            if (ev.IsNull) return;
            var cam = Camera.main;
            FMODUnity.RuntimeManager.PlayOneShot(ev, cam != null ? cam.transform.position : Vector3.zero);
        }
        catch (Exception e)
        {
            DebugLog.Error("core", e, "NativeLook.Play");
            _soundBroken = true;
            Core.CorePlugin.Logger?.LogWarning("Native menu sounds disabled: " + e.Message);
        }
    }

    // ---------- generated shapes (fallbacks, and for small parts like switches) ----------

    private const int GenRadius = 24;
    private static Sprite _roundSprite;

    private static void EnsureGeneratedShapes()
    {
        if (_roundSprite != null && (bool)_roundSprite && Square?.Sprite != null && (bool)Square.Sprite) return;
        int size = GenRadius * 2 + 2;
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
        tex.hideFlags = HideFlags.HideAndDontSave;
        tex.wrapMode = TextureWrapMode.Clamp;
        tex.filterMode = FilterMode.Bilinear;
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            float px = x + 0.5f, py = y + 0.5f;
            float cx = Mathf.Clamp(px, GenRadius, size - GenRadius);
            float cy = Mathf.Clamp(py, GenRadius, size - GenRadius);
            float d = Mathf.Sqrt((px - cx) * (px - cx) + (py - cy) * (py - cy));
            tex.SetPixel(x, y, new Color(1f, 1f, 1f, Mathf.Clamp01(GenRadius - d + 0.5f)));
        }
        tex.Apply();
        _roundSprite = Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100f, 0,
            SpriteMeshType.FullRect, new Vector4(GenRadius, GenRadius, GenRadius, GenRadius));
        _roundSprite.hideFlags = HideFlags.HideAndDontSave;

        var white = new Texture2D(4, 4, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave };
        for (int y = 0; y < 4; y++) for (int x = 0; x < 4; x++) white.SetPixel(x, y, Color.white);
        white.Apply();
        var sq = Sprite.Create(white, new Rect(0, 0, 4, 4), new Vector2(0.5f, 0.5f), 100f);
        sq.hideFlags = HideFlags.HideAndDontSave;

        RoundSmall = Rounded(5f);
        RoundMedium = Rounded(10f);
        Pill = Rounded(999f);
        Square = new SpriteLook { Sprite = sq, Source = "square" };
    }

    /// <summary>Our own anti-aliased rounded rectangle with the given corner radius (menu units).</summary>
    internal static SpriteLook Rounded(float radius) => new()
    {
        Sprite = _roundSprite,
        Sliced = true,
        BorderPx = GenRadius,
        CornerUnits = radius,
        Source = "rounded",
    };
}
