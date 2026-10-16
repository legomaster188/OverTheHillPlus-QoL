using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Configuration;
using OverTheHillPlus.Features.Public;
using OverTheHillPlus.Multiplayer;
using OverTheHillPlus.ServerBrowser;
using OverTheHillPlus.UI;
using OverTheHillPlus.UI.Native;
using UnityEngine;

namespace OverTheHillPlus;

/// <summary>
/// The mod menu: a draggable dark panel with tabs, cards, switches and sliders.
/// Pure IMGUI with Rect-based calls (GUILayout's params arrays are awkward under IL2CPP).
///
/// Two views share the window (switch in the header, remembered in ModConfig.AdvancedMenu):
/// - Simple (default): a few short pages of big option rows, see UI/SimpleMenu and UI/SimplePages.
/// - Advanced: every card. Tabs registered by features are grouped into fewer Advanced tabs (AdvancedGroups);
///   cards start folded except each tab's first. The menu's own settings sit behind the header's menu button.
/// Both views read and write the same ConfigEntries.
/// </summary>
internal static class Menu
{
    private const float W = 600f, H = 720f, HeaderH = 60f, TabsH = 36f, FooterH = 34f, Pad = 16f, Inset = 14f;
    private static readonly List<string>[] TabsByMode =
    {
        new() { "QoL", "Lobby", "Servers" },
    };
    private static bool _featureTabsAdded;

    /// <summary>Tabs whose content is drawn by this file (features add cards to them, or new tabs).</summary>
    private static readonly HashSet<string> BuiltInTabs = new()
    {
        "QoL", "Lobby", "Servers",
    };

    /// <summary>
    /// Advanced view: its tabs and the registered tabs each one shows, in order. Registered tabs not listed here get
    /// an Advanced tab of their own; MenuUxFeature.Tab ("Menu") is the settings page behind the header's menu button.
    /// </summary>
    private static readonly (string tab, string[] sources)[][] AdvancedGroups =
    {
        new[]
        {
            ("General", new[] { "QoL" }),
            ("Servers", new[] { "Servers" }),
            ("Hosting", new[] { "Lobby", "Network", "Moderation" }),
            ("Chat", new[] { "Chat" }),
            ("Graphics", new[] { "Graphics" }),
            ("Photo", new[] { "Photo" }),
        },
    };

    private static readonly List<string>[] AdvTabsByMode = new List<string>[TabsByMode.Length];
    private static readonly Dictionary<(int, string), List<string>> AdvSources = new();
    private static readonly Dictionary<(int, string), string> AdvGroupOf = new();
    private static string SettingsTab => MenuUxFeature.Tab;
    private static List<string>[] _searchTabs;

    /// <summary>What the settings search covers: the Advanced tabs plus the menu settings page (as SettingsTab).</summary>
    private static List<string>[] SearchTabs()
    {
        if (_searchTabs != null) return _searchTabs;
        _searchTabs = new List<string>[AdvTabsByMode.Length];
        for (int m = 0; m < AdvTabsByMode.Length; m++)
        {
            _searchTabs[m] = new List<string>(AdvTabsByMode[m] ?? new List<string>());
            if (m == Features.Registry.PlusMode) _searchTabs[m].Add(SettingsTab);
        }
        return _searchTabs;
    }

    /// <summary>Adds tabs that features registered (Features.Registry.Section with a new tab name) and builds the
    /// Advanced view's grouped tabs.</summary>
    private static void EnsureFeatureTabs()
    {
        if (_featureTabsAdded) return;
        _featureTabsAdded = true;
        foreach (var s in Features.Registry.Sections)
        {
            if (s.Mode < 0 || s.Mode >= TabsByMode.Length) continue;
            if (!TabsByMode[s.Mode].Contains(s.Tab)) TabsByMode[s.Mode].Add(s.Tab);
        }
        for (int m = 0; m < TabsByMode.Length; m++) BuildAdvancedTabs(m);
    }

    private static void BuildAdvancedTabs(int m)
    {
        var tabs = new List<string>();
        void Add(string tab, string source)
        {
            if (!tabs.Contains(tab)) tabs.Add(tab);
            if (!AdvSources.TryGetValue((m, tab), out var list)) AdvSources[(m, tab)] = list = new List<string>();
            if (!list.Contains(source)) list.Add(source);
        }
        if (m < AdvancedGroups.Length)
            foreach (var (tab, sources) in AdvancedGroups[m])
                foreach (var src in sources)
                    if (TabsByMode[m].Contains(src)) { AdvGroupOf[(m, src)] = tab; Add(tab, src); }
        foreach (var src in TabsByMode[m])
        {
            if (AdvGroupOf.ContainsKey((m, src)) || (m == Features.Registry.PlusMode && src == SettingsTab)) continue;
            AdvGroupOf[(m, src)] = src;
            Add(src, src);
        }
        foreach (var s in Features.Registry.Sections) // sections that asked for another Advanced tab
            if (s.Mode == m && !string.IsNullOrEmpty(s.Group) && s.Group != SettingsTab) Add(s.Group, s.Tab);
        AdvTabsByMode[m] = tabs;
    }

    /// <summary>The Advanced tab a section shows in.</summary>
    private static string GroupOf(int mode, Features.Registry.SectionEntry s) =>
        !string.IsNullOrEmpty(s.Group) ? s.Group : AdvGroupOf.TryGetValue((mode, s.Tab), out var g) ? g : s.Tab;

    // ---- view state ----

    /// <summary>Advanced view (every card) instead of the Simple pages.</summary>
    internal static bool Advanced => ModConfig.AdvancedMenu == null || ModConfig.AdvancedMenu.Value;

    // The menu's own settings (MenuUxFeature.Tab), opened with the header's menu button in either view.
    private static bool _settingsOpen;

    private static void SetView(bool advanced)
    {
        if (ModConfig.AdvancedMenu == null || Advanced == advanced) return;
        ModConfig.AdvancedMenu.Value = advanced;
        _settingsOpen = false;
        MenuNav.Reset(false);
        NativeLook.PlayTab();
        SetStatus(advanced ? "Advanced view: every setting" : "Simple view: the settings most players use");
    }

    private static void ToggleSettings()
    {
        _settingsOpen = !_settingsOpen;
        MenuNav.Reset(false);
        NativeLook.PlayTab();
    }
    public const string LobbyTab = "Lobby";
    private static readonly string[] ModeNames =
    {
        "PLUS",
    };

    public static bool CloseRequested;

    private static int _mode;
    private static readonly int[,] TabIndex = new int[2, TabsByMode.Length]; // [simple 0 / advanced 1, mode]
    private static List<string> Tabs => Advanced ? AdvTabsByMode[_mode] ?? TabsByMode[_mode] : SimpleMenu.TabsFor(_mode);
    private static int _tab { get => TabIndex[Advanced ? 1 : 0, _mode]; set => TabIndex[Advanced ? 1 : 0, _mode] = value; }

    /// <summary>Jumps to a tab by name in whichever mode has it: an Advanced tab, a registered tab (its Advanced
    /// tab, or its Simple page in the Simple view) or MenuUxFeature.Tab (the menu settings).</summary>
    public static void SelectTab(string name)
    {
        EnsureFeatureTabs();
        if (name == SettingsTab) { _settingsOpen = true; return; }
        for (int m = 0; m < TabsByMode.Length; m++)
        {
            int i;
            if (Advanced)
            {
                var tabs = AdvTabsByMode[m];
                i = tabs.IndexOf(name);
                if (i < 0 && AdvGroupOf.TryGetValue((m, name), out var g)) i = tabs.IndexOf(g);
            }
            else i = SimpleMenu.IndexFor(m, name);
            if (i >= 0) { _mode = m; _tab = i; _settingsOpen = false; return; }
        }
    }

    private static Vector2 _pos = new(40f, 40f);
    private static bool _dragging;
    private static Vector2 _dragOffset;

    private static readonly Dictionary<string, Vector2> Scroll = new();
    private static readonly Dictionary<string, float> ContentH = new();
    private static readonly Dictionary<string, float> CardH = new();

    private static float _y, _cw;
    private static string _status = "";
    private static float _statusAt = -100f;

    // ---- frame ----

    /// <summary>Time.frameCount of the last menu draw; lets features tell whether the menu is open.</summary>
    internal static int DrawnFrame = -100;

    // ---- native style (game-look uGUI renderer, see UI/Native) ----
    // The same draw pass runs in both styles: layout, input and IMGUI controls (text fields, sliders, clicks) stay
    // here; in the native style the widgets report their visuals to NativeCanvas during Repaint instead of painting
    // IMGUI boxes. Anything a feature paints with raw GUI calls still shows (IMGUI draws on top of the canvas).
    private static bool _native;
    private static int _styleFrame = -1;
    private static int _nativeShownFrame = -100;
    private static bool _inContent;
    private static Vector2 _shownPos;
    private static readonly Dictionary<string, Vector2> ShownScroll = new();
    private static bool _scrollDrag;
    private static int _textRows;

