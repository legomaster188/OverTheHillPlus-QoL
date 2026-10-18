using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace OverTheHillPlus.UI.GameMenu;

// After the namespace line on purpose: the game has its own global ModManager (mod.io).
using OverTheHillPlus.Core.Manager;

/// <summary>
/// Our mods inside the game's MOD BROWSER (StateLiveryModsMenu / ContextUILiveryModsMenu).
///
/// Decoded from the game (Cpp2IL): the browser is a filter column on the left (Subscribed only / Enabled only, each
/// a ContextUIButtonSelectorWithTextAndIcon: a button with a label and a check icon, wired by WireFilterButton and
/// shown checked by SetFilterChecked), a grid of mod cells in the middle (<c>Content</c>, under a scroll view, with a
/// pager and a "no mods found" notice), a details panel on the right (image, name, author, description, dates) and
/// key hints along the bottom (subscribe / enable / sort / order). The state opens from the main menu's MOD BROWSER
/// button (StateMainMenu.LiveryModsButtonPressed pushes it on the GameStateManager) and pops on the back input
/// unless CanPop is off.
///
/// What we add: one button per MODS tab in the filter column (INSTALLED MODS, AVAILABLE MODS: clones of the game's
/// filter button, checked while open). Pressing one hides the grid, its pager and the livery hints and shows a page
/// of the game's own settings widgets in the grid's place (<see cref="GamePage"/>), with the selected mod's name,
/// version / type and the selected row's explanation in the details panel. A mod's page opens from its row; Esc /
/// B goes back to the list (and from the list closes the browser, as the game does). Pressing the button again,
/// or one of the game's own filters, goes back to the liveries, which work as before.
///
/// The MODS lists (INSTALLED, AVAILABLE) look like the browser itself: each mod is a copy of the browser's own mod
/// cell (StateLiveryModsMenu.modCellPrefab, a ContextUIModCell; <see cref="GamePrefabs.TakeModCell"/>) with the mod's
/// image (<see cref="ModImages"/>), name, status, version and side, in a grid of the browser's column count
/// (<see cref="GamePage"/>). The selected card fills the details panel (image, name, author, description, version /
/// side, status, and the enabled panel); the browser's enable hint switches the selected mod on / off as the row's
/// arrows did. Pressing a card does what pressing its row did (a mod's page opens). Without the cell (a game update)
/// the lists keep their rows.
///
/// The rows come from the page's own draw code run in a recording pass every frame (<see cref="RowRecorder"/>), so
/// every mod's settings, the switches, install / update / uninstall and the required-mods prompt behave exactly as
/// before; text boxes are copies of the browser's own search field. Anything that goes wrong (a template or a part of
/// the browser missing after a game update, a widget that can't be made) is logged once and the old window
/// (UI/ModsPage, the full view) opens instead: it is only that fallback.
/// </summary>
internal static class GameModsTab
{
    private const string ButtonPrefix = "OTH_ModsTab_";

    private sealed class TabButton
    {
        public int Tab;
        public GameObject Go;
        public ContextUIButtonSelectorWithTextAndIcon Ui;
        public Selectable Selectable;
        public string Label;
    }

    private static StateLiveryModsMenu _state;
    private static ContextUILiveryModsMenu _ctx;
    private static RectTransform _area;
    private static GamePage _page;
    private static readonly List<TabButton> Buttons = new();

    /// <summary>A part of the browser we hide: through a CanvasGroup (invisible, not clickable or selectable), so its
    /// place in the browser's layout stays and the game can keep switching it on and off as it likes.</summary>
    private sealed class Veil
    {
        public GameObject Go;
        public CanvasGroup Group;
        public bool Added;
        public float Alpha;
        public bool Interactable, Blocks;
        /// <summary>Shown again (not clickable) while a card that uses it is selected: the enabled panel, the enable hint.</summary>
        public bool Show;
    }

    private static readonly List<Veil> Hidden = new();
    private static GameObject _details;
    private static bool _detailsWasActive;

    private static int _tab = -1;
    private static string _modId;
    private static bool _closeOnBack;
    private static bool _selectFirst;
    private static string _openNext;
    private static bool _canPopTaken;
    private static int _canPopBackFrame = -1;
    private static bool _broken;

    private static readonly RowRecorder Recorder = new();
    private static readonly List<GamePage.Entry> Entries = new();
    private static readonly HashSet<string> Logged = new();
    private static int _recordedFrame = -1;

    private static readonly RowModel BackRow = new() { Kind = RowKind.Button, Label = "Back", Value = "to the list of mods", Hint = "Esc / B goes back too." };
    private static readonly RowModel CloseRow = new() { Kind = RowKind.Button, Label = "Close", Hint = "Back to where you opened it. Esc / B too." };

    /// <summary>True while a MOD BROWSER is open with our buttons in it.</summary>
    internal static bool Attached => _state != null;

    /// <summary>True while one of our tabs is shown in it.</summary>
    internal static bool Active => _state != null && _tab >= 0;

    /// <summary>The browser we are in (null: none).</summary>
    internal static StateLiveryModsMenu State => _state;

    // ---------------------------------------------------------------- attach / detach (StateLiveryModsMenu.Open / Close)

    internal static void Attach(StateLiveryModsMenu state)
    {
        Detach();
        if (state == null) return;
        try
        {
            var ctx = state.contextUI;
            if (ctx == null) { Warn("parts", "the MOD BROWSER's parts were not found; the MODS page opens from the ESC menu and F7 instead"); return; }
            _state = state;
            _ctx = ctx;
            _broken = false;
            GamePrefabs.TakeTextField(ctx.SearchField);
            GamePrefabs.TakeModCell(state, ctx);
            AddButtons();
            if (Buttons.Count == 0) { Detach(); return; }
            LogLayoutOnce();
        }
        catch (Exception e)
        {
            Warn("attach", "could not add MODS to the MOD BROWSER: " + e.Message);
            Detach();
        }
    }

