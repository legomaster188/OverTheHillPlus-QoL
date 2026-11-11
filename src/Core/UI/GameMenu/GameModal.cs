using System;
using System.Collections.Generic;
#if MELON
using Il2CppFunCore.Managers;
using Il2CppFunCore.StateMachine;
#else
using FunCore.Managers;
using FunCore.StateMachine;
#endif
using HarmonyLib;
using Il2CppInterop.Runtime;
#if MELON
using Il2CppTMPro;
#else
using TMPro;
#endif
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace OverTheHillPlus.UI.GameMenu;

/// <summary>
/// A pop-up of ours in the game's own pop-up (decoded from the game: Prefabs/UI/Menus/Modals/TwoOptionModal, a
/// ContextUITwoOptionModal with a Title, a Body and a column of buttons in the middle of the screen over a blurred
/// background, the key hints bottom right). The game opens it as a state of its own (StateKickPlayerConfirmationModal,
/// "kick this player?"): we push one the same way, and while it is ours its title, text and buttons are ours (the
/// buttons' presses run our choices instead of the kick question's, which is never asked); more buttons than its two
/// are copies of its second. The game's back input closes it like any of its pop-ups (that is the "cancel" choice).
/// <see cref="Show"/>'s view is asked for again every frame, so the text follows what happens (a download running,
/// done, failed). When the pop-up can't be opened, <see cref="Show"/> says so and the caller shows its rows instead.
/// </summary>
internal static class GameModal
{
    internal sealed class Choice
    {
        public string Label;
        public Action Act;
        public bool Enabled = true;
    }

    internal sealed class View
    {
        public string Title;
        public string Body;
        public readonly List<Choice> Choices = new();

        public View Add(string label, Action act, bool enabled = true)
        {
            Choices.Add(new Choice { Label = label, Act = act, Enabled = enabled });
            return this;
        }
    }

    private const string ExtraPrefix = "OTH_ModalChoice_";

    private static Func<View> _build;
    private static Action _cancel, _failed;
    // The pop-up we pushed (by pointer: it is ours from the push until the game closes it, shown or not).
    private static StateKickPlayerConfirmationModal _pushed;
    private static IntPtr _pushedPtr;
    private static bool _pending, _inShow, _openFailed, _closeWhenTop;
    private static float _pendingSince, _closeSince;
    private static StateKickPlayerConfirmationModal _state;
    private static ContextUITwoOptionModal _ctx;
    private static readonly List<UIButtonSelector> Buttons = new();
    private static readonly List<TMP_Text> Labels = new();
    private static readonly List<IntPtr> OwnedTexts = new();
    private static bool _optionTwoWasActive = true;
    private static View _view;
    private static bool _closingByUs;
    private static int _shownChoices = -1;
    private static bool _warned;

    /// <summary>True while a pop-up of ours is up (or being opened).</summary>
    internal static bool Showing => _state != null || _pending;

    /// <summary>
    /// Shows a pop-up whose content <paramref name="build"/> gives (asked again every frame; null closes it).
    /// <paramref name="cancel"/> runs when it goes without one of its choices (the player's back, or the screen went);
    /// <paramref name="failed"/> when it was pushed but never opened. False: it can't be shown (the caller shows its
    /// content another way; <paramref name="failed"/> is not called then). Showing again while one is up replaces its content.
    /// </summary>
    internal static bool Show(Func<View> build, Action cancel, Action failed = null)
    {
        if (build == null) return false;
        if (Showing)
        {
            _build = build;
            _cancel = cancel;
            _failed = failed;
            return true;
        }
        if (_pushedPtr != IntPtr.Zero) return false; // the last one is still closing
        try
        {
            var gsm = HybridSingleton<GameStateManager>.Instance;
            if (gsm == null) return false;
            if (gsm.ContainsState(Il2CppType.Of<StateKickPlayerConfirmationModal>())) return false; // the game's own is up
            _build = build;
            _cancel = cancel;
            _failed = failed;
            _pending = true;
            _openFailed = false;
            _pendingSince = Time.unscaledTime;
            OverTheHillPlus.CrashTrail.Mark("pop-up: opening");
            var s = new StateKickPlayerConfirmationModal("");
            _pushed = s;
            _pushedPtr = s.Pointer;
            _inShow = true;
            try { gsm.PushState(s); }
            finally { _inShow = false; }
            if (!_openFailed) return true;
            _openFailed = false;
            return false; // it opened at once and couldn't be used: it goes again (Tick), the caller shows its content
        }
        catch (Exception e)
        {
            Warn("could not open the game's pop-up: " + e.Message);
            ClearContent();
            if (_pushedPtr != IntPtr.Zero) CloseWhenTop(); // in case it was pushed after all
            return false;
        }
    }

