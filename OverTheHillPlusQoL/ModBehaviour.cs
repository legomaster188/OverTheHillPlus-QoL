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
            BrowserProfiler.Tick();
            FirstSeenTracker.SaveIfDue();
            GameUiButtons.Tick();
            Qol.Tick();
            Multiplayer.JoinMessage.Tick();
            Multiplayer.BigLobby.Tick();

            var kb = Keyboard.current;
            if (kb == null) return;
            if (Menu.CloseRequested) { Menu.CloseRequested = false; if (_menuOpen) SetMenuOpen(false); }
            if (kb[ModConfig.MenuKey.Value].wasPressedThisFrame) SetMenuOpen(!_menuOpen);
            if (_menuOpen && kb[Key.Escape].wasPressedThisFrame) SetMenuOpen(false);
            if (kb[ModConfig.HudKey.Value].wasPressedThisFrame) ModConfig.ShowHud.Value = !ModConfig.ShowHud.Value;

            if (_menuOpen)
            {
                // The game re-locks the cursor while driving; keep it free while the menu is up.
                Cursor.visible = true;
                Cursor.lockState = CursorLockMode.None;
            }
        }
        catch (Exception e) { Plugin.Logger.LogError("ModBehaviour.Update: " + e); }
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
        }
        else
        {
            Cursor.visible = _savedCursorVisible;
            Cursor.lockState = _savedLockState;
            if (_pausedEventSystem != null) { _pausedEventSystem.enabled = true; _pausedEventSystem = null; }
            ModConfig.File.Save();
        }
    }

    private void OnGUI()
    {
        if (ModConfig.ShowHud.Value)
        {
            try { Hud.Draw(); }
            catch (Exception e) { Plugin.Logger.LogError("Hud.Draw: " + e); ModConfig.ShowHud.Value = false; }
        }
        if (!_menuOpen) return;
        try { Menu.Draw(); }
        catch (Exception e) { Plugin.Logger.LogError("Menu.Draw: " + e); _menuOpen = false; }
    }

    private void OnApplicationQuit()
    {
        FirstSeenTracker.SaveIfDue(force: true);
        ModConfig.File.Save();
    }
}
