using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime;
using OverTheHillPlus.Multiplayer;
using OverTheHillPlus.ServerCreator;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace OverTheHillPlus.Features.Public.CreateScreen;

/// <summary>
/// Typing the server name right on the Create Server screen: Enter / click on the Server Name row (or picking
/// "TYPE A NAME…") puts a text field over the row's value. The field is a copy of the game's own room-code field from
/// the Join by Code tab (same look and keyboard handling); with a controller the game's on-screen / Steam keyboard
/// opens instead. Names go through the same filter as everywhere else (<see cref="Filter.NameFilter"/>).
///
/// While typing, the game's menu shortcuts are paused (Q/E would switch tabs, Escape would close the browser and
/// Enter would create the server: StateServerBrowser.OnSubmit creates on any Submit while this tab is shown).
/// </summary>
internal static class CreateNameEditor
{
    private const string FieldName = "OTHPlus_NameField";
    private const int MaxLength = 40;

    private static CreateScreenBuilder.Live _row;
    private static TMP_InputField _field;
    private static bool _editing, _gamepad, _seenFocus, _openRequested, _gamepadDone;
    private static float _startedAt, _endedAt = -10f;
    private static readonly List<InputAction> Paused = new();
    private static readonly List<GameObject> HiddenBits = new();
    private static bool _optionWasEnabled;

    /// <summary>Creating the server is held back while typing and just after (the Enter that ends typing).</summary>
    internal static bool BlocksCreate => _editing || _openRequested || Time.unscaledTime - _endedAt < 0.35f;

    internal static bool IsEditing(CreateScreenBuilder.Live row) => (_editing || _openRequested) && row != null && row == _row;

    /// <summary>Opens on the next frame (called from inside the game's arrow/submit handling).</summary>
    internal static void Request()
    {
        _row = CreateScreenBuilder.Find(CreateScreenFeature.NameRowId);
        if (_row == null || _editing) return;
        _openRequested = true;
        _gamepad = FromGamepad();
    }

    internal static void RequestFromSubmit(bool gamepad)
    {
        Request();
        _gamepad = gamepad;
    }

    /// <summary>True when the current press came from a controller rather than the keyboard or mouse.</summary>
    internal static bool FromGamepad()
    {
        try
        {
            var gp = Gamepad.current;
            if (gp == null) return false;
            if (gp.buttonSouth.wasPressedThisFrame || gp.dpad.left.wasPressedThisFrame || gp.dpad.right.wasPressedThisFrame) return true;
            var kb = Keyboard.current;
            return gp.wasUpdatedThisFrame && (kb == null || !kb.anyKey.isPressed);
        }
        catch (Exception ex) { DebugLog.Error("servercreator", ex, "CreateNameEditor.FromGamepad"); return false; }
    }

    // ------------------------------------------------------------------ per frame

    internal static void Tick()
    {
        if (_openRequested)
        {
            _openRequested = false;
            try { Begin(); }
            catch (Exception e)
            {
                DebugLog.Error("servercreator", e, "CreateNameEditor.Tick");
                CreateScreenBuilder.LogOnce("edit", "name field failed: " + e.Message);
                End(commit: false);
            }
            return;
        }
        if (!_editing) return;
        try
        {
            if (_row == null || _row.Go == null || _field == null || _field.WasCollected || _field.gameObject == null) { End(false); return; }
            if (_gamepad)
            {
                if (_gamepadDone) { End(false); return; }
                if (Time.unscaledTime - _startedAt > 0.4f && !GamepadKeyboardOpen()) End(false); // cancelled
                return;
            }
            if (_field.isFocused) { _seenFocus = true; return; }
            if (_seenFocus || Time.unscaledTime - _startedAt > 0.5f) End(commit: _seenFocus);
        }
        catch (Exception e)
        {
            DebugLog.Error("servercreator", e, "CreateNameEditor.Tick");
            CreateScreenBuilder.LogOnce("edit tick", "name field: " + e.Message);
            End(false);
        }
    }

    // ------------------------------------------------------------------ open / close

    private static void Begin()
    {
        var row = _row;
        if (row == null || row.Go == null) return;
        var joinByCode = JoinByCode();
        var template = joinByCode != null ? joinByCode.RoomCodeInputField : null;
        if (template == null)
        {
            // no field to copy: this mod's MODS page has the name box
            CreateScreenBuilder.LogOnce("no field", "room code field not found; the Server Name row opens the MODS page instead");
            UI.ModsPage.Open(ServerCreatorPlugin.ModId);
            return;
        }

        _field = EnsureField(row, template);
        if (_field == null) return;
        _field.text = ServerCreatorConfig.CustomRoomName.Value ?? "";
        _field.gameObject.SetActive(true);
        HideValue(row);
        _editing = true;
        _seenFocus = false;
        _gamepadDone = false;
        _startedAt = Time.unscaledTime;

        if (_gamepad && TryGamepadKeyboard(joinByCode)) return;
        _gamepad = false;
        PauseShortcuts();
        var es = EventSystem.current;
        if (es != null) es.SetSelectedGameObject(_field.gameObject);
        _field.ActivateInputField();
        _field.MoveTextEnd(false);
    }

