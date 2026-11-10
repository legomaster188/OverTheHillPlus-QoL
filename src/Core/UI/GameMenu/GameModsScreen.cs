using System;
#if MELON
using Il2CppFunCore.Managers;
using Il2CppFunCore.StateMachine;
#else
using FunCore.Managers;
using FunCore.StateMachine;
#endif
using HarmonyLib;
using Il2CppInterop.Runtime;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace OverTheHillPlus.UI.GameMenu;

/// <summary>
/// Opens and closes the MODS screen of the game's Settings menu (<see cref="GameModsTab"/>): from the Settings menu's
/// MODS button (<see cref="SettingsMenuButton"/>), a mod's own button (e.g. Create Server's PLUS LOBBY SETTINGS), a row
/// that links to another mod's page, or the hidden debug key ([Debug] ModsKey). The screen is a settings screen of the
/// game pushed on its state stack the way the Settings menu pushes its own (GameStateManager.PushState of a new
/// StateLanguageSettingsMenu), turned into MODS when it opens; the game's back input pops it to where it came from.
/// Returns false when that can't be done (switched off with [Core] GameMenus, no state manager, the language screen
/// already open, or the push failed): the caller then opens the full view.
/// </summary>
internal static class GameModsScreen
{
    private sealed class Pending
    {
        public string ModId;
        public bool CloseOnBack;
        public float Since;
        public GameScreenSpec Screen;
        public string Section;
    }

    private static Pending _pending;
    // The language screen we turned into MODS (by pointer: ours from its Open until the game closes it, also when the
    // MODS screen on it failed), and whether it should go as soon as it is the top screen.
    private static IntPtr _screenPtr;
    private static StateLanguageSettingsMenu _screen;
    private static bool _closeWhenTop;
    private static bool _fromGameplay;
    private static bool _cursorVisible;
    private static CursorLockMode _cursorLock;
    private static GameObject _reselect;

    /// <summary>True when the game-widget screen is switched on ([Core] GameMenus).</summary>
    internal static bool Enabled => CoreConfig.GameMenus == null || CoreConfig.GameMenus.Value;

    /// <summary>Opens MODS (on <paramref name="modId"/>'s page when given). <paramref name="from"/>: the button to select
    /// again when it closes (the Settings menu's MODS button).</summary>
    internal static bool Open(string modId, GameObject from = null) => Push(modId, null, null, from);

    /// <summary>
    /// Opens a mod's own settings screen (<see cref="GameScreenSpec"/>: its title, its sections on the left) on
    /// <paramref name="section"/> (null: the first). False when it can't be (as <see cref="Open"/>, or the MODS screen
    /// is up already): the caller then opens the full view on <see cref="GameScreenSpec.FallbackModId"/>.
    /// </summary>
    internal static bool OpenScreen(GameScreenSpec screen, string section = null, GameObject from = null) =>
        screen != null && screen.Sections != null && screen.Draw != null && Push(null, screen, section, from);

    private static bool Push(string modId, GameScreenSpec screen, string section, GameObject from)
    {
        if (!Enabled || GameModsTab.Broken) return false;
        try
        {
            if (GameModsTab.Attached && IsTop(GameModsTab.State))
            {
                if (screen != null) return GameModsTab.Screen == screen; // one screen at a time
                // Already up: the page opens there (after the page's pass when a row asked for it).
                GameModsTab.OpenLater(modId, false);
                return true;
            }
            var gsm = HybridSingleton<GameStateManager>.Instance;
            if (gsm == null) return false;
            if (_screenPtr != IntPtr.Zero || gsm.ContainsState(Il2CppType.Of<StateLanguageSettingsMenu>())) return false; // ours or the game's: one at a time
            var top = gsm.PeekState();
            _fromGameplay = top == null || top.TryCast<StateUI>() == null;
            if (_fromGameplay)
            {
                _cursorVisible = Cursor.visible;
                _cursorLock = Cursor.lockState;
            }
            _reselect = from;
            _pending = new Pending { ModId = modId, CloseOnBack = modId != null && from == null, Since = Time.unscaledTime, Screen = screen, Section = section };
            OverTheHillPlus.CrashTrail.Mark(screen != null ? "settings screen: opening " + screen.Title : "MODS screen: opening");
            gsm.PushState(new StateLanguageSettingsMenu());
            return true;
        }
        catch (Exception e)
        {
            _pending = null;
            _fromGameplay = false;
            Core.CorePlugin.Logger?.LogWarning("[Core] could not open the MODS screen: " + e.Message + "; showing the full view");
            return false;
        }
    }

