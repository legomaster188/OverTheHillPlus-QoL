using System;
using BepInEx.Configuration;
using HarmonyLib;
using OverTheHillPlus.ServerBrowser;
using OverTheHillPlus.UI;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;

namespace OverTheHillPlus.Features.Public.Rejoin;

/// <summary>
/// Rejoin last server: a button in PLUS > Servers (and optionally on the main menu), plus an automatic rejoin with a
/// countdown after an unexpected disconnect (never after a kick, a ban or quitting).
/// </summary>
internal sealed class RejoinFeature : IFeature
{
    private const string Section = "Rejoin";
    private const float PanelW = 520f, PanelH = 70f;

    public static ConfigEntry<bool> AutoRejoin, AfterRoomClosed, MainMenuButton;
    public static ConfigEntry<float> CountdownSeconds, KeepTryingSeconds;
    public static ConfigEntry<int> MaxAttempts;
    public static ConfigEntry<Key> CancelKey;

    private static GUIStyle _center, _title, _line;
    private static bool _hover;
    private static EventSystem _heldEventSystem;
    private static bool _loggedGuiError;

    public void Register()
    {
        var cfg = ServerBrowserConfig.File;
        AutoRejoin = cfg.Bind(Section, "AutoRejoin", true,
            "After an unexpected disconnect from a server you joined (connection lost, timeout), count down and rejoin it. Never after a kick, a ban or quitting.");
        AfterRoomClosed = cfg.Bind(Section, "AfterRoomClosed", false,
            "Also try to rejoin when the host closed the room (useful when they restart it under the same name).");
        CountdownSeconds = cfg.Bind(Section, "CountdownSeconds", 8f,
            new ConfigDescription("Seconds to wait (with a cancel button) before rejoining automatically.", new AcceptableValueRange<float>(0f, 30f)));
        KeepTryingSeconds = cfg.Bind(Section, "KeepTryingSeconds", 60f,
            new ConfigDescription("How long to look for the room in the server list before giving up.", new AcceptableValueRange<float>(15f, 300f)));
        MaxAttempts = cfg.Bind(Section, "MaxAttempts", 3,
            new ConfigDescription("Automatic join attempts before giving up.", new AcceptableValueRange<int>(1, 5)));
        MainMenuButton = cfg.Bind(Section, "MainMenuButton", true,
            "Add a REJOIN button next to PLUS on the main menu once you have joined a server.");
        CancelKey = cfg.Bind(Section, "CancelKey", Key.F4, "Cancels an automatic rejoin while it counts down or searches (None = no hotkey).");

        Registry.Section(Registry.PlusMode, "Servers", DrawMenu, -20);
        Registry.OnTick(Tick);
        Registry.OnGui(DrawPanel);
    }

    private static bool MenuOpen => Time.frameCount - Menu.DrawnFrame <= 2;

    // ---------------------------------------------------------------- menu

