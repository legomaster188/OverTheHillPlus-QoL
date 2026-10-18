using System;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;

namespace OverTheHillPlus;

/// <summary>
/// Per-frame driver: game state, every mod's ticks and overlays, saving, MODS in the MOD BROWSER (UI/GameMenu: its
/// keys, and the recording pass of its pages in OnGUI) and the full view window's open/close (cursor, input map, the
/// game's EventSystem). Mods plug their own per-frame work in through Registry.OnTick / OnGui, or through the static
/// hooks below for work that must run at a fixed point of the frame. The full view itself is UI/ModsPage.
/// </summary>
public class CoreBehaviour : MonoBehaviour
{
    public CoreBehaviour(IntPtr ptr) : base(ptr) { }

    private static CoreBehaviour _instance;

    // ---- hooks set by mods ----

    /// <summary>Ticks that run after GameState.Tick and before the features' ticks.</summary>
    internal static Action BeforeFeatureTicks;
    /// <summary>Ticks that run after the features' ticks.</summary>
    internal static Action AfterFeatureTicks;
    /// <summary>Hotkeys of a mod, run when hotkeys are allowed (no key capture, not typing in the page).</summary>
    internal static Action<Keyboard> Hotkeys;

    private static readonly CheckList HideGuiChecks = new();
    private static readonly CheckList CleanViewChecks = new();

    /// <summary>
    /// Adds a check that hides all of the mods' IMGUI (overlays, HUD, the MODS page) while it is true, e.g. while a
    /// screenshot is being taken. Every mod can add its own; a check that throws counts as false.
    /// </summary>
    internal static void AddHideGuiCheck(Func<bool> hidden) => HideGuiChecks.Add(hidden);

    /// <summary>True when any <see cref="AddHideGuiCheck"/> check says nothing may draw this frame.</summary>
    internal static bool GuiHidden => HideGuiChecks.Any();

    /// <summary>
    /// Adds a check that is true while the player asked for a clean view (e.g. the photo mode's "hide HUD"): the mods'
    /// on-screen panels (session panel, chat, notices) stay hidden then, the MODS page still opens. A check that
    /// throws counts as false.
    /// </summary>
    internal static void AddCleanViewCheck(Func<bool> clean) => CleanViewChecks.Add(clean);

    /// <summary>True when any <see cref="AddCleanViewCheck"/> check asks for a clean view.</summary>
    internal static bool CleanView => CleanViewChecks.Any();

    /// <summary>Drawn in OnGUI after the features' overlays and before the MODS page (e.g. the driving HUD).</summary>
    internal static Action AfterGuis;
    /// <summary>Runs when the game quits, after the config is saved. Each subscriber runs in its own try/catch.</summary>
    internal static Action OnQuit;

    private bool _menuOpen;
    private bool _savedCursorVisible;
    private CursorLockMode _savedLockState;
    private EventSystem _pausedEventSystem;
    private Il2CppSystem.Guid? _inputMap;

    private void Awake() => _instance = this;

    /// <summary>True while the MODS page is open.</summary>
    internal static bool MenuOpen => _instance != null && _instance._menuOpen;

    /// <summary>Opens or closes the MODS page (UI.ModsPage.Open / Close call this).</summary>
    internal static void SetMenuOpen(bool open)
    {
        if (_instance == null || _instance._menuOpen == open) return;
        _instance.SetOpen(open);
    }

    // Trail steps around core's per-frame work: a hang or crash after an "end of" step is outside every hook of ours
    // (in the game's own code, physics, rendering, or an unguarded Harmony patch).
    private const string UpdateDone = "end of core's Update", OnGuiDone = "end of core's OnGUI";
    private const string ModsBrowserStep = "core: MOD BROWSER keys", ModsRecordStep = "core: MODS page rows", ModsDrawStep = "core: MODS page draw";

    private void Update()
    {
        // The frame clock first: frame spikes, and the beat the hang watchdog waits for.
        Logging.FrameWatch.Beat();
        // Everything core's Update runs is ours: an error the game logs meanwhile is blamed on core (or on the mod
        // whose hook runs, which sets its own id and puts "core" back after).
        CrashTrail.RunningMod = "core";
        try { Tick(); }
        finally
        {
            CrashTrail.RunningMod = null;
            CrashTrail.Mark(UpdateDone);
        }
    }