    /// <summary>Closes ours (no cancel: the choice that closes it already did what it does).</summary>
    internal static void Close()
    {
        if (_pushedPtr == IntPtr.Zero) return;
        ClearContent();
        if (!PopIfTop()) CloseWhenTop();
    }

    /// <summary>True when <paramref name="s"/> is the pop-up we pushed (shown as ours or not).</summary>
    internal static bool IsOurs(StateKickPlayerConfirmationModal s)
    {
        try { return s != null && _pushedPtr != IntPtr.Zero && s.Pointer == _pushedPtr; }
        catch { return false; }
    }

    /// <summary>StateKickPlayerConfirmationModal.Open: ours gets its texts and buttons (the game's own are left alone).</summary>
    internal static void Opened(StateKickPlayerConfirmationModal s)
    {
        if (!IsOurs(s)) return;
        if (!_pending)
        {
            // Opened after we gave up on it: never shown as a kick question, it goes again.
            CloseWhenTop();
            return;
        }
        _pending = false;
        try
        {
            _ctx = s.context;
            if (_ctx == null || _ctx.OptionOne == null || _ctx.Title == null || _ctx.Body == null) throw new InvalidOperationException("the pop-up's parts were not found");
            _state = s;
            Buttons.Clear();
            Labels.Clear();
            OwnedTexts.Clear();
            Take(_ctx.OptionOne, _ctx.OptionOneText);
            if (_ctx.OptionTwo != null)
            {
                _optionTwoWasActive = _ctx.OptionTwo.gameObject.activeSelf;
                Take(_ctx.OptionTwo, _ctx.OptionTwoText);
            }
            Own(_ctx.Title);
            Own(_ctx.Body);
            _shownChoices = -1;
            Apply();
        }
        catch (Exception e)
        {
            Warn("the game's pop-up could not be used: " + e.Message);
            var failed = _inShow ? null : _failed;
            if (_inShow) _openFailed = true;
            ClearContent();
            CloseWhenTop(); // popped from Tick, not from inside the game's own Open
            failed?.Invoke();
        }
    }

    internal static void Closed(StateKickPlayerConfirmationModal s)
    {
        if (!IsOurs(s)) return;
        var cancel = _closingByUs || _state == null ? null : _cancel;
        Reset();
        try { cancel?.Invoke(); }
        catch (Exception e) { DebugLog.Error("core", e, "GameModal.Closed"); }
    }

    /// <summary>One of the pop-up's own two buttons on ours: our choice instead of the kick question's (false skips the game's).</summary>
    internal static bool Pressed(StateKickPlayerConfirmationModal s, int index)
    {
        if (!IsOurs(s)) return true;
        if (_state != null && !_closeWhenTop) Choose(index);
        return false; // never the kick question's own action, also while it is closing
    }

    private static bool PopIfTop()
    {
        try
        {
            var gsm = HybridSingleton<GameStateManager>.Instance;
            var top = GameState.TopState(gsm);
            if (top == null || top.Pointer != _pushedPtr) return false;
            _closingByUs = true;
            gsm.PopState();
            return true;
        }
        catch (Exception e)
        {
            Warn("closing the pop-up: " + e.Message);
            return false;
        }
        finally { _closingByUs = false; }
    }

    private static void CloseWhenTop()
    {
        if (_closeWhenTop) return;
        _closeWhenTop = true;
        _closeSince = Time.unscaledTime;
    }

    /// <summary>The content goes (nothing more is shown or chosen); the pop-up itself stays ours until the game closes it.</summary>
    private static void ClearContent()
    {
        _pending = false;
        _view = null;
        _build = null;
        _cancel = null;
        _failed = null;
    }

    /// <summary>Ours is gone: the pop-up's own parts back as the game had them (in case it reuses them), nothing of ours kept.</summary>
    private static void Reset()
    {
        try
        {
            for (int i = 2; i < Buttons.Count; i++)
                if (Buttons[i] != null) Object.Destroy(Buttons[i].gameObject);
            for (int i = 0; i < Buttons.Count && i < 2; i++)
                if (Buttons[i] != null && !Buttons[i].interactable) Buttons[i].interactable = true;
            if (_ctx != null && _ctx.OptionTwo != null && _ctx.OptionTwo.gameObject.activeSelf != _optionTwoWasActive) _ctx.OptionTwo.gameObject.SetActive(_optionTwoWasActive);
        }
        catch { /* went with its screen */ }
        foreach (var p in OwnedTexts)
        {
            try { FunTexts.Ours.Remove(p); } catch { }
        }
        OwnedTexts.Clear();
        ClearContent();
        _state = null;
        _ctx = null;
        _pushed = null;
        _pushedPtr = IntPtr.Zero;
        _closeWhenTop = false;
        Buttons.Clear();
        Labels.Clear();
        _shownChoices = -1;
        _optionTwoWasActive = true;
    }

