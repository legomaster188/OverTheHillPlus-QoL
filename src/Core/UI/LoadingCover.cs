using System;
#if MELON
using Il2CppTMPro;
#else
using TMPro;
#endif
using OverTheHillPlus.UI.HudKit;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace OverTheHillPlus.UI;

/// <summary>
/// A full-screen loading card over everything, in the game's fonts: a title, a line of text and a bar. It stays up across
/// scene loads, so the game's own menus and lobby can run behind it.
/// </summary>
internal static class LoadingCover
{
    private static readonly Color Back = new(0.075f, 0.09f, 0.115f, 1f), Cream = new(1f, 0.976f, 0.933f, 1f), Yellow = new(1f, 0.82f, 0.36f, 1f);

    private static GameObject _go;
    private static TextMeshProUGUI _title, _line;
    private static RectTransform _bar;

    internal static bool Up => _go != null;

    internal static void Show(string title, string line, Sprite picture = null)
    {
        Hide();
        try
        {
            _go = new GameObject("OTH_LoadingCover");
            Object.DontDestroyOnLoad(_go);
            var canvas = _go.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 32600;
            var scaler = _go.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 1f;

            Fill(_go.transform, "Back", Back);
            if (picture != null)
            {
                var pic = Part(_go.transform, "Picture", new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0f, 110f), new Vector2(960f, 540f));
                var img = pic.gameObject.AddComponent<Image>();
                img.sprite = picture;
                img.preserveAspect = true;
                img.raycastTarget = false;
            }
            HudLook.Ensure();
            _title = Label(_go.transform, "Title", title, 54f, HudFont.Black, Cream, new Vector2(0f, -220f));
            _line = Label(_go.transform, "Line", line, 24f, HudFont.Regular, new Color(Cream.r, Cream.g, Cream.b, 0.75f), new Vector2(0f, -280f));

            var track = Part(_go.transform, "Track", new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0f, -330f), new Vector2(640f, 6f));
            var trackImage = track.gameObject.AddComponent<Image>();
            trackImage.color = new Color(1f, 1f, 1f, 0.14f);
            trackImage.raycastTarget = false;
            _bar = Part(track, "Bar", new Vector2(0f, 0f), new Vector2(0f, 1f), Vector2.zero, Vector2.zero);
            _bar.pivot = new Vector2(0f, 0.5f);
            var barImage = _bar.gameObject.AddComponent<Image>();
            barImage.color = Yellow;
            barImage.raycastTarget = false;
        }
        catch (Exception e)
        {
            Core.CorePlugin.Logger?.LogWarning("[Core] the loading card could not be made: " + e.Message);
            Hide();
        }
    }

    /// <summary><paramref name="progress"/>: 0 to 1, or below 0 to leave the bar as it is.</summary>
    internal static void Set(string line, float progress = -1f)
    {
        if (_go == null) return;
        try
        {
            if (line != null && _line != null && _line.text != line) _line.text = line;
            if (progress >= 0f && _bar != null) _bar.sizeDelta = new Vector2(640f * Mathf.Clamp01(progress), 0f);
        }
        catch (Exception e) { DebugLog.Error("core", e, "LoadingCover.Set"); }
    }

    internal static void Hide()
    {
        try { if (_go != null) Object.Destroy(_go); }
        catch (Exception e) { DebugLog.Error("core", e, "LoadingCover.Hide"); }
        _go = null;
        _title = _line = null;
        _bar = null;
    }

    private static RectTransform Part(Transform parent, string name, Vector2 min, Vector2 max, Vector2 at, Vector2 size)
    {
        var go = new GameObject(name);
        var rt = go.AddComponent<RectTransform>();
        rt.SetParent(parent, false);
        rt.anchorMin = min;
        rt.anchorMax = max;
        rt.anchoredPosition = at;
        rt.sizeDelta = size;
        return rt;
    }

    private static void Fill(Transform parent, string name, Color color)
    {
        var rt = Part(parent, name, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
        var img = rt.gameObject.AddComponent<Image>();
        img.color = color;
        img.raycastTarget = true;
    }

    private static TextMeshProUGUI Label(Transform parent, string name, string text, float size, HudFont font, Color color, Vector2 at)
    {
        var rt = Part(parent, name, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), at, new Vector2(1400f, size * 1.6f));
        var t = rt.gameObject.AddComponent<TextMeshProUGUI>();
        var asset = HudLook.Font(font);
        if (asset != null) t.font = asset;
        t.fontSize = size;
        t.color = color;
        t.alignment = TextAlignmentOptions.Center;
        t.enableWordWrapping = false;
        t.raycastTarget = false;
        t.text = text ?? "";
        return t;
    }
}