    internal static void Detach()
    {
        if (_state != null && _canPopTaken)
        {
            try { if (!_state.WasCollected) _state.CanPop = true; } catch { }
        }
        _canPopTaken = false;
        try { _page?.Destroy(); } catch { }
        _page = null;
        Buttons.Clear();
        Hidden.Clear();
        NavChanged.Clear();
        GameRow.ByPointer.Clear();
        // Also when a row's Destroy threw above (the screen is gone already): a stale pointer would let a later game
        // input field at the same address have its submit swallowed.
        GameRow.TextFields.Clear();
        _details = null;
        _state = null;
        _ctx = null;
        _area = null;
        _tab = -1;
        _modId = null;
        _closeOnBack = false;
        _openNext = null;
        _later = null;
        _detailsTaken = false;
        _shownImage = null;
        _enableAction = null;
        _enableActionLooked = false;
        Recorder.Abort();
    }

    private static void AddButtons()
    {
        var template = _ctx.EnabledOnlyFilter ?? _ctx.SubscribedOnlyFilter;
        if (template == null || template.buttonSelector == null)
        {
            Warn("filters", "the MOD BROWSER's filter buttons were not found; MODS opens from the ESC menu and F7");
            return;
        }
        var parent = template.transform.parent;
        bool layout = parent != null && parent.GetComponent<LayoutGroup>() != null;
        var tRt = template.transform.TryCast<RectTransform>();
        Vector2 step = Vector2.zero;
        var other = _ctx.SubscribedOnlyFilter;
        if (!layout && tRt != null)
        {
            var oRt = other != null && other.Pointer != template.Pointer && other.transform.parent != null && other.transform.parent.Pointer == parent.Pointer
                ? other.transform.TryCast<RectTransform>() : null;
            step = oRt != null ? tRt.anchoredPosition - oRt.anchoredPosition : new Vector2(0f, -Mathf.Max(30f, tRt.rect.height * 1.15f));
        }
        for (int i = parent.childCount - 1; i >= 0; i--)
        {
            var child = parent.GetChild(i);
            if (child != null && child.name.StartsWith(ButtonPrefix, StringComparison.Ordinal)) Object.Destroy(child.gameObject);
        }

        var tabs = ModsPage.TabNames;
        Selectable previous = template.buttonSelector;
        int at = template.transform.GetSiblingIndex() + 1;
        for (int i = 0; i < tabs.Count; i++)
        {
            // Made under the hidden holder and set up there, so it wakes up in the column with our label (no localization).
            var go = Object.Instantiate(template.gameObject, GamePrefabs.EnsureHolder(), false);
            go.name = ButtonPrefix + tabs[i];
            go.SetActive(true);
            StripLocalization(go);
            var ui = go.GetComponent<ContextUIButtonSelectorWithTextAndIcon>();
            if (ui == null || ui.buttonSelector == null) { Object.Destroy(go); continue; }
            var b = new TabButton { Tab = i, Go = go, Ui = ui, Selectable = ui.buttonSelector, Label = (tabs[i] + " mods").ToUpperInvariant() };
            SetLabel(b);
            go.transform.SetParent(parent, false);
            go.transform.SetSiblingIndex(at++);
            if (!layout && tRt != null)
            {
                var rt = go.transform.TryCast<RectTransform>();
                if (rt != null) rt.anchoredPosition = tRt.anchoredPosition + step * (i + 1);
            }
            int tab = i;
            ui.buttonSelector.add_OnPointerClickOrSubmit(DelegateSupport.ConvertDelegate<Il2CppSystem.Action<UIButtonSelector>>(
                new Action<UIButtonSelector>(_ => Guard("tab button", () => TabPressed(tab)))));
            Link(previous, b.Selectable);
            previous = b.Selectable;
            Buttons.Add(b);
        }
        // The game's own filters are about liveries: pressing one goes back to them.
        foreach (var f in new[] { _ctx.SubscribedOnlyFilter, _ctx.EnabledOnlyFilter })
        {
            if (f == null || f.buttonSelector == null) continue;
            f.buttonSelector.add_OnPointerClickOrSubmit(DelegateSupport.ConvertDelegate<Il2CppSystem.Action<UIButtonSelector>>(
                new Action<UIButtonSelector>(_ => Guard("filter", () => { if (Active) Deactivate(false); }))));
        }
        UpdateChecks();
    }

    /// <summary>Puts <paramref name="below"/> under <paramref name="above"/> in the keyboard order, where the game links its filters explicitly.</summary>
    private static void Link(Selectable above, Selectable below)
    {
        if (above == null || below == null) return;
        var a = above.navigation;
        var b = below.navigation;
        if (a.mode != Navigation.Mode.Explicit) return; // automatic: the game finds the new button by position
        var oldDown = a.selectOnDown;
        a.selectOnDown = below;
        above.navigation = a;
        b.mode = Navigation.Mode.Explicit;
        b.selectOnUp = above;
        b.selectOnDown = oldDown;
        below.navigation = b;
        if (oldDown != null)
        {
            var d = oldDown.navigation;
            if (d.mode == Navigation.Mode.Explicit && d.selectOnUp != null && d.selectOnUp.Pointer == above.Pointer)
            {
                d.selectOnUp = below;
                oldDown.navigation = d;
            }
        }
    }

    private static void StripLocalization(GameObject go)
    {
        foreach (var c in go.GetComponentsInChildren<Component>(true))
        {
            if (c == null) continue;
            string type = c.GetIl2CppType().FullName ?? "";
            if (type.Contains("Locali", StringComparison.OrdinalIgnoreCase)) Object.DestroyImmediate(c);
        }
        foreach (var t in go.GetComponentsInChildren<FunTextTMP>(true))
        {
            FunTexts.Register(t);
        }
    }

