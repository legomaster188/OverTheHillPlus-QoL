using OverTheHillPlus.UI;
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
        if (CoreBehaviour.GuiHidden) return; // never in a screenshot
        if (Event.current == null || Event.current.type != EventType.Repaint) return;

        Theme.Ensure();
        var keep = GUI.matrix;
        var color = GUI.color;
        float scale = Mathf.Max(1f, Screen.height / 1080f);
        GUI.matrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, new Vector3(scale, scale, 1f));
        float sw = Screen.width / scale;
        var r = new Rect(sw - 380f, 80f, 360f, 50f);
        GUI.color = new Color(1f, 1f, 1f, Mathf.Clamp01(Seconds - age)); // fades over the last second
        Theme.Fill(r, new Color(Theme.Bg.r, Theme.Bg.g, Theme.Bg.b, 0.9f), 10);
        Theme.Fill(new Rect(r.x, r.y, 4f, r.height), Theme.Bad, 2);
        GUI.Label(new Rect(r.x + 16f, r.y + 6f, r.width - 24f, 20f), _title, Theme.Label);
        GUI.Label(new Rect(r.x + 16f, r.y + 26f, r.width - 24f, 18f), _sub, Theme.Small);
        GUI.color = color;
        GUI.matrix = keep;
    }
}