    /// <summary>Closes the MODS screen: now when it is the top screen, else as soon as it is (a pop-up over it closed).</summary>
    internal static void Close()
    {
        if (_screenPtr == IntPtr.Zero) return;
        if (!PopIfTop()) _closeWhenTop = true;
    }

    private static bool PopIfTop()
    {
        try
        {
            var gsm = HybridSingleton<GameStateManager>.Instance;
            var top = gsm?.PeekState();
            if (top == null || _screenPtr == IntPtr.Zero || top.Pointer != _screenPtr) return false;
            gsm.PopState();
            return true;
        }
        catch (Exception e)
        {
            Core.CorePlugin.Logger?.LogWarning("[Core] closing the MODS screen: " + e.Message);
            return false;
        }
    }

    private static bool IsTop(State state)
    {
        if (state == null) return false;
        var top = HybridSingleton<GameStateManager>.Instance?.PeekState();
        return top != null && top.Pointer == state.Pointer;
    }

    /// <summary>True when <paramref name="state"/> is the language screen we pushed for MODS.</summary>
    internal static bool IsOurs(StateLanguageSettingsMenu state)
    {
        try { return state != null && _screenPtr != IntPtr.Zero && state.Pointer == _screenPtr; }
        catch { return false; }
    }

    /// <summary>StateLanguageSettingsMenu.Open: ours becomes MODS; the game's own language screen is left alone.</summary>
    internal static void Opened(StateLanguageSettingsMenu state)
    {
        var p = _pending;
        _pending = null;
        if (p == null || state == null) return;
        _screen = state;
        _screenPtr = state.Pointer;
        _closeWhenTop = false;
        GameModsTab.Attach(state, p.ModId, p.CloseOnBack, p.Screen, p.Section);
        if (GameModsTab.Attached) return;
        // The screen couldn't be made: it goes again (from Tick, not inside the game's own Open), and the full view opens.
        _closeWhenTop = true;
        ModsPage.OpenClassic(p.Screen != null ? p.Screen.FallbackModId : p.ModId);
    }

    internal static void Closed(StateLanguageSettingsMenu state)
    {
        if (!IsOurs(state)) return;
        GameModsTab.Detach();
        _screen = null;
        _screenPtr = IntPtr.Zero;
        _closeWhenTop = false;
        if (_fromGameplay)
        {
            _fromGameplay = false;
            Cursor.visible = _cursorVisible;
            Cursor.lockState = _cursorLock;
        }
        var back = _reselect;
        _reselect = null;
        if (back != null) _selectAgain = back;
    }

    private static GameObject _selectAgain;

    /// <summary>Every frame (CoreBehaviour.Update).</summary>
    internal static void Tick()
    {
        if (_pending != null && Time.unscaledTime - _pending.Since > 3f)
        {
            // Pushed, but the screen never opened: the full view instead.
            var p = _pending;
            _pending = null;
            _fromGameplay = false;
            Core.CorePlugin.Logger?.LogWarning("[Core] the MODS screen did not open; showing the full view");
            ModsPage.OpenClassic(p.Screen != null ? p.Screen.FallbackModId : p.ModId);
        }
        if (_closeWhenTop) PopIfTop();
        if (_screenPtr != IntPtr.Zero && _screen != null && _screen.WasCollected)
        {
            // Gone without its Close.
            GameModsTab.Detach();
            _screen = null;
            _screenPtr = IntPtr.Zero;
            _closeWhenTop = false;
        }
        if (_selectAgain != null)
        {
            // Back on the Settings menu: its MODS button selected again (the game puts its selection back a frame late).
            try
            {
                var es = EventSystem.current;
                if (es != null && _selectAgain.activeInHierarchy) es.SetSelectedGameObject(_selectAgain);
            }
            catch { }
            _selectAgain = null;
        }
        if (!GameModsTab.Attached) return;
        // Opened over driving: the game may keep the cursor locked; free it while the screen is up.
        if (_fromGameplay && IsTop(GameModsTab.State))
        {
            Cursor.visible = true;
            Cursor.lockState = CursorLockMode.None;
        }
        GameModsTab.Tick();
    }
}

