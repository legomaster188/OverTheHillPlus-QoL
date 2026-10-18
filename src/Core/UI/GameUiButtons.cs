using System;
using Il2CppInterop.Runtime;
using TMPro;
using UnityEngine;
using Object = UnityEngine.Object;

namespace OverTheHillPlus.UI;

/// <summary>
/// Adds buttons to the game's own menus by cloning an existing button, so they inherit the game's
/// look, hover animation and sounds. Localization components are stripped so our label sticks.
/// Used by core's MODS entry in the ESC menu (UI/ModsEntry) and by mods that add their own buttons.
/// </summary>
internal static class GameUiButtons
{
    // Several buttons can be added in the same frame (e.g. PLUS and REJOIN on the main menu): keep all their labels.
    private static readonly System.Collections.Generic.List<(GameObject go, string text)> Relabel = new();
    private static float _relabelUntil;

    /// <summary>Clones <paramref name="source"/> next to it with a new label and click action.</summary>
    public static GameObject Clone(UIButtonSelector source, string name, string label, int siblingIndex, Action onClick)
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
                catch (Exception e) { Core.CorePlugin.Logger?.LogError($"{name} click: {e}"); }
            })));

        if (siblingIndex >= 0) go.transform.SetSiblingIndex(siblingIndex);
        SetLabel(go, label);

        // Some text components get re-filled a frame or two later; keep our label for a moment.
        if (_relabelUntil <= 0f || Time.unscaledTime > _relabelUntil) Relabel.Clear();
        Relabel.RemoveAll(r => r.go == null || r.go.name == name);
        Relabel.Add((go, label));
        _relabelUntil = Time.unscaledTime + 2f;
        return go;
    }

    /// <summary>Called every frame (CoreBehaviour).</summary>
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
            catch (Exception ex) { DebugLog.Error("core", ex, "GameUiButtons.Tick"); Relabel.RemoveAt(i); }
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
}
