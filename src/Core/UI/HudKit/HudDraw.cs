using System;
using System.Collections.Generic;
using UnityEngine;

namespace OverTheHillPlus.UI.HudKit;

/// <summary>
/// What a mod's HUD code calls. Between <see cref="Begin"/> and <see cref="End"/> everything lands on the game-style uGUI
/// layer (<see cref="HudCanvas"/>) with the game's fonts and colours. When that layer can't be used (the classic HUD
/// style, a failure, no font yet) the same calls fall through to the old IMGUI drawing, so a mod has one draw path:
/// <code>
/// if (!HudDraw.Repaint || !HudDraw.Begin("mymod")) return;
/// try
/// {
///     var screen = HudLayout.Begin();
///     HudLayout.Panel(r);                       // Theme.Fill follows the open group by itself
///     HudDraw.Label(line, "Hello", _style);     // the GUIStyle says size, colour, alignment, bold
/// }
/// finally { HudLayout.End(); HudDraw.End(); }
/// </code>
/// Theme.Fill and OverlayDraw's rects, lines and labels need no change. Panels with buttons call Begin in every event and
/// use <see cref="Button"/>: they emit in Repaint and take clicks as IMGUI in the others.
/// </summary>
internal static class HudDraw
{
    /// <summary>True in the Repaint event of this OnGUI pass. Cheaper than Event.current, which makes a wrapper per call.</summary>
    internal static bool Repaint => HudCanvas.Repaint;

    /// <summary>The open group draws on the uGUI layer (false: old IMGUI drawing).</summary>
    internal static bool Native => HudCanvas.Native;

    /// <summary>Opens the mod's group; false when the layer is hidden now and nothing should draw.</summary>
    internal static bool Begin(string id, HudLayer layer = HudLayer.Driving) => HudCanvas.Open(id, layer);

    /// <summary>Closes the group (call in a finally).</summary>
    internal static void End() => HudCanvas.Close();

    // ---------- text ----------

    // A mod's GUIStyle read once: styles are static and only their colour ever changes, and that goes through the
    // overloads with a colour below.
    private static readonly Dictionary<GUIStyle, HudText> Specs = new(ReferenceEqualityComparer.Instance);

    /// <summary>The look of text of this IMGUI style in the game's fonts.</summary>
    internal static HudText Spec(GUIStyle style)
    {
        if (Specs.TryGetValue(style, out var t)) return t;
        var fs = style.fontStyle;
        t = new HudText(HudRules.NativeSize(style.fontSize), style.normal.textColor, style.alignment,
            fs == FontStyle.Bold || fs == FontStyle.BoldAndItalic ? HudFont.Bold : HudFont.Regular)
        {
            Wrap = style.wordWrap,
            Rich = style.richText,
            Italic = fs == FontStyle.Italic || fs == FontStyle.BoldAndItalic,
        };
        Specs[style] = t;
        return t;
    }

    /// <summary>GUI.Label on the layer.</summary>
    internal static void Label(Rect r, string text, GUIStyle style)
    {
        if (HudCanvas.Native)
        {
            if (!HudCanvas.Emitting || HudCanvas.Text(r, text, Spec(style))) return;
        }
        GUI.Label(r, text, style);
    }

    /// <summary>GUI.Label in the mod's style with another colour.</summary>
    internal static void Label(Rect r, string text, GUIStyle style, Color color)
    {
        if (HudCanvas.Native)
        {
            if (!HudCanvas.Emitting) return;
            var s = Spec(style);
            s.Color = color;
            if (HudCanvas.Text(r, text, s)) return;
        }
        var prev = style.normal.textColor;
        style.normal.textColor = color;
        GUI.Label(r, text, style);
        style.normal.textColor = prev;
    }

    /// <summary>GUI.Label in the mod's style with another alignment.</summary>
    internal static void Label(Rect r, string text, GUIStyle style, TextAnchor align)
    {
        if (HudCanvas.Native)
        {
            if (!HudCanvas.Emitting) return;
            var s = Spec(style);
            s.Align = align;
            if (HudCanvas.Text(r, text, s)) return;
        }
        var prev = style.alignment;
        style.alignment = align;
        GUI.Label(r, text, style);
        style.alignment = prev;
    }

