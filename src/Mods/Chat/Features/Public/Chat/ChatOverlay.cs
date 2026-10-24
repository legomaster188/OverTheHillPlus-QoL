using System;
using OverTheHillPlus.UI;
using UnityEngine;
using UnityEngine.InputSystem;
using Net = OverTheHillPlus.Features.Public.ModNet.ModNet;

namespace OverTheHillPlus.Features.Public.Chat;

/// <summary>
/// The chat box: a hotkey opens a one-line text field (while it is open the game's input is switched to its console
/// map, exactly like the mod menu does, so typing never drives the car), Enter sends, Escape cancels. Above it, the
/// last messages; each fades out a while after it arrived and all of them show again while the box is open.
///
/// Drawn with plain IMGUI and no rich text: a message containing tags shows them as typed.
/// </summary>
internal static class ChatOverlay
{
    private const string FieldName = "othp.chat.input";
    private const float PanelWidth = 560f, Margin = 18f, Pad = 7f, LineGap = 3f, InputHeight = 32f;

    private static bool _open;
    private static string _draft = "";
    private static int _openedFrame = -10;
    private static int _focusTries;
    private static bool _fieldFocused;
    private static bool _clearFocus;
    private static string _hint;
    private static float _hintAt = -100f;

    // input map (same as the menu: push the game's console map while typing)
    private static Il2CppSystem.Guid? _inputMap;
    private static bool _popPending;
    private static float _popRequestedAt;

    private static GUIStyle _name, _text, _field, _small;

    public static bool IsOpen => _open;

    /// <summary>Frame the box last closed (MenuUx keeps hotkeys off for one more frame after it).</summary>
    public static int ClosedFrame { get; private set; } = -10;

    // The game rebuilt its input stack while the box was open (our map is gone): checked every few frames.
    private static int _nextMapCheckFrame;

    // ---------------------------------------------------------------- per frame (Update)

    internal static void Tick()
    {
        try { TickCore(); }
        catch (Exception e)
        {
            DebugLog.Error("chat", e, "ChatOverlay.Tick");
            // Never leave the game's input switched off because of an error here.
            ChatNet.LogOnce("tick", "[Chat] tick failed: " + e);
            try { Close(null); } catch (Exception ex) { DebugLog.Error("chat", ex, "ChatOverlay.Tick"); }
        }
    }

    private static void TickCore()
    {
        if (_popPending) TryPopMap();

        var kb = Keyboard.current;
        if (_open)
        {
            // Every mod hotkey (menu key, HUD, unstuck...) is ignored while typing a message.
            MenuUx.SuppressHotkeysThisFrame();
            if (!StillAllowed(out string why))
            {
                Close(why != null ? "Chat closed: " + why : null);
                return;
            }
            // Respawns, state changes and portals can rebuild the game's input stack (StateGame replaces it when
            // returning to driving). Then typing would drive the car again: close the box rather than fight the game.
            if (_inputMap != null && Time.frameCount >= _nextMapCheckFrame)
            {
                _nextMapCheckFrame = Time.frameCount + 5;
                bool active = true;
                try { active = GameInput.IsMapActive(GameInput.Map.Terminal); }
                catch (Exception e) { DebugLog.Error("chat", e, "ChatOverlay.TickCore"); ChatNet.LogOnce("mapactive", "[Chat] could not check the input map: " + e.Message); }
                if (!active)
                {
                    _inputMap = null; // not in the stack any more: nothing to pop
                    Close("Chat closed: the game took the controls back");
                    return;
                }
            }
            if (kb != null)
            {
                // IMGUI handles Enter / Escape while the field has focus; these cover a field that lost it.
                bool esc = false, enter = false;
                try { esc = kb.escapeKey.wasPressedThisFrame; enter = kb.enterKey.wasPressedThisFrame || kb.numpadEnterKey.wasPressedThisFrame; }
                catch (Exception ex) { DebugLog.Error("chat", ex, "ChatOverlay.TickCore"); }
                if (esc) Close(null);
                else if (enter && !_fieldFocused && Time.frameCount - _openedFrame > 3) Submit();
            }
            return;
        }

        if (kb == null || !ChatFeature.Enabled.Value) return;
        if (MenuUx.MenuVisible || MenuUx.HotkeysCaptured || KeybindEditor.Capturing) return;
        if (Pressed(kb, ChatFeature.ChatKey.Value) || Pressed(kb, ChatFeature.ChatAltKey.Value)) TryOpen();
    }