    private void Tick()
    {
        // Every plugin's Load (and its patching) has run by the first frame.
        PatchReport.LogSummaryOnce();
        // Also on the first frame: the mod map, the session header and the crash report of an unclean last session.
        Logging.SessionReport.FirstFrame();

        // Results of the installer's downloads, handed back from their worker threads.
        try { Core.Install.MainThread.Drain(); }
        catch (Exception e) { DebugLog.Error("core", e, "CoreBehaviour.Tick"); LogThrottled("CoreBehaviour.Update main-thread queue", e); }

        try
        {
            GameState.Tick();
            // One by one: a mod's hook that throws must not skip the other mods' hooks or the features' ticks.
            // Each subscriber marks the crash trail with its own step and is timed (Logging/HookTiming).
            Logging.HookTiming.InvokeEach(BeforeFeatureTicks, "before ticks", e => LogThrottled("CoreBehaviour.Update BeforeFeatureTicks", e));
            Features.Registry.RunTicks();
            Logging.HookTiming.InvokeEach(AfterFeatureTicks, "after ticks", e => LogThrottled("CoreBehaviour.Update AfterFeatureTicks", e));
            UI.GameUiButtons.Tick();
        }
        catch (Exception e) { DebugLog.Error("core", e, "CoreBehaviour.Tick"); LogThrottled("CoreBehaviour.Update ticks", e); }

        // MODS in the MOD BROWSER: its keys (Esc / B back to the list, Tab for Advanced), keeping the liveries hidden.
        CrashTrail.Mark(ModsBrowserStep);
        try { UI.GameMenu.GameModsScreen.Tick(); }
        catch (Exception e) { DebugLog.Error("core", e, "CoreBehaviour.Tick"); LogThrottled("CoreBehaviour.Update MOD BROWSER", e); }

        // Separate from the ticks above: a tick that keeps failing must not also take the page's keys with it.
        try
        {
            // A close asked for by the page (its x, a prompt's Close): the full view if it is open, else the MOD BROWSER.
            if (Menu.CloseRequested)
            {
                Menu.CloseRequested = false;
                if (_menuOpen) SetOpen(false);
                else UI.ModsPage.Close();
            }
            var kb = Keyboard.current;
            // No hotkeys while a key is being captured for a rebind, and none (bar a non-typing open key) while
            // typing in a text box on the page.
            bool captured = Features.Public.MenuUx.HotkeysCaptured;
            bool typing = (_menuOpen && Features.Public.MenuUx.Typing) || UI.GameMenu.GameModsTab.Typing;
            bool hotkeys = !captured && !typing;
            // While the old plugin is loaded (ModManager.Blocked) its own menu answers that key: MODS then opens
            // from the ESC menu or the main menu's MOD BROWSER only.
            var openKey = Core.Manager.ModManager.Blocked || CoreConfig.OpenKey == null ? Key.None : CoreConfig.OpenKey.Value;
            // A text box of the MOD BROWSER page has the keys: the open key doesn't close the browser under the typing.
            bool boxTyping = UI.GameMenu.GameModsTab.Typing;
            if (kb != null && !captured && !boxTyping && !(typing && Features.Public.MenuUx.IsTextKey(openKey)) && Pressed(kb, openKey))
                UI.ModsPage.Toggle();
            // Esc / a gamepad's B: back one step (a mod's page -> the list -> closed, back to the ESC menu).
            else if (!captured && _menuOpen && ((kb != null && Pressed(kb, Key.Escape)) || PadBackPressed()))
                UI.ModsPage.Back();
            if (kb != null && hotkeys) Logging.HookTiming.InvokeEach(Hotkeys, kb, "hotkeys", e => LogThrottled("CoreBehaviour.Update hotkeys", e));

            if (_menuOpen)
            {
                // The game re-locks the cursor while driving; keep it free while the page is up.
                Cursor.visible = true;
                Cursor.lockState = CursorLockMode.None;
                // A scene load while the page is open brings a new EventSystem: pause that one too.
                if (_pausedEventSystem == null)
                {
                    var es = EventSystem.current;
                    if (es != null && es.enabled) { es.enabled = false; _pausedEventSystem = es; }
                }
            }
        }
        catch (Exception e) { DebugLog.Error("core", e, "CoreBehaviour.Tick"); LogThrottled("CoreBehaviour.Update input", e); }
    }