    private static void SetLabel(TabButton b)
    {
        TMP_Text label = b.Ui.label;
        if (label == null) label = b.Go.GetComponentInChildren<TMP_Text>(true);
        if (label != null && label.text != b.Label) label.text = b.Label;
    }

    private static void UpdateChecks()
    {
        foreach (var b in Buttons)
        {
            try { StateLiveryModsMenu.SetFilterChecked(b.Ui, b.Tab == _tab); }
            catch (Exception e) { Warn("check", "the MODS buttons' check marks: " + e.Message); }
        }
    }

    // ---------------------------------------------------------------- opening a tab

    private static void TabPressed(int tab)
    {
        if (_tab == tab && _modId == null) Deactivate(true);
        else Activate(tab, null, false);
    }

    /// <summary>Shows tab <paramref name="tab"/> (or the page of mod <paramref name="modId"/>). False when the game's
    /// widgets aren't there: the full view opened instead.</summary>
    internal static bool Activate(int tab, string modId, bool closeOnBack)
    {
        if (!Attached) return false;
        if (_broken || !GamePrefabs.Ensure() || !EnsurePage())
        {
            Warn("templates", "the game's settings widgets were not found (" + GamePrefabs.Summary + "); MODS shows the full view instead");
            OpenFullViewFor(modId);
            return false;
        }
        var tabs = ModsPage.TabNames;
        _tab = Mathf.Clamp(tab, 0, Math.Max(0, tabs.Count - 1));
        var m = ModManager.Find(modId);
        _modId = m?.Id;
        _closeOnBack = closeOnBack && _modId != null;
        HideLiveries();
        _page.SetVisible(true);
        _page.Above = ButtonFor(_tab)?.Selectable;
        _navFrame = -100; // the browser's links into the hidden grid go to the page on the next pass
        _page.ScrollToTop();
        _selectFirst = true;
        UpdateChecks();
        UpdateCanPop();
        try { _state.selectedMod = null; } catch { }
        return true;
    }

    /// <summary>Back to the game's liveries; <paramref name="select"/> puts the selection on our button.</summary>
    internal static void Deactivate(bool select)
    {
        if (!Attached) return;
        int was = _tab;
        _tab = -1;
        _modId = null;
        _closeOnBack = false;
        RestoreNavigation();
        _page?.SetVisible(false);
        RestoreLiveries();
        UpdateChecks();
        UpdateCanPop();
        if (select) Select(ButtonFor(was)?.Selectable);
    }

    /// <summary>Our button for tab <paramref name="tab"/> (a copy that failed is left out, so not by position).</summary>
    private static TabButton ButtonFor(int tab)
    {
        foreach (var b in Buttons) if (b.Tab == tab) return b;
        return null;
    }

    private static (int tab, string modId, bool closeOnBack)? _later;

    /// <summary>
    /// Opens a tab or a mod's page from code (ModsPage.Open while the browser is up): now, or right after the page's
    /// pass when a row asked for it from inside the pass. A page that was opened straight on a mod keeps closing the
    /// browser on Esc, so a mod opened from it does too.
    /// </summary>
    internal static void OpenLater(int tab, string modId, bool closeOnBack)
    {
        bool close = (closeOnBack && !Active) || (_closeOnBack && modId != null);
        if (RowRecorder.Recording) _later = (tab, modId, close);
        else Activate(tab, modId, close);
    }

    // ---------------------------------------------------------------- navigation while a tab of ours shows

    /// <summary>Directions of the browser's own selectables (and our buttons, copies of its filter) we pointed at the
    /// page, with where they pointed before: (selectable, direction 0 up / 1 down / 2 left / 3 right).</summary>
    private static readonly Dictionary<(IntPtr sel, int dir), (Selectable s, Selectable before)> NavChanged = new();
    private static int _navFrame = -100;

    /// <summary>
    /// The game links its filters to the grid explicitly (StateLiveryModsMenu.Populate → SetFilterLeftNavigation,
    /// again on every refresh), and explicit links don't skip hidden targets. While a tab of ours shows, every link of
    /// a selectable outside the hidden parts that goes into them (in any direction) goes to the page's first row
    /// instead; links we set that point at a row that is gone follow the new first row. Undone by
    /// <see cref="RestoreNavigation"/>.
    /// </summary>
    private static void FixNavigation(bool force)
    {
        if (!Active || _page == null || _ctx == null) return;
        if (!force && Time.frameCount - _navFrame < 15) return;
        _navFrame = Time.frameCount;
        var first = _page.First();
        if (first == null) return;
        foreach (var s in _ctx.GetComponentsInChildren<Selectable>(true))
        {
            if (s == null || InsideVeil(s.transform) || _page.Owns(s)) continue;
            var n = s.navigation;
            if (n.mode != Navigation.Mode.Explicit) continue;
            bool button = ButtonOf(s) != null;
            bool changed = false;
            for (int dir = 0; dir < 4; dir++)
            {
                var t = NavGet(n, dir);
                var key = (s.Pointer, dir);
                bool ours = NavChanged.ContainsKey(key);
                bool intoHidden = t != null && InsideVeil(t.transform);
                // a link we set whose row went (or isn't the first any more)
                bool stale = ours && (t == null || (_page.Owns(t) && t.Pointer != first.Pointer));
                // our buttons: an empty left / right leads to the page (the way back from the column)
                bool emptySide = !ours && t == null && (dir == 2 || dir == 3) && button;
                if (!intoHidden && !stale && !emptySide) continue;
                if (!ours) NavChanged[key] = (s, t);
                NavSet(ref n, dir, first);
                changed = true;
            }
            if (changed) s.navigation = n;
        }
    }

    private static TabButton ButtonOf(Selectable s)
    {
        if (s == null) return null;
        foreach (var b in Buttons) if (b.Selectable != null && b.Selectable.Pointer == s.Pointer) return b;
        return null;
    }