    private static void DrawMenu()
    {
        var last = LastServerStore.Current;
        Menu.Card("Rejoin last server", "Get back into the server you were in, or let PLUS do it after a dropped connection", () =>
        {
            var state = RejoinCore.State;
            if (state != RejoinCore.Phase.Idle)
            {
                var color = state == RejoinCore.Phase.Failed ? Theme.Bad : state == RejoinCore.Phase.Done ? Theme.Good : Theme.Info;
                Menu.Banner(StatusLine(), color);
            }

            if (last == null)
            {
                Menu.Small("No server joined yet. The next server you join as a guest is remembered here (your own hosted rooms are not).");
            }
            else
            {
                Menu.KeyValue("Server", RejoinCore.Name(last));
                Menu.KeyValue("Region", string.IsNullOrEmpty(last.Region) ? "unknown" : last.Region.ToUpperInvariant());
                Menu.KeyValue("Room", last.Private ? (string.IsNullOrEmpty(last.RoomCode) ? "private" : "private (room code saved)") : "public");
                Menu.KeyValue("Joined", LastServerStore.AgoText(last.JoinedAt));
                if (!string.IsNullOrEmpty(RejoinCore.LastLeaveReason)) Menu.KeyValue("Last left", RejoinCore.LastLeaveReason);
            }

            bool active = RejoinCore.Active;
            bool canJoin = last != null && !active && !RejoinCore.InAnySession;
            Menu.Chips(3,
                ("Rejoin now", (Action)RejoinCore.RequestManual, canJoin),
                ("Cancel rejoin", (Action)RejoinCore.RequestCancel, active),
                ("Forget server", (Action)(() => { LastServerStore.Forget(); Menu.SetStatus("Forgot the last server"); }), last != null && !active));
            if (last != null && RejoinCore.InAnySession && !active)
                Menu.Small("Rejoin works from the main menu or the server browser: leave the current session first.");

            Menu.Switch(AutoRejoin, "Auto-rejoin after a disconnect", "Counts down, then rejoins. Not after a kick, a ban or quitting", true);
            Menu.Switch(AfterRoomClosed, "Also when the host closed the room", "Waits for them to reopen it under the same name", AutoRejoin.Value);
            Menu.SliderRow(CountdownSeconds, "Countdown", 0f, 30f, "0", " s", AutoRejoin.Value);
            Menu.SliderRow(KeepTryingSeconds, "Keep looking for", 15f, 300f, "0", " s", true);
            float tries = Menu.RawSlider("Attempts", MaxAttempts.Value, 1f, 5f, MaxAttempts.Value.ToString(), AutoRejoin.Value,
                "Automatic join attempts before giving up", MaxAttempts.Description?.Description);
            int rounded = Mathf.Clamp(Mathf.RoundToInt(tries), 1, 5);
            if (rounded != MaxAttempts.Value) MaxAttempts.Value = rounded;
            Menu.Switch(MainMenuButton, "REJOIN button on the main menu", "Above Quit, once a server is remembered", true);
            Menu.Small(CancelKey.Value == Key.None
                ? "Cancel a running rejoin with the Cancel button on screen or here."
                : $"Cancel a running rejoin with {CancelKey.Value}, the Cancel button on screen, or here.");
        });
    }

    private static string StatusLine()
    {
        float left = Mathf.Max(0f, RejoinCore.Deadline - Time.unscaledTime);
        string name = RejoinCore.Name(LastServerStore.Current);
        string attempt = RejoinCore.Automatic && RejoinCore.Attempt > 0 ? $" (attempt {RejoinCore.Attempt + 1})" : "";
        switch (RejoinCore.State)
        {
            case RejoinCore.Phase.Countdown: return $"Rejoining {name} in {Mathf.CeilToInt(left)} s{attempt}";
            case RejoinCore.Phase.Seeking: return $"{RejoinCore.StatusText} ({Mathf.CeilToInt(left)} s left){attempt}";
            default: return RejoinCore.StatusText;
        }
    }

    // ---------------------------------------------------------------- per frame

    private static void Tick()
    {
        RejoinCore.Tick();

        if (RejoinCore.Active && RejoinCore.State != RejoinCore.Phase.Joining)
        {
            var kb = Keyboard.current;
            var key = CancelKey.Value;
            if (kb != null && key != Key.None && !Public.MenuUx.Typing)
            {
                try { if (kb[key].wasPressedThisFrame) RejoinCore.RequestCancel(); }
                catch (Exception ex) { DebugLog.Error("serverbrowser", ex, "RejoinFeature.Tick"); /* invalid key in config */ }
            }
        }

        // While the pointer is over the panel, pause the game's UI input so a click doesn't also hit the menu
        // underneath. Never while the mod menu is open: the menu manages the EventSystem itself.
        bool want = _hover && PanelVisible && !MenuOpen;
        if (want && _heldEventSystem == null)
        {
            var es = EventSystem.current;
            if (es != null && es.enabled) { es.enabled = false; _heldEventSystem = es; }
        }
        else if (!want && _heldEventSystem != null && !MenuOpen)
        {
            try { if (_heldEventSystem != null) _heldEventSystem.enabled = true; } catch (Exception ex) { DebugLog.Error("serverbrowser", ex, "RejoinFeature.Tick"); }
            _heldEventSystem = null;
        }
    }

    private static bool PanelVisible => RejoinCore.State != RejoinCore.Phase.Idle && !MenuOpen;

    // ---------------------------------------------------------------- on-screen panel

