using System;
using OverTheHillPlus.UI;
using OverTheHillPlus.UI.GameMenu;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace OverTheHillPlus.Stacking;

/// <summary>
/// The hint line at the bottom of the garage's cosmetics screen, in the game's own style: a copy of one of the
/// screen's key-hint rows (ContextUIMerchantMenu.ShowLocalModsLabel, the "show local mods" hint of the mod liveries
/// screen, else the row around the submit hint, SubmitButtonText), placed next to it, without its key icon and
/// navigation, with our text (its FunTextTMP is registered with core's FunTexts so the game's localization never writes
/// over it). Shown only while the cosmetics screen is the open one. When no row can be copied, a plain shadowed label
/// is drawn at the bottom of the screen instead (GUI.Label only).
/// </summary>
internal static class StackHint
{
    private const string Mod = StackingPlugin.ModId;
    private const string RowName = "OTH_StackingHint";

    private static StateMerchantMenu _menu;
    private static GameObject _row;
    private static FunTextTMP _text;
    private static IntPtr _builtFor;
    private static bool _drawGui;
    private static float _next;
    private static string _shownText;

    /// <summary>Why the game-style hint could not be made (shown on the MODS page), null when it could.</summary>
    public static string Problem { get; private set; }

    internal static void OnCosmeticsScreen(StateMerchantMenu menu)
    {
        _menu = menu;
        Refresh();
    }

    internal static void OnClose()
    {
        _menu = null;
        _drawGui = false;
        try { if (_row != null && _row.activeSelf) _row.SetActive(false); }
        catch (Exception e) { DebugLog.Error(Mod, e, "StackHint.OnClose"); }
    }

    internal static void Tick()
    {
        if (_menu == null && !_drawGui) return;
        if (Time.unscaledTime < _next) return;
        _next = Time.unscaledTime + 0.1f;
        try { Refresh(); }
        catch (Exception e)
        {
            DebugLog.Error(Mod, e, "StackHint.Tick");
            _menu = null;
            _drawGui = false;
        }
    }

    private static string Text() => StackRules.HintText(StackingConfig.PadName(StackingConfig.Gamepad?.Value ?? StackingConfig.PadHold.None));

    private static bool Wanted(out ContextUIMerchantMenu ctx)
    {
        ctx = null;
        if (_menu == null || StackingConfig.ShowHint == null || !StackingConfig.ShowHint.Value || !StackingConfig.ShiftStacks.Value) return false;
        if (_menu.currentSubMenu != StateMerchantMenu.MerchantSubMenu.CosmeticUpgrades) return false;
        ctx = _menu.merchantMenuUIContext;
        return ctx != null && ctx.gameObject.activeInHierarchy;
    }

    private static void Refresh()
    {
        bool want = Wanted(out var ctx);
        if (want && (_row == null || _builtFor != ctx.Pointer)) Build(ctx);
        if (_row != null)
        {
            if (_row.activeSelf != want) _row.SetActive(want);
            string text = Text();
            if (want && _text != null && _shownText != text)
            {
                _text.text = text;
                _shownText = text;
            }
        }
        // The copied row's bar may be hidden on this screen: then the plain label stands in.
        _drawGui = want && (_row == null || !_row.activeInHierarchy);
    }

    /// <summary>Copies one of the screen's key-hint rows into the same bar.</summary>
    private static void Build(ContextUIMerchantMenu ctx)
    {
        _builtFor = ctx.Pointer;
        _row = null;
        _text = null;
        _shownText = null;
        CrashTrail.Mark(Mod + ": hint row");
        GameObject template = null;
        string from = null;
        try
        {
            var local = ctx.ShowLocalModsLabel;
            if (local != null) { template = local; from = "ShowLocalModsLabel"; }
            if (template == null && ctx.SubmitButtonText != null)
            {
                for (var t = ctx.SubmitButtonText.transform; t != null && t != ctx.transform; t = t.parent)
                    if (t.GetComponent<ContextUIBindingWithLabel>() != null) { template = t.gameObject; from = "the submit hint row"; break; }
            }
        }
        catch (Exception e) { DebugLog.Error(Mod, e, "StackHint.Build"); template = null; }
        if (template == null || template.transform.parent == null)
        {
            Problem = "the cosmetics screen has no key-hint row to copy: a plain label is shown instead.";
            DebugLog.Info(Mod, "hint: " + Problem);
            return;
        }
        try
        {
            var parent = template.transform.parent;
            var go = Object.Instantiate(template, parent, false);
            go.name = RowName;
            Clean(go);
            FunTextTMP label = null;
            foreach (var t in go.GetComponentsInChildren<FunTextTMP>(true))
            {
                if (t == null) continue;
                FunTexts.Register(t);
                if (label == null) label = t;
                else t.text = "";
            }
            if (label == null)
            {
                Object.Destroy(go);
                Problem = $"the copied row ({from}) has no text: a plain label is shown instead.";
                DebugLog.Info(Mod, "hint: " + Problem);
                return;
            }
            try { label.raycastTarget = false; } catch (Exception ex) { DebugLog.Error(Mod, ex, "StackHint.Build"); }
            go.transform.SetAsLastSibling();
            _row = go;
            _text = label;
            Problem = null;
            DebugLog.Info(Mod, $"hint: a copy of {from} under {Path(parent)} (bar {(parent.gameObject.activeInHierarchy ? "shown" : "hidden")})");
        }
        catch (Exception e)
        {
            DebugLog.Error(Mod, e, "StackHint.Build");
            Problem = "could not copy the screen's key-hint row (" + e.Message + "): a plain label is shown instead.";
            _row = null;
            _text = null;
        }
    }

    /// <summary>No key icon, no binding, no localization, nothing selectable: just the text in the row's style.</summary>
    private static void Clean(GameObject go)
    {
        foreach (var icon in go.GetComponentsInChildren<UIInputActionIcon>(true))
        {
            if (icon == null) continue;
            if (icon.gameObject.Pointer == go.Pointer) Object.DestroyImmediate(icon);
            else Object.DestroyImmediate(icon.gameObject);
        }
        foreach (var b in go.GetComponentsInChildren<ContextUIBindingWithLabel>(true))
            if (b != null) Object.DestroyImmediate(b);
        foreach (var s in go.GetComponentsInChildren<Selectable>(true))
            if (s != null) Object.DestroyImmediate(s);
        foreach (var c in go.GetComponentsInChildren<Component>(true))
        {
            if (c == null) continue;
            string type = c.GetIl2CppType().FullName ?? "";
            if (type.Contains("Locali", StringComparison.OrdinalIgnoreCase)) Object.DestroyImmediate(c);
        }
        if (!go.activeSelf) go.SetActive(true);
    }

    private static string Path(Transform t)
    {
        string p = "";
        for (int i = 0; t != null && i < 6; i++, t = t.parent) p = t.name + (p.Length > 0 ? "/" + p : "");
        return p;
    }

    /// <summary>The stand-in label (IMGUI, Repaint only).</summary>
    internal static void Gui()
    {
        if (!_drawGui || Event.current == null || Event.current.type != EventType.Repaint) return;
        try { OverlayDraw.Label(Screen.width * 0.5f, Screen.height - 46f, Text(), 17, Theme.Text); }
        catch (Exception e) { DebugLog.Error(Mod, e, "StackHint.Gui"); _drawGui = false; }
    }
}
