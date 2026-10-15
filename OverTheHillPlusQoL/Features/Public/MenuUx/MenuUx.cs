using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using BepInEx.Configuration;
using OverTheHillPlus.UI;
using OverTheHillPlus.UI.Native;
using UnityEngine;
using UnityEngine.InputSystem;

namespace OverTheHillPlus.Features.Public;

/// <summary>
/// Menu quality of life shared by every tab: UI scale, settings search (tab highlights + row highlights),
/// collapsible cards (remembered for the session), hover tooltips (GUI.tooltip), keyboard focus handling and
/// hotkey suppression while typing or rebinding. <see cref="Menu"/> calls the small hooks below; all state lives here.
/// </summary>
internal static class MenuUx
{
    // ---------- settings (bound by MenuUxFeature; null-safe if it failed to register) ----------

    internal static ConfigEntry<float> UiScale;
    internal static ConfigEntry<bool> Tooltips;
    internal static ConfigEntry<bool> SearchBar;
    internal static ConfigEntry<bool> FoldCards;

    private static bool TooltipsOn => (Tooltips == null || Tooltips.Value) && !_tooltipBroken;
    private static bool SearchOn => SearchBar == null || SearchBar.Value;

    /// <summary>Advanced view: cards start folded, except the first card of each tab.</summary>
    internal static bool FoldByDefault => FoldCards == null || FoldCards.Value;

    // ---------- UI scale ----------

    private static float _appliedScale = -1f;

    /// <summary>Menu scale: the resolution-based scale times the user's multiplier. The multiplier is only
    /// picked up while no mouse button is held, so dragging the scale slider doesn't fight its own matrix.</summary>
    internal static float MenuScale(float baseScale)
    {
        float wanted = UiScale == null ? 1f : Mathf.Clamp(UiScale.Value, 0.5f, 2.5f);
        if (_appliedScale < 0f || !MouseHeld()) _appliedScale = wanted;
        // Never wider than the screen (the menu is 600 units wide).
        float max = Mathf.Max(0.4f, Screen.width / 640f);
        return Mathf.Clamp(baseScale * _appliedScale, 0.4f, Mathf.Min(5f, max));
    }

    private static bool MouseHeld()
    {
        try
        {
            var m = Mouse.current;
            return m != null && (m.leftButton.isPressed || m.rightButton.isPressed);
        }
        catch { return false; }
    }

    // ---------- hotkeys / typing ----------

    private static int _suppressFrame = -10;
    private static int _typingFrame = -10;
    private static int _lastDrawFrame = -10;
    private static bool _unfocusRequested;

    /// <summary>Blocks every mod hotkey (menu key, Escape, HUD...) for the current frame.</summary>
    internal static void SuppressHotkeysThisFrame() => _suppressFrame = Time.frameCount;

    /// <summary>
    /// True when hotkeys must be ignored this frame: a key is being captured for a rebind, or the chat box is open (or
    /// closed this frame or the last, so the Enter / Escape that closed it isn't read as a hotkey). The chat check
    /// doesn't depend on the order features tick in.
    /// </summary>
    internal static bool HotkeysCaptured
    {
        get
        {
            int f = Time.frameCount;
            if (f <= _suppressFrame) return true;
            try { return Chat.ChatOverlay.IsOpen || f - Chat.ChatOverlay.ClosedFrame <= 1; }
            catch { return false; }
        }
    }

    /// <summary>True while the menu is open and a text field in it has keyboard focus.</summary>
    internal static bool Typing => Time.frameCount - _typingFrame <= 2 && MenuVisible;

    /// <summary>True while the menu is being drawn (open).</summary>
    internal static bool MenuVisible => Time.frameCount - _lastDrawFrame <= 2;

    /// <summary>Keys that type a character into a text field (letters, digits, punctuation, space, numpad).</summary>
    internal static bool IsTextKey(Key k) =>
        (k >= Key.Space && k <= Key.Digit0) || (k >= Key.NumpadDivide && k <= Key.Numpad9);