/// <summary>
/// The MODS button of the game's Settings menu, under GRAPHICS, AUDIO, CONTROLS, HUD, CAMERA, LANGUAGE and PROFILE: a
/// clone of the menu's last button, so it has the game's look, highlight and sounds (decoded from the game:
/// ContextUISettingsMenu's buttons in its ButtonList, a layout column). StateSettingsMenu.Open wires the buttons each
/// time the menu opens, so the clone is made there too. One of the buttons is also kept as the template of the MODS
/// screen's list of mods (<see cref="GamePrefabs.TakeMenuButton"/>).
/// </summary>
internal static class SettingsMenuButton
{
    internal const string Name = "OTH_ModsSettingsButton";
    private static bool _warned;

    internal static void Add(StateSettingsMenu menu)
    {
        try
        {
            var ctx = menu?.settingsMenuUIContext;
            if (ctx == null) { Warn("the Settings menu's buttons were not found"); return; }
            UIButtonSelector last = null;
            foreach (var b in new[] { ctx.GraphicsButton, ctx.AudioButton, ctx.ControlsButton, ctx.HUDButton, ctx.InterfaceButton, ctx.CameraButton, ctx.LanguageButton, ctx.ProfileButton })
            {
                if (b == null || !b.gameObject.activeSelf) continue;
                if (last == null || b.transform.parent == last.transform.parent && b.transform.GetSiblingIndex() > last.transform.GetSiblingIndex()) last = b;
            }
            if (last == null) { Warn("the Settings menu has no buttons"); return; }
            GamePrefabs.TakeMenuButton(ctx.GraphicsButton != null ? ctx.GraphicsButton : last);
            GameObject button = null;
            button = GameUiButtons.Clone(last, Name, "MODS", last.transform.GetSiblingIndex() + 1, () =>
            {
                if (!GameModsScreen.Open(null, button)) ModsPage.OpenClassic();
            });
            foreach (var t in button.GetComponentsInChildren<FunTextTMP>(true)) FunTexts.Register(t);
            var mine = button.GetComponent<Selectable>();
            Link(last, mine);
        }
        catch (Exception e) { DebugLog.Error("core", e, "SettingsMenuButton.Add"); Warn(e.Message); }
    }

