using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace OverTheHillPlus.UI.GameMenu;

/// <summary>
/// A scrolling column of <see cref="GameRow"/>s laid out like the SETTINGS screens (the game's rows stacked top to
/// bottom), placed over the area of the MOD BROWSER that shows the livery grid. <see cref="Show"/> brings the rows
/// in line with a page's models: rows of the same widget kind are reused and only rebound, so the selection, the
/// game's hover / select animation and the scroll position stay while values change. Up / down go from row to row,
/// up from the first row (and left from a button row) to <see cref="Above"/> (the MODS button in the browser's
/// filter column). Rows that are cards (<see cref="WidgetKind.Card"/>: the MODS lists' mods as copies of the
/// browser's mod cell) are laid out in a grid like the browser's, one grid per run of cards; arrows go along and
/// across it (<see cref="CardGrid.Nav"/>), left from the first column goes to <see cref="Above"/>.
/// </summary>
internal sealed class GamePage
{
    internal readonly struct Entry
    {
        public readonly RowModel Model;
        public readonly int Index;
        public readonly Action Direct;

        public Entry(RowModel model, int index, Action direct)
        {
            Model = model;
            Index = index;
            Direct = direct;
        }
    }

    public readonly GameObject Root;
    private readonly RectTransform _viewport, _content;
    private readonly ScrollRect _scroll;
    private readonly List<GameRow> _rows = new();
    private readonly List<WidgetKind> _kinds = new();
    private readonly List<GridLayoutGroup> _grids = new();

    private Selectable _above;
    private IntPtr _lastSelected;

    /// <summary>Where up from the first row goes (the tab button).</summary>
    public Selectable Above
    {
        get => _above;
        set
        {
            if (ReferenceEquals(_above, value) || (_above != null && value != null && _above.Pointer == value.Pointer)) return;
            _above = value;
            LinkNavigation();
        }
    }

    /// <summary>Called with what the player did to a row.</summary>
    public Action<GameRow, RowInput> OnInput;

    private GamePage(GameObject root, RectTransform viewport, RectTransform content, ScrollRect scroll)
    {
        Root = root;
        _viewport = viewport;
        _content = content;
        _scroll = scroll;
    }

    /// <summary>A page over <paramref name="area"/>: same parent, same rect, drawn just above it.</summary>
    internal static GamePage Create(RectTransform area, string name)
    {
        var parent = area.parent;
        var root = new GameObject(name);
        var rt = root.AddComponent<RectTransform>();
        rt.SetParent(parent, false);
        rt.SetSiblingIndex(area.GetSiblingIndex() + 1);
        rt.anchorMin = area.anchorMin;
        rt.anchorMax = area.anchorMax;
        rt.pivot = area.pivot;
        rt.anchoredPosition = area.anchoredPosition;
        rt.sizeDelta = area.sizeDelta;
        rt.localScale = area.localScale;
        rt.localRotation = area.localRotation;
        var le = root.AddComponent<LayoutElement>();
        le.ignoreLayout = true; // never moves the browser's own layout

        var vp = new GameObject("Viewport");
        var vrt = vp.AddComponent<RectTransform>();
        vrt.SetParent(rt, false);
        vrt.anchorMin = Vector2.zero;
        vrt.anchorMax = Vector2.one;
        vrt.offsetMin = Vector2.zero;
        vrt.offsetMax = Vector2.zero;
        vp.AddComponent<RectMask2D>();
        var hit = vp.AddComponent<Image>(); // catches the mouse wheel between rows
        hit.color = new Color(0f, 0f, 0f, 0f);

        var content = new GameObject("Content");
        var crt = content.AddComponent<RectTransform>();
        crt.SetParent(vrt, false);
        crt.anchorMin = new Vector2(0f, 1f);
        crt.anchorMax = new Vector2(1f, 1f);
        crt.pivot = new Vector2(0.5f, 1f);
        crt.anchoredPosition = Vector2.zero;
        crt.sizeDelta = Vector2.zero;
        var vlg = content.AddComponent<VerticalLayoutGroup>();
        vlg.childControlWidth = true;
        vlg.childControlHeight = true;
        vlg.childForceExpandWidth = true;
        vlg.childForceExpandHeight = false;
        vlg.spacing = 6f;
        vlg.padding = new RectOffset(8, 24, 8, 24);
        var fit = content.AddComponent<ContentSizeFitter>();
        fit.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
        fit.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;

        var scroll = root.AddComponent<ScrollRect>();
        scroll.viewport = vrt;
        scroll.content = crt;
        scroll.horizontal = false;
        scroll.vertical = true;
        scroll.movementType = ScrollRect.MovementType.Clamped;
        scroll.scrollSensitivity = 40f;
        scroll.inertia = false;
        return new GamePage(root, vrt, crt, scroll);
    }