    /// <summary>Puts back the links <see cref="FixNavigation"/> changed (unless the game re-linked them meanwhile).</summary>
    private static void RestoreNavigation()
    {
        foreach (var kv in NavChanged)
        {
            try
            {
                var (s, before) = kv.Value;
                if (s == null) continue;
                var n = s.navigation;
                var now = NavGet(n, kv.Key.dir);
                if (now != null && (_page == null || !_page.Owns(now))) continue; // the game set its own link since
                NavSet(ref n, kv.Key.dir, before);
                s.navigation = n;
            }
            catch { /* went with its screen */ }
        }
        NavChanged.Clear();
    }

    private static Selectable NavGet(Navigation n, int dir) => dir switch
    {
        0 => n.selectOnUp,
        1 => n.selectOnDown,
        2 => n.selectOnLeft,
        _ => n.selectOnRight,
    };

    private static void NavSet(ref Navigation n, int dir, Selectable to)
    {
        switch (dir)
        {
            case 0: n.selectOnUp = to; break;
            case 1: n.selectOnDown = to; break;
            case 2: n.selectOnLeft = to; break;
            default: n.selectOnRight = to; break;
        }
    }

    private static bool InsideVeil(Transform t)
    {
        if (t == null) return false;
        foreach (var h in Hidden)
            if (h.Go != null && t.IsChildOf(h.Go.transform)) return true;
        return false;
    }

    /// <summary>The selection landed in a hidden part (a link the game set a moment ago, a click before it hid): onto the page.</summary>
    private static void PullSelection()
    {
        var es = EventSystem.current;
        var go = es != null ? es.currentSelectedGameObject : null;
        if (go == null || !InsideVeil(go.transform)) return;
        var to = _page?.First() ?? ButtonFor(_tab)?.Selectable;
        if (to != null) es.SetSelectedGameObject(to.gameObject);
    }

    private static bool EnsurePage()
    {
        if (_page != null && _page.Alive) return true;
        _page = null;
        _area = FindGridArea();
        if (_area == null) return false;
        _page = GamePage.Create(_area, "OTH_ModsPage");
        _page.OnInput = OnInput;
        _page.SetVisible(false);
        return true;
    }

    /// <summary>
    /// The grid of livery cells: its scroll view when it has one, else the cells' parent, else the cells' container;
    /// never one that also holds the filter column, the details panel or the search field (those stay visible).
    /// </summary>
    private static RectTransform FindGridArea()
    {
        var content = _ctx.Content;
        if (content == null) { Warn("grid", "the MOD BROWSER's grid was not found"); return null; }
        var scroll = content.GetComponentInParent<ScrollRect>();
        foreach (var t in new[] { scroll != null ? scroll.transform : null, content.parent, content })
        {
            if (t == null || t.Pointer == _ctx.transform.Pointer || HoldsOthers(t)) continue;
            var rt = t.TryCast<RectTransform>();
            if (rt != null) return rt;
        }
        Warn("grid", "the MOD BROWSER's grid shares its container with the other parts");
        return null;
    }

    private static bool HoldsOthers(Transform area)
    {
        bool Inside(Component c) => c != null && c.transform.IsChildOf(area);
        bool InsideGo(GameObject g) => g != null && g.transform.IsChildOf(area);
        if (Inside(_ctx.EnabledOnlyFilter) || Inside(_ctx.SubscribedOnlyFilter) || Inside(_ctx.SearchField) || InsideGo(_ctx.DescriptionPanel)) return true;
        foreach (var b in Buttons) if (b.Go != null && b.Go.transform.IsChildOf(area)) return true;
        return false;
    }

    private static void HideLiveries()
    {
        if (Hidden.Count == 0)
        {
            Remember(_area != null ? _area.gameObject : null);
            Remember(_ctx.Pager != null ? _ctx.Pager.gameObject : null);
            Remember(_ctx.NoModsFoundGO);
            Remember(_ctx.InstalledLabel);
            Remember(_ctx.EnabledPanel);
            Remember(_ctx.SubscribeAction != null ? _ctx.SubscribeAction.gameObject : null);
            Remember(_ctx.EnableAction != null ? _ctx.EnableAction.gameObject : null);
            Remember(_ctx.SortAction != null ? _ctx.SortAction.gameObject : null);
            Remember(_ctx.OrderAction != null ? _ctx.OrderAction.gameObject : null);
            Remember(_ctx.CurrentFilter != null ? _ctx.CurrentFilter.gameObject : null);
            Remember(_ctx.CurrentSort != null ? _ctx.CurrentSort.gameObject : null);
            Remember(_ctx.SortOrderIcon != null ? _ctx.SortOrderIcon.gameObject : null);
            _details = _ctx.DescriptionPanel;
            _detailsWasActive = _details != null && _details.activeSelf;
        }
        KeepHidden();
        if (_details != null && !_details.activeSelf) _details.SetActive(true);
    }

    private static void Remember(GameObject go)
    {
        if (go == null || _page != null && go.Pointer == _page.Root.Pointer) return;
        // Never a part that holds what stays on screen (the details we write into, our buttons, the page).
        bool Holds(Component c) => c != null && c.transform.IsChildOf(go.transform);
        if (Holds(_ctx.ModName) || Holds(_ctx.ModDescription) || (_ctx.DescriptionPanel != null && _ctx.DescriptionPanel.transform.IsChildOf(go.transform))) return;
        if (_page != null && _page.Root.transform.IsChildOf(go.transform)) return;
        foreach (var b in Buttons) if (b.Go != null && b.Go.transform.IsChildOf(go.transform)) return;
        foreach (var h in Hidden) if (h.Go.Pointer == go.Pointer) return;
        var group = go.GetComponent<CanvasGroup>();
        var veil = new Veil { Go = go, Group = group, Added = group == null };
        if (veil.Added) veil.Group = go.AddComponent<CanvasGroup>();
        else
        {
            veil.Alpha = group.alpha;
            veil.Interactable = group.interactable;
            veil.Blocks = group.blocksRaycasts;
        }
        Hidden.Add(veil);
    }

