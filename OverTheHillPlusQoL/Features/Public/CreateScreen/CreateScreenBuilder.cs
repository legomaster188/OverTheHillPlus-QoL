using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace OverTheHillPlus.Features.Public.CreateScreen;

/// <summary>
/// Puts the <see cref="CreateRow"/>s on the game's Create Server screen as clones of its own Privacy row.
///
/// Decoded from the game (Cpp2IL): ContextUICreateServer.Start fills four UIOptionSelector rows (Privacy, Max players,
/// Allow mods, Region), subscribes to each row's CarouselOption.OnChanged, puts the rows and the Create button in its
/// <c>selectables</c> list and calls UIUtilities.SetVerticalNavigation(selectables, true, false, false) (again from
/// UpdateServerRegionList). A cloned row wires itself: UIOptionSelector.Awake subscribes its own OnChanged to its
/// carousel, and the carousel's Start hooks its arrow buttons. Delegates aren't copied by Instantiate, so a clone never
/// calls the game's handlers; ours go on the clone's OnValueChanged.
///
/// Our rows go into the same <c>selectables</c> list (before Create, the PLUS button after it), so the game's own
/// navigation pass includes them. The screen is rebuilt each time the game creates it; the rows are its children and
/// go with it.
/// </summary>
internal static class CreateScreenBuilder
{
    internal const string RowPrefix = "OTHPlus_Row_";
    internal const string PlusButtonName = "OTHPlus_CreateServerButton";

    internal sealed class Live
    {
        public CreateRow Def;
        public GameObject Go;
        public UIOptionSelector Selector;
        public CarouselOption Carousel;
        public TMP_Text Label;
        public string Signature;
        public bool Hidden;
        public string Flash;
        public float FlashUntil;
    }

    internal static readonly List<Live> Rows = new();
    internal static ContextUICreateServer Screen;

    private static Transform _parent;
    private static RectTransform _templateRt, _anchorRt;
    private static float _templateHeight;
    private static bool _layout;
    private static int _fitFrames = -1;
    private static float _nextSync;
    private static bool _suppress;
    private static float _saveAt = -1f;
    private static readonly HashSet<string> Logged = new();

    /// <summary>Pointer of the row that wants Enter / click (the server name), for the cheap check in the UISelector patch.</summary>
    internal static IntPtr SubmitRowPtr;

    // ------------------------------------------------------------------ build

    public static void Build(ContextUICreateServer screen)
    {
        Clear();
        if (screen == null || CreateScreenFeature.NativeRows == null || !CreateScreenFeature.NativeRows.Value) return;

        var template = screen.PrivacyOptionSelector ?? screen.AllowModsOptionSelector;
        if (template == null || template.CarouselOption == null) { LogOnce("template", "no option row to copy; rows not added"); return; }
        var parent = template.transform.parent;
        if (parent == null) return;

        Screen = screen;
        _parent = parent;
        _layout = parent.GetComponent<LayoutGroup>() != null;
        _templateRt = template.transform.TryCast<RectTransform>();
        _templateHeight = _templateRt != null ? _templateRt.rect.height : 0f;

        // rows left behind by an earlier build of this same screen
        for (int i = parent.childCount - 1; i >= 0; i--)
        {
            var child = parent.GetChild(i);
            if (child != null && child.name.StartsWith(RowPrefix, StringComparison.Ordinal)) Object.Destroy(child.gameObject);
        }

        var anchor = LastGameRow(screen, parent) ?? template.transform;
        _anchorRt = anchor.TryCast<RectTransform>();
        int insertAt = anchor.GetSiblingIndex() + 1;
        foreach (var def in CreateScreenRows.Sorted())
        {
            bool available;
            try { available = def.Available == null || def.Available(); }
            catch { available = false; }
            if (!available) continue;

            Live row = null;
            try { row = CreateLive(template, parent, def); }
            catch (Exception e)
            {
                LogOnce("row " + def.Id, $"row '{def.Id}' failed: {e.Message}");
                try { if (row?.Go != null) Object.Destroy(row.Go); } catch { }
                row = null;
            }
            if (row == null) continue;
            if (_layout) row.Go.transform.SetSiblingIndex(insertAt++);
            Rows.Add(row);
            if (def.Submit != null && SubmitRowPtr == IntPtr.Zero) SubmitRowPtr = row.Selector.Pointer;
        }
        if (Rows.Count == 0) return;

        if (!_layout) PlaceManually(screen, template, anchor);
        UpdateSelectables();
        _fitFrames = 2; // measure once the layout has run
        _nextSync = Time.unscaledTime + 0.25f;
    }