    private static void DrawPanel()
    {
        if (!PanelVisible) { _hover = false; return; }
        var prevMatrix = GUI.matrix;
        try
        {
            Theme.Ensure();
            EnsureStyles();
            float scale = Mathf.Max(1f, Screen.height / 1080f);
            GUI.matrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, new Vector3(scale, scale, 1f));
            float sw = Screen.width / scale;
            // Below the server browser's PLUS quick bar (top centre).
            var r = new Rect((sw - PanelW) / 2f, 96f, PanelW, PanelH);
            var ev = Event.current;
            if (ev.type == EventType.Repaint) _hover = r.Contains(ev.mousePosition);

            var state = RejoinCore.State;
            var accent = state == RejoinCore.Phase.Failed ? Theme.Bad : state == RejoinCore.Phase.Done ? Theme.Good : Theme.Accent;
            Theme.Fill(r, new Color(Theme.Bg.r, Theme.Bg.g, Theme.Bg.b, 0.94f), 10);
            Theme.Fill(new Rect(r.x, r.y, 4f, r.height), accent, 2);

            bool countdown = state == RejoinCore.Phase.Countdown;
            bool cancellable = RejoinCore.Active && state != RejoinCore.Phase.Joining;
            float buttons = (countdown ? 2 : cancellable ? 1 : 0) * 96f;
            float textW = PanelW - 28f - buttons;

            _title.normal.textColor = accent;
            GUI.Label(new Rect(r.x + 16f, r.y + 8f, textW, 18f), "PLUS REJOIN", _title);
            GUI.Label(new Rect(r.x + 16f, r.y + 28f, textW, 34f), StatusLine(), _line);

            float bx = r.xMax - 12f - 88f, by = r.y + (PanelH - 32f) / 2f;
            if (cancellable)
            {
                if (Chip(new Rect(bx, by, 88f, 32f), "Cancel")) RejoinCore.RequestCancel();
                bx -= 96f;
            }
            if (countdown && Chip(new Rect(bx, by, 88f, 32f), "Join now")) RejoinCore.RequestNow();
        }
        catch (Exception e)
        {
            if (!_loggedGuiError)
            {
                _loggedGuiError = true;
                ServerBrowserPlugin.Logger.LogError("[Rejoin] panel failed (further errors suppressed): " + e);
            }
        }
        finally { GUI.matrix = prevMatrix; }
    }

    private static bool Chip(Rect r, string label)
    {
        bool hover = r.Contains(Event.current.mousePosition);
        Theme.Fill(r, hover ? Theme.AccentDim : Theme.Hex(0x222B36), 6);
        _center.normal.textColor = hover ? Theme.Text : Theme.Muted;
        GUI.Label(r, label, _center);
        return GUI.Button(r, GUIContent.none, Theme.Invisible);
    }

    private static void EnsureStyles()
    {
        if (_center != null && _title != null && _line != null) return;
        _center = new GUIStyle(Theme.Label) { alignment = TextAnchor.MiddleCenter, fontSize = 13, richText = false };
        _title = new GUIStyle(Theme.Section) { richText = false };
        _line = new GUIStyle(Theme.Label) { wordWrap = true, richText = false, alignment = TextAnchor.UpperLeft };
    }
}

/// <summary>REJOIN on the main menu, right after the PLUS button (runs after it: lower priority).</summary>
[HarmonyPatch(typeof(StateMainMenu), nameof(StateMainMenu.Open))]
internal static class RejoinMainMenuButton
{
    [HarmonyPriority(Priority.Low)]
    private static void Postfix(StateMainMenu __instance)
    {
        try
        {
            if (RejoinFeature.MainMenuButton == null || !RejoinFeature.MainMenuButton.Value || !LastServerStore.HasServer) return;
            var ctx = __instance.context;
            if (ctx == null) return;
            var source = ctx.SettingsButton != null && ctx.SettingsButton.gameObject.activeSelf ? ctx.SettingsButton : ctx.ExtrasButton;
            if (source == null) return;
            var parent = source.transform.parent;
            int index = ctx.QuitGameButton != null && ctx.QuitGameButton.transform.parent == parent
                ? ctx.QuitGameButton.transform.GetSiblingIndex()
                : source.transform.GetSiblingIndex() + 1;
            GameUiButtons.Clone(source, "OTHPlus_RejoinButton", "REJOIN", index, RejoinCore.RequestManual);
        }
        catch (Exception e) { ServerBrowserPlugin.Logger.LogError("Rejoin main menu button failed: " + e); }
    }
}