    private static void Take(UIButtonSelector b, TMP_Text label)
    {
        Buttons.Add(b);
        if (label == null) label = b.GetComponentInChildren<TMP_Text>(true);
        Labels.Add(label);
        Own(label);
    }

    /// <summary>Our text stays: the game's localization doesn't write over it (until ours goes).</summary>
    private static void Own(TMP_Text t)
    {
        if (t == null) return;
        var fun = t.TryCast<FunTextTMP>();
        if (fun == null) return;
        FunTexts.Register(fun);
        OwnedTexts.Add(fun.Pointer);
    }

    private static void Choose(int index)
    {
        var v = _view;
        if (v == null || index < 0 || index >= v.Choices.Count) return;
        var c = v.Choices[index];
        if (c == null || !c.Enabled || c.Act == null) return;
        try { c.Act(); }
        catch (Exception e)
        {
            DebugLog.Error("core", e, "GameModal.Choose");
            Core.CorePlugin.Logger?.LogWarning("[Core] pop-up choice '" + c.Label + "' failed: " + e.Message);
        }
    }

    /// <summary>Every frame (CoreBehaviour.Update): the content again, a push that never opened, closing ours once it is on top.</summary>
    internal static void Tick()
    {
        if (_pushedPtr == IntPtr.Zero) return;
        if (_pending && _state == null && Time.unscaledTime - _pendingSince > 3f)
        {
            Warn("the game's pop-up did not open");
            var failed = _failed;
            ClearContent();
            CloseWhenTop();
            failed?.Invoke();
            return;
        }
        if (_closeWhenTop)
        {
            if (PopIfTop()) return;
            // Not on the stack (never opened, or gone without its Close): forgotten, so the next pop-up can be pushed.
            bool onStack = true;
            try { onStack = HybridSingleton<GameStateManager>.Instance?.ContainsState(Il2CppType.Of<StateKickPlayerConfirmationModal>()) ?? false; }
            catch { }
            if (!onStack && Time.unscaledTime - _closeSince > 1f) Reset(); // (while it is on the stack, its buttons stay swallowed)
            return;
        }
        if (_state == null) return;
        try
        {
            if (_state.WasCollected || _ctx == null || _ctx.WasCollected)
            {
                // Gone without its Close: the caller hears it like a back press.
                var cancel = _cancel;
                Reset();
                cancel?.Invoke();
                return;
            }
            Apply();
        }
        catch (Exception e)
        {
            Warn("the pop-up: " + e);
            var cancel = _cancel;
            Close();
            try { cancel?.Invoke(); } catch { }
        }
    }

    private static void Apply()
    {
        if (_state == null || _closeWhenTop) return;
        View v;
        try { v = _build?.Invoke(); }
        catch (Exception e)
        {
            DebugLog.Error("core", e, "GameModal.Apply");
            v = null;
        }
        if (v == null)
        {
            Close();
            return;
        }
        _view = v;
        SetText(_ctx.Title, v.Title);
        SetText(_ctx.Body, v.Body);
        while (Buttons.Count < v.Choices.Count)
            if (!AddButton()) break;
        for (int i = 0; i < Buttons.Count; i++)
        {
            var b = Buttons[i];
            if (b == null) continue;
            bool shown = i < v.Choices.Count;
            if (b.gameObject.activeSelf != shown) b.gameObject.SetActive(shown);
            if (!shown) continue;
            var c = v.Choices[i];
            SetText(Labels[i], c.Label);
            if (b.interactable != c.Enabled) b.interactable = c.Enabled;
        }
        if (_shownChoices != v.Choices.Count)
        {
            _shownChoices = v.Choices.Count;
            LinkButtons(v.Choices.Count);
        }
        KeepSelection(v);
    }