    /// <summary>The game's option row that comes last under <paramref name="parent"/>.</summary>
    private static Transform LastGameRow(ContextUICreateServer s, Transform parent)
    {
        Transform best = null;
        void Consider(UIOptionSelector o)
        {
            if (o == null || o.transform.parent == null || o.transform.parent.Pointer != parent.Pointer) return;
            if (best == null || o.transform.GetSiblingIndex() > best.GetSiblingIndex()) best = o.transform;
        }
        Consider(s.PrivacyOptionSelector);
        Consider(s.MaxPlayersOptionSelector);
        Consider(s.AllowModsOptionSelector);
        Consider(s.ServerRegionOptionSelector);
        return best;
    }

    private static Live CreateLive(UIOptionSelector template, Transform parent, CreateRow def)
    {
        var go = Object.Instantiate(template.gameObject, parent);
        go.name = RowPrefix + def.Id;
        go.SetActive(true);
        StripLocalization(go);
        var sel = go.GetComponent<UIOptionSelector>();
        var car = sel != null ? sel.CarouselOption : null;
        if (sel == null || car == null)
        {
            Object.Destroy(go);
            throw new InvalidOperationException("the copied row has no option selector");
        }
        var live = new Live { Def = def, Go = go, Selector = sel, Carousel = car, Label = FindLabel(go, car) };
        if (live.Label != null) live.Label.text = def.Label;
        else LogOnce("label", "row label text not found; rows show no name");
        sel.interactable = true;
        try { car.CarouselInteractable = true; } catch { }
        Fill(live, force: true);
        var captured = live;
        sel.add_OnValueChanged(DelegateSupport.ConvertDelegate<Il2CppSystem.Action>(new Action(() => Changed(captured))));
        return live;
    }

    /// <summary>The row's name text: any text that isn't the carousel's value or inside its arrows.</summary>
    private static TMP_Text FindLabel(GameObject go, CarouselOption car)
    {
        IntPtr value = car.Option != null ? car.Option.Pointer : IntPtr.Zero;
        foreach (var t in go.GetComponentsInChildren<TMP_Text>(true))
        {
            if (t == null || t.Pointer == value) continue;
            if (Under(t.transform, car.Left) || Under(t.transform, car.Right)) continue;
            return t;
        }
        return null;
    }

    private static bool Under(Transform t, Component c) => c != null && t.IsChildOf(c.transform);

    private static void StripLocalization(GameObject go)
    {
        foreach (var c in go.GetComponentsInChildren<Component>(true))
        {
            if (c == null) continue;
            var typeName = c.GetIl2CppType().FullName ?? "";
            if (typeName.Contains("Locali", StringComparison.OrdinalIgnoreCase)) Object.DestroyImmediate(c);
        }
    }

    /// <summary>No layout group: stack the rows under the last of the game's rows / Create / PLUS, with the game's spacing.</summary>
    private static void PlaceManually(ContextUICreateServer screen, UIOptionSelector template, Transform anchor)
    {
        var parentRt = _parent.TryCast<RectTransform>();
        if (parentRt == null || _templateRt == null) return;
        float h = Mathf.Max(10f, _templateRt.rect.height);
        float gap = h * 0.15f;
        var second = screen.MaxPlayersOptionSelector;
        if (second != null && second.transform.parent != null && second.transform.parent.Pointer == _parent.Pointer)
        {
            var srt = second.transform.TryCast<RectTransform>();
            if (srt != null)
            {
                float g = Mathf.Abs(LocalBottom(parentRt, _templateRt) - LocalTop(parentRt, srt));
                if (g < h) gap = g;
            }
        }

        // below whichever of the game's rows, Create and PLUS sits lowest in this container
        float bottom = LocalBottom(parentRt, anchor.TryCast<RectTransform>());
        foreach (var t in new[] { screen.CreateButton != null ? screen.CreateButton.transform : null, _parent.Find(PlusButtonName) })
        {
            if (t == null || t.parent == null || t.parent.Pointer != _parent.Pointer) continue;
            var rt = t.TryCast<RectTransform>();
            if (rt != null) bottom = Mathf.Min(bottom, LocalBottom(parentRt, rt));
        }

        var basePos = _templateRt.localPosition;
        float pivotDown = (1f - _templateRt.pivot.y) * h;
        float top = bottom - gap;
        foreach (var row in Rows)
        {
            row.Go.transform.localPosition = new Vector3(basePos.x, top - pivotDown, basePos.z);
            top -= h + gap;
        }
    }

