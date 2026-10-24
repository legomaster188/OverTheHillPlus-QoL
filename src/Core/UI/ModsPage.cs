using System;
using System.Collections.Generic;
using OverTheHillPlus.Features.Public;
using OverTheHillPlus.UI.GameMenu;
using OverTheHillPlus.UI.Native;
using UnityEngine;
using UnityEngine.EventSystems;

namespace OverTheHillPlus.UI;

// After the namespace line on purpose: the game has its own global ModManager (mod.io).
using OverTheHillPlus.Core.Manager;

/// <summary>
/// The MODS page's full view: the mod manager in a window of its own, in the game's style (Server Browser > Create).
/// Normally MODS opens inside the game's MOD BROWSER with the game's own settings widgets (UI/GameMenu); this window
/// is what shows when that can't be done (switched off with [Core] GameMenus, the browser's parts or the settings
/// widgets not found after a game update, a page that failed). It is only that fallback. A window with the big title
/// "MODS", a tab bar (INSTALLED first; more tabs through <see cref="RegisterTab"/>) with [Q] [E] keycaps, game-style
/// rows (UI/SimpleMenu: label left, "◂ value ▸" right, the selected row filled with the game's yellow) and a footer of
/// keycap hints. INSTALLED lists every registered mod with its version, a CLIENT / HOST / SERVER badge (plus PRIVATE) and an ON/OFF switch
/// (takes effect after a restart); Enter or a click opens the mod's page (<see cref="ModPageView"/>), Esc / B goes
/// back, and closes the page from the list (back to the ESC menu when it was opened from there).
///
/// <see cref="Open"/> (the ESC menu's MODS entry, [Core] OpenKey (F7), a mod's own button) tries the MOD BROWSER
/// first and opens this window when it can't. Open/close mechanics (cursor, the game's EventSystem, the Terminal input map, so no driving
/// while the page is up) are CoreBehaviour's; this class draws the window (both menu styles, like the widgets).
/// </summary>
internal static class ModsPage
{
    private const float W = 640f, H = 720f, HeaderH = 64f, TabsH = 38f, FooterH = 34f, Pad = 16f;

    /// <summary>The list of installed mods (always the first tab).</summary>
    internal const string InstalledTab = "Installed";

    private sealed class TabEntry
    {
        public string Name;
        public Action Draw;
    }

    private static readonly List<TabEntry> Tabs = new() { new TabEntry { Name = InstalledTab, Draw = DrawInstalled } };
    private static int _tab;
    // The mod whose page is open (null: the tabs), and the list row that opened it (focus comes back there).
    private static string _modId;
    private static int _listFocus = -1;
    // The page was opened straight on a mod's page (Open(modId), e.g. from Create Server): Esc / B closes it, back
    // to where it was opened from, instead of going to the list.
    private static bool _closeOnBack;

    // ---------------------------------------------------------------- API

    /// <summary>True while MODS is open: this window, or the MOD BROWSER with our tabs in it.</summary>
    public static bool IsOpen => IsClassicOpen || GameModsTab.Attached;

    /// <summary>True while this window (the full view) is open.</summary>
    internal static bool IsClassicOpen => CoreBehaviour.MenuOpen;

    /// <summary>Opens MODS on the list, or on the page of mod <paramref name="modId"/> (the list if it isn't
    /// registered): in the game's MOD BROWSER when it can be opened, otherwise in this window.</summary>
    public static void Open(string modId = null)
    {
        if (!IsClassicOpen && GameModsScreen.Open(modId)) return;
        OpenClassic(modId);
    }

    /// <summary>Opens this window (the full view) on the list or on mod <paramref name="modId"/>'s page.</summary>
    internal static void OpenClassic(string modId = null)
    {
        ShowMod(modId, false);
        _closeOnBack = _modId != null;
        CoreBehaviour.SetMenuOpen(true);
    }