    public bool Alive
    {
        get
        {
            try { return Root != null && _content != null; }
            catch { return false; }
        }
    }

    /// <summary>Stays over <paramref name="area"/> when the browser's layout moves it.</summary>
    public void Follow(RectTransform area)
    {
        if (area == null || Root == null) return;
        var rt = Root.transform.TryCast<RectTransform>();
        if (rt == null) return;
        if (rt.anchorMin != area.anchorMin) rt.anchorMin = area.anchorMin;
        if (rt.anchorMax != area.anchorMax) rt.anchorMax = area.anchorMax;
        if (rt.pivot != area.pivot) rt.pivot = area.pivot;
        if (rt.anchoredPosition != area.anchoredPosition) rt.anchoredPosition = area.anchoredPosition;
        if (rt.sizeDelta != area.sizeDelta) rt.sizeDelta = area.sizeDelta;
    }

    public void SetVisible(bool on)
    {
        if (Root != null && Root.activeSelf != on) Root.SetActive(on);
    }

    /// <summary>Scrolls to the top (a new page).</summary>
    public void ScrollToTop()
    {
        if (_content != null) _content.anchoredPosition = new Vector2(_content.anchoredPosition.x, 0f);
    }

    // ---------------------------------------------------------------- rows

    /// <summary>Makes the rows match <paramref name="entries"/>. False when a widget could not be made (the host falls back).</summary>
    public bool Show(IReadOnlyList<Entry> entries)
    {
        var selected = SelectedRow(out int selectedAt);
        bool changed = false;
        for (int i = 0; i < entries.Count; i++)
        {
            var e = entries[i];
            var kind = KindFor(e.Model);
            GameRow row = i < _rows.Count ? _rows[i] : null;
            if (row == null || row.Kind != kind)
            {
                row?.Destroy();
                row = GameRow.Create(kind, _content);
                if (row == null && kind == WidgetKind.Card)
                {
                    // The browser's cell couldn't be made into a card: rows from now on (never the full view for this).
                    GamePrefabs.DropModCell("a card could not be made from the MOD BROWSER's mod cell");
                    kind = KindFor(e.Model);
                    row = GameRow.Create(kind, _content);
                }
                if (row == null) return false;
                row.Input = Send;
                if (i < _rows.Count) _rows[i] = row;
                else _rows.Add(row);
                row.Go.transform.SetSiblingIndex(i);
                changed = true;
            }
            row.Index = e.Index;
            row.Direct = e.Direct;
            row.Bind(e.Model);
        }
        for (int i = _rows.Count - 1; i >= entries.Count; i--)
        {
            _rows[i].Destroy();
            _rows.RemoveAt(i);
            changed = true;
        }
        if (changed)
        {
            _kinds.Clear();
            foreach (var r in _rows) _kinds.Add(r.Kind);
            Arrange();
            LinkNavigation();
            // The row that was selected went: the nearest selectable one takes over (keyboard / gamepad keep going).
            if (selectedAt >= 0 && (selected == null || selectedAt >= _rows.Count || !ReferenceEquals(_rows[selectedAt], selected)))
                Select(NavOrder.Nearest(_kinds, selectedAt));
        }
        SizeCells();
        return true;
    }

    /// <summary>The widget a model shows with (a template missing after a game update makes the page fall back).</summary>
    internal static WidgetKind KindFor(RowModel m) => RowMap.WidgetFor(m, GamePrefabs.CardsReady);

