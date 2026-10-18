using System.Collections.Generic;
using OverTheHillPlus.Features.Public;
using UnityEngine;
using UnityEngine.InputSystem;

namespace OverTheHillPlus.UI.Native;

internal enum NavKind
{
    Button,
    Switch,
    Slider,
    Fold,
    Text,
}

/// <summary>
/// Keyboard / gamepad / mouse-hover selection for the native menu, in the game's own style: one selected row at a
/// time (drawn with the game's highlight bar), arrows or the d-pad move it, Enter / Space / A activates, left/right
/// changes sliders and switches, Q / E (or the shoulder buttons) change tabs, Page Up/Down scroll.
///
/// Widgets register in draw order during the Repaint pass (<see cref="Register"/>); keys are read in Update
/// (<see cref="Tick"/>) and applied to the next Repaint, so every action happens exactly once per frame.
/// </summary>
internal static class MenuNav
{
    internal static int Focus = -1;

    // True when the selection was last moved by the keyboard or gamepad. Enter / Space / A only activate a selection
    // the keyboard owns: a mouse resting over a button must never turn a stray Space press into a click.
    private static bool _focusByKeyboard;

    // pending input from Update
    private static int _move, _side, _tabStep, _modeStep, _page;
    private static bool _activate, _home, _end;

    // this frame
    private static bool _active;
    private static int _count;
    private static bool _mouseMoved, _mouseInContent, _keyboardMoved;
    private static Vector2 _lastMouse = new(-1f, -1f);
    private static readonly List<NavKind> Kinds = new(), PrevKinds = new();
    private static readonly List<Rect> Rects = new();
    private static int _pendingSide;
    private static bool _pendingActivate;

    /// <summary>Scroll position (content y) the menu should jump to next frame, if any.</summary>
    internal static float? ScrollRequest;

    internal static bool UsedKeyboardRecently => Time.unscaledTime - _lastKeyAt < 4f;
    private static float _lastKeyAt = -100f;

    // ---------- input (Update) ----------

    /// <summary>Reads navigation keys. Called every frame while the native menu is open.</summary>
    internal static void Tick()
    {
        if (!NativeStyle.KeyboardNavOn || MenuUx.Typing || MenuUx.HotkeysCaptured || KeybindEditor.Capturing) { Held.Clear(); return; }
        var kb = Keyboard.current;
        if (kb != null)
        {
            bool shift = kb.shiftKey.isPressed;
            if (Rep(1, kb.upArrowKey)) Key(ref _move, -1);
            if (Rep(2, kb.downArrowKey)) Key(ref _move, 1);
            if (Rep(3, kb.leftArrowKey)) Key(ref _side, -1);
            if (Rep(4, kb.rightArrowKey)) Key(ref _side, 1);
            if (kb.enterKey.wasPressedThisFrame || kb.numpadEnterKey.wasPressedThisFrame || kb.spaceKey.wasPressedThisFrame) { _activate = true; _lastKeyAt = Time.unscaledTime; }
            if (kb.qKey.wasPressedThisFrame) { if (shift) _modeStep--; else _tabStep--; _lastKeyAt = Time.unscaledTime; }
            if (kb.eKey.wasPressedThisFrame) { if (shift) _modeStep++; else _tabStep++; _lastKeyAt = Time.unscaledTime; }
            if (Rep(5, kb.pageUpKey)) Key(ref _page, -1);
            if (Rep(6, kb.pageDownKey)) Key(ref _page, 1);
            if (kb.homeKey.wasPressedThisFrame) _home = true;
            if (kb.endKey.wasPressedThisFrame) _end = true;
            if (kb.tabKey.wasPressedThisFrame) { _viewToggle = true; _lastKeyAt = Time.unscaledTime; }
        }
        Gamepad pad = null;
        try { pad = Gamepad.current; } catch (System.Exception ex) { DebugLog.Error("core", ex, "MenuNav.Tick"); }
        if (pad != null)
        {
            if (Rep(11, pad.dpad.up)) Key(ref _move, -1);
            if (Rep(12, pad.dpad.down)) Key(ref _move, 1);
            if (Rep(13, pad.dpad.left)) Key(ref _side, -1);
            if (Rep(14, pad.dpad.right)) Key(ref _side, 1);
            if (pad.buttonSouth.wasPressedThisFrame) { _activate = true; _lastKeyAt = Time.unscaledTime; }
            if (pad.leftShoulder.wasPressedThisFrame) _tabStep--;
            if (pad.rightShoulder.wasPressedThisFrame) _tabStep++;
            if (pad.leftTrigger.wasPressedThisFrame) _modeStep--;
            if (pad.rightTrigger.wasPressedThisFrame) _modeStep++;
            if (pad.selectButton.wasPressedThisFrame) _viewToggle = true;
        }
    }

    // Tab (keyboard) / Select (gamepad): switch between the Simple and Advanced views.
    private static bool _viewToggle;

    /// <summary>True once after Tab / Select was pressed (switch Simple / Advanced).</summary>
    internal static bool TakeViewToggle() { bool v = _viewToggle; _viewToggle = false; return v; }

    private static void Key(ref int field, int step)
    {
        field += step;
        _lastKeyAt = Time.unscaledTime;
    }

    private static readonly Dictionary<int, float> Held = new();

