using OverTheHillPlus.Shared;
using OverTheHillPlus.UI;
using OverTheHillPlus.UI.HudKit;
using UnityEngine;

namespace OverTheHillPlus.Unreleased;

/// <summary>A small corner notice (the garage and join messages), drawn from the mod's OnGui hook.</summary>
internal static class Notice
{
    private const float Seconds = 6f;
    private static string _title, _sub;
    private static float _at = -100f;

    internal static void Show(string title, string sub)
    {
        _title = title;
        _sub = sub ?? "";
        _at = Time.unscaledTime;
    }

    internal static void Draw()
    {
        float age = Time.unscaledTime - _at;
        if (age < 0f || age > Seconds || string.IsNullOrEmpty(_title)) return;
        if (!HudDraw.Repaint) return;
        // The screen layer: these notes come while joining and in the garage, when the game's HUD may be away.
        if (!HudDraw.Begin(UnreleasedPlugin.ModId, HudLayer.Screen)) return;
        var color = GUI.color;
        try
        {
            Theme.Ensure();
            var screen = HudLayout.Begin();
            GUI.color = new Color(1f, 1f, 1f, Mathf.Clamp01(Seconds - age)); // fades over the last second
            var inner = WorldOverlay.PanelBox(new Rect(screen.x - 380f, 80f, 360f, 50f), Theme.Bad);
            HudDraw.Label(new Rect(inner.x + 4f, inner.y, inner.width - 6f, 20f), _title, Theme.Label);
            HudDraw.Label(new Rect(inner.x + 4f, inner.y + 20f, inner.width - 6f, 18f), _sub, Theme.Small);
        }
        finally
        {
            GUI.color = color;
            HudLayout.End();
            HudDraw.End();
        }
    }
}