    private static float LocalBottom(RectTransform space, RectTransform rt) =>
        rt == null ? 0f : space.InverseTransformPoint(rt.TransformPoint(new Vector3(0f, rt.rect.yMin, 0f))).y;

    private static float LocalTop(RectTransform space, RectTransform rt) =>
        rt == null ? 0f : space.InverseTransformPoint(rt.TransformPoint(new Vector3(0f, rt.rect.yMax, 0f))).y;

    private static float WorldBottom(RectTransform rt) => rt.TransformPoint(new Vector3(0f, rt.rect.yMin, 0f)).y;
    private static float WorldTop(RectTransform rt) => rt.TransformPoint(new Vector3(0f, rt.rect.yMax, 0f)).y;

    // ------------------------------------------------------------------ values

    /// <summary>Shows the setting's current value (and new option labels, if they changed).</summary>
    internal static void Fill(Live live, bool force)
    {
        int idx = live.Def.Selected();
        var opts = live.Def.Options();
        if (opts == null || opts.Count == 0) return;
        idx = Mathf.Clamp(idx, 0, opts.Count - 1);
        string sig = string.Join("\u0001", opts);
        _suppress = true;
        try
        {
            if (force || sig != live.Signature)
            {
                var list = new Il2CppSystem.Collections.Generic.List<string>();
                foreach (var o in opts) list.Add(o);
                live.Selector.Initialize(list, idx);
                live.Signature = sig;
            }
            else if (live.Carousel.GetSelected() != idx) live.Selector.SetSelected(idx);
        }
        finally { _suppress = false; }
    }

    private static void Changed(Live live)
    {
        if (_suppress) return;
        try
        {
            if (live.Go == null) return;
            live.Def.Choose(live.Carousel.GetSelected());
            _saveAt = Time.unscaledTime + 1f;
            _nextSync = Time.unscaledTime + 0.1f; // option labels may depend on the choice (server name)
        }
        catch (Exception e) { LogOnce("choose " + live.Def.Id, $"row '{live.Def.Id}' change failed: {e.Message}"); }
    }

    // ------------------------------------------------------------------ navigation

    /// <summary>Our visible rows before Create (after it without a layout group), PLUS right after Create; then the game's navigation pass.</summary>
    internal static void UpdateSelectables()
    {
        var screen = Screen;
        var list = screen?.selectables;
        if (list == null) return;
        for (int i = list.Count - 1; i >= 0; i--)
        {
            var s = list[i];
            if (s == null) continue;
            string n = s.gameObject.name;
            if (n.StartsWith(RowPrefix, StringComparison.Ordinal) || n == PlusButtonName) list.RemoveAt(i);
        }

        int create = -1;
        var createButton = screen.CreateButton;
        if (createButton != null)
            for (int i = 0; i < list.Count; i++)
                if (list[i] != null && list[i].Pointer == createButton.Pointer) { create = i; break; }

        Selectable plus = null;
        var plusTf = createButton != null && createButton.transform.parent != null ? createButton.transform.parent.Find(PlusButtonName) : null;
        if (plusTf != null) plus = plusTf.GetComponent<Selectable>();

        int at = create < 0 ? list.Count : create;
        if (_layout)
        {
            foreach (var r in Rows)
                if (!r.Hidden && r.Go != null) list.Insert(at++, r.Selector);
            if (plus != null) list.Insert(create < 0 ? list.Count : at + 1, plus);
        }
        else
        {
            at = create < 0 ? list.Count : create + 1;
            if (plus != null) list.Insert(at++, plus);
            foreach (var r in Rows)
                if (!r.Hidden && r.Go != null) list.Insert(at++, r.Selector);
        }
        ApplyNavigation(list);
    }

    private static void ApplyNavigation(Il2CppSystem.Collections.Generic.List<Selectable> list)
    {
        try
        {
            UIUtilities.SetVerticalNavigation(list.Cast<Il2CppSystem.Collections.Generic.IList<Selectable>>(), true, false, false);
            return;
        }
        catch (Exception e) { LogOnce("nav", "game navigation helper failed, linking rows directly: " + e.Message); }

        var valid = new List<Selectable>();
        for (int i = 0; i < list.Count; i++)
        {
            var s = list[i];
            if (s != null && s.interactable && s.gameObject.activeInHierarchy) valid.Add(s);
        }
        for (int i = 0; i < valid.Count; i++)
        {
            var nav = valid[i].navigation;
            nav.mode = Navigation.Mode.Explicit;
            nav.selectOnUp = i > 0 ? valid[i - 1] : null;
            nav.selectOnDown = i < valid.Count - 1 ? valid[i + 1] : null;
            nav.selectOnLeft = null;
            nav.selectOnRight = null;
            valid[i].navigation = nav;
        }
    }

