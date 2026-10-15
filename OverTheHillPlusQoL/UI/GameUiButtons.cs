using System;
using HarmonyLib;
using Il2CppInterop.Runtime;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace OverTheHillPlus.UI;

/// <summary>
/// Adds buttons to the game's own menus by cloning an existing button, so they inherit the game's
/// look, hover animation and sounds. Localization components are stripped so our label sticks.
/// </summary>
internal static class GameUiButtons
{
    private const string MainMenuName = "OTHPlus_MainMenuButton";
    private const string CreateServerName = "OTHPlus_CreateServerButton";

    private static Sprite _icon;
    // Several buttons can be added in the same frame (e.g. PLUS and REJOIN on the main menu): keep all their labels.
    private static readonly System.Collections.Generic.List<(GameObject go, string text)> Relabel = new();
    private static float _relabelUntil;

    /// <summary>Clones <paramref name="source"/> next to it with a new label and click action.</summary>
    public static GameObject Clone(UIButtonSelector source, string name, string label, int siblingIndex, Action onClick, bool withIcon)
    {
        var parent = source.transform.parent;
        var existing = parent.Find(name);
        if (existing != null) Object.Destroy(existing.gameObject);

        var go = Object.Instantiate(source.gameObject, parent);
        go.name = name;
        go.SetActive(true);
        StripLocalization(go);

        var button = go.GetComponent<UIButtonSelector>();
        button.add_OnPointerClickOrSubmit(DelegateSupport.ConvertDelegate<Il2CppSystem.Action<UIButtonSelector>>(
            new Action<UIButtonSelector>(_ =>
            {
                try { onClick(); }
                catch (Exception e) { Plugin.Logger.LogError($"{name} click: {e}"); }
            })));

        if (siblingIndex >= 0) go.transform.SetSiblingIndex(siblingIndex);
        SetLabel(go, label);
        if (withIcon) AddIcon(go);

        // Some text components get re-filled a frame or two later; keep our label for a moment.
        if (_relabelUntil <= 0f || Time.unscaledTime > _relabelUntil) Relabel.Clear();
        Relabel.RemoveAll(r => r.go == null || r.go.name == name);
        Relabel.Add((go, label));
        _relabelUntil = Time.unscaledTime + 2f;
        return go;
    }

    /// <summary>Called every frame by ModBehaviour.</summary>
    public static void Tick()
    {
        // Time first: the Unity null check is a native call, and this runs every frame forever.
        if (_relabelUntil <= 0f) return;
        if (Time.unscaledTime > _relabelUntil || Relabel.Count == 0)
        {
            _relabelUntil = 0f;
            Relabel.Clear(); // drop the references so destroyed buttons' wrappers can be collected
            return;
        }
        for (int i = Relabel.Count - 1; i >= 0; i--)
        {
            var (go, text) = Relabel[i];
            try
            {
                if (go == null) { Relabel.RemoveAt(i); continue; }
                SetLabel(go, text);
            }
            catch { Relabel.RemoveAt(i); }
        }
    }

    private static void StripLocalization(GameObject go)
    {
        foreach (var c in go.GetComponentsInChildren<Component>(true))
        {
            if (c == null) continue;
            var typeName = c.GetIl2CppType().FullName ?? "";
            if (typeName.Contains("Locali", StringComparison.OrdinalIgnoreCase)) Object.DestroyImmediate(c);
        }
    }

    private static void SetLabel(GameObject go, string label)
    {
        var text = go.GetComponentInChildren<TMP_Text>(true);
        if (text != null && text.text != label) text.text = label;
    }

    private static void AddIcon(GameObject go)
    {
        var text = go.GetComponentInChildren<TMP_Text>(true);
        if (text == null) return;
        var iconGo = new GameObject("OTHPlus_Icon");
        iconGo.transform.SetParent(text.transform, false);
        var img = iconGo.AddComponent<Image>();
        img.sprite = Icon();
        img.raycastTarget = false;
        img.preserveAspect = true;
        var rt = iconGo.GetComponent<RectTransform>();
        float size = Mathf.Clamp(text.fontSize * 1.05f, 18f, 48f);
        rt.sizeDelta = new Vector2(size, size);
        text.ForceMeshUpdate();
        var b = text.textBounds;
        float x = b.size.x > 0f ? b.max.x + size * 0.85f : size;
        rt.localPosition = new Vector3(x, b.center.y, 0f);
    }

    /// <summary>A small rounded square in the game's yellow with a dark plus.</summary>
    public static Sprite Icon()
    {
        if (_icon != null) return _icon;
        const int n = 64;
        var tex = new Texture2D(n, n, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave, filterMode = FilterMode.Bilinear };
        var yellow = Theme.Accent;
        var dark = Theme.Hex(0x14181F);
        const float r = 14f;
        for (int y = 0; y < n; y++)
        for (int x = 0; x < n; x++)
        {
            float px = x + 0.5f, py = y + 0.5f;
            float cx = Mathf.Clamp(px, r, n - r), cy = Mathf.Clamp(py, r, n - r);
            float a = Mathf.Clamp01(r - Mathf.Sqrt((px - cx) * (px - cx) + (py - cy) * (py - cy)) + 0.5f);
            bool plus = (Mathf.Abs(px - n / 2f) < 5f && Mathf.Abs(py - n / 2f) < 19f) || (Mathf.Abs(py - n / 2f) < 5f && Mathf.Abs(px - n / 2f) < 19f);
            var c = plus ? dark : yellow;
            tex.SetPixel(x, y, new Color(c.r, c.g, c.b, a));
        }
        tex.Apply();
        _icon = Sprite.Create(tex, new Rect(0, 0, n, n), new Vector2(0.5f, 0.5f), 100f);
        _icon.hideFlags = HideFlags.HideAndDontSave;
        return _icon;
    }
}

[HarmonyPatch(typeof(StateMainMenu), nameof(StateMainMenu.Open))]
internal static class MainMenuButton
{
    private static void Postfix(StateMainMenu __instance)
    {
        if (!ModConfig.MainMenuButton.Value) return;
        try
        {
            var ctx = __instance.context;
            if (ctx == null) return;
            var source = ctx.SettingsButton != null && ctx.SettingsButton.gameObject.activeSelf ? ctx.SettingsButton : ctx.ExtrasButton;
            if (source == null) return;
            int index = ctx.QuitGameButton != null && ctx.QuitGameButton.transform.parent == source.transform.parent
                ? ctx.QuitGameButton.transform.GetSiblingIndex()
                : source.transform.GetSiblingIndex() + 1;
            GameUiButtons.Clone(source, "OTHPlus_MainMenuButton", "PLUS", index, () => ModBehaviour.OpenMenu("QoL"), withIcon: true);
        }
        catch (Exception e) { Plugin.Logger.LogError("Main menu button failed: " + e); }
    }
}

internal static class CreateServerButton
{
    public static void Add(ContextUICreateServer screen)
    {
        try
        {
            var source = screen.CreateButton;
            if (source == null) return;
            GameUiButtons.Clone(source, "OTHPlus_CreateServerButton", "PLUS LOBBY SETTINGS",
                source.transform.GetSiblingIndex() + 1, () => ModBehaviour.OpenMenu(Menu.LobbyTab), withIcon: false);
        }
        catch (Exception e) { Plugin.Logger.LogError("Create Server button failed: " + e); }
    }
}