    /// <summary>One more button: a copy of the pop-up's second (or first), under the others.</summary>
    private static bool AddButton()
    {
        var template = _ctx.OptionTwo != null ? _ctx.OptionTwo : _ctx.OptionOne;
        if (template == null) return false;
        try
        {
            int index = Buttons.Count;
            var go = Object.Instantiate(template.gameObject, GamePrefabs.EnsureHolder(), false);
            go.name = ExtraPrefix + index;
            go.SetActive(true);
            foreach (var c in go.GetComponentsInChildren<Component>(true))
            {
                if (c == null) continue;
                string type = c.GetIl2CppType().FullName ?? "";
                if (type.Contains("Locali", StringComparison.OrdinalIgnoreCase)) Object.DestroyImmediate(c);
            }
            var b = go.GetComponent<UIButtonSelector>();
            if (b == null) { Object.Destroy(go); return false; }
            var label = go.GetComponentInChildren<TMP_Text>(true);
            foreach (var t in go.GetComponentsInChildren<FunTextTMP>(true)) FunTexts.Register(t);
            b.add_OnPointerClickOrSubmit(DelegateSupport.ConvertDelegate<Il2CppSystem.Action<UIButtonSelector>>(
                new Action<UIButtonSelector>(_ => Choose(index))));
            go.transform.SetParent(template.transform.parent, false);
            go.transform.SetSiblingIndex(Buttons[Buttons.Count - 1].transform.GetSiblingIndex() + 1);
            Buttons.Add(b);
            Labels.Add(label);
            return true;
        }
        catch (Exception e)
        {
            Warn("a button could not be added to the pop-up: " + e.Message);
            return false;
        }
    }

    /// <summary>Up / down along the shown buttons, round from the last to the first.</summary>
    private static void LinkButtons(int count)
    {
        int n = Math.Min(count, Buttons.Count);
        for (int i = 0; i < n; i++)
        {
            var s = Buttons[i];
            if (s == null) continue;
            var nav = s.navigation;
            nav.mode = Navigation.Mode.Explicit;
            nav.selectOnUp = Buttons[(i - 1 + n) % n];
            nav.selectOnDown = Buttons[(i + 1) % n];
            nav.selectOnLeft = null;
            nav.selectOnRight = null;
            s.navigation = nav;
        }
    }

    /// <summary>The selection stays on a shown, usable button (the first such one when it is elsewhere).</summary>
    private static void KeepSelection(View v)
    {
        var es = EventSystem.current;
        if (es == null) return;
        var go = es.currentSelectedGameObject;
        int first = -1;
        for (int i = 0; i < v.Choices.Count && i < Buttons.Count; i++)
        {
            var b = Buttons[i];
            if (b == null) continue;
            if (go != null && b.gameObject.Pointer == go.Pointer && v.Choices[i].Enabled) return;
            if (first < 0 && v.Choices[i].Enabled) first = i;
        }
        if (first >= 0) es.SetSelectedGameObject(Buttons[first].gameObject);
    }

    private static void SetText(TMP_Text t, string text)
    {
        if (t == null) return;
        text ??= "";
        if (t.text != text) t.text = text;
    }

    private static void Warn(string why)
    {
        if (_warned) { Core.CorePlugin.Logger?.LogDebug("[Core] " + why); return; }
        _warned = true;
        Core.CorePlugin.Logger?.LogWarning("[Core] " + why + "; our questions show on the MODS screen instead");
    }
}

[HarmonyPatch(typeof(StateKickPlayerConfirmationModal), nameof(StateKickPlayerConfirmationModal.Open))]
internal static class GameModalOpenPatch
{
    private static void Postfix(StateKickPlayerConfirmationModal __instance)
    {
        try { GameModal.Opened(__instance); }
        catch (Exception e) { Core.CorePlugin.Logger?.LogError("[Core] pop-up: " + e); }
    }
}

[HarmonyPatch(typeof(StateKickPlayerConfirmationModal), nameof(StateKickPlayerConfirmationModal.Close))]
internal static class GameModalClosePatch
{
    private static void Prefix(StateKickPlayerConfirmationModal __instance)
    {
        try { GameModal.Closed(__instance); }
        catch (Exception e) { Core.CorePlugin.Logger?.LogError("[Core] pop-up (close): " + e); }
    }
}

[HarmonyPatch(typeof(StateKickPlayerConfirmationModal), nameof(StateKickPlayerConfirmationModal.OptionOneOnClickOrSubmit))]
internal static class GameModalOptionOnePatch
{
    private static bool Prefix(StateKickPlayerConfirmationModal __instance) => GameModal.Pressed(__instance, 0);
}

[HarmonyPatch(typeof(StateKickPlayerConfirmationModal), nameof(StateKickPlayerConfirmationModal.OptionTwoOnClickOrSubmit))]
internal static class GameModalOptionTwoPatch
{
    private static bool Prefix(StateKickPlayerConfirmationModal __instance) => GameModal.Pressed(__instance, 1);
}