    /// <summary>Per frame (before the hotkeys are read): Escape while typing only leaves the text field.</summary>
    internal static void Tick()
    {
        if (!Typing) return;
        var kb = Keyboard.current;
        if (kb == null) return;
        if (kb.escapeKey.wasPressedThisFrame)
        {
            _unfocusRequested = true;
            SuppressHotkeysThisFrame();
        }
    }

    // ---------- frame hooks called by Menu.Draw ----------

    private static bool _mouseDownEvent;
    private static int _kbBefore;
    private static Rect _contentArea;

    internal static void BeginFrame()
    {
        try { BeginFrameCore(); }
        catch (Exception ex) { LogOnce("begin", ex); }
    }

    private static void BeginFrameCore()
    {
        var e = Event.current;
        // Menu was closed: forget any stale text focus so hotkeys work straight away.
        if (Time.frameCount - _lastDrawFrame > 2 || _unfocusRequested)
        {
            _unfocusRequested = false;
            GUIUtility.keyboardControl = 0;
        }
        _lastDrawFrame = Time.frameCount;
        _mouseDownEvent = e.type == EventType.MouseDown;
        _kbBefore = GUIUtility.keyboardControl;

        // Ctrl+F focuses the search box.
        if (SearchOn && e.type == EventType.KeyDown && e.control && e.keyCode == KeyCode.F)
        {
            _focusSearch = true;
            e.Use();
        }
    }

    internal static void EndFrame(Rect contentArea)
    {
        try { EndFrameCore(contentArea); }
        catch (Exception ex) { LogOnce("end", ex); }
    }

    private static void EndFrameCore(Rect contentArea)
    {
        _contentArea = contentArea;
        // A click that went somewhere other than the focused text field takes the focus away
        // (IMGUI keeps a text field focused forever otherwise, which would also swallow hotkeys).
        if (_mouseDownEvent && _kbBefore != 0 && GUIUtility.keyboardControl == _kbBefore && GUIUtility.hotControl != _kbBefore)
            GUIUtility.keyboardControl = 0;
        if (GUIUtility.keyboardControl != 0) _typingFrame = Time.frameCount;
        DrawTooltip();
    }

    // ---------- tooltips ----------

    private static readonly Dictionary<ConfigEntryBase, GUIContent> TipCache = new();
    private static readonly Dictionary<string, GUIContent> TextTipCache = new();
    private static string _tipText = "";
    private static float _tipSince;
    private static bool _tooltipBroken;
    private const float TipDelay = 0.35f;

    /// <summary>GUIContent for an invisible hit area that shows the entry's config description as a tooltip
    /// (only when it says more than the visible hint).</summary>
    internal static GUIContent TipContent(ConfigEntryBase entry, string visibleHint)
    {
        if (entry == null || !TooltipsOn) return GUIContent.none;
        if (!TipCache.TryGetValue(entry, out var c) || c == null)
        {
            string d = entry.Description?.Description;
            if (string.IsNullOrWhiteSpace(d) || (visibleHint != null && Same(d, visibleHint))) c = GUIContent.none;
            else c = new GUIContent("", d.Trim());
            TipCache[entry] = c;
        }
        return c;
    }

    /// <summary>Tooltip text for an entry's description (null when there is nothing extra to say).</summary>
    internal static string TipFor(ConfigEntryBase entry, string visibleHint)
    {
        var c = TipContent(entry, visibleHint);
        return c == null || ReferenceEquals(c, GUIContent.none) ? null : c.tooltip;
    }

    /// <summary>Makes a rect show a tooltip on hover without taking its clicks (GUI.Box hit area).</summary>
    internal static void Tooltip(Rect r, string tip)
    {
        // Always issue the Box (even with no tip) so control ids stay identical across IMGUI events.
        if (string.IsNullOrEmpty(tip) || !TooltipsOn) { GUI.Box(r, GUIContent.none, Theme.Invisible); return; }
        if (!TextTipCache.TryGetValue(tip, out var c) || c == null)
        {
            if (TextTipCache.Count > 512) TextTipCache.Clear();
            c = new GUIContent("", tip);
            TextTipCache[tip] = c;
        }
        GUI.Box(r, c, Theme.Invisible);
    }