    /// <summary>True while the menu draws in the native (game) style this frame.</summary>
    internal static bool NativeActive => _native;

    /// <summary>True while visuals go to the native canvas (native style, Repaint event).</summary>
    private static bool Emit => NativeCanvas.Emitting;

    public static void Draw()
    {
        DrawnFrame = Time.frameCount;
        Theme.Ensure();
        EnsureFeatureTabs();
        LatchStyle();
        if (!_native)
        {
            DrawWindow();
            return;
        }
        try { DrawWindow(); }
        catch (Exception e) when (IsNativeError(e))
        {
            // Auto-fallback: the classic look takes over from the next frame, the menu stays open. Errors that
            // have nothing to do with the native renderer propagate as before (ModBehaviour closes the menu).
            NativeStyle.Fail("draw", e);
            _native = false;
        }
        finally
        {
            if (NativeCanvas.Emitting) NativeCanvas.EndFrame();
            _inContent = false;
        }
    }

    private static bool IsNativeError(Exception e)
    {
        string st = e.StackTrace ?? "";
        return st.Contains("UI.Native.") || st.Contains("Menu.Native") || st.Contains("Menu.DrawNative") || st.Contains("NativeCanvas");
    }

    /// <summary>Picks the style once per frame (switching mid-frame would mismatch IMGUI control ids).</summary>
    private static void LatchStyle()
    {
        if (_styleFrame == Time.frameCount) return;
        _styleFrame = Time.frameCount;
        bool want = NativeStyle.Wanted;
        if (want)
        {
            try { NativeCanvas.Ensure(); }
            catch (Exception e)
            {
                NativeStyle.Fail("setup", e);
                want = false;
            }
        }
        if (!want) NativeCanvas.Hide();
        _native = want;
    }

    private static void DrawWindow()
    {
        var ev = Event.current;
        bool repaint = ev.type == EventType.Repaint;
        MenuUx.BeginFrame();
        float scale = MenuUx.MenuScale(Mathf.Max(1f, Screen.height / 1080f));
        GUI.matrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, new Vector3(scale, scale, 1f));

        float sw = Screen.width / scale, sh = Screen.height / scale;
        _pos.x = Mathf.Clamp(_pos.x, 0f, Mathf.Max(0f, sw - W));
        _pos.y = Mathf.Clamp(_pos.y, 0f, Mathf.Max(0f, sh - 80f));
        float h = Mathf.Min(H, sh - _pos.y - 10f);

        // Canvas changes made during OnGUI show up one frame later, so in the native style IMGUI repaints at the
        // position (and scroll) the canvas currently shows; the canvas gets the live values. Keeps text fields and
        // raw feature labels glued to the native panels while dragging or scrolling.
        if (_native && Time.frameCount - _nativeShownFrame > 2)
        {
            _shownPos = _pos;
            ShownScroll.Clear();
            MenuNav.ClearPending();
        }
        var drawPos = _native && repaint ? _shownPos : _pos;
        var win = new Rect(drawPos.x, drawPos.y, W, h);
        if (_native && repaint)
        {
            NativeCanvas.BeginFrame(scale, _pos - drawPos);
            _shownPos = _pos;
            _nativeShownFrame = Time.frameCount;
            int ts = MenuNav.TakeTabStep();
            if (ts != 0) StepTab(ts);
            int ms = MenuNav.TakeModeStep();
            if (ms != 0) StepMode(ms);
            if (MenuNav.TakeViewToggle()) SetView(!Advanced);
        }
        _textRows = 0;

        HeaderLayout(win, out var viewSeg, out _, out _, out _);
        HandleDrag(new Rect(win.x, win.y, viewSeg.x - win.x - 8f, HeaderH)); // title area only: the right side has buttons
        if (_native) NativeWindowBack(win);
        else GUI.Box(win, GUIContent.none, Theme.Window);
        DrawHeader(win);
        float tabsH = TabRows * TabsH;
        DrawTabs(new Rect(win.x + Pad, win.y + HeaderH, W - Pad * 2, tabsH));
        // Search is part of the Advanced view (the Simple pages are short enough to read). A query left in the box
        // does nothing while the box isn't shown.
        bool search = Advanced;
        MenuUx.SearchShown = search;
        float searchH = search ? MenuUx.SearchBarHeight : 0f;
        if (search) MenuUx.DrawSearchBar(new Rect(win.x + Pad, win.y + HeaderH + tabsH + 8f, W - Pad * 2, searchH), _mode, SearchTabs(), ModeNames);

        var area = new Rect(win.x + Pad, win.y + HeaderH + tabsH + searchH + 10f, W - Pad * 2, h - HeaderH - tabsH - searchH - FooterH - 18f);
        string tab = Tabs.Count == 0 ? "" : Tabs[Mathf.Clamp(_tab, 0, Tabs.Count - 1)];
        // Scroll positions and heights per page: the views (and the settings page) don't share them.
        string page = _settingsOpen ? "settings" : (Advanced ? "a" : "s") + _mode + "/" + tab;
        Scroll.TryGetValue(page, out var scroll);
        float contentH = ContentH.TryGetValue(page, out var ch) ? ch : 600f;
        var viewRect = new Rect(0f, 0f, area.width - 16f, contentH);
        if (!_native) Scroll[page] = GUI.BeginScrollView(area, scroll, viewRect);
        else
        {
            // No IMGUI scrollbars here (the canvas draws one), so keep the position in range ourselves.
            float maxScroll = Mathf.Max(0f, contentH - area.height);
            float wantedY = MenuNav.ScrollRequest ?? scroll.y;
            MenuNav.ScrollRequest = null;
            if (Mathf.Abs(Mathf.Clamp(wantedY, 0f, maxScroll) - scroll.y) > 0.01f || scroll.x != 0f)
            {
                scroll = new Vector2(0f, Mathf.Clamp(wantedY, 0f, maxScroll));
                Scroll[page] = scroll;
            }
            if (HandleScrollbar(area, contentH, ref scroll)) Scroll[page] = scroll;
            MenuNav.BeginFrame(repaint, ev.mousePosition, area.Contains(ev.mousePosition));
            if (repaint)
            {
                var shown = ShownScroll.TryGetValue(page, out var ss) ? ss : scroll;
                GUI.BeginScrollView(area, shown, viewRect, false, false, GUIStyle.none, GUIStyle.none);
                NativeCanvas.BeginContent(area, scroll);
                ShownScroll[page] = scroll;
            }
            else Scroll[page] = GUI.BeginScrollView(area, scroll, viewRect, false, false, GUIStyle.none, GUIStyle.none);
        }
        _y = 0f;
        _cw = area.width - 18f;
        _inContent = true;
        try { DrawPage(tab); }
        finally { _inContent = false; }
        ContentH[page] = _y + 6f;
        if (_native && repaint) NativeCanvas.EndContent();
        GUI.EndScrollView();
        if (_native)
        {
            Scroll.TryGetValue(page, out var now);
            MenuNav.EndFrame(now.y, area.height);
            NativeScrollbar(area, ContentH[page], now.y);
        }

        if (search && MenuUx.NeedsIndex && Event.current.type == EventType.Layout)
        {
            // Search index: walk every Advanced tab once in a Layout event (nothing is drawn or clicked in Layout).
            float cw = _cw;
            MenuUx.BuildIndex(SearchTabs(), (m, t) =>
            {
                _y = 0f;
                _cw = cw;
                if (m == Features.Registry.PlusMode && t == SettingsTab) DrawSettingsPage();
                else DrawAdvancedTab(m, t);
            });
            _drawMode = _mode;
            _drawTab = tab;
        }

