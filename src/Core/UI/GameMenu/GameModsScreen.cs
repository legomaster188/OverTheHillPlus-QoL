using System;
using FunCore.Managers;
using FunCore.StateMachine;
using HarmonyLib;
using Il2CppInterop.Runtime;
using UnityEngine;
using UnityEngine.EventSystems;

namespace OverTheHillPlus.UI.GameMenu;

/// <summary>
/// Opens and closes the MODS pages in the game's MOD BROWSER: from the ESC menu's MODS entry, [Core] OpenKey (F7),
/// a mod's own button (e.g. Create Server's PLUS LOBBY SETTINGS) or the required-mods prompt. The browser is pushed
/// on the game's state stack the way its main menu button does it (GameStateManager.PushState of a new
/// StateLiveryModsMenu), with a MODS tab (or a mod's page) asked to open; Esc / B pops it back to where it came
/// from. Returns false when that can't be done (switched off with [Core] GameMenus, no state manager, a browser
/// already lower in the stack, or the push failed): the caller then opens the full view.
/// </summary>
internal static class GameModsScreen
{
    private sealed class Pending
    {
        public int Tab;
        public string ModId;
        public bool CloseOnBack;
        public float Since;
    }

    private static Pending _pending;
    private static bool _fromGameplay;
    private static bool _cursorVisible;
    private static CursorLockMode _cursorLock;

    /// <summary>True when the game-widget pages are switched on ([Core] GameMenus).</summary>
    internal static bool Enabled => CoreConfig.GameMenus == null || CoreConfig.GameMenus.Value;

    /// <summary>Opens MODS tab <paramref name="tab"/> (or the page of <paramref name="modId"/>) in the MOD BROWSER.</summary>
    internal static bool Open(string modId, int tab = 0)
    {
        if (!Enabled) return false;
        try
        {
            bool closeOnBack = modId != null;
            if (GameModsTab.Attached && IsTop(GameModsTab.State))
            {
                // Already in the browser: the tab (or the full view, when the widgets are missing) opens there, after
                // the page's pass when a row of a page asked for it (e.g. Server Creator+'s Lobby size opens Lobby+).
                GameModsTab.OpenLater(tab, modId, closeOnBack);
                return true;
            }
            var gsm = HybridSingleton<GameStateManager>.Instance;
            if (gsm == null) return false;
            if (gsm.ContainsState(Il2CppType.Of<StateLiveryModsMenu>())) return false; // one browser at a time
            var top = gsm.PeekState();
            _fromGameplay = top == null || top.TryCast<StateUI>() == null;
            if (_fromGameplay)
            {
                _cursorVisible = Cursor.visible;
                _cursorLock = Cursor.lockState;
            }
            _pending = new Pending { Tab = tab, ModId = modId, CloseOnBack = closeOnBack, Since = Time.unscaledTime };
            gsm.PushState(new StateLiveryModsMenu());
            return true;
        }
        catch (Exception e)
        {
            _pending = null;
            _fromGameplay = false;
            Core.CorePlugin.Logger?.LogWarning("[Core] could not open the MOD BROWSER for MODS: " + e.Message + "; showing the full view");
            return false;
        }
    }

    /// <summary>Closes the browser we opened or are in, when it is the top screen.</summary>
    internal static void Close()
    {
        try
        {
            var state = GameModsTab.State;
            if (state == null || !IsTop(state)) return;
            HybridSingleton<GameStateManager>.Instance.PopState();
        }
        catch (Exception e) { Core.CorePlugin.Logger?.LogWarning("[Core] closing the MOD BROWSER: " + e.Message); }
    }

    private static bool IsTop(State state)
    {
        if (state == null) return false;
        var top = HybridSingleton<GameStateManager>.Instance?.PeekState();
        return top != null && top.Pointer == state.Pointer;
    }

