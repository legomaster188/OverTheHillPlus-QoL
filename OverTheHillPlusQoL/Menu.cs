using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Configuration;
using OverTheHillPlus.Multiplayer;
using OverTheHillPlus.ServerBrowser;
using OverTheHillPlus.UI;
using UnityEngine;

namespace OverTheHillPlus;

/// <summary>
/// The mod menu: a draggable dark panel with tabs, cards, switches and sliders.
/// Pure IMGUI with Rect-based calls (GUILayout's params arrays are awkward under IL2CPP).
/// </summary>
internal static class Menu
{
    private const float W = 600f, H = 720f, HeaderH = 60f, TabsH = 36f, FooterH = 34f, Pad = 16f, Inset = 14f;
    // Two menus in one window: PLUS (no cheats: QoL, hosting, lobby, servers) and CHEATS.
    private static readonly string[][] TabsByMode =
    {
        new[] { "QoL", "Lobby", "Servers" },
    };
    public const string LobbyTab = "Lobby";

    public static bool CloseRequested;

    private static int _mode;
    private static readonly int[] TabByMode = new int[2];
    private static string[] Tabs => TabsByMode[_mode];
    private static int _tab { get => TabByMode[_mode]; set => TabByMode[_mode] = value; }

    /// <summary>Jumps to a tab by name in whichever mode has it.</summary>
    public static void SelectTab(string name)
    {
        for (int m = 0; m < TabsByMode.Length; m++)
        {
            int i = Array.IndexOf(TabsByMode[m], name);
            if (i >= 0) { _mode = m; TabByMode[m] = i; return; }
        }
    }

    private static Vector2 _pos = new(40f, 40f);
    private static bool _dragging;
    private static Vector2 _dragOffset;

    private static readonly Dictionary<string, Vector2> Scroll = new();
    private static readonly Dictionary<string, float> ContentH = new();
    private static readonly Dictionary<string, float> CardH = new();

    private static float _y, _cw;
    private static string _status = "";
    private static float _statusAt = -100f;


    // ---- frame ----

    public static void Draw()
    {
        Theme.Ensure();
        float scale = Mathf.Max(1f, Screen.height / 1080f);
        GUI.matrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, new Vector3(scale, scale, 1f));

        float sw = Screen.width / scale, sh = Screen.height / scale;
        _pos.x = Mathf.Clamp(_pos.x, 0f, Mathf.Max(0f, sw - W));
        _pos.y = Mathf.Clamp(_pos.y, 0f, Mathf.Max(0f, sh - 80f));
        float h = Mathf.Min(H, sh - _pos.y - 10f);
        var win = new Rect(_pos.x, _pos.y, W, h);

        HandleDrag(new Rect(win.x, win.y, 250f, HeaderH)); // title area only: the right side has buttons
        GUI.Box(win, GUIContent.none, Theme.Window);
        DrawHeader(win);
        DrawTabs(new Rect(win.x + Pad, win.y + HeaderH, W - Pad * 2, TabsH));

        var area = new Rect(win.x + Pad, win.y + HeaderH + TabsH + 10f, W - Pad * 2, h - HeaderH - TabsH - FooterH - 18f);
        string tab = Tabs[Mathf.Clamp(_tab, 0, Tabs.Length - 1)];
        Scroll.TryGetValue(tab, out var scroll);
        float contentH = ContentH.TryGetValue(tab, out var ch) ? ch : 600f;
        Scroll[tab] = GUI.BeginScrollView(area, scroll, new Rect(0f, 0f, area.width - 16f, contentH));
        _y = 0f;
        _cw = area.width - 18f;
        switch (tab)
        {
            case "QoL": DrawQol(); break;
            case "Lobby": DrawLobby(); break;
            case "Servers": DrawServers(); break;
        }
        ContentH[tab] = _y + 6f;
        GUI.EndScrollView();