    private static bool Same(string a, string b)
    {
        string x = a.Trim().TrimEnd('.').ToLowerInvariant(), y = b.Trim().TrimEnd('.').ToLowerInvariant();
        return x == y;
    }

    private static void DrawTooltip()
    {
        if (!TooltipsOn || Event.current.type != EventType.Repaint) return;
        string tip;
        try
        {
            tip = GUI.tooltip ?? "";
            GUI.tooltip = "";
        }
        catch (Exception e)
        {
            _tooltipBroken = true;
            Plugin.Logger.LogWarning("Menu tooltips disabled (GUI.tooltip unavailable): " + e.Message);
            return;
        }
        var mouse = Event.current.mousePosition;
        if (tip.Length == 0 || !_contentArea.Contains(mouse) || MouseHeld()) { _tipText = ""; return; }
        if (tip != _tipText) { _tipText = tip; _tipSince = Time.unscaledTime; }
        if (Time.unscaledTime - _tipSince < TipDelay) return;

        const float maxW = 300f;
        var content = new GUIContent(tip);
        var style = Theme.Muted_;
        float w = Mathf.Min(maxW, style.CalcSize(content).x + 2f);
        float h = style.CalcHeight(content, w);
        var r = new Rect(mouse.x + 14f, mouse.y + 18f, w + 20f, h + 14f);
        float scale = GUI.matrix.m00 > 0.01f ? GUI.matrix.m00 : 1f;
        float sw = Screen.width / scale, sh = Screen.height / scale;
        if (r.xMax > sw - 4f) r.x = Mathf.Max(4f, mouse.x - r.width - 6f);
        if (r.yMax > sh - 4f) r.y = Mathf.Max(4f, mouse.y - r.height - 6f);

        Theme.Fill(new Rect(r.x - 1f, r.y - 1f, r.width + 2f, r.height + 2f), Theme.Line, 7);
        Theme.Fill(r, Theme.Hex(0x0E1218, 0.98f), 6);
        Menu.Label(new Rect(r.x + 10f, r.y + 7f, w, h), tip, style, Menu.NativeActive ? NativeLook.Text : Theme.Text);
    }

    // ---------- collapsible cards ----------

    // Cards the user opened or folded (true = open); others follow their default. Kept for the session.
    private static readonly Dictionary<string, bool> OpenState = new();
    // Every card drawn so far, with whether it starts open.
    private static readonly Dictionary<string, bool> SeenCards = new();

    internal static string CardKey(int mode, string tab, string title) => $"{mode}/{tab}/{title}";

    private static bool IsOpen(string key, bool defaultOpen) => OpenState.TryGetValue(key, out var open) ? open : defaultOpen;

    internal static int CollapsedCount
    {
        get
        {
            int n = 0;
            foreach (var kv in SeenCards) if (!IsOpen(kv.Key, kv.Value)) n++;
            return n;
        }
    }

    internal static void CollapseAll(bool collapse)
    {
        foreach (var k in SeenCards.Keys)
        {
            if (collapse && k.Contains("/" + MenuUxFeature.Tab + "/")) continue; // keep the menu settings' own cards open
            OpenState[k] = !collapse;
        }
    }

