using System;
using Il2CppInterop.Runtime;
#if MELON
using Il2CppTMPro;
#else
using TMPro;
#endif
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
    // A menu's buttons fade and slide in when it opens (the game animates each one); a clone made during that
    // animation keeps the frame it was copied in (often invisible). For a few seconds the clone follows its source.
    private static readonly System.Collections.Generic.List<(GameObject clone, GameObject source)> Mirror = new();

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
        Mirror.RemoveAll(m => m.clone == null || m.clone.name == name);
        Mirror.Add((go, source.gameObject));
        _relabelUntil = Time.unscaledTime + 3f;
        FollowSource(go, source.gameObject);
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
            foreach (var (clone, _) in Mirror) Settle(clone);
            Mirror.Clear();
            Relabel.Clear(); // drop the references so destroyed buttons' wrappers can be collected
            return;
        }
        for (int i = Mirror.Count - 1; i >= 0; i--)
        {
            var (clone, source) = Mirror[i];
            try
            {
                if (clone == null || source == null) { Mirror.RemoveAt(i); continue; }
                FollowSource(clone, source);
            }
            catch (Exception ex) { DebugLog.Error("core", ex, "GameUiButtons.Mirror"); Mirror.RemoveAt(i); }
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

    /// <summary>The clone's fade (canvas groups and each graphic's canvas alpha) and scale follow the source's, part by part.</summary>
    private static void FollowSource(GameObject clone, GameObject source)
    {
        clone.transform.localScale = source.transform.localScale;
        var sg = source.GetComponentsInChildren<CanvasGroup>(true);
        var cg = clone.GetComponentsInChildren<CanvasGroup>(true);
        for (int i = 0; i < sg.Length && i < cg.Length; i++) cg[i].alpha = sg[i].alpha;
        var sr = source.GetComponentsInChildren<CanvasRenderer>(true);
        var cr = clone.GetComponentsInChildren<CanvasRenderer>(true);
        for (int i = 0; i < sr.Length && i < cr.Length; i++) cr[i].SetAlpha(sr[i].GetAlpha());
    }

    /// <summary>After following: never leave the clone faded out (its source may be hidden or gone by then).</summary>
    private static void Settle(GameObject clone)
    {
        try
        {
            if (clone == null) return;
            foreach (var g in clone.GetComponentsInChildren<CanvasGroup>(true)) if (g.alpha < 0.99f) g.alpha = 1f;
            foreach (var r in clone.GetComponentsInChildren<CanvasRenderer>(true)) if (r.GetAlpha() < 0.99f) r.SetAlpha(1f);
        }
        catch (Exception ex) { DebugLog.Error("core", ex, "GameUiButtons.Settle"); }
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