    // ------------------------------------------------------------------ fitting

    /// <summary>
    /// Leaves out the last rows when they don't fit: below the screen's area / the canvas, squeezing the game's rows,
    /// or (Create in another container) running into the Create button.
    /// </summary>
    private static void Fit()
    {
        var parentRt = _parent != null ? _parent.TryCast<RectTransform>() : null;
        if (Screen == null || parentRt == null) return;

        foreach (var r in Rows) if (r.Go != null) r.Go.SetActive(false);
        Relayout(parentRt);

        var createRt = Screen.CreateButton != null ? Screen.CreateButton.transform.TryCast<RectTransform>() : null;
        var plusTf = Screen.CreateButton != null && Screen.CreateButton.transform.parent != null
            ? Screen.CreateButton.transform.parent.Find(PlusButtonName) : null;
        var plusRt = plusTf != null ? plusTf.TryCast<RectTransform>() : null;

        float floor = float.NegativeInfinity;
        try
        {
            var canvas = Screen.GetComponentInParent<Canvas>();
            var root = canvas != null ? canvas.rootCanvas : null;
            var rootRt = root != null ? root.transform.TryCast<RectTransform>() : null;
            if (rootRt != null) floor = WorldBottom(rootRt);
        }
        catch { }
        float gameLowest = GameLowest(createRt, plusRt);
        var screenRt = Screen.transform.TryCast<RectTransform>();
        // the screen's own rect counts only if the game's rows fit inside it (it may be a zero-size holder)
        if (screenRt != null && screenRt.rect.height > _templateHeight * 3f && gameLowest >= WorldBottom(screenRt) - 1f)
            floor = Mathf.Max(floor, WorldBottom(screenRt));
        // a mask between the rows and the screen would clip rows below it
        for (var t = _parent; t != null; t = t.parent)
        {
            var mrt = t.TryCast<RectTransform>();
            if (mrt != null && (t.GetComponent<RectMask2D>() != null || t.GetComponent<Mask>() != null) && gameLowest >= WorldBottom(mrt) - 1f)
                floor = Mathf.Max(floor, WorldBottom(mrt));
            if (t.Pointer == Screen.transform.Pointer) break;
        }

        // Create in another container, below the rows: our rows must stay above it
        bool createElsewhere = createRt != null && (createRt.parent == null || createRt.parent.Pointer != _parent.Pointer);
        float createTop = createElsewhere ? WorldTop(createRt) : float.NegativeInfinity;
        bool createBelow = createElsewhere && _anchorRt != null && createTop <= WorldBottom(_anchorRt) + 1f;
        float templateHeight = _templateRt != null ? _templateRt.rect.height : 0f;

        int shown = 0;
        bool full = false;
        foreach (var r in Rows)
        {
            if (r.Go == null) continue;
            if (full) { r.Hidden = true; continue; }
            r.Go.SetActive(true);
            Relayout(parentRt);
            var rt = r.Go.transform.TryCast<RectTransform>();
            bool fits = rt != null;
            if (fits)
            {
                float lowest = Mathf.Min(WorldBottom(rt), GameLowest(createRt, plusRt));
                if (lowest < floor - 1f) fits = false;
                if (templateHeight > 1f && _templateRt.rect.height < templateHeight * 0.85f) fits = false; // rows squeezed
                if (createBelow && WorldBottom(rt) < createTop - 1f) fits = false;
            }
            if (!fits)
            {
                r.Go.SetActive(false);
                r.Hidden = true;
                full = true;
                continue;
            }
            r.Hidden = false;
            shown++;
        }
        Relayout(parentRt);
        if (shown < Rows.Count)
            Plugin.Logger.LogInfo($"[CreateScreen] {Rows.Count - shown} of {Rows.Count} rows left out: the screen is too short (they're in PLUS > Lobby)");
        UpdateSelectables();
    }

    private static float GameLowest(RectTransform createRt, RectTransform plusRt)
    {
        float y = float.PositiveInfinity;
        if (createRt != null) y = Mathf.Min(y, WorldBottom(createRt));
        if (plusRt != null) y = Mathf.Min(y, WorldBottom(plusRt));
        return float.IsPositiveInfinity(y) ? 0f : y;
    }

