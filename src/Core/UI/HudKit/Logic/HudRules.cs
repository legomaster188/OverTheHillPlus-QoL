using System;

namespace OverTheHillPlus.UI.HudKit;

/// <summary>What the HUD layer needs to know about the game this frame, read by <see cref="HudCanvas"/>.</summary>
internal struct HudView
{
    /// <summary>Nothing of the mods may draw (a screenshot is being taken).</summary>
    public bool GuiHidden;
    /// <summary>The player asked for a clean view (the photo mod's hide HUD).</summary>
    public bool CleanView;
    /// <summary>The game's photo mode or free camera.</summary>
    public bool PhotoOrFreeCamera;
    /// <summary>A cutscene or a loading screen.</summary>
    public bool Cutscene;
    /// <summary>The game's HUD exists (in the world; no HUD in the main menu).</summary>
    public bool GameHudUp;
    /// <summary>The opacity of the game's HUD (its CanvasGroup); negative when it could not be read.</summary>
    public float GameHudAlpha;
}

/// <summary>
/// The pure part of the HUD kit: how the mods' HUD layer follows the game's HUD, fades, the geometry of lines and turned
/// shapes, text sizes. No Unity types, so it is unit tested.
/// </summary>
internal static class HudRules
{
    /// <summary>The driving layer's canvas order: just over the game's HUD (GameUI/HUD is 20), under its pop-ups (100).</summary>
    public const int DrivingSort = 21;

    /// <summary>The screen layer's canvas order: over the game's settings and server screens (12), under its pop-ups (100).</summary>
    public const int ScreenSort = 90;

    /// <summary>Below this opacity a layer is switched off instead of drawn.</summary>
    public const float Invisible = 0.004f;

    /// <summary>
    /// The opacity of the driving layer: it is hidden in photo mode, cutscenes and a clean view, and otherwise follows the
    /// game's own HUD, which the game fades out for the pause menu, the map, the shops and every other screen that covers
    /// the world. With no game HUD (main menu, lobby) there is no driving layer.
    /// </summary>
    public static float DrivingAlpha(in HudView v)
    {
        if (v.GuiHidden || v.CleanView || v.PhotoOrFreeCamera || v.Cutscene || !v.GameHudUp) return 0f;
        if (v.GameHudAlpha < 0f || float.IsNaN(v.GameHudAlpha)) return 1f;
        return v.GameHudAlpha > 1f ? 1f : v.GameHudAlpha;
    }

    /// <summary>The opacity of the screen layer (panels over menus): only a screenshot hides it.</summary>
    public static float ScreenAlpha(in HudView v) => v.GuiHidden ? 0f : 1f;

    /// <summary>
    /// A note's opacity at <paramref name="age"/> seconds: full for <paramref name="life"/> seconds, the last
    /// <paramref name="fade"/> of them fading out; 0 afterwards and before it starts.
    /// </summary>
    public static float FadeOut(float age, float life, float fade)
    {
        if (age < 0f || age >= life) return 0f;
        if (fade <= 0f) return 1f;
        float left = life - age;
        return left >= fade ? 1f : left / fade;
    }

    /// <summary>
    /// The black's alpha for a panel's Opacity setting: the default (0.82) is the game's 40%, below it fades out, above it
    /// goes up to solid at 1, so a player's saved value keeps its meaning.
    /// </summary>
    public static float PanelAlpha(float opacity) =>
        opacity <= 0.82f ? opacity * (0.4f / 0.82f) : 0.4f + (opacity - 0.82f) / 0.18f * 0.6f;

    /// <summary>
    /// The size of text in the game's fonts for a size given in IMGUI pixels: they read a little smaller, and the game's
    /// own smallest HUD text is 12 to 14.
    /// </summary>
    public static float NativeSize(float imguiSize)
    {
        if (imguiSize <= 0f) return 14.5f;
        return imguiSize <= 14f ? imguiSize + 1.5f : imguiSize * 1.08f;
    }

    /// <summary>
    /// Where a <paramref name="w"/> x <paramref name="h"/> panel sits in a screen corner: <paramref name="margin"/> from the
    /// sides, <paramref name="top"/> from the top edge (the bottom corners use the margin), pushed <paramref name="offset"/>
    /// away from the corner along the edge to stack panels. Never off the screen, however small the window.
    /// </summary>
    public static (float x, float y) Place(bool right, bool bottom, float screenW, float screenH, float w, float h, float margin, float top, float offset)
    {
        float x = right ? screenW - w - margin : margin;
        float y = bottom ? screenH - h - margin - offset : top + offset;
        return (Clamp(x, 0f, Math.Max(0f, screenW - w)), Clamp(y, 0f, Math.Max(0f, screenH - h)));
    }

    private static float Clamp(float v, float min, float max) => v < min ? min : v > max ? max : v;