    /// <summary>Keeps the hidden parts hidden while a tab of ours is up (the game may fade a group of its own back in).</summary>
    private static void KeepHidden()
    {
        if (_details != null && !_details.activeSelf) _details.SetActive(true); // a refresh may close the details
        foreach (var h in Hidden)
        {
            var g = h.Group;
            if (g == null) continue;
            float alpha = h.Show ? 1f : 0f;
            if (g.alpha != alpha) g.alpha = alpha;
            if (g.interactable) g.interactable = false;
            if (g.blocksRaycasts) g.blocksRaycasts = false;
        }
    }

    private static void RestoreLiveries()
    {
        foreach (var h in Hidden)
        {
            try
            {
                if (h.Group == null) continue;
                if (h.Added) Object.Destroy(h.Group);
                else
                {
                    h.Group.alpha = h.Alpha;
                    h.Group.interactable = h.Interactable;
                    h.Group.blocksRaycasts = h.Blocks;
                }
            }
            catch { }
        }
        Hidden.Clear();
        RestoreDetails();
        if (_details != null)
        {
            // The game's own empty details (it shows a livery's again once a cell is selected).
            bool cleared = false;
            try
            {
                _state.ClearModDetails();
                cleared = true;
            }
            catch (Exception e) { Warn("details", "clearing the details panel: " + e.Message); }
            if (!cleared)
            {
                SetDetail(_ctx.ModName, "");
                SetDetail(_ctx.ModAuthor, "");
                SetDetail(_ctx.ModDescription, "");
                SetDetail(_ctx.ModCreationDate, "");
                SetDetail(_ctx.ModLastUpdateDate, "");
                if (_details.activeSelf != _detailsWasActive) _details.SetActive(_detailsWasActive);
            }
        }
        _details = null;
    }

    // ---------------------------------------------------------------- pages

    private static ModInfo CurrentMod() => _modId == null ? null : ModManager.Find(_modId);

    private static void ShowMod(string modId)
    {
        _modId = ModManager.Find(modId)?.Id;
        _closeOnBack = false;
        _page?.ScrollToTop();
        _selectFirst = true;
        UpdateCanPop();
    }

    /// <summary>Esc / B: from a mod's page back to the list (or closed when the browser was opened on that page).</summary>
    private static void Back()
    {
        if (_modId == null) return;
        if (_closeOnBack)
        {
            GameModsScreen.Close();
            return;
        }
        string was = _modId;
        _modId = null;
        _page?.ScrollToTop();
        _selectFirst = true;
        _reselectMod = was;
        _canPopBackFrame = Time.frameCount + 1; // the back press that brought us here must not also close the browser
        UpdateCanPop();
    }

    private static string _reselectMod;
    private static int _mouseFrame = -100;

    /// <summary>The browser must not close on Esc / B while a mod's page is open (that goes back to the list).</summary>
    private static void UpdateCanPop()
    {
        if (_state == null) return;
        bool block = Active && (_modId != null && !_closeOnBack || KeysElsewhere) || Time.frameCount <= _canPopBackFrame;
        try
        {
            if (block && !_canPopTaken)
            {
                _state.CanPop = false;
                _canPopTaken = true;
            }
            else if (!block && _canPopTaken)
            {
                _state.CanPop = true;
                _canPopTaken = false;
            }
        }
        catch (Exception e) { Warn("canpop", "the MOD BROWSER's back key: " + e.Message); }
    }

    private static void OnInput(GameRow row, RowInput input)
    {
        if (row.Direct != null)
        {
            if (input.Kind == RowInputKind.Press) row.Direct();
            return;
        }
        Recorder.Queue(row.Index, input);
    }

    private static void OpenFullViewFor(string modId)
    {
        if (_state == null) { ModsPage.Open(modId); return; }
        ModsPage.OpenOver(_state, FindCanvasGroup(), null, modId);
    }

    private static CanvasGroup FindCanvasGroup()
    {
        try
        {
            var group = _state != null ? _state._UICanvasGroup : null;
            if (group != null) return group;
            return _ctx != null ? _ctx.GetComponentInParent<CanvasGroup>() ?? _ctx.GetComponentInChildren<CanvasGroup>() : null;
        }
        catch { return null; }
    }

    // ---------------------------------------------------------------- per frame

    /// <summary>Update: the browser's back key, Tab for Advanced, keeping the livery parts hidden.</summary>
    internal static void Tick()
    {
        if (_state == null) return;
        try
        {
            if (_state.WasCollected || _ctx == null || _ctx.WasCollected) { Detach(); return; }
            // A late language load may re-fill a copied label: ours again, now and then.
            if (Time.frameCount % 30 == 0) foreach (var b in Buttons) SetLabel(b);
            if (!Active)
            {
                // Left a tab of ours while typing / capturing / just after a back press: the browser's back key comes back.
                if (!ModsPage.IsClassicOpen) UpdateCanPop();
                return;
            }
            KeepHidden();
            _page?.Follow(_area);
            PullSelection();
            try { if (_state.selectedMod != null) _state.selectedMod = null; } catch { }
            if (ModsPage.IsClassicOpen) return; // the full view on top has the keys (and the browser's back key)
            UpdateCanPop();
            bool focus;
            try { focus = _state.IsInFocus; } catch { focus = true; }
            if (!focus) return;
            if (KeysElsewhere) return; // Esc ends the typing / the key capture, Tab is a character: not ours
            var kb = Keyboard.current;
            var pad = Gamepad.current;
            // Opened straight on a mod's page: the game's own back closes the browser (CanPop stays on), not us.
            if (!_closeOnBack && ((kb != null && kb.escapeKey.wasPressedThisFrame) || (pad != null && pad.buttonEast.wasPressedThisFrame))) Back();
            else if ((kb != null && kb.tabKey.wasPressedThisFrame) || (pad != null && pad.selectButton.wasPressedThisFrame))
            {
                var m = CurrentMod();
                if (m != null && ModPageView.HasAdvanced(m)) ModPageView.ToggleAdvanced();
            }
            EnablePressed();
            var mouse = Mouse.current;
            if (mouse != null && (mouse.delta.ReadValue().sqrMagnitude > 0.01f || mouse.scroll.ReadValue().sqrMagnitude > 0.01f)) _mouseFrame = Time.frameCount;
            _page?.KeepSelectedVisible(Time.frameCount - _mouseFrame < 30);
        }
        catch (Exception e) { Fail("tick", e); }
    }