    /// <summary>Draws a card's title row (title, chevron, search highlight), handles the collapse click and
    /// returns whether the body should be drawn. <paramref name="defaultOpen"/>: open until the user folds it.</summary>
    internal static bool CardHeader(string key, string title, Rect card, float inset, float iw, bool defaultOpen = true)
    {
        if (Indexing)
        {
            _indexCard = key;
            Record(title, null, null);
            return true;
        }
        SeenCards[key] = defaultOpen;
        bool hit = Searching && CardHits.Contains(key);
        bool collapsed = !IsOpen(key, defaultOpen) && !hit;
        var head = new Rect(card.x, card.y, card.width, 32f);
        bool hover = head.Contains(Event.current.mousePosition);
        bool native = Menu.NativeActive;
        Menu.NavItem(head, NavKind.Fold, out bool focused, out bool activated);

        if (focused) Menu.SelectionBar(new Rect(card.x + 4f, card.y + 4f, card.width - 8f, 28f));
        if (hit) Theme.Fill(new Rect(card.x, card.y + 6f, 3f, Mathf.Max(20f, card.height - 12f)), Theme.Accent, 1);
        Color titleColor = native
            ? focused ? NativeLook.HighlightText : hit ? NativeLook.Highlight : NativeLook.Text
            : hit ? Theme.Accent : hover ? Theme.Text : Theme.Section.normal.textColor;
        Menu.Label(new Rect(inset, card.y + 12f, iw - 24f, 16f), title.ToUpperInvariant(), Theme.Section, titleColor);
        if (hover || collapsed || focused)
        {
            Color chevron = native ? focused ? NativeLook.HighlightText : NativeLook.Muted : Theme.Small.normal.textColor;
            Menu.Label(new Rect(inset + iw - 40f, card.y + 10f, 40f, 20f), collapsed ? "show ►" : "▼", Theme.Small, TextAnchor.MiddleRight, chevron);
        }
        if (GUI.Button(head, GUIContent.none, Theme.Invisible) || activated)
        {
            if (collapsed) OpenState[key] = true;
            else if (!hit) OpenState[key] = false;
            if (native) NativeLook.PlayPress();
        }
        return !collapsed;
    }

    /// <summary>Colour tint for a card body: cards without a match fade while a search is active.</summary>
    internal static Color CardTint(string key, Color current)
    {
        if (!Searching || Indexing || CardHits.Count == 0 || CardHits.Contains(key)) return current;
        return new Color(current.r, current.g, current.b, current.a * 0.55f);
    }

    // ---------- search ----------

    private sealed class Item
    {
        public int Mode;
        public string Tab;
        public string Card;
        public string Text; // lower-case, tags stripped
    }

    private static readonly List<Item> Index = new();
    private static readonly Dictionary<string, int> TabHits = new();   // "mode/tab" -> matches
    private static readonly HashSet<string> CardHits = new();
    private static readonly Regex Tags = new("<[^>]*>", RegexOptions.Compiled);
    private static string _query = "";
    private static string[] _tokens = Array.Empty<string>();
    private static bool _hitsValid;
    private static float _indexedAt = -100f;
    private static bool _focusSearch;
    private const string SearchControl = "otp.menu.search";

    internal static bool Indexing { get; private set; }
    private static int _indexMode;
    private static string _indexTab, _indexCard;

    internal static bool Searching => SearchOn && SearchShown && _tokens.Length > 0;

    /// <summary>Set by the menu each frame: the search box is on screen (Advanced view). A query typed earlier
    /// neither highlights nor dims anything while the box is hidden.</summary>
    internal static bool SearchShown = true;

    /// <summary>True when the menu should run an indexing pass this Layout event.</summary>
    internal static bool NeedsIndex => Searching && Time.unscaledTime - _indexedAt > 4f;

    /// <summary>Walks every tab of every mode once (in a Layout event, so nothing is drawn or clicked) and
    /// records the labels the widgets report through <see cref="Note"/>.</summary>
    internal static void BuildIndex(IList<string>[] tabsByMode, Action<int, string> drawTab)
    {
        _indexedAt = Time.unscaledTime;
        Index.Clear();
        Indexing = true;
        try
        {
            for (int m = 0; m < tabsByMode.Length; m++)
            {
                var tabs = tabsByMode[m];
                for (int i = 0; i < tabs.Count; i++)
                {
                    _indexMode = m;
                    _indexTab = tabs[i];
                    _indexCard = null;
                    try { drawTab(m, tabs[i]); }
                    catch (Exception e) { LogOnce("index " + tabs[i], e); }
                }
            }
        }
        finally
        {
            Indexing = false;
            _hitsValid = false;
        }
    }

