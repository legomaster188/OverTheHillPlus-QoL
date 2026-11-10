using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime;
#if MELON
using Il2CppTMPro;
#else
using TMPro;
#endif
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace OverTheHillPlus.UI.GameMenu;

// After the namespace line on purpose: the game has its own global ModManager (mod.io).
using OverTheHillPlus.Core.Manager;

/// <summary>
/// The MODS screen of the game's Settings menu (Settings > MODS, next to GRAPHICS, AUDIO, CONTROLS ...).
///
/// The game's settings screens (decoded from the game: Prefabs/UI/Menus/Settings/*SettingsMenu) are all alike: the big
/// title top left, a column of rows 807 wide under it (Content, at 78 / -141), the blurred background and the key hints
/// bottom right; each is a state pushed on the GameStateManager (StateHUDSettingsMenu, StateLanguageSettingsMenu ...),
/// and the back input pops it. The MODS screen is one of them for real: <see cref="GameModsScreen"/> pushes a
/// StateLanguageSettingsMenu (the smallest one: a single row, nothing else) and this class turns it into MODS. The
/// title reads MODS, the language row is hidden (and its save is skipped), and in its place:
/// <list type="bullet">
/// <item>on the left, the list of mods: copies of the Settings menu's own buttons (<see cref="GamePrefabs.MenuButton"/>),
///   "All mods" first, then core and our mods, the mods of other creators made with our API, "This server" in a
///   session, and "Get more mods" (the installer);</item>
/// <item>beside it, the selected mod's settings as copies of the game's own settings rows (<see cref="GamePage"/>):
///   its ModInfo.DrawSimple page and SimpleMenu rows, run as a recording pass every frame (<see cref="RowRecorder"/>),
///   so every mod's settings show without being rewritten;</item>
/// <item>on the right, when the screen is wide enough, the mod's picture, name, version and the selected row's
///   explanation, in copies of the game's own text.</item>
/// </list>
/// Keyboard, gamepad and mouse work like the game's: up / down along the list, Enter / A or right into the settings,
/// Esc / B back to the list, and from the list back to Settings (the game's own back). Anything that goes wrong (a
/// template missing after a game update, a widget that can't be made) is logged once and the full view (UI/ModsPage)
/// opens instead.
/// </summary>
internal static class GameModsTab
{
    private const float ListWidth = 352f, Gap = 48f, BottomMargin = 112f, DetailsMargin = 73f, DetailsMaxWidth = 470f, DetailsMinWidth = 280f;

    private static StateLanguageSettingsMenu _state;
    private static Transform _root;
    private static GameObject _content;
    private static CanvasGroup _veil;
    private static bool _veilAdded;
    private static TMP_Text _title;
    private static string _titleWas;
    private static RectTransform _listArea, _pageArea;
    private static GamePage _list, _page;
    private static bool _broken;

    // What the page on the right shows: a mod's id, or one of ModsScreenModel's own keys.
    private static string _key = ModsScreenModel.AllKey;
    private static bool _inPage, _closeOnBack, _selectList, _focusPage;
    private static string _openNext;
    private static (string modId, bool closeOnBack)? _later;
    private static bool _canPopTaken;
    private static int _canPopBackFrame = -1;
    private static int _mouseFrame = -100, _typingFrame = -100, _keysFrame = -100, _recordedFrame = -1;

    private static readonly RowRecorder Recorder = new();
    private static readonly List<GamePage.Entry> Entries = new();
    private static readonly List<GamePage.Entry> ListEntries = new();
    private static List<ModsScreenModel.Entry> _listModel = new();
    private static readonly Dictionary<string, RowModel> ListRows = new(StringComparer.OrdinalIgnoreCase);
    private static readonly List<ModsScreenModel.Mod> ModsScratch = new();
    private static readonly List<string> TabsScratch = new();
    private static readonly HashSet<string> Logged = new();

    private static readonly RowModel FullViewRow = new()
    {
        Kind = RowKind.Button, Label = "More settings", Value = "full view",
        Hint = "This mod draws some of its settings itself: the full view shows them. Esc there comes back here.",
    };

    /// <summary>True while the MODS screen is up.</summary>
    internal static bool Attached => _state != null;

    /// <summary>Same as <see cref="Attached"/> (the screen always shows a page).</summary>
    internal static bool Active => _state != null;

    /// <summary>The game state the screen lives in (null: none).</summary>
    internal static StateLanguageSettingsMenu State => _state;