    /// <summary>The names of the tabs (INSTALLED first), for the MOD BROWSER's MODS buttons.</summary>
    internal static IReadOnlyList<string> TabNames
    {
        get
        {
            if (TabNameList.Count != Tabs.Count)
            {
                TabNameList.Clear();
                foreach (var t in Tabs) TabNameList.Add(t.Name);
            }
            return TabNameList;
        }
    }

    private static readonly List<string> TabNameList = new();

    /// <summary>Draws tab <paramref name="index"/>'s rows (a tab added with <see cref="RegisterTab"/>; INSTALLED is
    /// <see cref="DrawInstalledList"/>). A tab that throws shows the error.</summary>
    internal static void DrawTab(int index)
    {
        if (index < 0 || index >= Tabs.Count) return;
        var tab = Tabs[index];
        try { tab.Draw(); }
        catch (Exception e) when (!(_native && IsNativeError(e))) { DebugLog.Error("core", e, "ModsPage.DrawTab"); SimpleMenu.Note($"<color=#E5534B>{tab.Name} failed: {e.Message}</color>"); }
    }

    /// <summary>Adds a tab after INSTALLED (e.g. AVAILABLE); <paramref name="draw"/> draws its rows. Same name again: replaces the draw.</summary>
    public static void RegisterTab(string name, Action draw)
    {
        if (string.IsNullOrEmpty(name) || draw == null) return;
        var existing = Tabs.Find(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));
        if (existing != null) existing.Draw = draw;
        else Tabs.Add(new TabEntry { Name = name, Draw = draw });
    }

    /// <summary>[Core] OpenKey: opens MODS, or closes it (this window, or the MOD BROWSER while a MODS tab shows in it).</summary>
    internal static void Toggle()
    {
        if (IsClassicOpen) CoreBehaviour.SetMenuOpen(false);
        else if (GameModsTab.Active) GameModsScreen.Close();
        else Open();
    }

    /// <summary>Closes MODS: this window when it is open, otherwise the MOD BROWSER we are in.</summary>
    internal static void Close()
    {
        if (IsClassicOpen) CoreBehaviour.SetMenuOpen(false);
        else GameModsScreen.Close();
    }

    /// <summary>Esc / B: from a mod's page back to the list (or closed, when the page was opened on that mod's page);
    /// from the list, closes the page.</summary>
    internal static void Back()
    {
        if (_modId != null && !_closeOnBack)
        {
            ShowMod(null, true);
            NativeLook.PlayTab();
            return;
        }
        Close();
    }

    private static ModInfo CurrentMod() => _modId == null ? null : ModManager.Find(_modId);

    private static void ShowMod(string modId, bool backToList)
    {
        var m = ModManager.Find(modId);
        bool keyboard = MenuNav.UsedKeyboardRecently;
        if (m != null && _modId == null) _listFocus = MenuNav.Focus;
        _modId = m?.Id;
        MenuNav.Reset(keyboard);
        // Back on the list: the row of the mod that was open stays selected.
        if (backToList && keyboard && _listFocus >= 0) MenuNav.Focus = _listFocus;
    }

    private static void SelectTab(int i)
    {
        if (Tabs.Count == 0) return;
        _tab = ((i % Tabs.Count) + Tabs.Count) % Tabs.Count;
        _modId = null;
        _closeOnBack = false;
        MenuNav.Reset(MenuNav.UsedKeyboardRecently);
        NativeLook.PlayTab();
    }

    // ---------------------------------------------------------------- the game's screen underneath

    private static StateUIPoppableState _under;
    private static CanvasGroup _underGroup;
    private static GameObject _underButton;
    private static bool _underCanPop = true;

    /// <summary>The ESC menu's MODS button: MODS in the MOD BROWSER, or this window over the ESC menu (which hides
    /// until the window closes).</summary>
    internal static void OpenFromPauseMenu(StatePauseMenu menu, GameObject button)
    {
        if (IsOpen) return;
        if (GameModsScreen.Open(null)) return;
        OpenOver(menu, menu != null && menu.contextUIPauseMenu != null ? menu.contextUIPauseMenu.CanvasGroup : null, button, null);
    }

    /// <summary>
    /// Opens this window over a screen of the game (the ESC menu, the MOD BROWSER for a mod's full view): the screen
    /// hides (<paramref name="group"/>) and keeps Esc / B away from it until the window closes; then
    /// <paramref name="reselect"/> gets the selection back.
    /// </summary>
    internal static void OpenOver(StateUIPoppableState state, CanvasGroup group, GameObject reselect, string modId)
    {
        if (IsClassicOpen) return;
        OpenClassic(modId);
        if (!IsClassicOpen || state == null) return;
        _under = state;
        _underButton = reselect;
        try
        {
            _underGroup = group;
            if (_underGroup != null) _underGroup.alpha = 0f;
            // Esc / B belong to the window while it is open: the screen must not close underneath it.
            _underCanPop = state.CanPop;
            state.CanPop = false;
        }
        catch (Exception e) { DebugLog.Error("core", e, "ModsPage.OpenOver"); Core.CorePlugin.Logger?.LogDebug("MODS: hiding the screen underneath: " + e.Message); }
    }

    /// <summary>Called by CoreBehaviour after the window closed (cursor, input and EventSystem already given back).</summary>
    internal static void OnClosed()
    {
        _modId = null;
        _closeOnBack = false;
        if (_under == null) return;
        try
        {
            if (_underGroup != null) _underGroup.alpha = 1f;
            if (!_under.WasCollected) _under.CanPop = _underCanPop;
            // Back on the screen with its MODS button selected (keyboard / gamepad carry on from there).
            var es = EventSystem.current;
            if (es != null && es.enabled && _underButton != null && _underButton.activeInHierarchy) es.SetSelectedGameObject(_underButton);
        }
        catch (Exception e) { DebugLog.Error("core", e, "ModsPage.OnClosed"); Core.CorePlugin.Logger?.LogDebug("MODS: restoring the screen underneath: " + e.Message); }
        _under = null;
        _underGroup = null;
        _underButton = null;
    }

    /// <summary>The screen underneath fades in when it opens: keep it hidden while the window is up.</summary>
    private static void KeepPauseMenuHidden()
    {
        if (_underGroup == null) return;
        try
        {
            if (_underGroup != null && _underGroup.alpha > 0f) _underGroup.alpha = 0f;
        }
        catch (Exception ex) { DebugLog.Error("core", ex, "ModsPage.KeepPauseMenuHidden"); _underGroup = null; }
    }

    // ---------------------------------------------------------------- INSTALLED

    private static readonly List<ModInfo> Sorted = new();

    // The INSTALLED list's search and filter (kept while the game runs).
    private static string _search = "";
    private static ModListModel.Show _show = ModListModel.Show.All;

    /// <summary>Core first, then by name.</summary>
    private static List<ModInfo> SortedMods()
    {
        Sorted.Clear();
        Sorted.AddRange(ModManager.All);
        ModListModel.Sort(Sorted, ModPageView.IsCore, m => m.Name ?? m.Id);
        return Sorted;
    }

    private static void DrawInstalled()
    {
        var open = DrawInstalledList();
        if (open != null) ShowMod(open.Id, false);
    }

    /// <summary>
    /// The INSTALLED list (both looks draw it): every mod with its badge, detail and switch, then the plugins of other
    /// creators. Returns the mod whose row was pressed (its page should open), or null.
    /// </summary>
    internal static ModInfo DrawInstalledList()
    {
        var mods = SortedMods();
        if (ModManager.Blocked) Menu.Banner(ModManager.BlockedText, Theme.Bad); // the old plugin is loaded: nothing runs
        if (mods.Count == 0)
        {
            SimpleMenu.Note("No mods are installed.");
            return null;
        }
        ModInfo open = null;
        // Finding a mod in a long list: a search box (name, id or description) and an on / off / maturity filter.
        _search = SimpleMenu.Text("Search", _search ?? "", 40, "Type part of a mod's name or what it does.");
        SimpleMenu.Choice("Show", "Which mods the list shows.", (int)_show, ModListModel.ShowLabels, i => _show = (ModListModel.Show)i);
        int listed = 0;
        foreach (var m in mods)
        {
            bool core = ModPageView.IsCore(m);
            bool on = core || ModManager.SwitchedOn(m.Id);
            bool apiMod = ThirdParty.IsApiMod(m.Id);
            var maturity = ModMaturity.Of(m, !apiMod);
            if (!ModListModel.Matches(_search, m.Name, m.Id, m.Description, _show, on, maturity)) continue;
            listed++;
            string value = ModListModel.SwitchValue(core, ModStatus.SwitchText(on, ModManager.PendingRestart(m.Id)));
            // "update available" once the AVAILABLE tab fetched the list
            string detail = ModListModel.Detail(m.Version, ModManager.FailedReason(m.Id) != null, PatchReport.Failed(m.Id) > 0,
                AvailableTab.InstalledMark(m), maturity);
            string hint = ModMaturity.Describe(string.IsNullOrEmpty(m.Description) ? "Enter opens its settings." : m.Description, m.Id, maturity);
            string badge = ModListModel.Badge(apiMod, RequiredMods.SideBadge(m.Side), RequiredMods.Badge(m));
            bool failed = ModManager.FailedReason(m.Id) != null;
            // The MOD BROWSER shows the mod as one of its cards: the image from the mod's DLL (card.png).
            var card = new CardInfo
            {
                ModId = m.Id, Seed = m.Id, Title = m.Name ?? m.Id,
                Author = apiMod ? ModCards.OtherAuthor : ModCards.OurAuthor,
                Version = m.Version, Side = apiMod ? RequiredMods.SideBadge(m.Side) : RequiredMods.Badge(m),
                Status = ModCards.InstalledStatus(core, on, ModManager.PendingRestart(m.Id), failed, AvailableTab.UpdateFor(m) != null),
                Maturity = maturity,
            };
            bool pressed = SimpleMenu.ListRow(m.Name ?? m.Id, detail, badge, value, on, hint, !core, out int step, card);
            if (step != 0 && !core)
            {
                ModManager.SetEnabled(m.Id, !on);
                Menu.SetStatus($"{m.Name ?? m.Id}: {(on ? "off" : "on")} after a restart");
            }
            if (pressed) open = m;
        }
        if (listed == 0) SimpleMenu.Note("No mod matches the search.");
        SimpleMenu.Note("Switching a mod on or off takes effect after you restart the game.");
        ThirdPartyRows.Draw(); // plugins of other creators, with an OTHER CREATOR badge
        return open;
    }

    // ---------------------------------------------------------------- frame

    private static bool _native;
    private static int _styleFrame = -1;
    private static int _nativeShownFrame = -100;
    private static Vector2 _pos;
    private static bool _placed;
    private static Vector2 _shownPos;
    private static bool _dragging;
    private static Vector2 _dragOffset;
    private static bool _scrollDrag;
    private static readonly Dictionary<string, Vector2> Scroll = new();
    private static readonly Dictionary<string, Vector2> ShownScroll = new();
    private static readonly Dictionary<string, float> ContentH = new();

    private static bool Emit => NativeCanvas.Emitting;

    /// <summary>Draws the page (CoreBehaviour.OnGUI, every IMGUI event while open).</summary>
    internal static void Draw()
    {
        Theme.Ensure();
        LatchStyle();
        Menu.BeginWindow(_native);
        if (Event.current.type == EventType.Repaint) KeepPauseMenuHidden();
        if (!_native)
        {
            DrawWindow();
            return;
        }
        try { DrawWindow(); }
        catch (Exception e) when (IsNativeError(e))
        {
            DebugLog.Error("core", e, "ModsPage.Draw");
            // Auto-fallback: the classic look takes over from the next frame, the page stays open. Errors that have
            // nothing to do with the native renderer propagate (CoreBehaviour closes the page).
            NativeStyle.Fail("draw", e);
            _native = false;
        }
        finally
        {
            if (NativeCanvas.Emitting) NativeCanvas.EndFrame();
        }
    }

    internal static bool IsNativeError(Exception e)
    {
        string st = e.StackTrace ?? "";
        return st.Contains("UI.Native.") || st.Contains("NativeCanvas") || st.Contains("Menu.Native") || st.Contains("ModsPage.Native");
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
                DebugLog.Error("core", e, "ModsPage.LatchStyle");
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
        MenuUx.SearchShown = false; // no search box on this page: nothing highlights matches
        float scale = MenuUx.MenuScale(Mathf.Max(1f, Screen.height / 1080f));
        GUI.matrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, new Vector3(scale, scale, 1f));

        float sw = Screen.width / scale, sh = Screen.height / scale;
        if (!_placed)
        {
            _placed = true;
            _pos = new Vector2(Mathf.Round((sw - W) / 2f), Mathf.Round(Mathf.Max(20f, (sh - H) / 2f)));
        }
        _pos.x = Mathf.Clamp(_pos.x, 0f, Mathf.Max(0f, sw - W));
        _pos.y = Mathf.Clamp(_pos.y, 0f, Mathf.Max(0f, sh - 80f));
        float h = Mathf.Min(H, sh - _pos.y - 10f);

        // Canvas changes made during OnGUI show up one frame later, so in the native style IMGUI repaints at the
        // position (and scroll) the canvas currently shows; the canvas gets the live values. Keeps text fields glued
        // to the native panels while dragging or scrolling.
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
            if (ts != 0 && _modId == null && Tabs.Count > 1) SelectTab(_tab + ts);
            MenuNav.TakeModeStep();
            if (MenuNav.TakeViewToggle() && _modId != null)
            {
                ModPageView.ToggleAdvanced();
                NativeLook.PlayTab();
            }
        }

        var close = CloseRect(win);
        HandleDrag(new Rect(win.x, win.y, close.x - win.x - 8f, HeaderH));
        if (_native) NativeWindowBack(win);
        else GUI.Box(win, GUIContent.none, Theme.Window);
        DrawHeader(win, close);
        DrawTabs(new Rect(win.x + Pad, win.y + HeaderH, W - Pad * 2, TabsH));

        var area = new Rect(win.x + Pad, win.y + HeaderH + TabsH + 10f, W - Pad * 2, h - HeaderH - TabsH - FooterH - 18f);
        var mod = CurrentMod();
        if (_modId != null && mod == null) _modId = null;
        _tab = Mathf.Clamp(_tab, 0, Mathf.Max(0, Tabs.Count - 1));
        string page = mod != null ? "mod/" + mod.Id : "tab/" + (Tabs.Count == 0 ? "" : Tabs[_tab].Name);
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
        Menu.BeginContent(area.width - 18f);
        try { DrawContent(mod); }
        finally { ContentH[page] = Menu.EndContent() + 6f; }
        if (_native && repaint) NativeCanvas.EndContent();
        GUI.EndScrollView();
        if (_native)
        {
            Scroll.TryGetValue(page, out var now);
            MenuNav.EndFrame(now.y, area.height);
            NativeScrollbar(area, ContentH[page], now.y);
        }

        DrawFooter(new Rect(win.x, win.yMax - FooterH, W, FooterH), mod);
        NativeCanvas.BeginOverlay(); // tooltips above everything (no-op in the classic style)
        MenuUx.EndFrame(area);
    }

    private static void DrawContent(ModInfo mod)
    {
        if (RequiredModsPrompt.Draw()) return; // a server's "needs mods" prompt, over the tabs and mod pages
        if (mod != null)
        {
            ModPageView.Draw(mod);
            return;
        }
        if (Tabs.Count == 0) return;
        var tab = Tabs[_tab];
        try { tab.Draw(); }
        catch (Exception e) when (!(_native && IsNativeError(e))) { DebugLog.Error("core", e, "ModsPage.DrawContent"); Menu.Small($"<color=#E5534B>{tab.Name} failed: {e.Message}</color>"); }
    }

    // ---------------------------------------------------------------- window parts

    private static Rect CloseRect(Rect win) => new(win.xMax - Pad - 32f, win.y + 17f, 32f, 30f);

    private static void NativeWindowBack(Rect win)
    {
        if (!Emit) return;
        NativeCanvas.Fill(win, NativeLook.Panel, NativeLook.PanelShape, Depth.Back);
        // Header band and the game's yellow rule under it.
        NativeCanvas.Fill(new Rect(win.x + 6f, win.y + 6f, win.width - 12f, HeaderH - 10f), NativeLook.HeaderBand, NativeLook.RoundMedium, Depth.Back);
        NativeCanvas.Fill(new Rect(win.x + Pad, win.y + HeaderH - 3f, win.width - Pad * 2, 2f), SimpleMenu.Yellow, NativeLook.Square, Depth.Front);
    }

    private static void DrawHeader(Rect win, Rect close)
    {
        float titleW = close.x - win.x - Pad - 8f;
        var mod = CurrentMod();
        int count = ModManager.All.Count;
        string sub = mod == null
            ? $"{count} mod{(count == 1 ? "" : "s")} installed"
            : $"{mod.Name ?? mod.Id}{(string.IsNullOrEmpty(mod.Version) ? "" : "  ·  v" + mod.Version)}";
        if (_native)
        {
            // The game's screen titles: big, thin, upper case.
            if (Emit)
                NativeCanvas.Text(new Rect(win.x + Pad, win.y + 6f, titleW, 34f), "MODS",
                    new TextSpec(30f, NativeLook.Text, heading: true, upper: true));
            Menu.Label(new Rect(win.x + Pad, win.y + 39f, titleW, 16f), sub, Theme.Small);
        }
        else
        {
            GUI.Label(new Rect(win.x + Pad, win.y + 10f, titleW, 26f), "MODS", Theme.Title);
            GUI.Label(new Rect(win.x + Pad, win.y + 38f, titleW, 16f), sub, Theme.Small);
        }
        bool closeClicked = _native ? Menu.Button(close, "×", Theme.Button) : GUI.Button(close, "×", Theme.Button);
        if (closeClicked) Menu.CloseRequested = true;
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

    /// <summary>The tab bar: the tabs with [Q] [E] keycaps at the ends; on a mod's page, BACK and the mod's name.</summary>
    private static void DrawTabs(Rect bar)
    {
        if (_native) { if (Emit) NativeCanvas.Fill(bar, Theme.Hex(0x000000, 0.28f), NativeLook.RoundSmall, Depth.Back); }
        else Theme.Fill(bar, Theme.Hex(0x0E1218), 8);
        var mod = CurrentMod();
        if (mod != null)
        {
            var back = new Rect(bar.x + 4f, bar.y + 4f, 112f, bar.height - 8f);
            if (TabButton(back, _closeOnBack ? "CLOSE" : "BACK", false, "Esc")) Back();
            var name = new Rect(back.xMax + 6f, bar.y + 4f, bar.xMax - back.xMax - 10f, bar.height - 8f);
            TabButton(name, mod.Name ?? mod.Id, true, null);
            return;
        }
        bool hints = _native && NativeStyle.KeyboardNavOn;
        float side = hints ? 30f : 4f;
        if (hints && Emit)
        {
            SimpleMenu.Keycap(new Rect(bar.x + 5f, bar.y + (TabsH - 20f) / 2f, 20f, 20f), "Q");
            SimpleMenu.Keycap(new Rect(bar.xMax - 25f, bar.y + (TabsH - 20f) / 2f, 20f, 20f), "E");
        }
        int n = Math.Max(1, Tabs.Count);
        float tw = (bar.width - side * 2f) / n;
        for (int i = 0; i < Tabs.Count; i++)
        {
            var r = new Rect(bar.x + side + i * tw + 2f, bar.y + 4f, tw - 4f, bar.height - 8f);
            bool active = i == _tab;
            if (TabButton(r, Tabs[i].Name, active, null) && !active) SelectTab(i);
        }
    }

    /// <summary>One tab: plain upper-case label, the selected one on the highlight bar. <paramref name="key"/> adds a keycap before the label.</summary>
    private static bool TabButton(Rect r, string text, bool active, string key)
    {
        if (!_native)
        {
            string label = key == null ? text.ToUpperInvariant() : $"{text.ToUpperInvariant()} ({key})";
            return GUI.Button(r, label, active ? Theme.TabActive : Theme.Tab);
        }
        if (Emit)
        {
            if (active) NativeCanvas.Fill(r, NativeLook.Highlight, NativeLook.HighlightShape, Depth.Mid);
            var textRect = r;
            if (key != null)
            {
                float kw = SimpleMenu.KeycapWidth(key);
                SimpleMenu.Keycap(new Rect(r.x + 8f, r.y + (r.height - 18f) / 2f, kw, 18f), key);
                textRect = new Rect(r.x + kw + 12f, r.y, r.width - kw - 16f, r.height);
            }
            NativeCanvas.Text(textRect, text, new TextSpec(13.5f, active ? NativeLook.HighlightText : NativeLook.Text,
                TextAnchor.MiddleCenter, bold: true, heading: true, upper: true));
        }
        bool clicked = GUI.Button(r, GUIContent.none, Theme.Invisible);
        if (clicked && !active) NativeLook.PlayPress();
        return clicked;
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

    // ---------------------------------------------------------------- footer

    private static void DrawFooter(Rect r, ModInfo mod)
    {
        if (_native) { if (Emit) NativeCanvas.Fill(new Rect(r.x + Pad, r.y, r.width - Pad * 2, 1f), NativeLook.Line, NativeLook.Square, Depth.Front); }
        else Theme.Fill(new Rect(r.x + Pad, r.y, r.width - Pad * 2, 1f), Theme.Line, 0);
        var text = new Rect(r.x + Pad, r.y + 6f, r.width - Pad * 2, r.height - 8f);
        Color muted = _native ? NativeLook.Muted : Theme.Muted;

        // Priority: a fresh status message, then the selected row's explanation, then key hints.
        if (Menu.TryStatus(out var status))
        {
            Color color = status.StartsWith("Failed") ? Theme.Bad : status.StartsWith("Blocked") ? Theme.Warn : Theme.Good;
            Menu.Label(text, status, Theme.Status, color);
            return;
        }
        if (Menu.TryHint(out var hint))
        {
            Menu.Label(text, hint, Theme.Status, muted);
            return;
        }

        float x = text.x;
        string backLabel = mod != null && !_closeOnBack ? "Back" : _under != null ? "Back to the menu" : "Close";
        x = FooterKeys(x, text, muted, backLabel, "Esc");
        bool nav = _native && NativeStyle.KeyboardNavOn;
        if (mod == null)
        {
            if (nav)
            {
                if (Tabs.Count > 1) x = FooterKeys(x, text, muted, "Tabs", "Q", "E");
                x = FooterKeys(x, text, muted, "On / off", "◂", "▸");
                FooterKeys(x, text, muted, "Settings", "Enter");
            }
            else Menu.Label(new Rect(x, text.y, text.xMax - x, text.height), "Click a mod for its settings", Theme.Status, muted);
            return;
        }
        if (nav)
        {
            x = FooterKeys(x, text, muted, "Change", "Enter");
            if (ModPageView.HasAdvanced(mod)) FooterKeys(x, text, muted, "Advanced", "Tab");
        }
    }

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
        Menu.Label(new Rect(x + 2f, line.y, lw, line.height), label, Theme.Status, color);
        return x + lw + 14f;
    }
}