    private void Send(GameRow row, RowInput input) => OnInput?.Invoke(row, input);

    /// <summary>Puts the rows in order in the column, each run of cards in a grid of its own (made / dropped as needed).</summary>
    private void Arrange()
    {
        int pos = 0, run = 0, inGrid = 0;
        GridLayoutGroup grid = null;
        for (int i = 0; i < _rows.Count; i++)
        {
            var t = _rows[i].Go.transform;
            if (_rows[i].Kind == WidgetKind.Card)
            {
                if (i == 0 || _rows[i - 1].Kind != WidgetKind.Card)
                {
                    grid = GridAt(run++);
                    grid.transform.SetSiblingIndex(pos++);
                    inGrid = 0;
                }
                if (t.parent == null || t.parent.Pointer != grid.transform.Pointer) t.SetParent(grid.transform, false);
                t.SetSiblingIndex(inGrid++);
            }
            else
            {
                if (t.parent == null || t.parent.Pointer != _content.Pointer) t.SetParent(_content, false);
                t.SetSiblingIndex(pos++);
            }
        }
        for (int i = _grids.Count - 1; i >= run; i--)
        {
            try
            {
                _grids[i].gameObject.SetActive(false);
                Object.Destroy(_grids[i].gameObject);
            }
            catch { }
            _grids.RemoveAt(i);
        }
        _cellW = -1f; // new grids: sized on the next pass
    }

    private GridLayoutGroup GridAt(int index)
    {
        if (index < _grids.Count) return _grids[index];
        var go = new GameObject("OTH_Cards");
        var rt = go.AddComponent<RectTransform>();
        rt.SetParent(_content, false);
        var grid = go.AddComponent<GridLayoutGroup>();
        grid.startCorner = GridLayoutGroup.Corner.UpperLeft;
        grid.startAxis = GridLayoutGroup.Axis.Horizontal;
        grid.childAlignment = TextAnchor.UpperLeft;
        grid.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
        grid.constraintCount = GamePrefabs.GridColumns;
        var sp = GamePrefabs.CellSpacing;
        grid.spacing = sp.x > 0f || sp.y > 0f ? sp : new Vector2(16f, 16f);
        grid.padding = new RectOffset(0, 0, 4, 4);
        _grids.Add(grid);
        return grid;
    }

    private float _cellW = -1f;

    /// <summary>The cards' size: the browser's cell when its columns fit the page's width, else shrunk to fit.</summary>
    private void SizeCells()
    {
        if (_grids.Count == 0 || _content == null) return;
        var vlg = _content.GetComponent<VerticalLayoutGroup>();
        float width = _content.rect.width - (vlg != null ? vlg.padding.left + vlg.padding.right : 0);
        if (width < 1f) return;
        var grid0 = _grids[0];
        var size = CardGrid.CellSize(width, GamePrefabs.GridColumns, grid0.spacing.x, 0f, GamePrefabs.CellSize.x, GamePrefabs.CellSize.y);
        if (Mathf.Abs(size.w - _cellW) < 0.5f) return;
        _cellW = size.w;
        foreach (var g in _grids) g.cellSize = new Vector2(size.w, size.h);
    }

    private void LinkNavigation()
    {
        var nav = CardGrid.Nav(_kinds, GamePrefabs.GridColumns);
        for (int i = 0; i < _rows.Count; i++)
        {
            var s = _rows[i].Selectable;
            if (s == null || !RowMap.Selectable(_rows[i].Kind)) continue;
            var n = s.navigation;
            n.mode = Navigation.Mode.Explicit;
            n.selectOnUp = nav[i].up >= 0 ? _rows[nav[i].up].Selectable : Above;
            n.selectOnDown = nav[i].down >= 0 ? _rows[nav[i].down].Selectable : null;
            if (_rows[i].Kind == WidgetKind.Card)
            {
                // Along the cards' row; left from the first column back to the tab button (as the browser's grid does).
                n.selectOnLeft = nav[i].left >= 0 ? _rows[nav[i].left].Selectable : Above;
                n.selectOnRight = nav[i].right >= 0 ? _rows[nav[i].right].Selectable : null;
            }
            else
            {
                // Button rows: left and right go back to the tab button (option rows and sliders use them for their value).
                n.selectOnLeft = _rows[i].Kind == WidgetKind.Button ? Above : null;
                n.selectOnRight = _rows[i].Kind == WidgetKind.Button ? Above : null;
            }
            s.navigation = n;
        }
    }