    private static bool Pressed(Keyboard kb, Key key)
    {
        if (key == Key.None) return false;
        try { return kb[key].wasPressedThisFrame; } catch (Exception ex) { DebugLog.Error("chat", ex, "ChatOverlay.Pressed"); return false; }
    }

    /// <summary>Opens the box (from the hotkey, or from the menu's button once the menu is closed).</summary>
    internal static void TryOpen()
    {
        if (_open) return;
        if (!InMultiplayer()) return;          // the key does nothing outside a session (it may be bound to a letter)
        if (!Driving()) return;                // not in the pause menu, map, photo mode, cutscenes...
        if (CoreBehaviour.GuiHidden) return;
        _open = true;
        _draft = "";
        _openedFrame = Time.frameCount;
        _focusTries = 0;
        _fieldFocused = false;
        _popPending = false;
        _nextMapCheckFrame = Time.frameCount + 10; // let the pushed map settle before checking it is still there
        MenuUx.SuppressHotkeysThisFrame();
        try { if (_inputMap == null) _inputMap = GameInput.PushMap(GameInput.Map.Terminal, 1000); }
        catch (Exception e) { DebugLog.Error("chat", e, "ChatOverlay.TryOpen"); ChatNet.LogOnce("map", "[Chat] could not switch the input map: " + e.Message); }
        if (!ChatNet.CanSend(out string why)) SetHint("Can't send: " + why);
    }

    private static void Close(string hint)
    {
        if (!_open) return;
        _open = false;
        ClosedFrame = Time.frameCount;
        _draft = "";
        _clearFocus = true;
        if (hint != null) SetHint(hint);
        // Give the game its input back once Enter / Escape are released, so the key that closed the box doesn't also
        // reach the game (Enter is "interact" while driving, Escape opens the pause menu).
        _popPending = true;
        _popRequestedAt = Time.unscaledTime;
        TryPopMap();
    }

    private static void TryPopMap()
    {
        if (_inputMap == null) { _popPending = false; return; }
        bool held = false;
        try
        {
            var kb = Keyboard.current;
            held = kb != null && (kb.enterKey.isPressed || kb.numpadEnterKey.isPressed || kb.escapeKey.isPressed);
        }
        catch (Exception ex) { DebugLog.Error("chat", ex, "ChatOverlay.TryPopMap"); }
        if (held && Time.unscaledTime - _popRequestedAt < 1.5f) return;
        try { GameInput.PopMap(_inputMap.Value); }
        catch (Exception e) { DebugLog.Error("chat", e, "ChatOverlay.TryPopMap"); ChatNet.LogOnce("pop", "[Chat] could not restore the input map: " + e.Message); }
        _inputMap = null;
        _popPending = false;
    }

    private static void Submit()
    {
        string text = _draft;
        if (string.IsNullOrWhiteSpace(text)) { Close(null); return; }
        string result = ChatNet.Send(text);
        if (result == null) { Close(null); return; }
        // Keep the box open on a block so the text can be fixed; a rate limit or filter hit is shown above it.
        SetHint(result);
        try { Menu.SetStatus(result); } catch (Exception ex) { DebugLog.Error("chat", ex, "ChatOverlay.Submit"); }
    }

    private static void SetHint(string s)
    {
        _hint = s;
        _hintAt = Time.unscaledTime;
    }