    private static void End(bool commit)
    {
        bool was = _editing;
        _editing = false;
        _openRequested = false;
        ResumeShortcuts();
        if (!was) return;
        _endedAt = Time.unscaledTime;
        try { if (commit && _field != null && !_field.WasCollected) Commit(_field.text); }
        catch (Exception e) { DebugLog.Error("servercreator", e, "CreateNameEditor.End"); CreateScreenBuilder.LogOnce("commit", "saving the name failed: " + e.Message); }
        try { if (_field != null && !_field.WasCollected && _field.gameObject != null) _field.gameObject.SetActive(false); } catch (Exception ex) { DebugLog.Error("servercreator", ex, "CreateNameEditor.End"); }
        var row = _row;
        try
        {
            if (row != null && row.Go != null)
            {
                ShowValue(row);
                CreateScreenBuilder.Fill(row, force: true);
                var es = EventSystem.current;
                if (es != null && row.Go.activeInHierarchy) es.SetSelectedGameObject(row.Go);
            }
        }
        catch (Exception ex) { DebugLog.Error("servercreator", ex, "CreateNameEditor.End"); }
    }

    /// <summary>The screen went away or was rebuilt.</summary>
    internal static void Reset()
    {
        if (_editing) End(false);
        _openRequested = false;
        ResumeShortcuts();
        _row = null;
        _field = null;
        HiddenBits.Clear();
    }

    /// <summary>Saves a typed name: empty = the game's default; refused names leave the setting as it was.</summary>
    internal static void Commit(string text)
    {
        text = (text ?? "").Replace('\n', ' ').Replace('\r', ' ').Trim();
        if (text.Length > MaxLength)
        {
            text = text.Substring(0, MaxLength);
            if (char.IsHighSurrogate(text[text.Length - 1])) text = text.Substring(0, text.Length - 1);
            text = text.TrimEnd();
        }
        if (text.Length > 0 && Filter.NameFilter.IsBlocked(RoomNames.Escape(text), out string why))
        {
            ServerCreatorPlugin.Logger.LogInfo($"[CreateScreen] Server name not used: {why}");
            var row = _row;
            if (row != null)
            {
                row.Flash = "NAME NOT ALLOWED";
                row.FlashUntil = Time.unscaledTime + 2.5f;
            }
            return;
        }
        if (ServerCreatorConfig.CustomRoomName.Value != text)
        {
            ServerCreatorConfig.CustomRoomName.Value = text;
            try { ServerCreatorConfig.File.Save(); } catch (Exception ex) { DebugLog.Error("servercreator", ex, "CreateNameEditor.Commit"); }
        }
        CreateScreenFeature.NameTyped(text);
    }

    // ------------------------------------------------------------------ the field

    private static ContextUIServerJoinByCode JoinByCode()
    {
        var screen = CreateScreenBuilder.Screen;
        if (screen == null) return null;
        try
        {
            var menu = screen.GetComponentInParent<ContextUIServerListMenu>(true);
            if (menu != null && menu.ContextUIServerJoinByCode != null) return menu.ContextUIServerJoinByCode;
        }
        catch (Exception ex) { DebugLog.Error("servercreator", ex, "CreateNameEditor.JoinByCode"); }
        return null;
    }

    private static TMP_InputField EnsureField(CreateScreenBuilder.Live row, TMP_InputField template)
    {
        var host = row.Carousel.transform;
        var existing = host.Find(FieldName);
        if (existing != null)
        {
            var f = existing.GetComponent<TMP_InputField>();
            if (f != null) return f;
            Object.Destroy(existing.gameObject);
        }

        var go = Object.Instantiate(template.gameObject, host);
        go.name = FieldName;
        go.SetActive(false);
        foreach (var c in go.GetComponentsInChildren<Component>(true))
        {
            if (c == null) continue;
            var typeName = c.GetIl2CppType().FullName ?? "";
            if (typeName.Contains("Locali", StringComparison.OrdinalIgnoreCase)) Object.DestroyImmediate(c);
        }
        var field = go.GetComponent<TMP_InputField>();
        if (field == null) { Object.Destroy(go); return null; }

        // Fresh events: the copy must never run anything wired to the room-code field (joining by code).
        field.onSubmit = new TMP_InputField.SubmitEvent();
        field.onEndEdit = new TMP_InputField.SubmitEvent();
        field.onValueChanged = new TMP_InputField.OnChangeEvent();
        field.onSelect = new TMP_InputField.SelectionEvent();
        field.onDeselect = new TMP_InputField.SelectionEvent();
        field.onValidateInput = null;
        field.inputValidator = null;
        field.contentType = TMP_InputField.ContentType.Standard;
        field.lineType = TMP_InputField.LineType.SingleLine;
        field.characterValidation = TMP_InputField.CharacterValidation.None;
        field.characterLimit = MaxLength;
        field.richText = false;
        var nav = field.navigation;
        nav.mode = Navigation.Mode.None;
        field.navigation = nav;

        var rt = go.transform.TryCast<RectTransform>();
        if (rt != null)
        {
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
            rt.localScale = Vector3.one;
            rt.localRotation = Quaternion.identity;
        }
        var le = go.GetComponent<LayoutElement>() ?? go.AddComponent<LayoutElement>();
        le.ignoreLayout = true;

        var value = row.Carousel.Option;
        if (value != null)
        {
            try { field.pointSize = value.fontSize; } catch (Exception ex) { DebugLog.Error("servercreator", ex, "CreateNameEditor.EnsureField"); }
            try { if (field.textComponent != null) field.textComponent.alignment = value.alignment; } catch (Exception ex) { DebugLog.Error("servercreator", ex, "CreateNameEditor.EnsureField"); }
        }
        try
        {
            var ph = field.placeholder != null ? field.placeholder.TryCast<TMP_Text>() : null;
            if (ph != null)
            {
                ph.text = "Your name's room";
                if (value != null) ph.alignment = value.alignment;
            }
        }
        catch (Exception ex) { DebugLog.Error("servercreator", ex, "CreateNameEditor.EnsureField"); }
        go.transform.SetAsLastSibling();
        return field;
    }