    /// <summary>True while a text box of the page has the keyboard.</summary>
    public bool Typing
    {
        get
        {
            foreach (var r in _rows)
                if (r.Kind == WidgetKind.TextField && r.Editing) return true;
            return false;
        }
    }

    /// <summary>True when <paramref name="s"/> is one of this page's rows.</summary>
    public bool Owns(Selectable s)
    {
        if (s == null) return false;
        var ptr = s.Pointer;
        foreach (var r in _rows)
            if (r.Selectable != null && r.Selectable.Pointer == ptr) return true;
        return false;
    }

    /// <summary>The first selectable row, for the tab button's "right" and for opening a page.</summary>
    public Selectable First()
    {
        int i = NavOrder.First(_kinds);
        return i >= 0 ? _rows[i].Selectable : null;
    }

    public void SelectFirst() => Select(NavOrder.First(_kinds));

    /// <summary>Selects row <paramref name="index"/> when it is selectable.</summary>
    public bool SelectAt(int index) => index >= 0 && index < _kinds.Count && RowMap.Selectable(_kinds[index]) && Select(index);

    private bool Select(int index)
    {
        if (index < 0 || index >= _rows.Count) return false;
        var es = EventSystem.current;
        var go = _rows[index].Selectable != null ? _rows[index].Selectable.gameObject : null;
        if (es == null || go == null) return false;
        es.SetSelectedGameObject(go);
        return true;
    }

    /// <summary>The row the EventSystem has selected, if it is one of this page's.</summary>
    public GameRow SelectedRow(out int index)
    {
        index = -1;
        var es = EventSystem.current;
        var go = es != null ? es.currentSelectedGameObject : null;
        if (go == null) return null;
        var ptr = go.Pointer;
        for (int i = 0; i < _rows.Count; i++)
        {
            var s = _rows[i].Selectable;
            if (s != null && s.gameObject.Pointer == ptr)
            {
                index = i;
                return _rows[i];
            }
        }
        return null;
    }

    /// <summary>
    /// Keeps the selected row inside the visible part when the selection moved to another row (keyboard / gamepad
    /// going past the edge). Not while <paramref name="mouse"/>: hovering selects rows, and following that would
    /// scroll the page under the pointer.
    /// </summary>
    public void KeepSelectedVisible(bool mouse)
    {
        var row = SelectedRow(out _);
        var ptr = row != null && row.Selectable != null ? row.Selectable.Pointer : IntPtr.Zero;
        bool moved = ptr != _lastSelected;
        _lastSelected = ptr;
        if (!moved || mouse) return;
        if (row == null || _viewport == null || _content == null || _scroll == null) return;
        var rt = row.Go.transform.TryCast<RectTransform>();
        if (rt == null) return;
        var corners = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<Vector3>(4);
        rt.GetWorldCorners(corners);
        float rowTop = _viewport.InverseTransformPoint(corners[1]).y, rowBottom = _viewport.InverseTransformPoint(corners[0]).y;
        var rect = _viewport.rect;
        float delta = 0f;
        if (rowTop > rect.yMax) delta = rowTop - rect.yMax + 8f;
        else if (rowBottom < rect.yMin) delta = rowBottom - rect.yMin - 8f;
        if (Mathf.Abs(delta) < 0.5f) return;
        var p = _content.anchoredPosition;
        float max = Mathf.Max(0f, _content.rect.height - rect.height);
        _content.anchoredPosition = new Vector2(p.x, Mathf.Clamp(p.y - delta, 0f, max));
    }

    public void Destroy()
    {
        foreach (var r in _rows) r.Destroy();
        _rows.Clear();
        _kinds.Clear();
        _grids.Clear();
        try { if (Root != null) Object.Destroy(Root); } catch { }
    }
}