    private static bool StillAllowed(out string why)
    {
        why = null;
        if (!ChatFeature.Enabled.Value) { why = "chat was switched off"; return false; }
        if (!InMultiplayer()) { why = "left the session"; return false; }
        if (MenuUx.MenuVisible) return false; // the mod menu took over: just close quietly
        return true;
    }

    private static bool InMultiplayer() => Net.IsHost || Net.IsGuest;

    /// <summary>The game's top input map is Driving (unknown counts as yes, so a renamed API doesn't kill chat).</summary>
    private static bool Driving()
    {
        try
        {
            var current = GameInput.CurrentMap;
            if (current == null || !current.HasValue) return true;
            return current.Value == GameInput.Map.Driving;
        }
        catch (Exception e)
        {
            DebugLog.Error("chat", e, "ChatOverlay.Driving");
            ChatNet.LogOnce("currentmap", "[Chat] could not read the game's input map: " + e.Message);
            return true;
        }
    }

    // ---------------------------------------------------------------- drawing (OnGUI)

    internal static void OnGui()
    {
        try { OnGuiCore(); }
        catch (Exception e)
        {
            DebugLog.Error("chat", e, "ChatOverlay.OnGui");
            ChatNet.LogOnce("gui", "[Chat] drawing failed: " + e);
            try { Close("Chat closed after an error (see the log)"); } catch (Exception ex) { DebugLog.Error("chat", ex, "ChatOverlay.OnGui"); }
        }
    }

    private static void OnGuiCore()
    {
        var e = Event.current;
        if (e == null) return;
        if (_clearFocus)
        {
            _clearFocus = false;
            if (GUI.GetNameOfFocusedControl() == FieldName) GUIUtility.keyboardControl = 0;
        }
        if (CoreBehaviour.GuiHidden) return;
        if (!_open && (!ChatFeature.Enabled.Value || !ChatFeature.ShowOverlay.Value || CoreBehaviour.CleanView)) return;
        if (!_open && ChatNet.History.Count == 0 && Time.unscaledTime - _hintAt > 4f) return;
        if (!_open && MenuUx.MenuVisible) return; // the menu has a "recent messages" card

        Theme.Ensure();
        EnsureStyles();
        float scale = Mathf.Clamp(ChatFeature.TextScale.Value, 0.6f, 2f) * Mathf.Clamp(Screen.height / 1080f, 0.6f, 2.5f);
        GUI.matrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, new Vector3(scale, scale, 1f));
        float sw = Screen.width / scale, sh = Screen.height / scale;
        float w = Mathf.Min(PanelWidth, sw - Margin * 2f);
        float x = Margin;

        // Which lines: while open the latest ones in full, otherwise only those still fading.
        int maxLines = Mathf.Clamp(ChatFeature.MaxLines.Value, 1, 20);
        float fade = Mathf.Clamp(ChatFeature.FadeSeconds.Value, 2f, 120f);
        float now = Time.unscaledTime;
        var hist = ChatNet.History;
        int first = hist.Count;
        int count = 0;
        for (int i = hist.Count - 1; i >= 0 && count < (_open ? Math.Min(20, maxLines * 2) : maxLines); i--)
        {
            if (!_open && now - hist[i].At > fade) break;
            first = i;
            count++;
        }

        // Heights first, so the block can be anchored at the bottom.
        float textW = w - Pad * 2f;
        float total = 0f;
        for (int i = first; i < hist.Count; i++) total += Measure(hist[i], textW, scale) + LineGap;
        bool showHint = _hint != null && now - _hintAt < (_open ? 6f : 4f);
        float hintH = showHint ? 22f : 0f;
        float inputH = _open ? InputHeight + 4f : 0f;
        total += hintH + inputH;

        var pos = (ChatCorner)Mathf.Clamp(ChatFeature.Position.Value, 0, 2);
        float top = pos switch
        {
            ChatCorner.TopLeft => 90f,
            ChatCorner.MiddleLeft => sh * 0.55f - total,
            _ => sh - 150f - total,
        };
        top = Mathf.Clamp(top, 8f, Mathf.Max(8f, sh - total - 8f));