    /// <summary>How tall one line of the game's fonts is for its size (NunitoSans: ascent and descent 1.364 em).</summary>
    public const float LineHeight = 1.42f;

    /// <summary>
    /// The top and height to give a single line of text of <paramref name="size"/> so it fits: the game's fonts take more
    /// height than IMGUI's, and a line that doesn't fit its box shrinks or goes blank. The box grows, keeping the edge or
    /// middle the text is aligned to (vertical: -1 top, 0 middle, 1 bottom), so the words stay where they were meant to be.
    /// </summary>
    public static (float y, float height) FitLine(float y, float height, float size, int vertical)
    {
        float need = size * LineHeight;
        if (height >= need) return (y, height);
        float extra = need - height;
        return (vertical < 0 ? y : vertical > 0 ? y - extra : y - extra * 0.5f, need);
    }

    /// <summary>The midpoint, length and angle (degrees, clockwise on a y-down screen) of the line from a to b.</summary>
    public static (float cx, float cy, float length, float degrees) Line(float ax, float ay, float bx, float by)
    {
        float dx = bx - ax, dy = by - ay;
        float len = (float)Math.Sqrt(dx * dx + dy * dy);
        float deg = (float)(Math.Atan2(dy, dx) * 180.0 / Math.PI);
        return ((ax + bx) * 0.5f, (ay + by) * 0.5f, len, deg);
    }

    /// <summary>A point turned <paramref name="degrees"/> clockwise (on a y-down screen) round a pivot.</summary>
    public static (float x, float y) Turn(float x, float y, float pivotX, float pivotY, float degrees)
    {
        double a = degrees * Math.PI / 180.0;
        double c = Math.Cos(a), s = Math.Sin(a);
        double dx = x - pivotX, dy = y - pivotY;
        return ((float)(pivotX + dx * c - dy * s), (float)(pivotY + dx * s + dy * c));
    }

    /// <summary>
    /// How much a 9-sliced sprite's pixels are scaled so its corners look <paramref name="cornerUnits"/> units big on an
    /// element of <paramref name="width"/> x <paramref name="height"/> canvas units (never more than half of the element).
    /// </summary>
    public static float CornerMultiplier(float borderPx, float ppu, float cornerUnits, float k, float width, float height)
    {
        float want = Math.Max(0.5f, Math.Min(cornerUnits * k, Math.Min(width, height) * 0.5f));
        return borderPx * (100f / Math.Max(1f, ppu)) / want;
    }
}

/// <summary>
/// Keyboard-free selection for a row of HUD buttons, for a gamepad: left and right move the selection, A presses it. The
/// buttons count themselves in the Repaint pass (<see cref="BeginPass"/>, <see cref="Next"/> per button, <see cref="EndPass"/>),
/// so a panel whose buttons come and go needs no list. Input comes in through <see cref="Input"/> before the pass.
/// </summary>
internal sealed class HudFocusRing
{
    private int _index, _seen, _step;
    private bool _press;

    /// <summary>The selected button in the pass, -1 until the player moves.</summary>
    public int Focus { get; private set; } = -1;

    /// <summary>A move (-1 / +1) or a press since the last pass.</summary>
    public void Input(int step, bool press)
    {
        _step += step;
        _press |= press;
    }

    public void BeginPass()
    {
        _index = 0;
        if (_step != 0 && _seen > 0)
        {
            if (Focus < 0) Focus = _step > 0 ? 0 : _seen - 1;
            else Focus = ((Focus + _step) % _seen + _seen) % _seen;
        }
        _step = 0;
    }

    /// <summary>The next button: <paramref name="focused"/> if it is the selected one; true when it was pressed.</summary>
    public bool Next(out bool focused)
    {
        focused = _index == Focus;
        _index++;
        if (!focused || !_press) return false;
        _press = false;
        return true;
    }

    public void EndPass()
    {
        _seen = _index;
        _press = false;
        if (Focus >= _seen) Focus = _seen - 1;
    }
}

/// <summary>
/// One line of HUD text that is only worked out again when what it shows changes, not on every frame:
/// <c>if (memo.Changed(key)) memo.Text = ...;</c> The key packs the numbers shown, rounded to what the line prints.
/// </summary>
internal sealed class TextMemo
{
    private long _key = long.MinValue;

    /// <summary>The line as last worked out.</summary>
    public string Text = "";

    /// <summary>True when <paramref name="key"/> differs from the last one (the caller then sets <see cref="Text"/>).</summary>
    public bool Changed(long key)
    {
        if (key == _key) return false;
        _key = key;
        return true;
    }

    /// <summary>Packs up to four small numbers into a key.</summary>
    public static long Key(int a, int b = 0, int c = 0, int d = 0) =>
        ((long)(a & 0xFFFF) << 48) | ((long)(b & 0xFFFF) << 32) | ((long)(c & 0xFFFF) << 16) | (long)(d & 0xFFFF);
}