        DrawFooter(new Rect(win.x, win.yMax - FooterH, W, FooterH));
        NativeCanvas.BeginOverlay(); // tooltips above everything (no-op in the classic style)
        MenuUx.EndFrame(area);
    }

    // ---- native style: window parts ----

    private static void NativeWindowBack(Rect win)
    {
        if (!Emit) return;
        NativeCanvas.Fill(win, NativeLook.Panel, NativeLook.PanelShape, Depth.Back);
        // Header band and the game's yellow rule under it.
        NativeCanvas.Fill(new Rect(win.x + 6f, win.y + 6f, win.width - 12f, HeaderH - 10f), NativeLook.HeaderBand, NativeLook.RoundMedium, Depth.Back);
        NativeCanvas.Fill(new Rect(win.x + Pad, win.y + HeaderH - 3f, win.width - Pad * 2, 2f), NativeLook.Highlight, NativeLook.Square, Depth.Front);
    }

    /// <summary>Drag on the native scrollbar strip (right edge of the content area). True when it moved.</summary>
    private static bool HandleScrollbar(Rect area, float contentH, ref Vector2 scroll)
    {
        var e = Event.current;
        float max = contentH - area.height;
        if (max <= 0f) { _scrollDrag = false; return false; }
        var strip = new Rect(area.xMax - 12f, area.y, 12f, area.height);
        if (e.type == EventType.MouseDown && e.button == 0 && strip.Contains(e.mousePosition)) _scrollDrag = true;
        else if (e.type == EventType.MouseUp) { _scrollDrag = false; return false; }
        if (!_scrollDrag || e.type != EventType.MouseDown && e.type != EventType.MouseDrag) return false;
        float thumb = Mathf.Max(30f, area.height * area.height / contentH);
        float t = Mathf.InverseLerp(area.y + thumb / 2f, area.yMax - thumb / 2f, e.mousePosition.y);
        scroll.y = Mathf.Round(t * max);
        e.Use();
        return true;
    }

    private static void NativeScrollbar(Rect area, float contentH, float scrollY)
    {
        if (!Emit || contentH <= area.height + 1f) return;
        var track = new Rect(area.xMax - 6f, area.y + 2f, 4f, area.height - 4f);
        NativeCanvas.Fill(track, NativeLook.Track, NativeLook.Pill, Depth.Front);
        float thumb = Mathf.Max(30f, track.height * area.height / contentH);
        float t = Mathf.Clamp01(scrollY / (contentH - area.height));
        NativeCanvas.Fill(new Rect(track.x, track.y + t * (track.height - thumb), track.width, thumb),
            _scrollDrag ? NativeLook.Highlight : NativeLook.Muted, NativeLook.Pill, Depth.Front);
    }

    private static void StepTab(int step)
    {
        // Q / E go round the tabs and then the menu settings page (stop n), so keyboard and gamepad reach it too.
        int n = Tabs.Count;
        int pos = _settingsOpen || n == 0 ? n : Mathf.Clamp(_tab, 0, n - 1);
        pos = ((pos + step) % (n + 1) + n + 1) % (n + 1);
        _settingsOpen = pos == n;
        if (pos < n) _tab = pos;
        MenuNav.Reset(MenuNav.UsedKeyboardRecently);
        NativeLook.PlayTab();
    }

    private static void StepMode(int step)
    {
        if (TabsByMode.Length < 2) return;
        _mode = ((_mode + step) % TabsByMode.Length + TabsByMode.Length) % TabsByMode.Length;
        _settingsOpen = false;
        MenuNav.Reset(MenuNav.UsedKeyboardRecently);
        NativeLook.PlayTab();
    }

    private static int _drawMode;
    private static string _drawTab = "";
    private static int _cardIndex; // cards drawn so far on this page (Advanced: only the first starts open)

    private static void DrawPage(string tab)
    {
        _cardIndex = 0;
        if (_settingsOpen) { DrawSettingsPage(); return; }
        if (Advanced) { DrawAdvancedTab(_mode, tab); return; }
        _drawMode = _mode;
        _drawTab = tab;
        SimpleMenu.DrawPage(_mode, tab);
    }

    /// <summary>The menu's own settings (style, size, search, keybinds): MenuUxFeature.Tab's cards.</summary>
    private static void DrawSettingsPage()
    {
        _cardIndex = 0;
        GroupTitle("Menu settings", true);
        int plus = Features.Registry.PlusMode;
        DrawSource(plus, SettingsTab, false, SettingsTab);
        // Sections from other tabs that asked for this page (Registry.Section advancedTab: "Menu").
        _settingsSources.Clear();
        foreach (var s in Features.Registry.Sections)
            if (s.Mode == plus && s.Tab != SettingsTab && s.Group == SettingsTab && !_settingsSources.Contains(s.Tab)) _settingsSources.Add(s.Tab);
        foreach (var src in _settingsSources) DrawSource(plus, src, false, SettingsTab);
    }

    private static readonly List<string> _settingsSources = new();

    /// <summary>An Advanced tab: the cards of every registered tab grouped into it, each group under a title.</summary>
    private static void DrawAdvancedTab(int mode, string tab)
    {
        _cardIndex = 0;
        if (!AdvSources.TryGetValue((mode, tab), out var sources)) return;
        bool first = true;
        foreach (var src in sources)
        {
            bool builtIn = BuiltInTabs.Contains(src) && AdvGroupOf.TryGetValue((mode, src), out var g) && g == tab;
            bool any = builtIn;
            if (!any)
                foreach (var s in Features.Registry.SectionsFor(mode, src))
                    if (GroupOf(mode, s) == tab) { any = true; break; }
            if (!any) continue;
            if (sources.Count > 1) GroupTitle(SourceTitle(src), first);
            first = false;
            DrawSource(mode, src, builtIn, tab);
        }
    }

    private static string SourceTitle(string src) => src switch
    {
        "QoL" => "General",
        _ => src,
    };

    private static void GroupTitle(string title, bool first)
    {
        if (!first) _y += 6f;
        var r = new Rect(4f, _y, _cw - 8f, 22f);
        Label(r, title.ToUpperInvariant(), Theme.Section, _native ? NativeLook.Highlight : Theme.Accent);
        Theme.Fill(new Rect(4f, _y + 23f, _cw - 8f, 1f), _native ? NativeLook.Line : Theme.Line, 0);
        _y += 32f;
    }

    /// <summary>One registered tab's content: built-in cards, then feature sections (those shown in
    /// <paramref name="group"/>, or all when null).</summary>
    private static void DrawSource(int mode, string tab, bool builtIn, string group)
    {
        _drawMode = mode;
        _drawTab = tab;
        if (builtIn) DrawBuiltIn(tab);
        foreach (var section in Features.Registry.SectionsFor(mode, tab))
        {
            if (group != null && GroupOf(mode, section) != group) continue;
            // A failure inside the native renderer propagates to Draw, which falls back to Classic once; anything
            // else is the feature's own bug and shows inline.
            try { section.Draw(); }
            catch (Exception e) when (!(_native && IsNativeError(e))) { Small($"<color=#E5534B>{section.Owner} failed: {e.Message}</color>"); }
        }
    }

    private static void DrawBuiltIn(string tab)
    {
        switch (tab)
        {
            case "QoL": DrawQol(); break;
            case "Lobby": DrawLobby(); break;
            case "Servers": DrawServers(); break;
        }
    }

    private static void HandleDrag(Rect handle)
    {
        var e = Event.current;
        if (e.type == EventType.MouseDown && e.button == 0 && handle.Contains(e.mousePosition))
        {
            _dragging = true;
            _dragOffset = e.mousePosition - _pos;
            e.Use();
        }
        else if (_dragging && e.type == EventType.MouseDrag)
        {
            _pos = e.mousePosition - _dragOffset;
            e.Use();
        }
        else if (e.type == EventType.MouseUp) _dragging = false;
    }

    private static readonly string[] ViewNames = { "SIMPLE", "ADVANCED" };

    /// <summary>Right side of the header, right to left: close, menu settings, the mode switch (when there are two),
    /// SIMPLE / ADVANCED. The title gets what is left.</summary>
    private static void HeaderLayout(Rect win, out Rect view, out Rect mode, out Rect settings, out Rect close)
    {
        close = new Rect(win.xMax - Pad - 32f, win.y + 14f, 32f, 30f);
        settings = new Rect(close.x - 6f - 32f, win.y + 14f, 32f, 30f);
        float x = settings.x - 8f;
        mode = default;
        if (ModeNames.Length > 1)
        {
            mode = new Rect(x - 129f, win.y + 15f, 129f, 28f);
            x = mode.x - 8f;
        }
        view = new Rect(x - 165f, win.y + 15f, 165f, 28f);
    }

    private static void DrawHeader(Rect win)
    {
        HeaderLayout(win, out var viewRect, out var modeRect, out var settingsRect, out var closeRect);
        float titleW = viewRect.x - win.x - Pad - 8f;

        string badge = GameState.Badge;
        Color badgeColor = badge switch
        {
            "SOLO" or "HOST" => Theme.Good,
            "FRIENDS" => Theme.Info,
            "PUBLIC" => Theme.Warn,
            _ => Theme.Off,
        };
        // Session state as a coloured word (SOLO / HOST / FRIENDS / PUBLIC / MENU) where the badge pill used to be.
        string sub = $"v{Plugin.Version}  ·  <color=#{ColorUtility.ToHtmlStringRGB(badgeColor)}>{badge}</color>";
        if (_native)
        {
            string plus = "#" + ColorUtility.ToHtmlStringRGB(NativeLook.Highlight);
            var titleRect = new Rect(win.x + Pad, win.y + 8f, titleW, 28f);
            if (Emit)
                NativeCanvas.Text(titleRect, $"OVER THE HILL <color={plus}>PLUS</color>",
                    new TextSpec(titleW < 200f ? 18f : 22f, NativeLook.Text, heading: true, upper: true));
            Label(new Rect(win.x + Pad, win.y + 35f, titleW, 16f), sub, Theme.Small);
        }
        else
        {
            int titleSize = Theme.Title.fontSize;
            if (titleW < 200f) Theme.Title.fontSize = 15; // two switches in the header leave less room
            GUI.Label(new Rect(win.x + Pad, win.y + 12f, titleW, 22f), "OVER THE HILL <color=#E8ECF2>PLUS</color>", Theme.Title);
            Theme.Title.fontSize = titleSize;
            GUI.Label(new Rect(win.x + Pad, win.y + 34f, titleW, 16f), sub, Theme.Small);
        }

        int v = Segment(viewRect, ViewNames, Advanced ? 1 : 0);
        if (v >= 0) SetView(v == 1);
        if (ModeNames.Length > 1)
        {
            int m = Segment(modeRect, ModeNames, _mode);
            if (m >= 0 && m != _mode)
            {
                _mode = m;
                _settingsOpen = false;
                MenuNav.Reset(false);
                NativeLook.PlayTab();
            }
        }

        if (SettingsButton(settingsRect, _settingsOpen)) ToggleSettings();
        bool closeClicked = _native ? Button(closeRect, "×", Theme.Button) : GUI.Button(closeRect, "×", Theme.Button);
        if (closeClicked) CloseRequested = true;
    }

    /// <summary>A two- or three-way switch (SIMPLE / ADVANCED, the mode switch). Returns the clicked index or -1.</summary>
    private static int Segment(Rect r, string[] labels, int active)
    {
        if (_native) { if (Emit) NativeCanvas.Fill(r, Theme.Hex(0x000000, 0.35f), NativeLook.Pill, Depth.Back); }
        else Theme.Fill(r, Theme.Hex(0x0E1218), 8);
        int n = labels.Length, clicked = -1;
        float bw = (r.width - 3f * (n + 1)) / n;
        for (int i = 0; i < n; i++)
        {
            var b = new Rect(r.x + 3f + i * (bw + 3f), r.y + 3f, bw, r.height - 6f);
            bool hit = _native ? ModeSegment(b, labels[i], i == active) : GUI.Button(b, labels[i], i == active ? Theme.TabActive : Theme.Tab);
            if (hit) clicked = i;
        }
        return clicked;
    }

    private static bool ModeSegment(Rect r, string text, bool active)
    {
        if (Emit)
        {
            if (active) NativeCanvas.Fill(r, NativeLook.Highlight, NativeLook.HighlightShape, Depth.Mid);
            NativeCanvas.Text(r, text, new TextSpec(12f, active ? NativeLook.HighlightText : NativeLook.Muted, TextAnchor.MiddleCenter, bold: true, heading: true));
        }
        return GUI.Button(r, GUIContent.none, Theme.Invisible);
    }

    /// <summary>The menu-settings button: three bars (no font glyph needed), lit while the settings page is open.</summary>
    private static bool SettingsButton(Rect r, bool lit)
    {
        Color bars;
        if (_native)
        {
            if (Emit)
            {
                if (lit) NativeCanvas.Fill(r, NativeLook.Highlight, NativeLook.HighlightShape, Depth.Mid);
                else NativeCanvas.Fill(r, NativeLook.Button, NativeLook.ButtonShape, Depth.Front);
            }
            bars = lit ? NativeLook.HighlightText : NativeLook.ButtonText;
        }
        else
        {
            bool hover = r.Contains(Event.current.mousePosition);
            Theme.Fill(r, lit ? Theme.Accent : hover ? Theme.CardHover : Theme.Card, 6);
            bars = lit ? Theme.Hex(0x0E1116) : Theme.Text;
        }
        for (int k = 0; k < 3; k++)
            Theme.Fill(new Rect(r.center.x - 7f, r.y + 9f + k * 5f, 14f, 2f), bars, 0);
        bool clicked = GUI.Button(r, GUIContent.none, Theme.Invisible);
        if (clicked && _native) NativeLook.PlayPress();
        return clicked;
    }

    private const int TabsPerRow = 7;
    private static int TabRows => Math.Max(1, (Tabs.Count + TabsPerRow - 1) / TabsPerRow);

    private static void DrawTabs(Rect bar)
    {
        if (_native)
        {
            DrawNativeTabs(bar);
            return;
        }
        Theme.Fill(bar, Theme.Hex(0x0E1218), 8);
        int rows = TabRows;
        int perRow = (Tabs.Count + rows - 1) / rows;
        float tw = (bar.width - 8f) / perRow;
        for (int i = 0; i < Tabs.Count; i++)
        {
            int row = i / perRow, col = i % perRow;
            var r = new Rect(bar.x + 4f + col * tw, bar.y + 4f + row * TabsH, tw - 4f, TabsH - 8f);
            var prev = GUI.color;
            if (Advanced) GUI.color = MenuUx.TabTint(_mode, Tabs[i], prev);
            if (GUI.Button(r, Tabs[i], i == _tab && !_settingsOpen ? Theme.TabActive : Theme.Tab)) { _tab = i; _settingsOpen = false; }
            GUI.color = prev;
            if (Advanced) MenuUx.TabBadge(r, _mode, Tabs[i]);
        }
    }

    /// <summary>The game's tab bar look: plain labels, the selected one on the highlight bar, Q / E hints at the ends.</summary>
    private static void DrawNativeTabs(Rect bar)
    {
        if (Emit) NativeCanvas.Fill(bar, Theme.Hex(0x000000, 0.28f), NativeLook.RoundSmall, Depth.Back);
        bool hints = NativeStyle.KeyboardNavOn;
        float side = hints ? 30f : 4f;
        if (hints && Emit)
        {
            SimpleMenu.Keycap(new Rect(bar.x + 5f, bar.y + (TabsH - 20f) / 2f, 20f, 20f), "Q");
            SimpleMenu.Keycap(new Rect(bar.xMax - 25f, bar.y + (TabsH - 20f) / 2f, 20f, 20f), "E");
        }
        int rows = TabRows;
        int perRow = (Tabs.Count + rows - 1) / rows;
        float tw = (bar.width - side * 2f) / perRow;
        for (int i = 0; i < Tabs.Count; i++)
        {
            int row = i / perRow, col = i % perRow;
            var r = new Rect(bar.x + side + col * tw + 2f, bar.y + 4f + row * TabsH, tw - 4f, TabsH - 8f);
            var prev = GUI.color;
            if (Advanced) GUI.color = MenuUx.TabTint(_mode, Tabs[i], prev);
            bool active = i == _tab && !_settingsOpen;
            if (Emit)
            {
                if (active) NativeCanvas.Fill(r, NativeLook.Highlight, NativeLook.HighlightShape, Depth.Mid);
                NativeCanvas.Text(r, Tabs[i], new TextSpec(13.5f, active ? NativeLook.HighlightText : NativeLook.Text,
                    TextAnchor.MiddleCenter, bold: true, heading: true, upper: true));
            }
            if (GUI.Button(r, GUIContent.none, Theme.Invisible) && !active)
            {
                _tab = i;
                _settingsOpen = false;
                MenuNav.Reset(false);
                NativeLook.PlayTab();
            }
            GUI.color = prev;
            if (Advanced) MenuUx.TabBadge(r, _mode, Tabs[i]);
        }
    }

    private static void DrawFooter(Rect r)
    {
        if (_native) { if (Emit) NativeCanvas.Fill(new Rect(r.x + Pad, r.y, r.width - Pad * 2, 1f), NativeLook.Line, NativeLook.Square, Depth.Front); }
        else Theme.Fill(new Rect(r.x + Pad, r.y, r.width - Pad * 2, 1f), Theme.Line, 0);
        var text = new Rect(r.x + Pad, r.y + 6f, r.width - Pad * 2, r.height - 8f);
        Color muted = _native ? NativeLook.Muted : Theme.Muted;

        // Priority: a fresh status message, then the selected Simple row's explanation, then key hints.
        if (Time.unscaledTime - _statusAt < 8f && _status.Length > 0)
        {
            Color color = _status.StartsWith("Failed") ? Theme.Bad : _status.StartsWith("Blocked") ? Theme.Warn : Theme.Good;
            Label(text, _status, Theme.Status, color);
            return;
        }
        if (_hintFrame == Time.frameCount && !string.IsNullOrEmpty(_hint))
        {
            Label(text, _hint, Theme.Status, muted);
            return;
        }

        float x = text.x;
        x = FooterKeys(x, text, muted, "Close", KeyLabel(ModConfig.MenuKey.Value));
        if (_native && NativeStyle.KeyboardNavOn)
        {
            x = FooterKeys(x, text, muted, "Tabs", "Q", "E");
            x = FooterKeys(x, text, muted, "Change", "Enter");
            FooterKeys(x, text, muted, Advanced ? "Simple" : "Advanced", "Tab");
        }
        else
        {
            x = FooterKeys(x, text, muted, "HUD", KeyLabel(ModConfig.HudKey.Value));
            Label(new Rect(x, text.y, text.xMax - x, text.height), "Drag the title to move", Theme.Status, muted);
        }
    }

    private static string KeyLabel(UnityEngine.InputSystem.Key k) => k == UnityEngine.InputSystem.Key.None ? "-" : k.ToString();

    /// <summary>Keycaps then a word, e.g. [Q] [E] Tabs. Returns the x after it.</summary>
    private static float FooterKeys(float x, Rect line, Color color, string label, params string[] keys)
    {
        foreach (var k in keys)
        {
            float w = SimpleMenu.KeycapWidth(k);
            SimpleMenu.Keycap(new Rect(x, line.y + (line.height - 18f) / 2f, w, 18f), k);
            x += w + 4f;
        }
        float lw = label.Length * 7.2f + 6f;
        Label(new Rect(x + 2f, line.y, lw, line.height), label, Theme.Status, color);
        return x + lw + 14f;
    }

    // The selected Simple row's explanation, shown in the footer for the frame it was set in.
    private static string _hint;
    private static int _hintFrame = -1;

    /// <summary>Shows a line of help in the footer this frame (the selected Simple row's explanation).</summary>
    internal static void ShowHint(string hint)
    {
        _hint = hint;
        _hintFrame = Time.frameCount;
    }

    internal static void SetStatus(string s)
    {
        if (string.IsNullOrEmpty(s)) return;
        _status = s;
        _statusAt = Time.unscaledTime;
    }

    // ---- tabs ----

    private static string _customSizeText;

    /// <summary>Drops text typed into menu fields so they re-read their settings (after settings change from outside).</summary>
    internal static void ForgetTypedText() => _customSizeText = null;

    // ---- PLUS mode ----

    private static void DrawQol()
    {
        Card("Performance", null, () =>
        {
            float cap = RawSlider("Frame-rate cap", ModConfig.FpsCap.Value, 0f, 360f,
                ModConfig.FpsCap.Value <= 0 ? "game setting" : $"{ModConfig.FpsCap.Value} fps", true);
            int capInt = cap < 15f ? 0 : Mathf.RoundToInt(cap / 5f) * 5;
            if (capInt != ModConfig.FpsCap.Value) ModConfig.FpsCap.Value = capInt;
            Small("Above the game's own options (it tops out lower). Setting a cap turns VSync off.");
            Switch(ModConfig.RunInBackground, "Keep running when alt-tabbed", "Stops the session freezing for everyone when the host alt-tabs", true);
            if (Features.Public.AltTab.AltTabHandbrake.Enabled != null)
                Switch(Features.Public.AltTab.AltTabHandbrake.Enabled, "No parking brake when alt-tabbed", "Your car isn't parked when the game window loses focus", true);
            float bg = RawSlider("Background frame cap", ModConfig.BackgroundFpsCap.Value, 0f, 120f,
                ModConfig.BackgroundFpsCap.Value <= 0 ? "off" : $"{ModConfig.BackgroundFpsCap.Value} fps", true);
            int bgInt = bg < 10f ? 0 : Mathf.RoundToInt(bg / 5f) * 5;
            if (ModConfig.BackgroundFpsCap.Value <= 0 && bg > 0f && bg < 10f) bgInt = 10; // a key step up from "off"
            if (bgInt != ModConfig.BackgroundFpsCap.Value) ModConfig.BackgroundFpsCap.Value = bgInt;
        });
        Card("Camera", null, () =>
        {
            float fov = RawSlider("Field of view", ModConfig.FovOverride.Value, 0f, 110f,
                ModConfig.FovOverride.Value <= 0f ? "game setting" : $"{ModConfig.FovOverride.Value:0}°", true);
            float fovRounded = fov < 30f ? 0f : Mathf.Round(fov);
            if (ModConfig.FovOverride.Value <= 0f && fov > 0f && fov < 30f) fovRounded = 30f; // a key step up from "game setting"
            if (Math.Abs(fovRounded - ModConfig.FovOverride.Value) > 0.01f) ModConfig.FovOverride.Value = fovRounded;
            Switch(ModConfig.ShowHud, $"Driving HUD ({ModConfig.HudKey.Value})", "Speed, RPM, gear, power, torque, slip", true);
        });
        Card("Navigation", null, () =>
        {
            SliderRow(ModConfig.CompassRange, "Compass range", 1f, 4f, "0.0", "×", true);
            Switch(ModConfig.AutoHeadlights, "Auto headlights", "On from 19:00 to 07:00, off in daylight", true);
        });
        Card("Menus & startup", null, () =>
        {
            Switch(ModConfig.SkipSplash, "Skip logo screens", "Straight to the main menu at startup", true);
            SliderRow(ModConfig.FadeSpeed, "Screen fade length", 0.1f, 1f, "0.00", "×", true);
            Switch(ModConfig.HideTutorialHints, "Hide tutorial hints", "No more reminder pop-ups", true);
            Switch(ModConfig.MainMenuButton, "PLUS button on the main menu", "Takes effect next time the main menu opens", true);
        });
    }

    private static void DrawLobby()
    {
        Card("Lobby size", "Bigger lobbies in the game's Create Server screen. Everyone in a lobby above 4 players should run this mod.", () =>
        {
            Switch(ModConfig.ExpandLobbySize, "Up to 16 players", "Adds 2–16 to the Max Players option", true);
            Label(new Rect(Inset + 4f, _y, 190f, 28f), "Exact size (typed)", Theme.Label);
            _customSizeText ??= ModConfig.CustomMaxPlayers.Value >= 2 ? ModConfig.CustomMaxPlayers.Value.ToString() : "";
            _customSizeText = GUI.TextField(new Rect(Inset + 200f, _y, 80f, 28f), _customSizeText, 3, Theme.TextField);
            int typed = int.TryParse(_customSizeText, out int ts) ? ts : 0;
            int wanted = typed >= 2 ? Math.Min(typed, RoomNames.MaxCustomPlayers) : 0;
            if (wanted != ModConfig.CustomMaxPlayers.Value) ModConfig.CustomMaxPlayers.Value = wanted;
            Label(new Rect(Inset + 290f, _y, IW - 290f, 28f), wanted >= 2 ? $"hosting uses {wanted} (max {RoomNames.MaxCustomPlayers}{(wanted > 32 ? ", untested above 32" : "")})" : "empty = Create Server choice", Theme.Small);
            _y += 34f;
            float v = RawSlider("Preselected size", ModConfig.DefaultMaxPlayers.Value, 2f, LobbyRules.MaxPlayers,
                LobbyRules.OptionLabel(ModConfig.DefaultMaxPlayers.Value), ModConfig.ExpandLobbySize.Value, keyStep: 1f);
            int n = Mathf.RoundToInt(v);
            if (n != ModConfig.DefaultMaxPlayers.Value) ModConfig.DefaultMaxPlayers.Value = n;
            KeyValue("Network capacity", ModConfig.MeasuredCapacity.Value > 0
                ? $"{ModConfig.MeasuredCapacity.Value} players (measured)"
                : "not measured yet — join or host any session once");
            Small("Above the network capacity, extra players get fallback spawn spots and placeholder names instead of crashing the lobby.");
        });
        Card("Server name", "Used for servers you host from now on. Leave empty for the game's default.", () =>
        {
            Label(new Rect(Inset + 4f, _y, 60f, 28f), "Name", Theme.Label);
            string name = GUI.TextField(new Rect(Inset + 64f, _y, IW - 64f - 98f, 28f), ModConfig.CustomRoomName.Value ?? "", 40, Theme.TextField);
            if (name != ModConfig.CustomRoomName.Value) ModConfig.CustomRoomName.Value = name;
            if (ChipButton(new Rect(Inset + IW - 92f, _y, 92f, 28f), "Clear", !string.IsNullOrEmpty(ModConfig.CustomRoomName.Value)))
                ModConfig.CustomRoomName.Value = "";
            _y += 36f;
            if (Features.Public.Filter.NameFilter.IsBlocked(RoomNames.Escape(ModConfig.CustomRoomName.Value), out string nameWhy))
                Small($"<color=#E5534B>Not used: {nameWhy}. Servers keep the game's default name until you change it.</color>");
            Small("Text colour");
            var styles = RoomNames.Styles.Select(st => (st == ModConfig.RoomNameStyle.Value ? $"[{st}]" : st, (Action)(() => ModConfig.RoomNameStyle.Value = st), true)).ToArray();
            Chips(5, styles);
            Features.Public.ServerNameStyles.ServerNameStyleMenu.Draw();
            Small("Colours and styles show in everyone's server browser, including players without the mod.");
        });
        Card("Join message", "When you host: a pop-up for everyone each time a player joins. {name} = who joined, {host} = you.", () =>
        {
            Switch(ModConfig.JoinMessageEnabled, "Greet players who join", null, true);
            string msg = GUI.TextField(new Rect(Inset, _y, IW, 28f), ModConfig.JoinMessage.Value ?? "", 90, Theme.TextField);
            if (msg != ModConfig.JoinMessage.Value) ModConfig.JoinMessage.Value = msg;
            _y += 34f;
            if (Features.Public.Filter.NameFilter.IsBlocked(RoomNames.Escape(ModConfig.JoinMessage.Value), out string msgWhy))
                Small($"<color=#E5534B>Not sent: {msgWhy}.</color>");
            var colors = JoinMessage.Colors.Select(c => (c == ModConfig.JoinMessageColor.Value ? $"[{c}]" : c, (Action)(() => ModConfig.JoinMessageColor.Value = c), true)).ToArray();
            Chips(7, colors);
            KeyValue("Preview", JoinMessage.Preview());
        });
        Card("Stability", null, () =>
        {
            Switch(ModConfig.LobbyGuards, "Lobby crash guards", "Smart spawn points, safe join/leave, finish registering extra players", true);
            Switch(ModConfig.FallRescue, "Fall rescue", "Host/solo: cars under the ground or off the map are put back", true);
            KeyValue("Cars rescued", BigLobby.Rescued.ToString());
        });
        Card("This session", null, () =>
        {
            var r = GameState.Runner;
            if (r == null) { Small("Not in a session."); return; }
            int players = 0, max = 0;
            try { players = OverlandSessionPlayers.Instance != null ? OverlandSessionPlayers.Instance.GetPlayerCount() : 0; } catch { }
            try { max = r.SessionInfo != null ? r.SessionInfo.MaxPlayers : 0; } catch { }
            KeyValue("Players", max > 0 ? $"{players} / {max}" : players.ToString());
            KeyValue("You are", GameState.Badge.ToLowerInvariant());
        });
    }

    private static void DrawServers()
    {
        Card("Server browser", null, () =>
        {
            Switch(ModConfig.ColorRows, "Color-code rows", "Stripe and tint by server status", true);
            Switch(ModConfig.ShowAge, "Show server age", "How long ago the mod first saw each server", true);
            Switch(ModConfig.ThrottleSessionUpdates, "Smooth list refreshes", "Skip duplicate updates and merge bursts", true);
            SliderRow(ModConfig.SessionUpdateInterval, "Min time between rebuilds", 0.25f, 5f, "0.00", " s", ModConfig.ThrottleSessionUpdates.Value);
        });
        Card("Legend", null, () =>
        {
            Legend(ServerStatus.Public, "Public", "joinable");
            Legend(ServerStatus.Private, "Private", "locked / room code");
            Legend(ServerStatus.Full, "Full", "or closed to new players");
            Legend(ServerStatus.Incompatible, "Other version", "can't join");
            Small("Age is exact if the mod saw the server appear; \"≥\" means it already existed.");
        });
        Card("Stats", null, () =>
        {
            KeyValue("Servers remembered", FirstSeenTracker.Count.ToString());
        });
    }

    // ---- widgets ----

    private static float IW => _cw - Inset * 2;

    // ---- layout access for feature files (Features/*) ----

    /// <summary>Current vertical position inside the tab's scroll area.</summary>
    internal static float Y { get => _y; set => _y = value; }
    /// <summary>Left edge for content inside a card.</summary>
    internal static float Left => Inset;
    /// <summary>Usable content width inside a card.</summary>
    internal static float Width => IW;
    /// <summary>Full width of the tab's content area (cards and Simple rows span it).</summary>
    internal static float ContentWidth => _cw;

    /// <summary>Reserves a full-width row of the given height inside a card and returns its rect.</summary>
    internal static Rect NextRow(float height)
    {
        var r = new Rect(Inset, _y, IW, height);
        _y += height + 6f;
        return r;
    }

    // ---- text and buttons that follow the menu style (use these instead of GUI.Label / GUI.Button) ----

    private static TextSpec SpecLabel => new(14f, NativeLook.Text);
    private static TextSpec SpecMuted => new(12.5f, NativeLook.Muted, wrap: true);
    private static TextSpec SpecSmall => new(11.5f, NativeLook.Muted);
    private static TextSpec SpecValue => new(14f, NativeLook.Highlight, TextAnchor.MiddleRight, bold: true);

    private static TextSpec SpecFor(GUIStyle style)
    {
        if (ReferenceEquals(style, Theme.Label)) return SpecLabel;
        if (ReferenceEquals(style, Theme.Muted_)) return SpecMuted;
        if (ReferenceEquals(style, Theme.Small)) return SpecSmall;
        if (ReferenceEquals(style, Theme.Value)) return SpecValue;
        if (ReferenceEquals(style, Theme.Section)) return new TextSpec(13f, NativeLook.Text, bold: true, heading: true, upper: true);
        if (ReferenceEquals(style, Theme.Title)) return new TextSpec(22f, NativeLook.Text, heading: true, upper: true);
        if (ReferenceEquals(style, Theme.Status)) return new TextSpec(12.5f, NativeLook.Text);
        if (ReferenceEquals(style, Theme.Badge)) return new TextSpec(11f, Theme.Hex(0x0E1116), TextAnchor.MiddleCenter, bold: true);
        if (ReferenceEquals(style, Theme.Chip) || ReferenceEquals(style, Theme.Button) || ReferenceEquals(style, Theme.ChipDanger)
            || ReferenceEquals(style, Theme.Tab) || ReferenceEquals(style, Theme.TabActive))
            return new TextSpec(13f, NativeLook.ButtonText, TextAnchor.MiddleCenter);
        return SpecLabel;
    }

    /// <summary>GUI.Label that renders with the game's font and colours in the native style.</summary>
    internal static void Label(Rect r, string text, GUIStyle style)
    {
        if (!Emit) { GUI.Label(r, text, style); return; }
        NativeCanvas.Text(r, text, SpecFor(style));
    }

    /// <summary>Label with an alignment override (instead of changing the shared style).</summary>
    internal static void Label(Rect r, string text, GUIStyle style, TextAnchor align)
    {
        if (Emit) { NativeCanvas.Text(r, text, SpecFor(style).With(align)); return; }
        var prev = style.alignment;
        style.alignment = align;
        GUI.Label(r, text, style);
        style.alignment = prev;
    }

    /// <summary>Label with a colour override (instead of changing the shared style).</summary>
    internal static void Label(Rect r, string text, GUIStyle style, Color color)
    {
        if (Emit) { NativeCanvas.Text(r, text, SpecFor(style).With(color)); return; }
        var prev = style.normal.textColor;
        style.normal.textColor = color;
        GUI.Label(r, text, style);
        style.normal.textColor = prev;
    }

    /// <summary>Label with alignment and colour overrides.</summary>
    internal static void Label(Rect r, string text, GUIStyle style, TextAnchor align, Color color)
    {
        if (Emit) { NativeCanvas.Text(r, text, SpecFor(style).With(align).With(color)); return; }
        var prevA = style.alignment;
        var prevC = style.normal.textColor;
        style.alignment = align;
        style.normal.textColor = color;
        GUI.Label(r, text, style);
        style.alignment = prevA;
        style.normal.textColor = prevC;
    }

    /// <summary>GUI.Button that follows the menu style (and is keyboard-selectable in the native style).
    /// Theme.TabActive draws as selected, Theme.ChipDanger as a destructive action.</summary>
    internal static bool Button(Rect r, string label, GUIStyle style)
    {
        if (!_native) return GUI.Button(r, label, style);
        return NativeButton(r, label, true, ReferenceEquals(style, Theme.TabActive), ReferenceEquals(style, Theme.ChipDanger));
    }

    /// <summary>Registers a selectable rect with keyboard navigation (native style, inside the tab content).
    /// <paramref name="activated"/> is true once when the user presses Enter / A on it.</summary>
    internal static void NavItem(Rect r, NavKind kind, out bool focused, out bool activated) => NavItem(r, kind, out focused, out activated, out _);

    /// <summary><see cref="NavItem(Rect, NavKind, out bool, out bool)"/> with the left/right step (-1 / +1) for
    /// switches and sliders.</summary>
    internal static void NavItem(Rect r, NavKind kind, out bool focused, out bool activated, out int side)
    {
        focused = activated = false;
        side = 0;
        if (_native && _inContent) MenuNav.Register(r, kind, out focused, out activated, out side);
    }

    /// <summary>The game's selection bar behind a row (native style only).</summary>
    internal static void SelectionBar(Rect r)
    {
        if (Emit) NativeCanvas.Fill(r, NativeLook.Highlight, NativeLook.HighlightShape, Depth.Mid);
    }

    private static bool NativeButton(Rect r, string label, bool enabled, bool selected, bool danger)
    {
        bool focused = false, act = false;
        if (_inContent) MenuNav.Register(r, NavKind.Button, out focused, out act, out _);
        if (Emit)
        {
            bool lit = focused || selected;
            if (lit) NativeCanvas.Fill(r, selected && !focused ? Dim(NativeLook.Highlight, 0.8f) : NativeLook.Highlight, NativeLook.HighlightShape, Depth.Mid);
            else NativeCanvas.Fill(r, danger ? new Color(NativeLook.Danger.r, NativeLook.Danger.g, NativeLook.Danger.b, 0.28f) : NativeLook.Button, NativeLook.ButtonShape, Depth.Front);
            NativeCanvas.Text(new Rect(r.x + 4f, r.y, r.width - 8f, r.height), label,
                new TextSpec(13f, lit ? NativeLook.HighlightText : danger ? Theme.Hex(0xFFB4AE) : NativeLook.ButtonText, TextAnchor.MiddleCenter, bold: lit));
        }
        bool clicked = (GUI.Button(r, GUIContent.none, Theme.Invisible) || act) && enabled;
        if (clicked) NativeLook.PlayPress();
        return clicked;
    }

    private static Color Dim(Color c, float k) => new(c.r * k, c.g * k, c.b * k, c.a);

    /// <summary>Text height for wrapped text, measured the way the active style will draw it.</summary>
    private static float WrappedHeight(string text, float width)
    {
        if (_native)
        {
            try
            {
                var h = NativeCanvas.MeasureHeight(text, width, SpecMuted);
                if (h.HasValue) return h.Value;
            }
            catch (Exception e) { NativeStyle.Fail("measure", e); }
        }
        return Theme.Muted_.CalcHeight(new GUIContent(text), width);
    }

    /// <summary>A labelled single-line text field row.</summary>
    internal static string TextRow(string label, string value, int maxLength = 64)
    {
        var row = new Rect(Inset - 6f, _y - 2f, IW + 12f, 32f);
        bool hit = MenuUx.Note(label);
        if (!_native)
        {
            if (hit) MenuUx.Highlight(row);
            GUI.Label(new Rect(Inset + 4f, _y, 150f, 28f), label, Theme.Label);
            string v0 = GUI.TextField(new Rect(Inset + 156f, _y, IW - 156f, 28f), value ?? "", maxLength, Theme.TextField);
            _y += 34f;
            return v0;
        }
        NavItem(row, NavKind.Text, out bool focused, out bool act);
        if (focused) SelectionBar(row);
        else if (hit) MenuUx.Highlight(row);
        Label(new Rect(Inset + 4f, _y, 150f, 28f), label, Theme.Label, focused ? NativeLook.HighlightText : NativeLook.Text);
        string name = "otp.textrow." + _textRows++;
        GUI.SetNextControlName(name);
        string v = GUI.TextField(new Rect(Inset + 156f, _y, IW - 156f, 28f), value ?? "", maxLength, Theme.TextField);
        if (act) GUI.FocusControl(name);
        _y += 34f;
        return v;
    }

    internal static void Card(string title, string subtitle, Action body)
    {
        float top = _y;
        string key = MenuUx.CardKey(_drawMode, _drawTab, title);
        float h = CardH.TryGetValue(key, out var cached) ? cached : 80f;
        if (!_native) GUI.Box(new Rect(0f, top, _cw, h), GUIContent.none, Theme.CardBox);
        else if (Emit) NativeCanvas.Fill(new Rect(0f, top, _cw, h), NativeLook.Card, NativeLook.RoundMedium, Depth.Back);
        // Advanced pages start with only their first card open (the menu settings page stays open).
        bool defaultOpen = _cardIndex++ == 0 || _drawTab == SettingsTab || !MenuUx.FoldByDefault;
        bool open = MenuUx.CardHeader(key, title, new Rect(0f, top, _cw, h), Inset, IW, defaultOpen);
        _y += 32f;
        if (open)
        {
            var prev = GUI.color;
            GUI.color = MenuUx.CardTint(key, prev);
            try
            {
                if (!string.IsNullOrEmpty(subtitle)) Small(subtitle);
                body();
            }
            finally { GUI.color = prev; }
            _y += 8f;
        }
        else _y = top + 38f; // folded: title row only
        if (!MenuUx.Indexing) CardH[key] = _y - top;
        _y += 10f;
    }

    internal static void Banner(string text, Color color)
    {
        MenuUx.Note(text);
        float th = WrappedHeight(text, _cw - 36f);
        var r = new Rect(0f, _y, _cw, th + 16f);
        if (_native)
        {
            if (Emit)
            {
                NativeCanvas.Fill(r, new Color(color.r, color.g, color.b, 0.18f), NativeLook.RoundMedium, Depth.Back);
                NativeCanvas.Fill(new Rect(r.x, r.y, 4f, r.height), color, NativeLook.Square, Depth.Front);
                NativeCanvas.Text(new Rect(16f, r.y + 8f, _cw - 32f, th), text, SpecMuted.With(NativeLook.Text));
            }
            _y += r.height + 10f;
            return;
        }
        Theme.Fill(r, new Color(color.r, color.g, color.b, 0.16f), 8);
        Theme.Fill(new Rect(r.x, r.y, 4f, r.height), color, 2);
        var prev = Theme.Muted_.normal.textColor;
        Theme.Muted_.normal.textColor = Theme.Text;
        GUI.Label(new Rect(16f, r.y + 8f, _cw - 32f, th), text, Theme.Muted_);
        Theme.Muted_.normal.textColor = prev;
        _y += r.height + 10f;
    }

    internal static void Small(string text)
    {
        MenuUx.Note(text);
        float th = WrappedHeight(text, IW);
        Label(new Rect(Inset, _y, IW, th), text, Theme.Muted_);
        _y += th + 6f;
    }

    internal static void Switch(ConfigEntry<bool> entry, string label, string hint, bool enabled)
    {
        float rh = hint == null ? 30f : 42f;
        var row = new Rect(Inset - 6f, _y, IW + 12f, rh);
        var prev = GUI.color;
        if (!enabled) GUI.color = new Color(1f, 1f, 1f, 0.45f);
        bool hit = MenuUx.Note(label, hint, entry?.Description?.Description);
        if (_native)
        {
            NativeSwitch(entry, label, hint, row, rh, hit, enabled);
            GUI.color = prev;
            _y += rh + 2f;
            return;
        }
        if (hit) MenuUx.Highlight(row);
        else if (row.Contains(Event.current.mousePosition)) Theme.Fill(row, Theme.CardHover, 6);

        GUI.Label(new Rect(Inset + 4f, _y + (hint == null ? 4f : 2f), IW - 70f, 22f), label, Theme.Label);
        if (hint != null) GUI.Label(new Rect(Inset + 4f, _y + 21f, IW - 70f, 18f), hint, Theme.Small);

        var track = new Rect(Inset + IW - 44f, _y + (rh - 22f) / 2f, 42f, 22f);
        Theme.Fill(track, entry.Value ? Theme.Accent : Theme.Off, 11);
        Theme.Fill(new Rect(entry.Value ? track.xMax - 20f : track.x + 2f, track.y + 2f, 18f, 18f), Theme.Text, 9);

        // A greyed-out switch (host only, physics only, parent off) can't be flipped.
        if (GUI.Button(row, MenuUx.TipContent(entry, hint), Theme.Invisible) && enabled) entry.Value = !entry.Value;
        GUI.color = prev;
        _y += rh + 2f;
    }

    /// <summary>A switch drawn like the game's option rows: label on the left, "&lt; ON &gt;" carousel on the right.</summary>
    private static void NativeSwitch(ConfigEntry<bool> entry, string label, string hint, Rect row, float rh, bool hit, bool enabled)
    {
        int side = 0;
        bool focused = false, act = false;
        if (_inContent) MenuNav.Register(row, NavKind.Switch, out focused, out act, out side);
        if (Emit)
        {
            if (focused) SelectionBar(row);
            else if (hit) MenuUx.Highlight(row);
            var text = focused ? NativeLook.HighlightText : NativeLook.Text;
            NativeCanvas.Text(new Rect(Inset + 4f, _y + (hint == null ? 4f : 2f), IW - 120f, 22f), label, SpecLabel.With(text));
            if (hint != null)
                NativeCanvas.Text(new Rect(Inset + 4f, _y + 21f, IW - 120f, 18f), hint, SpecSmall.With(focused ? Dim(NativeLook.HighlightText, 1f) : NativeLook.Muted));
            var box = new Rect(Inset + IW - 100f, _y + (rh - 24f) / 2f, 100f, 24f);
            bool on = entry.Value;
            var arrows = focused ? NativeLook.HighlightText : NativeLook.Muted;
            var value = focused ? NativeLook.HighlightText : on ? NativeLook.Highlight : NativeLook.Muted;
            NativeCanvas.Text(new Rect(box.x, box.y, 16f, box.height), "<", new TextSpec(14f, arrows, TextAnchor.MiddleCenter, bold: true, heading: true));
            NativeCanvas.Text(new Rect(box.xMax - 16f, box.y, 16f, box.height), ">", new TextSpec(14f, arrows, TextAnchor.MiddleCenter, bold: true, heading: true));
            NativeCanvas.Text(new Rect(box.x + 16f, box.y, box.width - 32f, box.height), on ? "ON" : "OFF",
                new TextSpec(13.5f, value, TextAnchor.MiddleCenter, bold: true, heading: true));
        }
        bool clicked = GUI.Button(row, MenuUx.TipContent(entry, hint), Theme.Invisible) || act;
        if (!enabled) { clicked = false; side = 0; } // greyed out: unavailable, not just dimmed
        bool want = clicked ? !entry.Value : side != 0 ? side > 0 : entry.Value;
        if (want != entry.Value)
        {
            entry.Value = want;
            NativeLook.PlayOption();
        }
    }

    internal static void SliderRow(ConfigEntry<float> entry, string label, float min, float max, string fmt, string suffix, bool enabled, bool percent = false)
    {
        string shown = percent ? (entry.Value * 100f).ToString("0") + "%" : entry.Value.ToString(fmt) + suffix;
        float v = RawSlider(label, entry.Value, min, max, shown, enabled, MenuUx.TipFor(entry, null), entry.Description?.Description);
        if (Mathf.Abs(v - entry.Value) > 0.0001f) entry.Value = Mathf.Round(v * 100f) / 100f;
    }

    private static GUIStyle _thumbHit;

    /// <param name="keyStep">Smallest change the caller keeps (1 for whole numbers): keyboard / gamepad steps are at
    /// least this big, or the caller's rounding would undo them and the slider couldn't move without a mouse.</param>
    internal static float RawSlider(string label, float value, float min, float max, string shown, bool enabled, string tooltip = null, string searchText = null,
        float keyStep = 0f)
    {
        bool hit = MenuUx.Note(label, searchText);
        if (_native) return NativeSlider(label, value, min, max, shown, enabled, tooltip, hit, keyStep);
        if (hit) MenuUx.Highlight(new Rect(Inset - 6f, _y - 1f, IW + 12f, 30f));
        var prev = GUI.color;
        if (!enabled) GUI.color = new Color(1f, 1f, 1f, 0.45f);
        GUI.Label(new Rect(Inset + 4f, _y, 190f, 28f), label, Theme.Label);
        MenuUx.Tooltip(new Rect(Inset, _y, 196f, 28f), tooltip);
        GUI.Label(new Rect(Inset + IW - 80f, _y, 80f, 28f), shown, Theme.Value);

        var track = new Rect(Inset + 200f, _y + 11f, IW - 290f, 6f);
        float t = Mathf.InverseLerp(min, max, value);
        Theme.Fill(track, Theme.Off, 3);
        Theme.Fill(new Rect(track.x, track.y, track.width * t, track.height), Theme.AccentDim, 3);
        float v = GUI.HorizontalSlider(new Rect(track.x - 7f, _y + 7f, track.width + 14f, 14f), value, min, max, Theme.Slider, Theme.Thumb);
        GUI.color = prev;
        _y += 30f;
        return enabled ? v : value;
    }

    private static float NativeSlider(string label, float value, float min, float max, string shown, bool enabled, string tooltip, bool hit, float keyStep)
    {
        var row = new Rect(Inset - 6f, _y - 1f, IW + 12f, 30f);
        var prev = GUI.color;
        if (!enabled) GUI.color = new Color(1f, 1f, 1f, 0.45f);
        int side = 0;
        bool focused = false;
        if (_inContent) MenuNav.Register(row, NavKind.Slider, out focused, out _, out side);
        var track = new Rect(Inset + 200f, _y + 11f, IW - 290f, 6f);
        float t = Mathf.InverseLerp(min, max, value);
        if (Emit)
        {
            if (focused) SelectionBar(row);
            else if (hit) MenuUx.Highlight(row);
            var text = focused ? NativeLook.HighlightText : NativeLook.Text;
            NativeCanvas.Text(new Rect(Inset + 4f, _y, 190f, 28f), label, SpecLabel.With(text));
            NativeCanvas.Text(new Rect(Inset + IW - 80f, _y, 80f, 28f), shown, SpecValue.With(focused ? NativeLook.HighlightText : NativeLook.Highlight));
            var fill = focused ? NativeLook.HighlightText : NativeLook.Highlight;
            NativeCanvas.Fill(track, focused ? new Color(0f, 0f, 0f, 0.25f) : NativeLook.Track, NativeLook.TrackShape, Depth.Front);
            if (t > 0.001f) NativeCanvas.Fill(new Rect(track.x, track.y, Mathf.Max(track.height, track.width * t), track.height), fill, NativeLook.FillShape, Depth.Front);
            NativeCanvas.Fill(new Rect(track.x + track.width * t - 7f, _y + 7f, 14f, 14f), focused ? NativeLook.HighlightText : NativeLook.Text, NativeLook.KnobShape, Depth.Front);
        }
        MenuUx.Tooltip(new Rect(Inset, _y, 196f, 28f), tooltip);
        _thumbHit ??= new GUIStyle { fixedWidth = 14, fixedHeight = 14 };
        float v = GUI.HorizontalSlider(new Rect(track.x - 7f, _y + 7f, track.width + 14f, 14f), value, min, max, Theme.Slider, _thumbHit);
        if (side != 0 && enabled)
        {
            float step = (max - min) / (ShiftHeld() ? 100f : 20f);
            if (keyStep > 0f) step = ShiftHeld() ? keyStep : Mathf.Max(keyStep, Mathf.Round(step / keyStep) * keyStep);
            v = Mathf.Clamp(value + side * step, min, max);
            NativeLook.PlaySlider();
        }
        GUI.color = prev;
        _y += 30f;
        return enabled ? v : value;
    }

    private static bool ShiftHeld()
    {
        try
        {
            var kb = UnityEngine.InputSystem.Keyboard.current;
            return kb != null && kb.shiftKey.isPressed;
        }
        catch { return false; }
    }

    internal static void KeyValue(string key, string value)
    {
        if (MenuUx.Note(key)) MenuUx.Highlight(new Rect(Inset - 6f, _y, IW + 12f, 22f));
        Label(new Rect(Inset + 4f, _y, 176f, 22f), key, Theme.Muted_);
        Label(new Rect(Inset + 180f, _y, IW - 184f, 22f), value, Theme.Label);
        _y += 24f;
    }

    internal static void Bar(string label, float fraction, Color color, string text)
    {
        MenuUx.Note(label);
        Label(new Rect(Inset + 4f, _y, 176f, 22f), label, Theme.Muted_);
        var track = new Rect(Inset + 180f, _y + 7f, IW - 250f, 8f);
        if (_native)
        {
            if (Emit)
            {
                NativeCanvas.Fill(track, NativeLook.Track, NativeLook.TrackShape, Depth.Front);
                float f = Mathf.Clamp01(fraction);
                if (f > 0.001f) NativeCanvas.Fill(new Rect(track.x, track.y, Mathf.Max(track.height, track.width * f), track.height), color, NativeLook.FillShape, Depth.Front);
            }
        }
        else
        {
            Theme.Fill(track, Theme.Off, 4);
            Theme.Fill(new Rect(track.x, track.y, track.width * Mathf.Clamp01(fraction), track.height), color, 4);
        }
        Label(new Rect(Inset + IW - 60f, _y, 60f, 22f), text, Theme.Value);
        _y += 24f;
    }

    private static void Legend(ServerStatus status, string name, string meaning)
    {
        var c = RowStyler.ColorFor(status);
        Theme.Fill(new Rect(Inset + 4f, _y + 4f, 6f, 16f), c, 2);
        Theme.Fill(new Rect(Inset + 14f, _y + 4f, 40f, 16f), new Color(c.r, c.g, c.b, 0.25f), 3);
        Label(new Rect(Inset + 64f, _y, 120f, 24f), name, Theme.Label);
        Label(new Rect(Inset + 184f, _y, IW - 184f, 24f), meaning, Theme.Muted_);
        _y += 26f;
    }

    internal static void Chips(int columns, params (string label, Action action, bool enabled)[] items)
    {
        const float gap = 6f;
        float w = (IW - gap * (columns - 1)) / columns;
        for (int i = 0; i < items.Length; i++)
        {
            int col = i % columns;
            if (i > 0 && col == 0) _y += 36f;
            var r = new Rect(Inset + col * (w + gap), _y, w, 30f);
            bool hit = MenuUx.Note(items[i].label);
            if (ChipButton(r, items[i].label, items[i].enabled)) items[i].action();
            if (hit) Theme.Fill(new Rect(r.x + 6f, r.yMax - 3f, r.width - 12f, 2f), Theme.Accent, 1);
        }
        _y += 36f;
    }

    internal static bool ChipButton(Rect r, string label, bool enabled)
    {
        var prev = GUI.color;
        if (!enabled) GUI.color = new Color(1f, 1f, 1f, 0.35f);
        bool clicked = _native ? NativeButton(r, label, enabled, false, false) : GUI.Button(r, label, Theme.Chip) && enabled;
        GUI.color = prev;
        return clicked;
    }
}