    private static bool PadBackPressed()
    {
        try
        {
            var pad = Gamepad.current;
            return pad != null && pad.buttonEast.wasPressedThisFrame;
        }
        catch (Exception ex) { DebugLog.Error("core", ex, "CoreBehaviour.PadBackPressed"); return false; }
    }

    /// <summary>Key.None (or a key the keyboard doesn't have) in the config counts as "not pressed" instead of throwing.</summary>
    internal static bool Pressed(Keyboard kb, Key key)
    {
        if (key == Key.None) return false;
        try { return kb[key].wasPressedThisFrame; }
        catch (Exception ex) { DebugLog.Error("core", ex, "CoreBehaviour.Pressed"); return false; }
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
        Core.CorePlugin.Logger?.LogError(where + ": " + e);
    }

    private void SetOpen(bool open)
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
            catch (Exception e) { DebugLog.Error("core", e, "CoreBehaviour.SetOpen"); Core.CorePlugin.Logger?.LogDebug("Menu input map: " + e.Message); }
        }
        else
        {
            Cursor.visible = _savedCursorVisible;
            Cursor.lockState = _savedLockState;
            if (_pausedEventSystem != null) { _pausedEventSystem.enabled = true; _pausedEventSystem = null; }
            if (_inputMap != null)
            {
                try { GameInput.PopMap(_inputMap.Value); }
                catch (Exception e) { DebugLog.Error("core", e, "CoreBehaviour.SetOpen"); Core.CorePlugin.Logger?.LogDebug("Menu input map: " + e.Message); }
                _inputMap = null;
            }
            // The native-style menu is a canvas: take it off screen now, not when the next tick notices.
            try { UI.Native.NativeCanvas.Hide(); } catch (Exception e) { DebugLog.Error("core", e, "CoreBehaviour.SetOpen"); Core.CorePlugin.Logger?.LogDebug("Native menu hide: " + e.Message); }
            UI.Native.MenuNav.ClearPending();
            Features.Public.Stability.ConfigAutosave.SaveNow();
            try { UI.ModsPage.OnClosed(); } catch (Exception e) { Core.CorePlugin.Logger?.LogError("MODS page close: " + e); }
        }
    }

    private void OnGUI()
    {
        CrashTrail.RunningMod = "core";
        try { DrawGui(); }
        finally
        {
            CrashTrail.RunningMod = null;
            CrashTrail.Mark(OnGuiDone);
        }
    }

    private void DrawGui()
    {
        // Nothing of the mods' IMGUI goes into a screenshot (e.g. the photo mode's self-expiring flag).
        if (GuiHidden)
        {
            UI.Native.NativeCanvas.Hide();
            return;
        }
        Features.Registry.RunGuis();
        Logging.HookTiming.InvokeEach(AfterGuis, "after overlays", e => LogThrottled("CoreBehaviour.OnGUI overlays", e));
        // The MOD BROWSER's MODS page: its rows come from the page's draw code, run once a frame (Layout) as a recording pass.
        CrashTrail.Mark(ModsRecordStep);
        try { UI.GameMenu.GameModsTab.Record(); }
        catch (Exception e) { DebugLog.Error("core", e, "CoreBehaviour.DrawGui"); LogThrottled("CoreBehaviour.OnGUI MOD BROWSER", e); }
        if (!_menuOpen) return;
        CrashTrail.Mark(ModsDrawStep);
        try { UI.ModsPage.Draw(); }
        catch (Exception e)
        {
            Core.CorePlugin.Logger?.LogError("MODS page: " + e);
            // Close properly: a bare flag change left the game's EventSystem disabled and the cursor unlocked.
            try { SetOpen(false); } catch (Exception ex) { DebugLog.Error("core", ex, "CoreBehaviour.DrawGui"); _menuOpen = false; }
        }
    }

    private void OnApplicationQuit()
    {
        Features.Public.Stability.ConfigAutosave.SaveNow();
        // One by one: a subscriber that throws must not skip the others (e.g. putting back the host's settings).
        Hooks.InvokeEach(OnQuit, e => Core.CorePlugin.Logger?.LogError("OnQuit: " + e));
    }
}