        var prevColor = GUI.color;
        float y = top;
        for (int i = first; i < hist.Count; i++)
        {
            var line = hist[i];
            float a = _open ? 1f : Mathf.Clamp01((fade - (now - line.At)) / 1.5f);
            DrawLine(line, x, y, w, a);
            y += line.Height + LineGap;
        }
        GUI.color = prevColor;

        if (showHint)
        {
            float a = Mathf.Clamp01(((_open ? 6f : 4f) - (now - _hintAt)) / 1f);
            GUI.color = new Color(1f, 1f, 1f, a);
            var hr = new Rect(x, y, w, 20f);
            if (e.type == EventType.Repaint) Theme.Fill(hr, new Color(0.07f, 0.09f, 0.11f, 0.8f), 4);
            _small.normal.textColor = _hint.StartsWith("Blocked", StringComparison.Ordinal) || _hint.StartsWith("Can't", StringComparison.Ordinal) ? Theme.Warn
                : _hint.StartsWith("Failed", StringComparison.Ordinal) ? Theme.Bad : Theme.Muted;
            GUI.Label(new Rect(hr.x + Pad, hr.y, hr.width - Pad * 2f, hr.height), _hint, _small);
            GUI.color = prevColor;
            y += hintH;
        }

        if (_open) DrawInput(e, x, y + 4f, w);
        else _fieldFocused = false;
    }

    private static float Measure(ChatNet.Line line, float textW, float scale)
    {
        if (Mathf.Approximately(line.CacheWidth, textW) && Mathf.Approximately(line.CacheScale, scale)) return line.Height;
        line.CacheWidth = textW;
        line.CacheScale = scale;
        line.NameWidth = 0f;
        if (line.Kind != ChatNet.LineKind.System && !string.IsNullOrEmpty(line.Name))
            line.NameWidth = Mathf.Min(textW * 0.45f, _name.CalcSize(new GUIContent(line.Name + ":")).x + 6f);
        float h = _text.CalcHeight(new GUIContent(line.Text), Mathf.Max(40f, textW - line.NameWidth));
        line.Height = Mathf.Max(22f, h) + 4f;
        return line.Height;
    }

    private static void DrawLine(ChatNet.Line line, float x, float y, float w, float alpha)
    {
        if (alpha <= 0.01f) return;
        GUI.color = new Color(1f, 1f, 1f, alpha);
        var r = new Rect(x, y, w, line.Height);
        if (Event.current.type == EventType.Repaint)
        {
            Theme.Fill(r, new Color(0.05f, 0.07f, 0.09f, 0.62f), 5);
            if (line.Kind == ChatNet.LineKind.System) Theme.Fill(new Rect(r.x, r.y, 3f, r.height), Theme.Warn, 1);
        }
        float tx = x + Pad;
        if (line.NameWidth > 0f)
        {
            _name.normal.textColor = line.NameColor;
            GUI.Label(new Rect(tx, y + 2f, line.NameWidth, 22f), line.Name + ":", _name);
            tx += line.NameWidth;
        }
        _text.normal.textColor = line.Kind == ChatNet.LineKind.System ? Theme.Muted : Theme.Text;
        GUI.Label(new Rect(tx, y + 2f, w - Pad - (tx - x), line.Height - 4f), line.Text, _text);
    }

    private static void DrawInput(Event e, float x, float y, float w)
    {
        // The key that opened the box must not end up as its first letter.
        if (e.type == EventType.KeyDown && Time.frameCount - _openedFrame <= 1 && string.IsNullOrEmpty(_draft))
        {
            e.Use();
            return;
        }
        bool focused = GUI.GetNameOfFocusedControl() == FieldName;
        _fieldFocused = focused;
        if (e.type == EventType.KeyDown && focused)
        {
            if (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter || e.character == '\n')
            {
                e.Use();
                if (e.keyCode != KeyCode.None) Submit(); // the '\n' character event that follows is just eaten
                return;
            }
            if (e.keyCode == KeyCode.Escape)
            {
                e.Use();
                Close(null);
                return;
            }
        }

        var box = new Rect(x, y, w, InputHeight);
        if (e.type == EventType.Repaint)
        {
            Theme.Fill(new Rect(box.x - 1f, box.y - 1f, box.width + 2f, box.height + 2f), Theme.Accent, 7);
            Theme.Fill(box, new Color(0.06f, 0.08f, 0.10f, 0.95f), 6);
        }
        GUI.SetNextControlName(FieldName);
        string v = GUI.TextField(new Rect(box.x + 2f, box.y + 2f, box.width - 64f, box.height - 4f), _draft ?? "", ChatNet.MaxChars, _field);
        if (v != _draft) _draft = v.Replace("\n", "").Replace("\r", "");
        if (!focused && _focusTries < 30)
        {
            _focusTries++;
            GUI.FocusControl(FieldName);
        }
        if (string.IsNullOrEmpty(_draft))
        {
            _small.normal.textColor = Theme.Muted;
            GUI.Label(new Rect(box.x + 12f, box.y, box.width - 80f, box.height), Placeholder(), _small);
        }
        _small.normal.textColor = (_draft?.Length ?? 0) > ChatNet.MaxChars - 15 ? Theme.Warn : Theme.Muted;
        _small.alignment = TextAnchor.MiddleRight;
        GUI.Label(new Rect(box.xMax - 60f, box.y, 52f, box.height), $"{_draft?.Length ?? 0}/{ChatNet.MaxChars}", _small);
        _small.alignment = TextAnchor.MiddleLeft;
    }

    private static string _placeholder;
    private static float _placeholderAt = -100f;

    private static string Placeholder()
    {
        if (Time.unscaledTime - _placeholderAt < 1f && _placeholder != null) return _placeholder;
        _placeholderAt = Time.unscaledTime;
        if (!ChatNet.CanSend(out string why)) _placeholder = "Can't send: " + why + "  (Esc closes)";
        else if (Net.IsHost)
        {
            int n = ChatNet.ReadersAsHost();
            _placeholder = n == 0 ? "Say something (nobody with this mod and chat on yet)  ·  Enter sends, Esc cancels"
                : $"Say something to {n} player(s) with this mod  ·  Enter sends, Esc cancels";
        }
        else _placeholder = "Say something to players with this mod  ·  Enter sends, Esc cancels";
        return _placeholder;
    }

    private static void EnsureStyles()
    {
        if (_name != null && _text != null && _field != null && _small != null) return;
        _name = new GUIStyle(Theme.Label) { richText = false, wordWrap = false, fontStyle = FontStyle.Bold, fontSize = 15, alignment = TextAnchor.UpperLeft, clipping = TextClipping.Clip };
        _text = new GUIStyle(Theme.Label) { richText = false, wordWrap = true, fontSize = 15, alignment = TextAnchor.UpperLeft, clipping = TextClipping.Clip };
        _text.normal.textColor = Theme.Text;
        _small = new GUIStyle(Theme.Small) { richText = false, wordWrap = false, fontSize = 12, alignment = TextAnchor.MiddleLeft, clipping = TextClipping.Clip };
        _field = new GUIStyle(Theme.TextField) { richText = false, fontSize = 15 };
        _field.normal.background = null;
        _field.focused.background = null;
        _field.hover.background = null;
        _field.active.background = null;
        _field.normal.textColor = Theme.Text;
        _field.focused.textColor = Theme.Text;
    }
}

/// <summary>Where the chat sits on screen (stored as an int in the config).</summary>
internal enum ChatCorner { BottomLeft = 0, MiddleLeft = 1, TopLeft = 2 }