    /// <summary>
    /// A label in another of the game's fonts or sizes than the style gives: <paramref name="spec"/> on the layer,
    /// <paramref name="style"/> in the old drawing.
    /// </summary>
    internal static void Label(Rect r, string text, GUIStyle style, in HudText spec)
    {
        if (HudCanvas.Native)
        {
            if (!HudCanvas.Emitting || HudCanvas.Text(r, text, spec)) return;
        }
        GUI.Label(r, text, style);
    }

    /// <summary>
    /// Text with a dark shadow, for words straight on the world with no panel behind them. The old drawing uses
    /// <paramref name="shadow"/> (a copy of the style in the shadow's colour) one pixel down and right.
    /// </summary>
    internal static void Shadowed(Rect r, string text, GUIStyle style, GUIStyle shadow)
    {
        if (HudCanvas.Native)
        {
            if (!HudCanvas.Emitting) return;
            var s = Spec(style);
            s.Shadow = true;
            if (HudCanvas.Text(r, text, s)) return;
        }
        GUI.Label(new Rect(r.x + 1f, r.y + 1f, r.width, r.height), text, shadow);
        GUI.Label(r, text, style);
    }

    // Sizes of texts the layout depends on (a label's box), kept so a marker doesn't measure every frame.
    private static readonly Dictionary<(int, string), Vector2> Sizes = new();

    /// <summary>GUIStyle.CalcSize, remembered: what a label of this text takes.</summary>
    internal static Vector2 Measure(string text, GUIStyle style)
    {
        if (string.IsNullOrEmpty(text)) return Vector2.zero;
        var key = (System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(style), text);
        if (Sizes.TryGetValue(key, out var size)) return size;
        if (Sizes.Count > 512) Sizes.Clear();
        size = style.CalcSize(new GUIContent(text));
        Sizes[key] = size;
        return size;
    }

    private static readonly Dictionary<(int, string, int, bool), float> Heights = new();

    /// <summary>
    /// How high this wrapped text comes at this width, remembered. On the layer the words are bigger than the style's and
    /// their lines taller: the style wraps the same way in a box as much narrower, and the height scales back up.
    /// </summary>
    internal static float Height(string text, GUIStyle style, float width)
    {
        if (string.IsNullOrEmpty(text)) return 0f;
        bool native = HudCanvas.Native;
        var key = (System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(style), text, (int)width, native);
        if (Heights.TryGetValue(key, out var h)) return h;
        if (Heights.Count > 256) Heights.Clear();
        float k = 1f;
        if (native)
        {
            float size = style.fontSize > 0 ? style.fontSize : 13f;
            k = HudRules.NativeSize(size) / size * 1.03f;
        }
        h = style.CalcHeight(new GUIContent(text), width / k) * k * (native ? 1.1f : 1f);
        Heights[key] = h;
        return h;
    }

    // ---------- shapes ----------

    /// <summary>A HUD panel: the game's black at 40% with 5 unit corners.</summary>
    internal static void Panel(Rect r) => Theme.Fill(r, HudLook.Panel, HudLook.PanelRadius);

    /// <summary>A bar: a faint track and the filled part, <paramref name="fill"/> from 0 to 1 from the left.</summary>
    internal static void Bar(Rect r, float fill, Color color)
    {
        int radius = Mathf.Max(1, (int)(r.height * 0.5f));
        Theme.Fill(r, HudLook.Track, radius);
        float f = Mathf.Clamp01(fill);
        if (f > 0f) Theme.Fill(new Rect(r.x, r.y, r.width * f, r.height), color, radius);
    }