    /// <summary>Pressed this frame, or held long enough to auto-repeat.</summary>
    private static bool Rep(int id, UnityEngine.InputSystem.Controls.ButtonControl key)
    {
        if (key == null) return false;
        float now = Time.unscaledTime;
        if (key.wasPressedThisFrame) { Held[id] = now + 0.38f; return true; }
        if (!key.isPressed) { Held.Remove(id); return false; }
        if (Held.TryGetValue(id, out var next) && now >= next) { Held[id] = now + 0.055f; return true; }
        return false;
    }

    /// <summary>Tab steps (Q/E) since the last call.</summary>
    internal static int TakeTabStep() { int v = _tabStep; _tabStep = 0; return v; }

    /// <summary>Menu steps (Shift+Q/E) since the last call.</summary>
    internal static int TakeModeStep() { int v = _modeStep; _modeStep = 0; return v; }

    internal static void ClearPending()
    {
        _move = _side = _tabStep = _modeStep = _page = 0;
        _activate = _home = _end = false;
        _viewToggle = false;
        _pendingActivate = false;
        _pendingSide = 0;
    }

    // ---------- frame (Repaint) ----------

    /// <summary>Starts the Repaint pass: applies moves from the keyboard to the selection.</summary>
    internal static void BeginFrame(bool repaint, Vector2 mouse, bool mouseInContent)
    {
        _active = repaint && NativeStyle.KeyboardNavOn;
        if (!_active) return;
        _mouseMoved = (mouse - _lastMouse).sqrMagnitude > 0.5f && _lastMouse.x >= 0f;
        _lastMouse = mouse;
        _mouseInContent = mouseInContent;
        _count = 0;
        Kinds.Clear();
        Rects.Clear();
        _keyboardMoved = false;

        int last = PrevKinds.Count;
        int before = Focus;
        if (_home && last > 0) { Focus = 0; _keyboardMoved = true; }
        if (_end && last > 0) { Focus = last - 1; _keyboardMoved = true; }
        if (_move != 0 && last > 0)
        {
            Focus = Focus < 0 ? (_move > 0 ? 0 : last - 1) : Mathf.Clamp(Focus + _move, 0, last - 1);
            _keyboardMoved = true;
        }
        _pendingSide = 0;
        if (_side != 0 && last > 0)
        {
            if (Focus < 0) { Focus = 0; _keyboardMoved = true; }
            else
            {
                var kind = PrevKinds[Mathf.Clamp(Focus, 0, last - 1)];
                if (kind is NavKind.Slider or NavKind.Switch) _pendingSide = _side;
                else { Focus = Mathf.Clamp(Focus + _side, 0, last - 1); _keyboardMoved = true; }
            }
        }
        _pendingActivate = _activate && Focus >= 0;
        if (_activate && Focus < 0 && last > 0) { Focus = 0; _keyboardMoved = true; }
        if (_keyboardMoved) _focusByKeyboard = true;
        if (_pendingActivate && !_focusByKeyboard)
        {
            // Selection came from mouse hover: this press only adopts it (no click); the next press activates.
            _pendingActivate = false;
            _focusByKeyboard = true;
        }
        if (Focus != before && Focus >= 0) NativeLook.PlayHover();
        _move = _side = 0;
        _activate = _home = _end = false;
    }

    /// <summary>
    /// A selectable widget (content coordinates). Returns its index; <paramref name="focused"/> says whether to draw
    /// the selection, <paramref name="activated"/> is a keyboard Enter/A for it this frame, <paramref name="side"/> a
    /// left/right step for sliders and switches.
    /// </summary>
    internal static int Register(Rect r, NavKind kind, out bool focused, out bool activated, out int side)
    {
        focused = activated = false;
        side = 0;
        if (!_active) return -1;
        int id = _count++;
        Kinds.Add(kind);
        Rects.Add(r);
        if (_mouseMoved && _mouseInContent && r.Contains(Event.current.mousePosition) && Focus != id)
        {
            Focus = id;
            _focusByKeyboard = false;
            _pendingActivate = false;
            NativeLook.PlayHover();
        }
        if (id != Focus) return id;
        focused = true;
        if (_pendingActivate) { activated = true; _pendingActivate = false; }
        if (_pendingSide != 0) { side = _pendingSide; _pendingSide = 0; }
        return id;
    }

    /// <summary>Ends the Repaint pass: remembers the items and asks for a scroll that keeps the selection visible.</summary>
    internal static void EndFrame(float scrollY, float viewH)
    {
        if (!_active) return;
        _active = false;
        PrevKinds.Clear();
        PrevKinds.AddRange(Kinds);
        if (Focus >= _count) Focus = _count - 1;
        _pendingActivate = false;

        if (_page != 0) ScrollRequest = Mathf.Max(0f, scrollY + _page * viewH * 0.85f);
        else if (_keyboardMoved && Focus >= 0 && Focus < Rects.Count)
        {
            var r = Rects[Focus];
            if (r.y < scrollY + 6f) ScrollRequest = Mathf.Max(0f, r.y - 12f);
            else if (r.yMax > scrollY + viewH - 6f) ScrollRequest = r.yMax - viewH + 12f;
        }
        _page = 0;
    }

    /// <summary>Forgets the selection (new tab or menu reopened).</summary>
    internal static void Reset(bool keepFirst)
    {
        Focus = keepFirst ? 0 : -1;
        _focusByKeyboard = keepFirst;
        PrevKinds.Clear();
    }
}
