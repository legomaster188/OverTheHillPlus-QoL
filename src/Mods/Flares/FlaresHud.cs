using System;
using OverTheHillPlus.UI;
using UnityEngine;

namespace OverTheHillPlus.Flares;

/// <summary>A one-line message in the game HUD style for a few seconds ("Flare fired", "Flare ready in 6 s").</summary>
internal static class FlaresHud
{
    private const float ShownFor = 2.5f, W = 300f, H = 28f;
    private static string _text;
    private static float _at = -100f;
    private static GUIStyle _style;

    internal static void Say(string text)
    {
        _text = text;
        _at = Time.unscaledTime;
    }

    internal static void Draw()
    {
        var ev = Event.current;
        if (ev == null || ev.type != EventType.Repaint || string.IsNullOrEmpty(_text)) return;
        float age = Time.unscaledTime - _at;
        if (age > ShownFor || CameraView.HudHidden || CoreBehaviour.CleanView) return;
        try
        {
            Theme.Ensure();
            _style ??= new GUIStyle(Theme.Label) { fontSize = 13, alignment = TextAnchor.MiddleCenter, richText = false };
            _style.normal.textColor = Theme.Text;
            var screen = HudLayout.Begin();
            var r = HudLayout.Place(HudCorner.BottomRight, screen, W, H);
            r.x = (screen.x - W) / 2f;
            r.y = screen.y * 0.78f;
            var prev = GUI.color;
            GUI.color = new Color(1f, 1f, 1f, Mathf.Clamp01((ShownFor - age) / 0.4f));
            Theme.Fill(r, new Color(0.07f, 0.09f, 0.11f, 0.86f), 8);
            Theme.Fill(new Rect(r.x, r.y, 4f, r.height), Theme.Accent, 2);
            GUI.Label(r, _text, _style);
            GUI.color = prev;
        }
        catch (Exception e) { DebugLog.Error(FlaresPlugin.ModId, e, "FlaresHud.Draw"); }
        finally { HudLayout.End(); }
    }
}
