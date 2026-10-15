using System;
using OverTheHillPlus.ServerBrowser;
using OverTheHillPlus.UI;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;

namespace OverTheHillPlus;

/// <summary>Per-frame driver: hotkeys, cached lookups, deferred session updates, saving, and the menu.</summary>
public class ModBehaviour : MonoBehaviour
{
    public ModBehaviour(IntPtr ptr) : base(ptr) { }

    private static ModBehaviour _instance;

    private bool _menuOpen;
    private bool _savedCursorVisible;
    private CursorLockMode _savedLockState;
    private EventSystem _pausedEventSystem;
    private Il2CppSystem.Guid? _inputMap;

    private void Awake() => _instance = this;

    /// <summary>Opens the menu, optionally on a given tab (used by the in-game PLUS buttons).</summary>
    public static void OpenMenu(string tab = null)
    {
        if (tab != null) Menu.SelectTab(tab);
        if (_instance != null && !_instance._menuOpen) _instance.SetMenuOpen(true);
    }

    private void Update()
    {
        try
        {
            GameState.Tick();
            SessionThrottle.Tick();
            FirstSeenTracker.SaveIfDue();
            GameUiButtons.Tick();
            Qol.Tick();
            Features.Registry.RunTicks();
            Multiplayer.JoinMessage.Tick();
            Multiplayer.BigLobby.Tick();
        }
        catch (Exception e) { LogThrottled("ModBehaviour.Update ticks", e); }

        // Separate from the ticks above: a tick that keeps failing must not also take the menu hotkey with it.
        try
        {
            var kb = Keyboard.current;
            if (kb == null) return;
            if (Menu.CloseRequested) { Menu.CloseRequested = false; if (_menuOpen) SetMenuOpen(false); }
            // No hotkeys while a key is being captured for a rebind, and none (bar a non-typing menu key) while
            // typing in a menu text box.
            bool captured = Features.Public.MenuUx.HotkeysCaptured;
            bool typing = _menuOpen && Features.Public.MenuUx.Typing;
            bool hotkeys = !captured && !typing;
            if (!captured && !(typing && Features.Public.MenuUx.IsTextKey(ModConfig.MenuKey.Value))
                && Pressed(kb, ModConfig.MenuKey.Value)) SetMenuOpen(!_menuOpen);
            if (!captured && _menuOpen && Pressed(kb, Key.Escape)) SetMenuOpen(false);
            if (hotkeys && Pressed(kb, ModConfig.HudKey.Value)) ModConfig.ShowHud.Value = !ModConfig.ShowHud.Value;

            if (_menuOpen)
            {
                // The game re-locks the cursor while driving; keep it free while the menu is up.
                Cursor.visible = true;
                Cursor.lockState = CursorLockMode.None;
                // A scene load while the menu is open brings a new EventSystem: pause that one too.
                if (_pausedEventSystem == null)
                {
                    var es = EventSystem.current;
                    if (es != null && es.enabled) { es.enabled = false; _pausedEventSystem = es; }
                }
            }
        }
        catch (Exception e) { LogThrottled("ModBehaviour.Update input", e); }
    }

    /// <summary>Key.None (or a key the keyboard doesn't have) in the config counts as "not pressed" instead of throwing.</summary>
    private static bool Pressed(Keyboard kb, Key key)
    {
        if (key == Key.None) return false;
        try { return kb[key].wasPressedThisFrame; }
        catch { return false; }
    }

    private static readonly System.Collections.Generic.Dictionary<string, float> LastErrorLog = new();

    /// <summary>An error repeating every frame used to write a full stack trace 60+ times a second; now once per 10 s.</summary>
    private static void LogThrottled(string where, Exception e)
    {
        string key = where + ":" + e.GetType().Name + ":" + e.Message;
        float now = Time.unscaledTime;
        if (LastErrorLog.TryGetValue(key, out var last) && now - last < 10f) return;
        if (LastErrorLog.Count > 64) LastErrorLog.Clear();
        LastErrorLog[key] = now;
        Plugin.Logger.LogError(where + ": " + e);
    }

    private void SetMenuOpen(bool open)
    {
        _menuOpen = open;
        if (open)
        {
            _savedCursorVisible = Cursor.visible;
            _savedLockState = Cursor.lockState;
            // Stop clicks on the menu from also pressing the game's buttons underneath.
            var es = EventSystem.current;
            if (es != null && es.enabled) { es.enabled = false; _pausedEventSystem = es; }
            // Switch the game's input to the console map (like its own dev console): driving keys stop steering the
            // car while you type or click in the menu, and no key gets "stuck" when the menu closes.
            try { if (_inputMap == null) _inputMap = GameInput.PushMap(GameInput.Map.Terminal, 1000); }
            catch (Exception e) { Plugin.Logger.LogDebug("Menu input map: " + e.Message); }
        }
        else
        {
            Cursor.visible = _savedCursorVisible;
            Cursor.lockState = _savedLockState;
            if (_pausedEventSystem != null) { _pausedEventSystem.enabled = true; _pausedEventSystem = null; }
            if (_inputMap != null)
            {
                try { GameInput.PopMap(_inputMap.Value); }
                catch (Exception e) { Plugin.Logger.LogDebug("Menu input map: " + e.Message); }
                _inputMap = null;
            }
            Features.Public.Stability.ConfigAutosave.SaveNow();
        }
    }

    private float _hudRetryAt;
    private bool _hudErrorLogged;

    private void OnGUI()
    {
        // Nothing of the mod's IMGUI goes into a screenshot (self-expiring flag, see PhotoCapture).
        if (Features.Public.Photo.PhotoCapture.Capturing) return;
        Features.Registry.RunGuis();
        if (ModConfig.ShowHud.Value && !Features.Public.Photo.PhotoCapture.CleanViewActive && Time.unscaledTime >= _hudRetryAt)
        {
            try { Hud.Draw(); }
            catch (Exception e)
            {
                // Usually transient (car despawning mid-draw): pause the HUD briefly instead of switching the saved
                // setting off, and log only the first failure.
                _hudRetryAt = Time.unscaledTime + 5f;
                if (!_hudErrorLogged) { _hudErrorLogged = true; Plugin.Logger.LogError("Hud.Draw (retrying every 5 s, further errors not logged): " + e); }
            }
        }
        if (!_menuOpen) return;
        try { Menu.Draw(); }
        catch (Exception e)
        {
            Plugin.Logger.LogError("Menu.Draw: " + e);
            // Close properly: a bare flag change left the game's EventSystem disabled and the cursor unlocked.
            try { SetMenuOpen(false); } catch { _menuOpen = false; }
        }
    }

    private void OnApplicationQuit()
    {
        FirstSeenTracker.SaveIfDue(force: true);
        Features.Public.Stability.ConfigAutosave.SaveNow();
    }
}