    /// <summary>A rounded fill turned <paramref name="degrees"/> clockwise round <paramref name="pivot"/> (dial needles, ticks).</summary>
    internal static void Turned(Rect r, Color color, int radius, float degrees, Vector2 pivot)
    {
        if (HudCanvas.Native)
        {
            if (!HudCanvas.Emitting) return;
            var (cx, cy) = HudRules.Turn(r.x + r.width * 0.5f, r.y + r.height * 0.5f, pivot.x, pivot.y, degrees);
            if (HudCanvas.FillRounded(new Rect(cx - r.width * 0.5f, cy - r.height * 0.5f, r.width, r.height), color, radius, degrees)) return;
        }
        var saved = GUI.matrix;
        GUI.matrix = HudLayout.Rotated(saved, degrees, pivot);
        Theme.Fill(r, color, radius);
        GUI.matrix = saved;
    }

    /// <summary>
    /// The whole of <paramref name="tex"/> placed at <paramref name="full"/>, seen only through <paramref name="window"/>
    /// (a map under its panel). Tinted by GUI.color. Fills made under <see cref="Behind"/> stay beneath it, all others
    /// over it. Not GUI.DrawTextureWithTexCoords on the old path: that call crashes the game.
    /// </summary>
    internal static void Texture(Rect window, Rect full, Texture tex)
    {
        if (tex == null) return;
        if (HudCanvas.Native)
        {
            if (!HudCanvas.Emitting || HudCanvas.Texture(window, full, tex, Color.white)) return;
        }
        GUI.BeginGroup(window);
        try { GUI.DrawTexture(new Rect(full.x - window.x, full.y - window.y, full.width, full.height), tex); }
        finally { GUI.EndGroup(); }
    }

    /// <summary>Fills made while <paramref name="behind"/> is true sit under the group's <see cref="Texture"/>s.</summary>
    internal static void Behind(bool behind) => HudCanvas.Behind = behind;

    /// <summary>A sprite, tinted; on the old drawing path the sprite's texture stretched over the rectangle.</summary>
    internal static void Sprite(Rect r, Sprite sprite, Color color, float degrees = 0f)
    {
        if (sprite == null) return;
        if (HudCanvas.Native)
        {
            if (!HudCanvas.Emitting || HudCanvas.Sprite(r, sprite, color, degrees)) return;
        }
        var saved = GUI.matrix;
        var prev = GUI.color;
        if (degrees != 0f) GUI.matrix = HudLayout.Rotated(saved, degrees, r.center);
        GUI.color = new Color(prev.r * color.r, prev.g * color.g, prev.b * color.b, prev.a * color.a);
        GUI.DrawTexture(r, sprite.texture);
        GUI.color = prev;
        GUI.matrix = saved;
    }

    // ---------- input ----------

    /// <summary>
    /// A button: drawn in the game's style on the layer (dark, turning the game's yellow when selected or under the pointer)
    /// with an invisible IMGUI control over it for the click; the old drawing is a plain GUI.Button in <paramref name="style"/>
    /// (Theme.ChipDanger comes out red here too). Call in every event. <paramref name="selected"/> is a state that stays on,
    /// <paramref name="focused"/> the gamepad's selection.
    /// </summary>
    internal static bool Button(Rect r, string label, GUIStyle style, bool selected = false, bool focused = false)
    {
        if (!HudCanvas.Native) return GUI.Button(r, label, style);
        if (HudCanvas.Emitting)
        {
            bool lit = selected || focused || r.Contains(Event.current.mousePosition);
            bool danger = ReferenceEquals(style, Theme.ChipDanger);
            var back = lit ? HudLook.Yellow : danger ? new Color(HudLook.Red.r, HudLook.Red.g, HudLook.Red.b, 0.55f) : HudLook.PanelStrong;
            Theme.Fill(r, back, HudLook.PanelRadius);
            var s = new HudText(13.5f, lit ? HudLook.Ink : HudLook.Cream, TextAnchor.MiddleCenter, HudFont.Bold);
            HudCanvas.Text(new Rect(r.x + 3f, r.y, r.width - 6f, r.height), label, s);
        }
        Theme.Ensure();
        return GUI.Button(r, GUIContent.none, Theme.Invisible);
    }
}