    private static void HideValue(CreateScreenBuilder.Live row)
    {
        HiddenBits.Clear();
        var car = row.Carousel;
        var value = car.Option;
        _optionWasEnabled = value != null && value.enabled;
        if (value != null) value.enabled = false;
        foreach (var b in new Component[] { car.Left, car.Right })
        {
            if (b == null || !b.gameObject.activeSelf) continue;
            b.gameObject.SetActive(false);
            HiddenBits.Add(b.gameObject);
        }
    }

    private static void ShowValue(CreateScreenBuilder.Live row)
    {
        var value = row.Carousel != null ? row.Carousel.Option : null;
        if (value != null && _optionWasEnabled) value.enabled = true;
        foreach (var go in HiddenBits)
        {
            try { if (go != null) go.SetActive(true); } catch (Exception ex) { DebugLog.Error("servercreator", ex, "CreateNameEditor.ShowValue"); }
        }
        HiddenBits.Clear();
    }

    // ------------------------------------------------------------------ controller keyboard

    private static GamepadTextInput _gti;

    private static bool TryGamepadKeyboard(ContextUIServerJoinByCode joinByCode)
    {
        try
        {
            _gti = joinByCode.gamepadTextInput;
            if (_gti == null) return false;
            _gti.Open(_field, "SERVER NAME", DelegateSupport.ConvertDelegate<Il2CppSystem.Action<string>>(new Action<string>(text =>
            {
                try { Commit(text); }
                catch (Exception e) { DebugLog.Error("servercreator", e, "CreateNameEditor.TryGamepadKeyboard"); CreateScreenBuilder.LogOnce("pad commit", "saving the name failed: " + e.Message); }
                _gamepadDone = true;
            })), GamepadVirtualKeyboard.InputMode.Text);
            return true;
        }
        catch (Exception e)
        {
            DebugLog.Error("servercreator", e, "CreateNameEditor.TryGamepadKeyboard");
            CreateScreenBuilder.LogOnce("pad", "controller keyboard failed, using the text field: " + e.Message);
            return false;
        }
    }

    private static bool GamepadKeyboardOpen()
    {
        try { return _gti != null && !_gti.WasCollected && _gti.IsOpen; }
        catch (Exception ex) { DebugLog.Error("servercreator", ex, "CreateNameEditor.GamepadKeyboardOpen"); return false; }
    }

    // ------------------------------------------------------------------ shortcuts

    private static void PauseShortcuts()
    {
        ResumeShortcuts();
        try
        {
            var pia = GameInput.PlayerInputActions;
            if (pia == null) return;
            foreach (var a in new[]
                     {
                         pia.m_UI_SwitchMenuLeft, pia.m_UI_SwitchMenuRight, pia.m_UI_Submit, pia.m_UI_Cancel,
                         pia.m_UI_ServerListFilter, pia.m_UI_ServerListSortBy, pia.m_UI_ServerListSortDirection,
                         pia.m_UI_GoToGarage, pia.m_UI_OpenPauseMenu,
                     })
            {
                if (a == null || !a.enabled) continue;
                a.Disable();
                Paused.Add(a);
            }
        }
        catch (Exception e) { DebugLog.Error("servercreator", e, "CreateNameEditor.PauseShortcuts"); CreateScreenBuilder.LogOnce("pause keys", "could not pause the menu shortcuts while typing: " + e.Message); }
    }

    private static void ResumeShortcuts()
    {
        foreach (var a in Paused)
        {
            try
            {
                if (a == null || a.WasCollected || a.enabled) continue;
                var map = a.actionMap;
                // the game may have switched input maps meanwhile: don't turn on actions of a map it turned off
                if (map == null || map.enabled) a.Enable();
            }
            catch (Exception ex) { DebugLog.Error("servercreator", ex, "CreateNameEditor.ResumeShortcuts"); }
        }
        Paused.Clear();
    }
}