    /// <summary>OnGUI (Layout, once a frame): runs the page's draw code as a recording pass and shows its rows.</summary>
    internal static void Record()
    {
        if (!Active || _page == null || _recordedFrame == Time.frameCount || ModsPage.IsClassicOpen) return;
        var ev = Event.current;
        if (ev == null || ev.type != EventType.Layout) return;
        _recordedFrame = Time.frameCount;
        try
        {
            RecordPass();
            BuildEntries();
            if (!_page.Show(Entries)) throw new InvalidOperationException("a game widget could not be made (" + GamePrefabs.Summary + ")");
            FixNavigation(false);
            if (_selectFirst)
            {
                _selectFirst = false;
                if (!ModsPage.IsClassicOpen && !SelectMod(_reselectMod)) _page.SelectFirst();
                _reselectMod = null;
            }
            UpdateDetails();
            if (_openNext != null)
            {
                string id = _openNext;
                _openNext = null;
                ShowMod(id);
            }
            if (_later != null)
            {
                var l = _later.Value;
                _later = null;
                Activate(l.tab, l.modId, l.closeOnBack);
            }
        }
        catch (Exception e)
        {
            Recorder.Abort();
            Fail("page", e);
        }
    }

    private static void RecordPass()
    {
        Menu.BeginWindow(false);
        Menu.BeginContent(640f);
        Recorder.Begin(Menu.Y);
        try
        {
            if (RequiredModsPrompt.Draw()) { }
            else
            {
                var mod = CurrentMod();
                if (_modId != null && mod == null) _modId = null;
                if (mod != null) ModPageView.Draw(mod);
                else if (_tab == 0)
                {
                    var open = ModsPage.DrawInstalledList();
                    if (open != null) _openNext = open.Id;
                }
                else ModsPage.DrawTab(_tab);
            }
        }
        finally
        {
            Recorder.End(Menu.Y);
            Menu.EndContent();
        }
    }

    private static void BuildEntries()
    {
        Entries.Clear();
        bool prompt = RequiredModsPrompt.Active;
        if (_modId != null && !prompt) Entries.Add(new GamePage.Entry(_closeOnBack ? CloseRow : BackRow, -1, Back));
        var rows = Recorder.Rows;
        for (int i = 0; i < rows.Count; i++) Entries.Add(new GamePage.Entry(rows[i], i, null));
        if (!Recorder.Untracked) return;
        // Every part of our pages has a game-widget form. Another creator's page that also draws by hand (raw GUI calls)
        // gets a row to the full view, where those parts show; anything else is only noted in the log.
        if (_modId != null && ThirdParty.IsApiMod(_modId)) Entries.Add(new GamePage.Entry(FullViewRow, -1, () => OpenFullViewFor(_modId)));
        else Warn("untracked " + (_modId ?? "tab " + _tab),
            $"the MODS page of {(_modId ?? ModsPage.TabNames[Math.Max(0, _tab)])} draws parts by hand (GUI calls); those parts don't show with the game's widgets");
    }

    private static readonly RowModel FullViewRow = new()
    {
        Kind = RowKind.Button, Label = "More settings", Value = "full view",
        Hint = "This mod draws some of its settings itself: the full view shows them. Esc there comes back here.",
    };

    private static int _typingFrame = -100;
    private static int _keysFrame = -100;

    /// <summary>True while (or just after) the keys belong to a text box or a hotkey capture (Keybinds): Esc / B
    /// then ends that, it doesn't go back or close the browser.</summary>
    private static bool KeysElsewhere
    {
        get
        {
            if (Typing || Features.Public.MenuUx.HotkeysCaptured || Features.Public.KeybindEditor.Capturing) _keysFrame = Time.frameCount;
            return Time.frameCount - _keysFrame <= 2;
        }
    }

    /// <summary>True while (or just after) the player types in a text box of the page: Esc / B, Tab and the mods'
    /// hotkeys leave the keys to the box.</summary>
    internal static bool Typing
    {
        get
        {
            if (!Active || _page == null) return false;
            if (_page.Typing) _typingFrame = Time.frameCount;
            return Time.frameCount - _typingFrame <= 2;
        }
    }

    /// <summary>Back on the list: the row of the mod that was open is selected again.</summary>
    private static bool SelectMod(string modId)
    {
        if (modId == null) return false;
        var m = ModManager.Find(modId);
        if (m == null) return false;
        string name = m.Name ?? m.Id;
        for (int i = 0; i < Entries.Count; i++)
        {
            var e = Entries[i].Model;
            if (e.Kind == RowKind.ListItem && e.Label == name) return _page.SelectAt(i);
        }
        return false;
    }

    // ---------------------------------------------------------------- the details panel

    /// <summary>True while a MODS list of ours is being recorded with cards (the list's draw code leaves out what
    /// would split the grid, e.g. a description line under each entry: it shows in the details panel).</summary>
    internal static bool ShowsCards => RowRecorder.Recording && Active && GamePrefabs.CardsReady;

    private static CardInfo _pageCard;