    /// <summary>Puts <paramref name="below"/> under <paramref name="above"/> where the game links its buttons explicitly
    /// (the last button's "down" goes to ours, ours goes on to where it went).</summary>
    internal static void Link(Selectable above, Selectable below)
    {
        if (above == null || below == null) return;
        var a = above.navigation;
        if (a.mode != Navigation.Mode.Explicit) return; // automatic: the game finds the new button by position
        var oldDown = a.selectOnDown;
        a.selectOnDown = below;
        above.navigation = a;
        var b = below.navigation;
        b.mode = Navigation.Mode.Explicit;
        b.selectOnUp = above;
        b.selectOnDown = oldDown;
        b.selectOnLeft = a.selectOnLeft;
        b.selectOnRight = a.selectOnRight;
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

    private static void Warn(string why)
    {
        if (_warned) return;
        _warned = true;
        Core.CorePlugin.Logger?.LogWarning($"[Core] MODS button not added to the Settings menu ({why}); the mods' settings open in the full view from [Debug] ModsKey.");
    }
}

[HarmonyPatch(typeof(StateSettingsMenu), nameof(StateSettingsMenu.Open))]
internal static class SettingsMenuModsButtonPatch
{
    private static void Postfix(StateSettingsMenu __instance) => SettingsMenuButton.Add(__instance);
}

[HarmonyPatch(typeof(StateLanguageSettingsMenu), nameof(StateLanguageSettingsMenu.Open))]
internal static class ModsScreenOpenPatch
{
    private static void Postfix(StateLanguageSettingsMenu __instance)
    {
        try { GameModsScreen.Opened(__instance); }
        catch (Exception e) { Core.CorePlugin.Logger?.LogError("[Core] the MODS screen: " + e); }
    }
}

[HarmonyPatch(typeof(StateLanguageSettingsMenu), nameof(StateLanguageSettingsMenu.Close))]
internal static class ModsScreenClosePatch
{
    private static void Prefix(StateLanguageSettingsMenu __instance)
    {
        try { GameModsScreen.Closed(__instance); }
        catch (Exception e) { Core.CorePlugin.Logger?.LogError("[Core] the MODS screen (close): " + e); }
    }
}

/// <summary>The MODS screen is a language screen underneath: it never saves (or changes) the game's language.</summary>
[HarmonyPatch(typeof(StateLanguageSettingsMenu), nameof(StateLanguageSettingsMenu.SaveToDisk))]
internal static class ModsScreenSavePatch
{
    private static bool Prefix(StateLanguageSettingsMenu __instance) => !GameModsScreen.IsOurs(__instance);
}

[HarmonyPatch(typeof(StateLanguageSettingsMenu), nameof(StateLanguageSettingsMenu.LocalizationChanged))]
internal static class ModsScreenLanguagePatch
{
    private static bool Prefix(StateLanguageSettingsMenu __instance) => !GameModsScreen.IsOurs(__instance);
}

/// <summary>
/// The screens that listen to every text box's submit (UITextMeshProInputFieldSelector.StaticOnSubmit) and may be
/// loaded while the MODS screen is up: the MOD BROWSER's livery search, the server browser's join-by-code box and search,
/// and the feedback form. They compare the box with their own, but our boxes are copies of a search field: never theirs.
/// </summary>
[HarmonyPatch(typeof(StateLiveryModsMenu), nameof(StateLiveryModsMenu.OnSearchInputFieldSubmit))]
internal static class LiverySearchSubmitPatch
{
    private static bool Prefix(UITextMeshProInputFieldSelector inputField) => !GamePrefabs.IsOurs(inputField);
}

[HarmonyPatch(typeof(ContextUIServerJoinByCode), nameof(ContextUIServerJoinByCode.OnInputFieldSubmit))]
internal static class JoinByCodeSubmitPatch
{
    private static bool Prefix(UITextMeshProInputFieldSelector inputField) => !GamePrefabs.IsOurs(inputField);
}

[HarmonyPatch(typeof(ContextUIServerList), nameof(ContextUIServerList.OnSearchInputFieldSubmit))]
internal static class ServerSearchSubmitPatch
{
    private static bool Prefix(UITextMeshProInputFieldSelector inputField) => !GamePrefabs.IsOurs(inputField);
}

[HarmonyPatch(typeof(StateFeedbackMenu), nameof(StateFeedbackMenu.OnInputFieldSubmit))]
internal static class FeedbackSubmitPatch
{
    private static bool Prefix(UITextMeshProInputFieldSelector inputField) => !GamePrefabs.IsOurs(inputField);
}

/// <summary>Enter / A on one of our "◂ value ▸" rows: a press (the game's option rows do nothing on submit).</summary>
[HarmonyPatch(typeof(UISelector), nameof(UISelector.OnSubmit))]
internal static class GameRowSubmitPatch
{
    private static void Prefix(UISelector __instance)
    {
        if (GameRow.ByPointer.Count == 0 || __instance == null) return;
        if (GameRow.ByPointer.TryGetValue(__instance.Pointer, out var row)) row.Submitted();
    }
}

/// <summary>A left click on one of our "◂ value ▸" rows, away from its arrows: a press.</summary>
[HarmonyPatch(typeof(UISelector), nameof(UISelector.OnPointerClick))]
internal static class GameRowClickPatch
{
    private static void Prefix(UISelector __instance, PointerEventData eventData)
    {
        if (GameRow.ByPointer.Count == 0 || __instance == null) return;
        if (eventData != null && eventData.button != PointerEventData.InputButton.Left) return;
        if (GameRow.ByPointer.TryGetValue(__instance.Pointer, out var row)) row.Submitted();
    }
}