    /// <summary>StateLiveryModsMenu.Open: our buttons go in; a MODS tab opens when we asked for one.</summary>
    internal static void Opened(StateLiveryModsMenu state)
    {
        GameModsTab.Attach(state);
        var p = _pending;
        _pending = null;
        if (p == null) return;
        if (GameModsTab.Attached) GameModsTab.Activate(p.Tab, p.ModId, p.CloseOnBack);
        else ModsPage.OpenOver(state, state._UICanvasGroup, null, p.ModId); // our buttons couldn't go in: the full view over it
    }

    internal static void Closed(StateLiveryModsMenu state)
    {
        if (GameModsTab.State != null && state != null && GameModsTab.State.Pointer != state.Pointer) return;
        GameModsTab.Detach();
        if (_fromGameplay)
        {
            _fromGameplay = false;
            Cursor.visible = _cursorVisible;
            Cursor.lockState = _cursorLock;
        }
    }

    /// <summary>Every frame (CoreBehaviour.Update).</summary>
    internal static void Tick()
    {
        if (_pending != null && Time.unscaledTime - _pending.Since > 3f)
        {
            // Pushed, but the browser never opened: the full view instead.
            var p = _pending;
            _pending = null;
            _fromGameplay = false;
            Core.CorePlugin.Logger?.LogWarning("[Core] the MOD BROWSER did not open; showing the full view");
            ModsPage.OpenClassic(p.ModId);
        }
        if (!GameModsTab.Attached) return;
        // Opened over driving: the game may keep the cursor locked; free it while the browser is up.
        if (_fromGameplay && IsTop(GameModsTab.State))
        {
            Cursor.visible = true;
            Cursor.lockState = CursorLockMode.None;
        }
        GameModsTab.Tick();
    }
}

[HarmonyPatch(typeof(StateLiveryModsMenu), nameof(StateLiveryModsMenu.Open))]
internal static class LiveryModsMenuOpenPatch
{
    private static void Postfix(StateLiveryModsMenu __instance)
    {
        try { GameModsScreen.Opened(__instance); }
        catch (Exception e) { Core.CorePlugin.Logger?.LogError("[Core] MODS in the MOD BROWSER: " + e); }
    }
}

[HarmonyPatch(typeof(StateLiveryModsMenu), nameof(StateLiveryModsMenu.Close))]
internal static class LiveryModsMenuClosePatch
{
    private static void Prefix(StateLiveryModsMenu __instance)
    {
        try { GameModsScreen.Closed(__instance); }
        catch (Exception e) { Core.CorePlugin.Logger?.LogError("[Core] MODS in the MOD BROWSER (close): " + e); }
    }
}

/// <summary>
/// The browser's livery shortcuts (subscribe, sort, filter, more options, order, previous / next page) all ask
/// CanUseModsShortcut first: none of them while a MODS tab shows (Select toggles Advanced there).
/// </summary>
[HarmonyPatch(typeof(StateLiveryModsMenu), nameof(StateLiveryModsMenu.CanUseModsShortcut))]
internal static class LiveryShortcutsPatch
{
    private static bool Prefix(StateLiveryModsMenu __instance, ref bool __result)
    {
        var state = GameModsTab.State;
        if (!GameModsTab.Active || state == null || __instance == null || __instance.Pointer != state.Pointer) return true;
        __result = false;
        return false;
    }
}

/// <summary>
/// The browser listens to every text box's submit (UITextMeshProInputFieldSelector.StaticOnSubmit) for its livery
/// search: not for our text rows (copies of its search field).
/// </summary>
[HarmonyPatch(typeof(StateLiveryModsMenu), nameof(StateLiveryModsMenu.OnSearchInputFieldSubmit))]
internal static class LiverySearchSubmitPatch
{
    private static bool Prefix(UITextMeshProInputFieldSelector inputField) => !GamePrefabs.IsOurs(inputField);
}

/// <summary>
/// The other screens that listen to every text box's submit (StaticOnSubmit) and may sit under the MOD BROWSER: the
/// server browser's join-by-code box and search, and the feedback form. They compare the box with their own, but our
/// boxes are copies of a search field: never theirs.
/// </summary>
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