    private static void UpdateDetails()
    {
        if (_ctx == null) return;
        var selected = _page.SelectedRow(out _);
        var model = selected?.Model;
        var mod = CurrentMod();
        string name, author, desc, line1, line2;
        string status = Menu.TryStatus(out var s) ? s : "";
        CardInfo image = null;
        bool? on = null;
        bool canSwitch = false;
        if (mod != null)
        {
            bool api = ThirdParty.IsApiMod(mod.Id);
            name = mod.Name ?? mod.Id;
            author = api ? ModCards.OtherAuthor : ModCards.OurAuthor;
            desc = !string.IsNullOrEmpty(model?.Hint) ? model.Hint : mod.Description ?? "";
            line1 = (string.IsNullOrEmpty(mod.Version) ? "" : "v" + mod.Version + "  ·  ") + RequiredMods.Badge(mod);
            line2 = status;
            if (_pageCard == null || !string.Equals(_pageCard.ModId, mod.Id, StringComparison.OrdinalIgnoreCase))
                _pageCard = new CardInfo { ModId = mod.Id, Seed = mod.Id, Title = name };
            image = _pageCard;
            on = ModPageView.IsCore(mod) || ModManager.SwitchedOn(mod.Id);
        }
        else if (model != null && model.Kind == RowKind.ListItem && model.Card != null && selected.Kind == WidgetKind.Card)
        {
            var c = model.Card;
            name = model.Label;
            author = c.Author ?? "";
            desc = model.Hint ?? "";
            line1 = ModCards.DetailLine(c);
            line2 = status.Length > 0 ? status : c.Status ?? "";
            image = c;
            if (model.CanStep)
            {
                on = model.On;
                canSwitch = true;
            }
        }
        else if (model != null && model.Kind == RowKind.ListItem)
        {
            name = model.Label;
            author = RowMap.ListLabel(new RowModel { Kind = RowKind.ListItem, Badge = model.Badge, Detail = model.Detail }).Trim();
            desc = model.Hint ?? "";
            line1 = status;
            line2 = "";
            image = model.Card;
        }
        else
        {
            var tabs = ModsPage.TabNames;
            name = _tab >= 0 && _tab < tabs.Count ? (tabs[_tab] + " mods").ToUpperInvariant() : "MODS";
            int n = ModManager.All.Count;
            author = _tab == 0 ? $"{n} mod{(n == 1 ? "" : "s")} installed" : "";
            desc = model?.Hint ?? "";
            line1 = status;
            line2 = "";
        }
        SetDetail(_ctx.ModName, name);
        SetDetail(_ctx.ModAuthor, author);
        SetDetail(_ctx.ModDescription, desc);
        SetDetail(_ctx.ModCreationDate, line1);
        SetDetail(_ctx.ModLastUpdateDate, line2);
        try
        {
            ShowImage(image);
            ShowEnabled(on, canSwitch);
        }
        catch (Exception e) { Warn("details image", "the details panel's image / enabled panel: " + e.Message); }
    }

    // What the details panel's image, enabled panel and enable hint showed before we wrote to them (put back with the liveries).
    private static bool _detailsTaken;
    private static Sprite _imageSprite;
    private static bool _imageEnabled, _imageActive, _enabledSelActive, _disabledSelActive;
    private static string _enableLabel;
    private static string _shownImage;
    private static bool? _shownOn;
    private static bool _shownSwitch;

    private static void TakeDetails()
    {
        if (_detailsTaken) return;
        _detailsTaken = true;
        _shownImage = null;
        _shownOn = null;
        _shownSwitch = false;
        var img = _ctx.ModImage;
        if (img != null)
        {
            _imageSprite = img.sprite;
            _imageEnabled = img.enabled;
            _imageActive = img.gameObject.activeSelf;
        }
        _enabledSelActive = _ctx.EnabledSelected != null && _ctx.EnabledSelected.activeSelf;
        _disabledSelActive = _ctx.DisabledSelected != null && _ctx.DisabledSelected.activeSelf;
        _enableLabel = _ctx.EnableAction != null && _ctx.EnableAction.Label != null ? _ctx.EnableAction.Label.text : null;
    }

    /// <summary>The details panel's image: the selected card's (or the open mod's), hidden when there is none.</summary>
    private static void ShowImage(CardInfo card)
    {
        var img = _ctx.ModImage;
        if (img == null) return;
        TakeDetails();
        string key = card?.ImageKey;
        // Again now and then: a livery's image the game was still loading when the tab opened may land late.
        if (key == _shownImage && Time.frameCount % 60 != 0) return;
        if (key != _shownImage && card != null) OverTheHillPlus.CrashTrail.Mark("MODS cards: details of " + (card.Title ?? card.Seed));
        _shownImage = key;
        Sprite sprite = card != null ? ModImages.For(card) : null;
        if (sprite == null)
        {
            if (img.enabled) img.enabled = false;
            return;
        }
        img.sprite = sprite;
        img.color = Color.white;
        if (!img.enabled) img.enabled = true;
        if (!img.gameObject.activeSelf) img.gameObject.SetActive(true);
    }

    /// <summary>The enabled panel (on / off of the selected mod) and the enable hint (when the card can be switched).</summary>
    private static void ShowEnabled(bool? on, bool canSwitch)
    {
        TakeDetails();
        SetShown(_ctx.EnabledPanel, on.HasValue);
        SetShown(_ctx.EnableAction != null ? _ctx.EnableAction.gameObject : null, canSwitch && EnableInput() != null);
        if (on == _shownOn && canSwitch == _shownSwitch) return;
        _shownOn = on;
        _shownSwitch = canSwitch;
        if (on.HasValue)
        {
            bool v = on.Value;
            if (_ctx.EnabledSelected != null && _ctx.EnabledSelected.activeSelf != v) _ctx.EnabledSelected.SetActive(v);
            if (_ctx.DisabledSelected != null && _ctx.DisabledSelected.activeSelf == v) _ctx.DisabledSelected.SetActive(!v);
            try { if (_ctx.EnabledIcon != null) _state.ApplyStatusIconColor(_ctx.EnabledIcon, v); } catch { }
            try { if (_ctx.DisabledIcon != null) _state.ApplyStatusIconColor(_ctx.DisabledIcon, !v); } catch { }
        }
        var label = _ctx.EnableAction != null ? _ctx.EnableAction.Label : null;
        if (canSwitch && on.HasValue && label != null)
        {
            string word = on.Value ? "Disable" : "Enable";
            if (_enableLabel != null && _enableLabel.Length > 0 && _enableLabel == _enableLabel.ToUpperInvariant()) word = word.ToUpperInvariant();
            FunTexts.Register(label); // our word stays (a language reload would put the game's back)
            if (label.text != word) label.text = word;
        }
    }