    private static void Relayout(RectTransform parentRt)
    {
        if (!_layout) return;
        LayoutRebuilder.ForceRebuildLayoutImmediate(parentRt);
        // Create / PLUS may sit in an outer layout that also moves
        var outer = parentRt.parent != null ? parentRt.parent.TryCast<RectTransform>() : null;
        if (outer != null && outer.GetComponent<LayoutGroup>() != null) LayoutRebuilder.ForceRebuildLayoutImmediate(outer);
    }

    // ------------------------------------------------------------------ per frame

    public static void Tick()
    {
        if (_saveAt > 0f && Time.unscaledTime >= _saveAt)
        {
            _saveAt = -1f;
            try { ModConfig.File.Save(); } catch { }
        }
        if (Rows.Count == 0) return;
        CreateNameEditor.Tick();

        if (_fitFrames >= 0)
        {
            if (_fitFrames-- == 0)
            {
                try { if (Alive()) Fit(); }
                catch (Exception e) { LogOnce("fit", "measuring the screen failed: " + e.Message); }
            }
            return;
        }

        if (Time.unscaledTime < _nextSync) return;
        _nextSync = Time.unscaledTime + 0.25f;
        if (!Alive()) { Clear(); return; }
        foreach (var r in Rows)
        {
            if (r.Hidden || CreateNameEditor.IsEditing(r)) continue;
            try
            {
                if (r.Go == null) continue;
                if (r.Label != null && r.Label.text != r.Def.Label) r.Label.text = r.Def.Label;
                if (r.Flash != null)
                {
                    if (Time.unscaledTime < r.FlashUntil)
                    {
                        if (r.Carousel.Option != null && r.Carousel.Option.text != r.Flash) r.Carousel.Option.text = r.Flash;
                        continue;
                    }
                    r.Flash = null;
                    Fill(r, force: true);
                    continue;
                }
                Fill(r, force: false);
            }
            catch (Exception e) { LogOnce("sync " + r.Def.Id, $"row '{r.Def.Id}' refresh failed: {e.Message}"); }
        }
    }

    private static bool Alive()
    {
        try { return Screen != null && !Screen.WasCollected && Screen.gameObject != null; }
        catch { return false; }
    }

    internal static Live Find(string id)
    {
        foreach (var r in Rows) if (r.Def.Id == id && r.Go != null) return r;
        return null;
    }

    /// <summary>Called by the UISelector submit/click patch for <see cref="SubmitRowPtr"/>.</summary>
    internal static void Submitted(IntPtr selector, bool pointer)
    {
        foreach (var r in Rows)
        {
            if (r.Selector == null || r.Selector.Pointer != selector || r.Def.Submit == null || r.Hidden) continue;
            bool gamepad = !pointer && CreateNameEditor.FromGamepad();
            try { r.Def.Submit(gamepad); }
            catch (Exception e) { LogOnce("submit " + r.Def.Id, $"row '{r.Def.Id}' submit failed: {e.Message}"); }
            return;
        }
    }

    /// <summary>
    /// The game creates the server on Enter / A anywhere on this screen (StateServerBrowser.OnSubmit). Not when that
    /// press opens the name field or belongs to the PLUS button.
    /// </summary>
    internal static bool ShouldBlockCreate()
    {
        if (CreateNameEditor.BlocksCreate) return true;
        var es = EventSystem.current;
        var selected = es != null ? es.currentSelectedGameObject : null;
        if (selected == null) return false;
        var mouse = Mouse.current;
        if (mouse != null && (mouse.leftButton.wasPressedThisFrame || mouse.leftButton.wasReleasedThisFrame)) return false; // a click on Create
        string n = selected.name;
        if (n == PlusButtonName) return true;
        var name = Find(CreateScreenFeature.NameRowId);
        return name != null && selected.Pointer == name.Go.Pointer;
    }

    internal static void Clear()
    {
        CreateNameEditor.Reset();
        foreach (var r in Rows)
        {
            try { if (r.Go != null) Object.Destroy(r.Go); } catch { }
        }
        Rows.Clear();
        Screen = null;
        _parent = null;
        _templateRt = null;
        _anchorRt = null;
        _fitFrames = -1;
        SubmitRowPtr = IntPtr.Zero;
    }

    internal static void LogOnce(string key, string message)
    {
        if (Logged.Add(key)) Plugin.Logger.LogWarning("[CreateScreen] " + message);
    }
}