    /// <summary>True after the screen failed once this session: MODS opens the full view from then on.</summary>
    internal static bool Broken => _broken;

    /// <summary>The list never shows cards (the mods are buttons, like the Settings menu).</summary>
    internal static bool ShowsCards => false;

    // ---------------------------------------------------------------- attach / detach

    // A mod's own settings screen shown instead of MODS (null: the MODS screen).
    private static GameScreenSpec _spec;

    /// <summary>The mod's own settings screen this is (null: the MODS screen).</summary>
    internal static GameScreenSpec Screen => _spec;

    /// <summary>Turns the language screen <paramref name="state"/> into MODS, on <paramref name="modId"/>'s page (null: the
    /// list); or, with <paramref name="screen"/>, into that mod's own settings screen on <paramref name="section"/>.</summary>
    internal static void Attach(StateLanguageSettingsMenu state, string modId, bool closeOnBack, GameScreenSpec screen = null, string section = null)
    {
        Detach();
        if (state == null) return;
        try
        {
            if (_broken || !GamePrefabs.Ensure()) { Warn("templates", "the game's settings widgets were not found (" + GamePrefabs.Summary + "); MODS shows the full view"); return; }
            var ctx = state.languageSettingsMenuUIContext;
            var row = ctx != null ? ctx.Language : null;
            if (row == null || row.transform.parent == null) { Warn("parts", "the settings screen's parts were not found; MODS shows the full view"); return; }
            _state = state;
            _spec = screen;
            _content = row.transform.parent.gameObject;
            _root = _content.transform.parent;
            if (_root == null) { Detach(); return; }
            HideContent();
            TakeTitle();
            BuildAreas();
            _list = GamePage.Create(_listArea, "OTH_ModsList", false);
            _list.UpFromFirst = false;
            _list.OwnSides = true;
            _list.ButtonTemplate = GamePrefabs.MenuButton;
            _list.OnInput = OnListInput;
            _page = GamePage.Create(_pageArea, "OTH_ModsPage", false);
            _page.UpFromFirst = false;
            _page.OwnSides = true;
            _page.OnInput = OnPageInput;
            ModDetails.Create(_root, _pageArea);
            if (_spec != null)
            {
                // A mod's own screen: like the game's Settings, the list of sections first, its rows on the right.
                _key = section ?? FirstSection();
                _closeOnBack = false;
                _focusPage = false;
                _selectList = true;
                _inPage = false;
                LogLayoutOnce();
                return;
            }
            var m = ModManager.Find(modId);
            _key = m != null ? m.Id : ModsScreenModel.AllKey;
            _closeOnBack = closeOnBack && m != null;
            _focusPage = m != null;
            _selectList = m == null;
            _inPage = m != null;
            LogLayoutOnce();
        }
        catch (Exception e)
        {
            Warn("attach", "could not make the MODS screen: " + e);
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
        try { _list?.Destroy(); } catch { }
        try { _page?.Destroy(); } catch { }
        try { ModDetails.Destroy(); } catch { }
        try { if (_listArea != null) Object.Destroy(_listArea.gameObject); } catch { }
        try { if (_pageArea != null) Object.Destroy(_pageArea.gameObject); } catch { }
        try
        {
            // The title as the game had it (in case it reuses the screen), and its own text updates again.
            if (_title != null)
            {
                FunTexts.Ours.Remove(_title.Pointer);
                if (_titleWas != null && _title.text != _titleWas) _title.text = _titleWas;
            }
        }
        catch { }
        try
        {
            if (_veil != null && _veilAdded) Object.Destroy(_veil);
            else if (_veil != null) { _veil.alpha = 1f; _veil.interactable = true; _veil.blocksRaycasts = true; }
        }
        catch { }
        _list = _page = null;
        _veil = null;
        _veilAdded = false;
        _title = null;
        _titleWas = null;
        _content = null;
        _root = null;
        _listArea = _pageArea = null;
        GameRow.ByPointer.Clear();
        // Also when a row's Destroy threw above (the screen is gone already): a stale pointer would let a later game
        // input field at the same address have its submit swallowed.
        GameRow.TextFields.Clear();
        _state = null;
        _spec = null;
        _key = ModsScreenModel.AllKey;
        _inPage = _closeOnBack = _selectList = _focusPage = false;
        _openNext = null;
        _later = null;
        ListRows.Clear();
        Recorder.Abort();
    }

    /// <summary>The game's own row column (the language row): invisible, not clickable or selectable.</summary>
    private static void HideContent()
    {
        _veil = _content.GetComponent<CanvasGroup>();
        _veilAdded = _veil == null;
        if (_veilAdded) _veil = _content.AddComponent<CanvasGroup>();
        KeepHidden();
    }

    private static void KeepHidden()
    {
        if (_veil == null) return;
        if (_veil.alpha != 0f) _veil.alpha = 0f;
        if (_veil.interactable) _veil.interactable = false;
        if (_veil.blocksRaycasts) _veil.blocksRaycasts = false;
    }

    /// <summary>The screen's title (LANGUAGE) reads MODS; the game's localization of it is kept off for this screen.</summary>
    private static void TakeTitle()
    {
        var t = _root.Find("Title");
        _title = t != null ? t.GetComponentInChildren<TMP_Text>(true) : null;
        if (_title == null) return;
        _titleWas = _title.text;
        var fun = _title.TryCast<FunTextTMP>();
        if (fun != null) FunTexts.Register(fun); // its localization doesn't write over ours (SetTitle keeps it anyway)
        SetTitle();
    }

    private static void SetTitle()
    {
        string title = _spec != null && !string.IsNullOrEmpty(_spec.Title) ? _spec.Title : "MODS";
        if (_title != null && _title.text != title) _title.text = title;
    }

    /// <summary>The first section of a mod's own screen (its key), or "" when it has none.</summary>
    private static string FirstSection()
    {
        try
        {
            var sections = _spec?.Sections?.Invoke();
            if (sections != null)
                foreach (var e in sections)
                    if (!e.Heading && !string.IsNullOrEmpty(e.Key)) return e.Key;
        }
        catch (Exception e) { DebugLog.Error("core", e, "GameModsTab.FirstSection"); }
        return "";
    }

    /// <summary>
    /// The list and the page, where the game's row column was: the list as wide as the Settings menu's buttons from the
    /// column's left edge, the page as wide as the column after it; both from the column's top down to above the key
    /// hints, stretching with the screen's height.
    /// </summary>
    private static void BuildAreas()
    {
        var crt = _content.transform.TryCast<RectTransform>();
        float left = 78f, top = -141f, rowWidth = 807f;
        if (crt != null)
        {
            if (crt.anchoredPosition.x > 1f) left = crt.anchoredPosition.x;
            if (crt.anchoredPosition.y < -1f) top = crt.anchoredPosition.y;
            if (crt.sizeDelta.x > 100f) rowWidth = crt.sizeDelta.x;
        }
        float listWidth = ListWidth;
        try
        {
            var mrt = GamePrefabs.MenuButton != null ? GamePrefabs.MenuButton.transform.TryCast<RectTransform>() : null;
            if (mrt != null && mrt.sizeDelta.x > 120f) listWidth = mrt.sizeDelta.x;
        }
        catch { }
        _listArea = MakeArea("OTH_ModsListArea", left, top, listWidth);
        _pageArea = MakeArea("OTH_ModsPageArea", left + listWidth + Gap, top, rowWidth);
    }

    private static RectTransform MakeArea(string name, float x, float top, float width)
    {
        var go = new GameObject(name);
        var rt = go.AddComponent<RectTransform>();
        rt.SetParent(_root, false);
        rt.SetSiblingIndex(_content.transform.GetSiblingIndex() + 1);
        rt.anchorMin = new Vector2(0f, 0f);
        rt.anchorMax = new Vector2(0f, 1f);
        rt.pivot = new Vector2(0f, 1f);
        rt.anchoredPosition = new Vector2(x, top);
        rt.sizeDelta = new Vector2(width, top - BottomMargin);
        var le = go.AddComponent<LayoutElement>();
        le.ignoreLayout = true;
        return rt;
    }

    // ---------------------------------------------------------------- opening a mod

    /// <summary>
    /// Opens <paramref name="modId"/>'s page from code while the screen is up (ModsPage.Open: a row that links to another
    /// mod's page, e.g. Suspension from a car's page): now, or right after the page's pass when a row asked for it.
    /// </summary>
    internal static void OpenLater(string modId, bool closeOnBack)
    {
        if (RowRecorder.Recording) _later = (modId, closeOnBack);
        else OpenMod(modId);
    }

    private static void OpenMod(string modId)
    {
        var m = ModManager.Find(modId);
        Show(m != null ? m.Id : ModsScreenModel.AllKey);
        _focusPage = true;
    }

    private static void Show(string key)
    {
        if (string.Equals(key, _key, StringComparison.OrdinalIgnoreCase)) return;
        _key = key ?? ModsScreenModel.AllKey;
        _page?.ScrollToTop();
    }

    private static ModInfo CurrentMod() => _spec == null && ModsScreenModel.IsMod(_key) ? ModManager.Find(_key) : null;

    // ---------------------------------------------------------------- input

    /// <summary>A row of the list pressed: its page shows; with Enter / A (not a click) the selection goes into it.</summary>
    private static void ListPressed(string key)
    {
        Show(key);
        var mouse = Mouse.current;
        bool click = mouse != null && (mouse.leftButton.wasPressedThisFrame || mouse.leftButton.wasReleasedThisFrame);
        if (!click) _focusPage = true;
    }

    private static void OnListInput(GameRow row, RowInput input)
    {
        if (row.Direct != null && input.Kind == RowInputKind.Press) row.Direct();
    }

    private static void OnPageInput(GameRow row, RowInput input)
    {
        if (row.Direct != null)
        {
            if (input.Kind == RowInputKind.Press) row.Direct();
            return;
        }
        Recorder.Queue(row.Index, input);
    }

    /// <summary>True while (or just after) the keys belong to a text box or a hotkey capture (Keybinds): Esc / B
    /// then ends that, it doesn't go back or close the screen.</summary>
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
            if (_state == null || _page == null) return false;
            if (_page.Typing) _typingFrame = Time.frameCount;
            return Time.frameCount - _typingFrame <= 2;
        }
    }

    /// <summary>The game may close the screen on its back input only from the list (Esc / B on the settings goes back to the list).</summary>
    private static void UpdateCanPop()
    {
        if (_state == null) return;
        bool may = ModsScreenModel.CanPop(_inPage, _closeOnBack, KeysElsewhere, Time.frameCount <= _canPopBackFrame) && !ModsPage.IsClassicOpen;
        try
        {
            if (!may && !_canPopTaken)
            {
                _state.CanPop = false;
                _canPopTaken = true;
            }
            else if (may && _canPopTaken)
            {
                _state.CanPop = true;
                _canPopTaken = false;
            }
        }
        catch (Exception e) { Warn("canpop", "the MODS screen's back key: " + e.Message); }
    }

    // ---------------------------------------------------------------- per frame

    /// <summary>Update: which page the list's selection shows, Esc / B, Tab for Advanced.</summary>
    internal static void Tick()
    {
        if (_state == null) return;
        try
        {
            if (_state.WasCollected || _root == null || _root.WasCollected) { Detach(); return; }
            KeepHidden();
            if (Time.frameCount % 30 == 0) SetTitle(); // a late language load may re-fill it
            _list?.Follow(_listArea);
            _page?.Follow(_pageArea);
            ModDetails.Follow(_root, _pageArea);
            if (ModsPage.IsClassicOpen) { UpdateCanPop(); return; } // the full view on top has the keys
            TrackSelection();
            UpdateCanPop();
            bool focus;
            try { focus = _state.IsInFocus; } catch { focus = true; }
            if (!focus) return;
            var kb = Keyboard.current;
            var pad = Gamepad.current;
            bool back = (kb != null && kb.escapeKey.wasPressedThisFrame) || (pad != null && pad.buttonEast.wasPressedThisFrame);
            if (back && ModsScreenModel.Back(_inPage, _closeOnBack, KeysElsewhere) == ModsScreenModel.BackStep.ToList)
            {
                _canPopBackFrame = Time.frameCount + 1; // the press that brings us back must not also close the screen
                SelectListRow(_key);
                _inPage = false; // the list row's own select sound plays (the game's button)
                UpdateCanPop();
            }
            else if (!KeysElsewhere && ((kb != null && kb.tabKey.wasPressedThisFrame) || (pad != null && pad.selectButton.wasPressedThisFrame)))
            {
                var m = CurrentMod();
                if (m != null && ModPageView.HasAdvanced(m)) ModPageView.ToggleAdvanced();
            }
            var mouse = Mouse.current;
            if (mouse != null && (mouse.delta.ReadValue().sqrMagnitude > 0.01f || mouse.scroll.ReadValue().sqrMagnitude > 0.01f)) _mouseFrame = Time.frameCount;
            bool usingMouse = Time.frameCount - _mouseFrame < 30;
            _list?.KeepSelectedVisible(usingMouse);
            _page?.KeepSelectedVisible(usingMouse);
        }
        catch (Exception e) { Fail("tick", e); }
    }

    /// <summary>
    /// The list's selection picks the page with the keyboard and gamepad (moving along the list shows each mod's
    /// settings); with the mouse a click does, so passing over the list on the way to the settings changes nothing. A
    /// selection that went elsewhere (the hidden language row the game selects on opening) comes back to the list.
    /// </summary>
    private static void TrackSelection()
    {
        if (_list == null || _page == null) return;
        var listRow = _list.SelectedRow(out int at);
        if (listRow != null)
        {
            _inPage = false;
            bool mouse = Time.frameCount - _mouseFrame < 30;
            if (!mouse && at >= 0 && at < _listModel.Count && !_listModel[at].Heading) Show(_listModel[at].Key);
            return;
        }
        if (_page.HasSelection)
        {
            _inPage = true;
            return;
        }
        var es = EventSystem.current;
        var go = es != null ? es.currentSelectedGameObject : null;
        bool lost = go == null || (_content != null && go.transform.IsChildOf(_content.transform));
        if (lost && Time.frameCount - _mouseFrame >= 30) _selectList = true;
    }

    /// <summary>OnGUI (Layout, once a frame): the list, and the page's draw code run as a recording pass.</summary>
    internal static void Record()
    {
        if (_state == null || _page == null || _list == null || _recordedFrame == Time.frameCount || ModsPage.IsClassicOpen) return;
        var ev = Event.current;
        if (ev == null || ev.type != EventType.Layout) return;
        _recordedFrame = Time.frameCount;
        try
        {
            BuildList();
            if (!_list.Show(ListEntries)) throw new InvalidOperationException("a button of the list could not be made (" + GamePrefabs.Summary + ")");
            RecordPass();
            if (_state == null || _page == null) return; // a row closed the screen during the pass
            BuildEntries();
            if (!_page.Show(Entries)) throw new InvalidOperationException("a game widget could not be made (" + GamePrefabs.Summary + ")");
            LinkSides();
            if (_focusPage)
            {
                _focusPage = false;
                _selectList = false;
                if (_page.First() != null) { _page.SelectFirst(); _inPage = true; }
                else SelectListRow(_key);
            }
            else if (_selectList)
            {
                _selectList = false;
                SelectListRow(_key);
            }
            ModDetails.Update(CurrentMod(), _key, _page.SelectedRow(out _)?.Model ?? _list.SelectedRow(out _)?.Model, _listModel);
            if (_openNext != null)
            {
                string id = _openNext;
                _openNext = null;
                OpenMod(id);
            }
            if (_later != null)
            {
                var l = _later.Value;
                _later = null;
                OpenMod(l.modId);
            }
        }
        catch (Exception e)
        {
            Recorder.Abort();
            Fail("page", e);
        }
    }

    /// <summary>The list's rows, from what is installed now (reused rows only get new texts).</summary>
    private static void BuildList()
    {
        if (_spec != null)
        {
            _listModel = new List<ModsScreenModel.Entry>(_spec.Sections() ?? Array.Empty<ModsScreenModel.Entry>());
            if (ModsScreenModel.IndexOf(_listModel, _key) < 0) _key = FirstSection();
            FillListEntries();
            return;
        }
        ModsScratch.Clear();
        foreach (var m in ModManager.All)
        {
            if (ModManager.IsCareerPart(m)) continue; // listed when no career mod holds it (the cars-only pack)
            bool core = ModPageView.IsCore(m);
            ModsScratch.Add(new ModsScreenModel.Mod(m.Id, m.Name, core, ThirdParty.IsApiMod(m.Id), core || ModManager.SwitchedOn(m.Id),
                ModManager.PendingRestart(m.Id), ModManager.FailedReason(m.Id) != null, AvailableTab.UpdateFor(m) != null));
        }
        TabsScratch.Clear();
        var tabs = ModsPage.TabNames;
        for (int i = 1; i < tabs.Count; i++) TabsScratch.Add(tabs[i]);
        bool session = Features.Public.ModTypes.RequiredModsSession.Role == SessionRole.Guest && Features.Public.ModTypes.RequiredModsSession.Advertised != null;
        _listModel = ModsScreenModel.Build(ModsScratch, session, TabsScratch);
        if (ModsScreenModel.IndexOf(_listModel, _key) < 0 && !RequiredModsPrompt.Active) _key = ModsScreenModel.AllKey;
        FillListEntries();
    }

    private static void FillListEntries()
    {
        ListEntries.Clear();
        foreach (var e in _listModel)
        {
            string id = e.Heading ? "#heading:" + e.Label : e.Key;
            if (!ListRows.TryGetValue(id, out var row))
            {
                row = new RowModel();
                ListRows[id] = row;
            }
            row.Kind = e.Heading ? RowKind.Heading : RowKind.Button;
            row.Label = e.Label;
            row.Value = e.Value;
            row.Enabled = true;
            row.Hint = null;
            string key = e.Key;
            ListEntries.Add(new GamePage.Entry(row, -1, e.Heading ? null : () => ListPressed(key)));
        }
    }

    private static void RecordPass()
    {
        Menu.BeginWindow(false);
        Menu.BeginContent(640f);
        Recorder.Begin(Menu.Y);
        try
        {
            if (_spec != null)
            {
                Menu.BeginGroup(0, "screen/" + _spec.Title + "/" + _key);
                _spec.Draw(_key);
            }
            else if (RequiredModsPrompt.Draw()) { }
            else if (_key == ModsScreenModel.AllKey)
            {
                var open = ModsPage.DrawInstalledList();
                if (open != null) _openNext = open.Id;
            }
            else if (_key == ModsScreenModel.ServerKey) Features.Public.ModTypes.RequiredModsSession.DrawSessionList();
            else if (ModsScreenModel.TabOf(_key) > 0) ModsPage.DrawTab(ModsScreenModel.TabOf(_key));
            else
            {
                var mod = CurrentMod();
                if (mod != null) ModPageView.Draw(mod);
                else _key = ModsScreenModel.AllKey;
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
        var rows = Recorder.Rows;
        for (int i = 0; i < rows.Count; i++) Entries.Add(new GamePage.Entry(rows[i], i, null));
        if (!Recorder.Untracked) return;
        // Every part of our pages has a game-widget form. Another creator's page that also draws by hand (raw GUI calls)
        // gets a row to the full view, where those parts show; anything else is only noted in the log.
        string mod = CurrentMod()?.Id;
        if (mod != null && ThirdParty.IsApiMod(mod)) Entries.Add(new GamePage.Entry(FullViewRow, -1, () => OpenFullViewFor(mod)));
        else Warn("untracked " + _key, $"the MODS page of {_key} draws parts by hand (GUI calls); those parts don't show with the game's widgets");
    }

    /// <summary>Right from the list goes into the page; left from the page's buttons back to the list row of its page.</summary>
    private static void LinkSides()
    {
        var first = _page.First();
        var mine = ListSelectable(_key);
        bool relinkList = !Same(_list.RightOf, first);
        bool relinkPage = !Same(_page.LeftOf, mine);
        if (relinkList)
        {
            _list.RightOf = first;
            _list.Relink();
        }
        if (relinkPage)
        {
            _page.LeftOf = mine;
            _page.Above = mine;
            _page.Relink();
        }
    }

    private static bool Same(Selectable a, Selectable b)
    {
        if (a == null || b == null) return a == null && b == null;
        try { return a.Pointer == b.Pointer; }
        catch { return false; }
    }

    private static Selectable ListSelectable(string key)
    {
        int i = ModsScreenModel.IndexOf(_listModel, key);
        return i >= 0 ? _list.SelectableAt(i) : null;
    }

    private static void SelectListRow(string key)
    {
        int i = ModsScreenModel.IndexOf(_listModel, key);
        if (i < 0 || !_list.SelectAt(i)) _list.SelectFirst();
    }

    private static void OpenFullViewFor(string modId)
    {
        if (_state == null) { ModsPage.OpenClassic(modId); return; }
        CanvasGroup group = null;
        try { group = _state._UICanvasGroup; } catch { }
        ModsPage.OpenOver(_state, group, null, modId);
    }

    /// <summary>Something of ours broke on the screen: logged once, the screen closes and the full view opens.</summary>
    internal static void Fail(string where, Exception e)
    {
        Warn("fail " + where, $"the MODS screen failed ({where}): {e}; showing the full view instead");
        string mod = _spec != null ? _spec.FallbackModId : CurrentMod()?.Id;
        _broken = true;
        try { GameModsScreen.Close(); } catch { } // now, or once a pop-up over it closed
        try { Detach(); } catch { }
        try { ModsPage.OpenClassic(mod); } catch { }
    }

    private static void Warn(string key, string message)
    {
        if (Logged.Add(key)) Core.CorePlugin.Logger?.LogWarning("[Core] " + message);
    }

    /// <summary>Logs once where things went (helps when a game update moves them).</summary>
    private static void LogLayoutOnce()
    {
        if (!Logged.Add("layout")) return;
        try
        {
            var rrt = _root.TryCast<RectTransform>();
            Core.CorePlugin.Logger?.LogInfo($"[Core] MODS screen: list at {_listArea.anchoredPosition} width {_listArea.sizeDelta.x}, settings at {_pageArea.anchoredPosition} " +
                                            $"width {_pageArea.sizeDelta.x}, screen {(rrt != null ? rrt.rect.size.ToString() : "?")}, list buttons from " +
                                            (GamePrefabs.MenuButton != null ? "the Settings menu" : "the settings' button row") + ", details " + (ModDetails.Shown ? "shown" : "hidden (narrow screen)"));
        }
        catch { }
    }

    // ---------------------------------------------------------------- the details on the right

    /// <summary>
    /// The right side of the screen: the mod's picture (its card), its name, a line with its version and type (or a
    /// status message), and the selected row's explanation, in copies of the game's row text. Hidden when the screen is
    /// too narrow for it beside the settings.
    /// </summary>
    private static class ModDetails
    {
        private static GameObject _go;
        private static RectTransform _rt;
        private static Image _picture;
        private static LayoutElement _pictureLe;
        private static TMP_Text _name, _meta, _hint;
        private static string _shownImage;
        private static CardInfo _card;
        private static float _base = 24f;

        internal static bool Shown => _go != null && _go.activeSelf;

        internal static void Create(Transform root, RectTransform page)
        {
            Destroy();
            try
            {
                if (GamePrefabs.Label == null) return;
                _go = new GameObject("OTH_ModsDetails");
                _rt = _go.AddComponent<RectTransform>();
                _rt.SetParent(root, false);
                _rt.anchorMin = new Vector2(1f, 0f);
                _rt.anchorMax = new Vector2(1f, 1f);
                _rt.pivot = new Vector2(1f, 1f);
                var le = _go.AddComponent<LayoutElement>();
                le.ignoreLayout = true;
                var v = _go.AddComponent<VerticalLayoutGroup>();
                v.childControlWidth = true;
                v.childControlHeight = true;
                v.childForceExpandWidth = true;
                v.childForceExpandHeight = false;
                v.spacing = 10f;
                v.childAlignment = TextAnchor.UpperLeft;

                var pic = new GameObject("Picture");
                var prt = pic.AddComponent<RectTransform>();
                prt.SetParent(_rt, false);
                _picture = pic.AddComponent<Image>();
                _picture.preserveAspect = true;
                _picture.raycastTarget = false;
                _picture.enabled = false;
                _pictureLe = pic.AddComponent<LayoutElement>();

                _name = Text("Name");
                _meta = Text("Meta");
                _hint = Text("Hint");
                if (_name != null) _base = Mathf.Max(14f, _name.fontSize);
                Style(_name, 1.25f, new Color(1f, 0.976f, 0.933f, 1f));
                Style(_meta, 0.72f, new Color(1f, 0.976f, 0.933f, 0.6f));
                Style(_hint, 0.8f, new Color(1f, 0.976f, 0.933f, 0.85f));
                Follow(root, page);
            }
            catch (Exception e)
            {
                Core.CorePlugin.Logger?.LogWarning("[Core] MODS screen: the details beside the settings could not be made (they stay off): " + e.Message);
                Destroy();
            }
        }

        private static TMP_Text Text(string name)
        {
            var go = Object.Instantiate(GamePrefabs.Label, GamePrefabs.EnsureHolder(), false);
            go.name = name;
            GameRow.Prepare(go, WidgetKind.Text);
            var t = go.GetComponent<TMP_Text>() ?? go.GetComponentInChildren<TMP_Text>(true);
            go.transform.SetParent(_rt, false);
            return t;
        }

        private static void Style(TMP_Text t, float size, Color color)
        {
            if (t == null) return;
            t.fontSize = _base * size;
            t.color = color;
            t.alignment = TextAlignmentOptions.TopLeft;
            t.enableWordWrapping = true;
        }

        /// <summary>Beside the page, up to the screen's right margin; hidden when that is too narrow.</summary>
        internal static void Follow(Transform root, RectTransform page)
        {
            if (_go == null || root == null || page == null) return;
            var rrt = root.TryCast<RectTransform>();
            float screenW = rrt != null ? rrt.rect.width : 1920f;
            float from = page.anchoredPosition.x + page.sizeDelta.x + Gap;
            float width = Mathf.Min(DetailsMaxWidth, screenW - DetailsMargin - from);
            bool show = width >= DetailsMinWidth;
            if (_go.activeSelf != show) _go.SetActive(show);
            if (!show) return;
            var pos = new Vector2(-DetailsMargin, page.anchoredPosition.y);
            var size = new Vector2(width, page.sizeDelta.y);
            if (_rt.anchoredPosition != pos) _rt.anchoredPosition = pos;
            if (_rt.sizeDelta != size) _rt.sizeDelta = size;
            float h = Mathf.Round(width * 9f / 16f);
            if (Mathf.Abs(_pictureLe.preferredHeight - h) > 0.5f) _pictureLe.preferredHeight = _pictureLe.minHeight = h;
        }

        internal static void Update(ModInfo mod, string key, RowModel selected, List<ModsScreenModel.Entry> list)
        {
            if (_go == null || !_go.activeSelf) return;
            string name, meta, hint;
            CardInfo card = null;
            string status = Menu.TryStatus(out var s) ? s : null;
            bool fromList = selected != null && ListRows.ContainsValue(selected);
            string rowHint = !fromList && selected != null ? selected.Hint : null;
            if (mod != null)
            {
                bool api = ThirdParty.IsApiMod(mod.Id);
                var maturity = ModMaturity.Of(mod, !api);
                name = mod.Name ?? mod.Id;
                string vm = ModMaturity.VersionAndLabel(mod.Version, maturity);
                meta = (vm.Length == 0 ? "" : vm + "  ·  ") + RequiredMods.Badge(mod) + (api ? "  ·  " + ModCards.OtherAuthor : "");
                hint = !string.IsNullOrEmpty(rowHint) ? rowHint : ModMaturity.Describe(mod.Description, mod.Id, maturity);
                if (_card == null || !string.Equals(_card.ModId, mod.Id, StringComparison.OrdinalIgnoreCase))
                    _card = new CardInfo { ModId = mod.Id, Seed = mod.Id, Title = name };
                card = _card;
            }
            else if (_spec != null)
            {
                int i = ModsScreenModel.IndexOf(list, key);
                name = i >= 0 ? list[i].Label : _spec.Title;
                meta = Ask(_spec.Meta, key) ?? "";
                hint = rowHint ?? Ask(_spec.About, key) ?? "";
                // a row with a picture (a car, a career) shows it here, big
                if (!fromList && selected?.Card != null)
                {
                    card = selected.Card;
                    if (!string.IsNullOrEmpty(selected.Label)) name = selected.Label;
                }
            }
            else
            {
                int i = ModsScreenModel.IndexOf(list, key);
                name = i >= 0 ? list[i].Label : "Mods";
                int n = ModManager.Listed.Count;
                meta = key == ModsScreenModel.AllKey ? $"{n} mod{(n == 1 ? "" : "s")} installed" : "";
                hint = rowHint ?? (key == ModsScreenModel.AllKey ? "Switch mods on or off here. It takes effect after you restart the game." : "");
            }
            if (RequiredModsPrompt.Active) { name = "Join a server"; card = null; }
            if (status != null) meta = "<color=" + ModsScreenModel.Yellow + ">" + ModsScreenModel.Plain(status) + "</color>";
            Set(_name, name);
            Set(_meta, meta);
            Set(_hint, hint);
            ShowPicture(card);
        }

        private static string Ask(Func<string, string> f, string key)
        {
            if (f == null) return null;
            try { return f(key); }
            catch (Exception e) { DebugLog.Error("core", e, "GameModsTab.ModDetails.Ask"); return null; }
        }

        private static void ShowPicture(CardInfo card)
        {
            string key = card?.ImageKey;
            if (key == _shownImage) return;
            _shownImage = key;
            Sprite sprite = null;
            try { sprite = card != null ? ModImages.For(card) : null; }
            catch (Exception e) { Core.CorePlugin.Logger?.LogDebug("[Core] MODS screen: picture: " + e.Message); }
            _picture.sprite = sprite;
            _picture.color = Color.white;
            _picture.enabled = sprite != null;
            if (_picture.gameObject.activeSelf != (sprite != null)) _picture.gameObject.SetActive(sprite != null);
        }

        private static void Set(TMP_Text t, string text)
        {
            if (t == null) return;
            text ??= "";
            if (t.text != text) t.text = text;
            bool any = text.Length > 0;
            if (t.gameObject.activeSelf != any) t.gameObject.SetActive(any);
        }

        internal static void Destroy()
        {
            try { if (_go != null) Object.Destroy(_go); } catch { }
            _go = null;
            _rt = null;
            _picture = null;
            _pictureLe = null;
            _name = _meta = _hint = null;
            _shownImage = null;
            _card = null;
        }
    }
}