    private static void Record(string a, string b, string c)
    {
        string text = Join(a, b, c);
        if (text.Length == 0) return;
        Index.Add(new Item { Mode = _indexMode, Tab = _indexTab, Card = _indexCard, Text = text });
    }

    private static string Join(string a, string b, string c)
    {
        string s = a ?? "";
        if (!string.IsNullOrEmpty(b)) s += " " + b;
        if (!string.IsNullOrEmpty(c)) s += " " + c;
        if (s.IndexOf('<') >= 0) s = Tags.Replace(s, "");
        return s.ToLowerInvariant();
    }

    private static bool Matches(string lowerText)
    {
        if (_tokens.Length == 0) return false;
        foreach (var t in _tokens)
            if (lowerText.IndexOf(t, StringComparison.Ordinal) < 0) return false;
        return true;
    }

    /// <summary>Widgets report their text here. While indexing it is recorded; otherwise returns true when it
    /// matches the active search (so the widget can highlight itself).</summary>
    internal static bool Note(string label, string hint = null, string extra = null)
    {
        if (Indexing) { Record(label, hint, extra); return false; }
        if (!Searching || string.IsNullOrEmpty(label)) return false;
        return Matches(Join(label, hint, extra));
    }

    /// <summary>Soft accent fill used to highlight a matching row.</summary>
    internal static void Highlight(Rect r) => Theme.Fill(r, new Color(Theme.Accent.r, Theme.Accent.g, Theme.Accent.b, 0.13f), 6);

    private static void RefreshHits()
    {
        if (_hitsValid) return;
        _hitsValid = true;
        TabHits.Clear();
        CardHits.Clear();
        if (_tokens.Length == 0) return;
        foreach (var it in Index)
        {
            if (!Matches(it.Text)) continue;
            string k = it.Mode + "/" + it.Tab;
            TabHits[k] = TabHits.TryGetValue(k, out var n) ? n + 1 : 1;
            if (it.Card != null) CardHits.Add(it.Card);
        }
    }

    private static int HitsFor(int mode, string tab)
    {
        if (!Searching) return 0;
        RefreshHits();
        int n = TabHits.TryGetValue(mode + "/" + tab, out var c) ? c : 0;
        if (Matches(tab.ToLowerInvariant())) n++;
        return n;
    }

    /// <summary>Tab button colour: tabs without a match fade while searching.</summary>
    internal static Color TabTint(int mode, string tab, Color current)
    {
        if (!Searching || Index.Count == 0 || HitsFor(mode, tab) > 0) return current;
        return new Color(current.r, current.g, current.b, current.a * 0.4f);
    }

    /// <summary>Match-count pill on a tab button.</summary>
    internal static void TabBadge(Rect r, int mode, string tab)
    {
        if (!Searching) return;
        int n = HitsFor(mode, tab);
        if (n <= 0) return;
        Theme.Fill(new Rect(r.x + 6f, r.yMax - 3f, r.width - 12f, 2f), Theme.Accent, 1);
        string s = n > 99 ? "99+" : n.ToString();
        var pill = new Rect(r.xMax - 8f - 8f * s.Length - 6f, r.y + 2f, 8f * s.Length + 8f, 14f);
        Theme.Fill(pill, Theme.Accent, 7);
        var prevSize = Theme.Badge.fontSize;
        Theme.Badge.fontSize = 10;
        Menu.Label(pill, s, Theme.Badge);
        Theme.Badge.fontSize = prevSize;
    }

    internal static float SearchBarHeight => SearchOn ? 34f : 0f;