        DrawFooter(new Rect(win.x, win.yMax - FooterH, W, FooterH));
    }

    private static void HandleDrag(Rect handle)
    {
        var e = Event.current;
        if (e.type == EventType.MouseDown && e.button == 0 && handle.Contains(e.mousePosition))
        {
            _dragging = true;
            _dragOffset = e.mousePosition - _pos;
            e.Use();
        }
        else if (_dragging && e.type == EventType.MouseDrag)
        {
            _pos = e.mousePosition - _dragOffset;
            e.Use();
        }
        else if (e.type == EventType.MouseUp) _dragging = false;
    }

    private static void DrawHeader(Rect win)
    {
        GUI.Label(new Rect(win.x + Pad, win.y + 12f, 300f, 22f), "OVER THE HILL <color=#E8ECF2>PLUS</color>", Theme.Title);
        GUI.Label(new Rect(win.x + Pad, win.y + 34f, 230f, 16f), $"v{Plugin.Version}  ·  {GameState.SessionDescription}", Theme.Small);

        string badge = GameState.Badge;
        Color badgeColor = badge switch
        {
            "SOLO" or "HOST" => Theme.Good,
            "FRIENDS" => Theme.Info,
            "PUBLIC" => Theme.Warn,
            _ => Theme.Off,
        };

        var badgeRect = new Rect(win.xMax - 56f - 84f, win.y + 18f, 76f, 22f);
        Theme.Fill(badgeRect, badgeColor, 11);
        GUI.Label(badgeRect, badge, Theme.Badge);

        if (GUI.Button(new Rect(win.xMax - 48f, win.y + 14f, 32f, 30f), "×", Theme.Button)) CloseRequested = true;
    }

    private static void DrawTabs(Rect bar)
    {
        Theme.Fill(bar, Theme.Hex(0x0E1218), 8);
        float tw = (bar.width - 8f) / Tabs.Length;
        for (int i = 0; i < Tabs.Length; i++)
        {
            var r = new Rect(bar.x + 4f + i * tw, bar.y + 4f, tw - 4f, bar.height - 8f);
            if (GUI.Button(r, Tabs[i], i == _tab ? Theme.TabActive : Theme.Tab)) _tab = i;
        }
    }

    private static void DrawFooter(Rect r)
    {
        Theme.Fill(new Rect(r.x + Pad, r.y, r.width - Pad * 2, 1f), Theme.Line, 0);
        bool fresh = Time.unscaledTime - _statusAt < 8f && _status.Length > 0;
        string hint = $"{ModConfig.MenuKey.Value} close  ·  {ModConfig.HudKey.Value} HUD  ·  drag the title to move";
        string text = fresh ? _status : hint;
        Color color = !fresh ? Theme.Muted
            : _status.StartsWith("Failed") ? Theme.Bad
            : _status.StartsWith("Blocked") ? Theme.Warn
            : Theme.Good;
        var prev = Theme.Status.normal.textColor;
        Theme.Status.normal.textColor = color;
        GUI.Label(new Rect(r.x + Pad, r.y + 6f, r.width - Pad * 2, r.height - 8f), text, Theme.Status);
        Theme.Status.normal.textColor = prev;
    }

    private static void SetStatus(string s)
    {
        if (string.IsNullOrEmpty(s)) return;
        _status = s;
        _statusAt = Time.unscaledTime;
    }

    // ---- tabs ----




    // ---- PLUS mode ----

    private static void DrawQol()
    {
        Card("Performance", null, () =>
        {
            float cap = RawSlider("Frame-rate cap", ModConfig.FpsCap.Value, 0f, 360f,
                ModConfig.FpsCap.Value <= 0 ? "game setting" : $"{ModConfig.FpsCap.Value} fps", true);
            int capInt = cap < 15f ? 0 : Mathf.RoundToInt(cap / 5f) * 5;
            if (capInt != ModConfig.FpsCap.Value) ModConfig.FpsCap.Value = capInt;
            Small("Above the game's own options (it tops out lower). Setting a cap turns VSync off.");
            Switch(ModConfig.RunInBackground, "Keep running when alt-tabbed", "Stops the session freezing for everyone when the host alt-tabs", true);
            float bg = RawSlider("Background frame cap", ModConfig.BackgroundFpsCap.Value, 0f, 120f,
                ModConfig.BackgroundFpsCap.Value <= 0 ? "off" : $"{ModConfig.BackgroundFpsCap.Value} fps", true);
            int bgInt = bg < 10f ? 0 : Mathf.RoundToInt(bg / 5f) * 5;
            if (bgInt != ModConfig.BackgroundFpsCap.Value) ModConfig.BackgroundFpsCap.Value = bgInt;
        });
        Card("Camera", null, () =>
        {
            float fov = RawSlider("Field of view", ModConfig.FovOverride.Value, 0f, 110f,
                ModConfig.FovOverride.Value <= 0f ? "game setting" : $"{ModConfig.FovOverride.Value:0}°", true);
            float fovRounded = fov < 30f ? 0f : Mathf.Round(fov);
            if (Math.Abs(fovRounded - ModConfig.FovOverride.Value) > 0.01f) ModConfig.FovOverride.Value = fovRounded;
            Switch(ModConfig.ShowHud, $"Driving HUD ({ModConfig.HudKey.Value})", "Speed, RPM, gear, power, torque, slip", true);
        });
        Card("Navigation", null, () =>
        {
            SliderRow(ModConfig.CompassRange, "Compass range", 1f, 4f, "0.0", "×", true);
            Switch(ModConfig.AutoHeadlights, "Auto headlights", "On from 19:00 to 07:00, off in daylight", true);
        });
        Card("Menus & startup", null, () =>
        {
            Switch(ModConfig.SkipSplash, "Skip logo screens", "Straight to the main menu at startup", true);
            SliderRow(ModConfig.FadeSpeed, "Screen fade length", 0.1f, 1f, "0.00", "×", true);
            Switch(ModConfig.HideTutorialHints, "Hide tutorial hints", "No more reminder pop-ups", true);
            Switch(ModConfig.MainMenuButton, "PLUS button on the main menu", null, true);
        });
    }


    // ---- CHEATS mode: vehicles ----






    private static void DrawLobby()
    {
        Card("Lobby size", "Bigger lobbies in the game's Create Server screen. Everyone in a lobby above 4 players should run this mod.", () =>
        {
            Switch(ModConfig.ExpandLobbySize, "Up to 16 players", "Adds 2–16 to the Max Players option", true);
            float v = RawSlider("Preselected size", ModConfig.DefaultMaxPlayers.Value, 2f, LobbyRules.MaxPlayers,
                LobbyRules.OptionLabel(ModConfig.DefaultMaxPlayers.Value), ModConfig.ExpandLobbySize.Value);
            int n = Mathf.RoundToInt(v);
            if (n != ModConfig.DefaultMaxPlayers.Value) ModConfig.DefaultMaxPlayers.Value = n;
            KeyValue("Network capacity", ModConfig.MeasuredCapacity.Value > 0
                ? $"{ModConfig.MeasuredCapacity.Value} players (measured)"
                : "not measured yet — join or host any session once");
            Small("Above the network capacity, extra players get fallback spawn spots and placeholder names instead of crashing the lobby.");
        });
        Card("Stability", null, () =>
        {
            Switch(ModConfig.LobbyGuards, "Lobby crash guards", "Smart spawn points + safe player join/leave", true);
            KeyValue("Errors absorbed", LobbyRules.GuardedErrors.ToString());
            KeyValue("Spawn fallbacks", LobbyRules.SpawnFallbacks.ToString());
        });
        Card("This session", null, () =>
        {
            var r = GameState.Runner;
            if (r == null) { Small("Not in a session."); return; }
            int players = 0, max = 0;
            try { players = OverlandSessionPlayers.Instance != null ? OverlandSessionPlayers.Instance.GetPlayerCount() : 0; } catch { }
            try { max = r.SessionInfo != null ? r.SessionInfo.MaxPlayers : 0; } catch { }
            KeyValue("Players", max > 0 ? $"{players} / {max}" : players.ToString());
            KeyValue("You are", GameState.Badge.ToLowerInvariant());
        });
        Card("Menu", null, () =>
        {
            Switch(ModConfig.MainMenuButton, "PLUS button on the main menu", "Takes effect next time the main menu opens", true);
        });
    }


    private static void DrawServers()
    {
        Card("Server browser", null, () =>
        {
            Switch(ModConfig.ColorRows, "Color-code rows", "Stripe and tint by server status", true);
            Switch(ModConfig.ShowAge, "Show server age", "How long ago the mod first saw each server", true);
            Switch(ModConfig.ThrottleSessionUpdates, "Smooth list refreshes", "Skip duplicate updates and merge bursts", true);
            SliderRow(ModConfig.SessionUpdateInterval, "Min time between rebuilds", 0.25f, 5f, "0.00", " s", ModConfig.ThrottleSessionUpdates.Value);
            Switch(ModConfig.ProfileServerBrowser, "Performance logging", "Troubleshooting only, takes effect after a restart", true);
        });
        Card("Legend", null, () =>
        {
            Legend(ServerStatus.Public, "Public", "joinable");
            Legend(ServerStatus.Private, "Private", "locked / room code");
            Legend(ServerStatus.Full, "Full", "or closed to new players");
            Legend(ServerStatus.Incompatible, "Other version", "can't join");
            Small("Age is exact if the mod saw the server appear; \"≥\" means it already existed.");
        });
        Card("Stats", null, () =>
        {
            KeyValue("Servers remembered", FirstSeenTracker.Count.ToString());
            KeyValue("List updates applied", SessionThrottle.Applied.ToString());
            KeyValue("Duplicates skipped", SessionThrottle.SkippedIdentical.ToString());
            KeyValue("Bursts merged", SessionThrottle.Deferred.ToString());
        });
    }

    // ---- widgets ----

    private static float IW => _cw - Inset * 2;

    private static void Card(string title, string subtitle, Action body)
    {
        float top = _y;
        float h = CardH.TryGetValue(title, out var cached) ? cached : 80f;
        GUI.Box(new Rect(0f, top, _cw, h), GUIContent.none, Theme.CardBox);
        _y += 12f;
        GUI.Label(new Rect(Inset, _y, IW, 16f), title.ToUpperInvariant(), Theme.Section);
        _y += 20f;
        if (!string.IsNullOrEmpty(subtitle)) Small(subtitle);
        body();
        _y += 8f;
        CardH[title] = _y - top;
        _y += 10f;
    }

    private static void Banner(string text, Color color)
    {
        float th = Theme.Muted_.CalcHeight(new GUIContent(text), _cw - 36f);
        var r = new Rect(0f, _y, _cw, th + 16f);
        Theme.Fill(r, new Color(color.r, color.g, color.b, 0.16f), 8);
        Theme.Fill(new Rect(r.x, r.y, 4f, r.height), color, 2);
        var prev = Theme.Muted_.normal.textColor;
        Theme.Muted_.normal.textColor = Theme.Text;
        GUI.Label(new Rect(16f, r.y + 8f, _cw - 32f, th), text, Theme.Muted_);
        Theme.Muted_.normal.textColor = prev;
        _y += r.height + 10f;
    }

    private static void Small(string text)
    {
        float th = Theme.Muted_.CalcHeight(new GUIContent(text), IW);
        GUI.Label(new Rect(Inset, _y, IW, th), text, Theme.Muted_);
        _y += th + 6f;
    }

    private static void Switch(ConfigEntry<bool> entry, string label, string hint, bool enabled)
    {
        float rh = hint == null ? 30f : 42f;
        var row = new Rect(Inset - 6f, _y, IW + 12f, rh);
        var prev = GUI.color;
        if (!enabled) GUI.color = new Color(1f, 1f, 1f, 0.45f);
        if (row.Contains(Event.current.mousePosition)) Theme.Fill(row, Theme.CardHover, 6);

        GUI.Label(new Rect(Inset + 4f, _y + (hint == null ? 4f : 2f), IW - 70f, 22f), label, Theme.Label);
        if (hint != null) GUI.Label(new Rect(Inset + 4f, _y + 21f, IW - 70f, 18f), hint, Theme.Small);

        var track = new Rect(Inset + IW - 44f, _y + (rh - 22f) / 2f, 42f, 22f);
        Theme.Fill(track, entry.Value ? Theme.Accent : Theme.Off, 11);
        Theme.Fill(new Rect(entry.Value ? track.xMax - 20f : track.x + 2f, track.y + 2f, 18f, 18f), Theme.Text, 9);

        if (GUI.Button(row, GUIContent.none, Theme.Invisible)) entry.Value = !entry.Value;
        GUI.color = prev;
        _y += rh + 2f;
    }

    private static void SliderRow(ConfigEntry<float> entry, string label, float min, float max, string fmt, string suffix, bool enabled, bool percent = false)
    {
        string shown = percent ? (entry.Value * 100f).ToString("0") + "%" : entry.Value.ToString(fmt) + suffix;
        float v = RawSlider(label, entry.Value, min, max, shown, enabled);
        if (Mathf.Abs(v - entry.Value) > 0.0001f) entry.Value = Mathf.Round(v * 100f) / 100f;
    }

    private static float RawSlider(string label, float value, float min, float max, string shown, bool enabled)
    {
        var prev = GUI.color;
        if (!enabled) GUI.color = new Color(1f, 1f, 1f, 0.45f);
        GUI.Label(new Rect(Inset + 4f, _y, 190f, 28f), label, Theme.Label);
        GUI.Label(new Rect(Inset + IW - 80f, _y, 80f, 28f), shown, Theme.Value);

        var track = new Rect(Inset + 200f, _y + 11f, IW - 290f, 6f);
        float t = Mathf.InverseLerp(min, max, value);
        Theme.Fill(track, Theme.Off, 3);
        Theme.Fill(new Rect(track.x, track.y, track.width * t, track.height), Theme.AccentDim, 3);
        float v = GUI.HorizontalSlider(new Rect(track.x - 7f, _y + 7f, track.width + 14f, 14f), value, min, max, Theme.Slider, Theme.Thumb);
        GUI.color = prev;
        _y += 30f;
        return enabled ? v : value;
    }

    private static void KeyValue(string key, string value)
    {
        GUI.Label(new Rect(Inset + 4f, _y, 176f, 22f), key, Theme.Muted_);
        GUI.Label(new Rect(Inset + 180f, _y, IW - 184f, 22f), value, Theme.Label);
        _y += 24f;
    }

    private static void Bar(string label, float fraction, Color color, string text)
    {
        GUI.Label(new Rect(Inset + 4f, _y, 176f, 22f), label, Theme.Muted_);
        var track = new Rect(Inset + 180f, _y + 7f, IW - 250f, 8f);
        Theme.Fill(track, Theme.Off, 4);
        Theme.Fill(new Rect(track.x, track.y, track.width * Mathf.Clamp01(fraction), track.height), color, 4);
        GUI.Label(new Rect(Inset + IW - 60f, _y, 60f, 22f), text, Theme.Value);
        _y += 24f;
    }

    private static void Legend(ServerStatus status, string name, string meaning)
    {
        var c = RowStyler.ColorFor(status);
        Theme.Fill(new Rect(Inset + 4f, _y + 4f, 6f, 16f), c, 2);
        Theme.Fill(new Rect(Inset + 14f, _y + 4f, 40f, 16f), new Color(c.r, c.g, c.b, 0.25f), 3);
        GUI.Label(new Rect(Inset + 64f, _y, 120f, 24f), name, Theme.Label);
        GUI.Label(new Rect(Inset + 184f, _y, IW - 184f, 24f), meaning, Theme.Muted_);
        _y += 26f;
    }


    private static void Chips(int columns, params (string label, Action action, bool enabled)[] items)
    {
        const float gap = 6f;
        float w = (IW - gap * (columns - 1)) / columns;
        for (int i = 0; i < items.Length; i++)
        {
            int col = i % columns;
            if (i > 0 && col == 0) _y += 36f;
            if (ChipButton(new Rect(Inset + col * (w + gap), _y, w, 30f), items[i].label, items[i].enabled)) items[i].action();
        }
        _y += 36f;
    }

    private static bool ChipButton(Rect r, string label, bool enabled)
    {
        var prev = GUI.color;
        if (!enabled) GUI.color = new Color(1f, 1f, 1f, 0.35f);
        bool clicked = GUI.Button(r, label, Theme.Chip) && enabled;
        GUI.color = prev;
        return clicked;
    }
}