    private static void SetShown(GameObject go, bool show)
    {
        if (go == null) return;
        foreach (var h in Hidden)
            if (h.Go != null && h.Go.Pointer == go.Pointer) { h.Show = show; return; }
    }

    /// <summary>Puts the details panel's image, enabled panel and enable hint back as the game had them.</summary>
    private static void RestoreDetails()
    {
        if (!_detailsTaken || _ctx == null) { _detailsTaken = false; return; }
        _detailsTaken = false;
        _shownImage = null;
        _shownOn = null;
        try
        {
            var img = _ctx.ModImage;
            if (img != null)
            {
                img.sprite = _imageSprite;
                img.enabled = _imageEnabled;
                if (img.gameObject.activeSelf != _imageActive) img.gameObject.SetActive(_imageActive);
            }
            if (_ctx.EnabledSelected != null && _ctx.EnabledSelected.activeSelf != _enabledSelActive) _ctx.EnabledSelected.SetActive(_enabledSelActive);
            if (_ctx.DisabledSelected != null && _ctx.DisabledSelected.activeSelf != _disabledSelActive) _ctx.DisabledSelected.SetActive(_disabledSelActive);
            var label = _ctx.EnableAction != null ? _ctx.EnableAction.Label : null;
            if (label != null)
            {
                try { FunTexts.Ours.Remove(label.Pointer); } catch { }
                if (_enableLabel != null && label.text != _enableLabel) label.text = _enableLabel;
            }
        }
        catch (Exception e) { Warn("restore details", "putting the details panel back: " + e.Message); }
        _imageSprite = null;
    }

    private static UnityEngine.InputSystem.InputAction _enableAction;
    private static bool _enableActionLooked;

    /// <summary>The browser's enable input (its EnableAction hint's binding), or null.</summary>
    private static UnityEngine.InputSystem.InputAction EnableInput()
    {
        if (_enableActionLooked) return _enableAction;
        _enableActionLooked = true;
        try { _enableAction = _ctx.EnableAction != null ? StateLiveryModsMenu.ResolveActionFor(_ctx.EnableAction) : null; }
        catch (Exception e) { Warn("enable input", "the MOD BROWSER's enable input was not found (the mods switch on their pages and with the arrows): " + e.Message); }
        return _enableAction;
    }

    /// <summary>The browser's enable input on a selected card that can be switched: a step, as the row's arrows gave.</summary>
    private static void EnablePressed()
    {
        if (_modId != null || _page == null) return;
        var act = EnableInput();
        if (act == null || !act.WasPressedThisFrame()) return;
        var row = _page.SelectedRow(out _);
        if (row == null || row.Kind != WidgetKind.Card || row.Model == null || !row.Model.CanStep) return;
        if (row.Direct != null) return;
        Recorder.Queue(row.Index, RowInput.Stepped(row.Model.On ? -1 : 1));
    }

    private static void SetDetail(TMP_Text t, string text)
    {
        if (t == null) return;
        text ??= "";
        if (t.text != text) t.text = text;
    }

    // ---------------------------------------------------------------- helpers

    private static void Select(Selectable s)
    {
        var es = EventSystem.current;
        if (es != null && s != null) es.SetSelectedGameObject(s.gameObject);
    }

    private static void Guard(string where, Action action)
    {
        try { action(); }
        catch (Exception e) { Fail(where, e); }
    }

    /// <summary>Something of ours broke inside the browser: logged once, the liveries come back and the full view opens.</summary>
    internal static void Fail(string where, Exception e)
    {
        Warn("fail " + where, $"MODS in the MOD BROWSER failed ({where}): {e}; showing the full view instead");
        string mod = _modId;
        _broken = true;
        try { Deactivate(false); } catch { }
        try { OpenFullViewFor(mod); } catch { }
    }

    private static void Warn(string key, string message)
    {
        if (Logged.Add(key)) Core.CorePlugin.Logger?.LogWarning("[Core] " + message);
    }

    /// <summary>Logs once where things were found (helps when a game update moves them).</summary>
    private static void LogLayoutOnce()
    {
        if (!Logged.Add("layout")) return;
        try
        {
            var area = FindGridArea();
            string filters = Buttons.Count > 0 && Buttons[0].Go.transform.parent != null ? Path(Buttons[0].Go.transform.parent) : "?";
            bool layout = Buttons.Count > 0 && Buttons[0].Go.transform.parent != null && Buttons[0].Go.transform.parent.GetComponent<LayoutGroup>() != null;
            Core.CorePlugin.Logger?.LogInfo($"[Core] MOD BROWSER: MODS buttons in {filters} ({(layout ? "layout group" : "placed by hand")}), " +
                                            $"page over {(area != null ? Path(area) + " " + area.rect.size : "?")}, details panel {(_ctx.DescriptionPanel != null ? "found" : "missing")}");
        }
        catch { }
    }

    private static string Path(Transform t)
    {
        string p = t.name;
        for (int i = 0; i < 4 && t.parent != null; i++)
        {
            t = t.parent;
            p = t.name + "/" + p;
        }
        return p;
    }
}