    /// <summary>The search row under the tabs. Enter jumps to the first matching tab.</summary>
    internal static void DrawSearchBar(Rect r, int mode, IList<string>[] tabsByMode, string[] modeNames)
    {
        if (!SearchOn) return;
        var e = Event.current;
        bool focused = SafeFocusedName() == SearchControl;

        if (focused && e.type == EventType.KeyDown && (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter))
        {
            JumpToFirstMatch(mode, tabsByMode);
            e.Use();
        }

        float infoW = 170f;
        // The clear button sits beside the field: IMGUI gives clicks to the field first if they overlap.
        var field = new Rect(r.x, r.y, r.width - infoW - 36f, 26f);
        GUI.SetNextControlName(SearchControl);
        string q = GUI.TextField(field, _query ?? "", 40, Theme.TextField);
        if (_focusSearch) { _focusSearch = false; GUI.FocusControl(SearchControl); }
        if (string.IsNullOrEmpty(q) && !focused)
            Menu.Label(new Rect(field.x + 9f, field.y, field.width - 40f, field.height), "Search settings…  (Ctrl+F)", Theme.Small);
        if (!string.IsNullOrEmpty(q))
        {
            var clear = new Rect(field.xMax + 4f, field.y, 26f, 26f);
            if (Menu.Button(clear, "×", Theme.Chip)) { q = ""; GUIUtility.keyboardControl = 0; }
        }
        if (q != _query) SetQuery(q);

        var info = new Rect(r.xMax - infoW, r.y, infoW, 26f);
        if (!Searching)
        {
            Menu.Label(info, "click a title to open or fold", Theme.Small, TextAnchor.MiddleRight);
            return;
        }
        if (Index.Count == 0) { Menu.Label(info, "indexing…", Theme.Small); return; }

        int here = 0, tabsHere = 0;
        foreach (var t in tabsByMode[mode]) { int n = HitsFor(mode, t); here += n; if (n > 0) tabsHere++; }
        int other = -1, otherHits = 0;
        for (int m = 0; m < tabsByMode.Length; m++)
        {
            if (m == mode) continue;
            foreach (var t in tabsByMode[m]) { int n = HitsFor(m, t); if (n > 0) { otherHits += n; if (other < 0) other = m; } }
        }
        if (otherHits > 0 && other >= 0 && other < modeNames.Length)
        {
            float bw = 74f;
            var lr = new Rect(info.x, info.y, infoW - bw - 4f, 26f);
            Menu.Label(lr, here == 0 ? "none here" : $"{here} in {tabsHere} tab{(tabsHere == 1 ? "" : "s")}", here == 0 ? Theme.Small : Theme.Label);
            if (Menu.Button(new Rect(info.xMax - bw, info.y, bw, 26f), $"{modeNames[other]} {otherHits}", Theme.Chip))
                JumpToFirstMatch(other, tabsByMode);
        }
        else if (here == 0) Menu.Label(info, "<color=#E5534B>no matches</color>", Theme.Small);
        else Menu.Label(info, $"{here} match{(here == 1 ? "" : "es")} in {tabsHere} tab{(tabsHere == 1 ? "" : "s")}", Theme.Label);
    }

    private static void SetQuery(string q)
    {
        bool wasEmpty = _tokens.Length == 0;
        _query = q ?? "";
        _tokens = _query.ToLowerInvariant().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        if (wasEmpty && _tokens.Length > 0) _indexedAt = -100f; // fresh index for a new search
        _hitsValid = false;
    }

    private static void JumpToFirstMatch(int startMode, IList<string>[] tabsByMode)
    {
        for (int pass = 0; pass < tabsByMode.Length; pass++)
        {
            int m = (startMode + pass) % tabsByMode.Length;
            foreach (var t in tabsByMode[m])
                if (HitsFor(m, t) > 0) { Menu.SelectTab(t); return; }
        }
    }

    private static string SafeFocusedName()
    {
        try { return GUI.GetNameOfFocusedControl() ?? ""; }
        catch { return ""; }
    }

    // ---------- misc ----------

    private static readonly HashSet<string> Logged = new();

    internal static void LogOnce(string what, Exception e)
    {
        if (Logged.Add(what)) Plugin.Logger.LogWarning($"MenuUx {what}: {e.Message}");
    }
}
