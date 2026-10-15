using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Configuration;
using OverTheHillPlus.Features.Public;
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
    private static readonly List<string>[] TabsByMode =
    {
        new() { "QoL", "Lobby", "Servers" },
    };
    private static bool _featureTabsAdded;

    /// <summary>Adds tabs that features registered (Features.Registry.Section with a new tab name).</summary>
    private static void EnsureFeatureTabs()
    {
        if (_featureTabsAdded) return;
        _featureTabsAdded = true;
        foreach (var s in Features.Registry.Sections)
        {
            if (s.Mode < 0 || s.Mode >= TabsByMode.Length) continue;
            if (!TabsByMode[s.Mode].Contains(s.Tab)) TabsByMode[s.Mode].Add(s.Tab);
        }
    }
    public const string LobbyTab = "Lobby";
    private static readonly string[] ModeNames =
    {
        "PLUS",
    };

    public static bool CloseRequested;

    private static int _mode;
    private static readonly int[] TabByMode = new int[TabsByMode.Length];
    private static List<string> Tabs => TabsByMode[_mode];
    private static int _tab { get => TabByMode[_mode]; set => TabByMode[_mode] = value; }

    /// <summary>Jumps to a tab by name in whichever mode has it.</summary>
    public static void SelectTab(string name)
    {
        for (int m = 0; m < TabsByMode.Length; m++)
        {
            EnsureFeatureTabs();
            int i = TabsByMode[m].IndexOf(name);
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

    /// <summary>Time.frameCount of the last menu draw; lets features tell whether the menu is open.</summary>
    internal static int DrawnFrame = -100;

    public static void Draw()
    {
        DrawnFrame = Time.frameCount;
        Theme.Ensure();
        EnsureFeatureTabs();
        MenuUx.BeginFrame();
        float scale = MenuUx.MenuScale(Mathf.Max(1f, Screen.height / 1080f));
        GUI.matrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, new Vector3(scale, scale, 1f));

        float sw = Screen.width / scale, sh = Screen.height / scale;
        _pos.x = Mathf.Clamp(_pos.x, 0f, Mathf.Max(0f, sw - W));
        _pos.y = Mathf.Clamp(_pos.y, 0f, Mathf.Max(0f, sh - 80f));
        float h = Mathf.Min(H, sh - _pos.y - 10f);
        var win = new Rect(_pos.x, _pos.y, W, h);

        HandleDrag(new Rect(win.x, win.y, 250f, HeaderH)); // title area only: the right side has buttons
        GUI.Box(win, GUIContent.none, Theme.Window);
        DrawHeader(win);
        float tabsH = TabRows * TabsH;
        DrawTabs(new Rect(win.x + Pad, win.y + HeaderH, W - Pad * 2, tabsH));
        float searchH = MenuUx.SearchBarHeight;
        MenuUx.DrawSearchBar(new Rect(win.x + Pad, win.y + HeaderH + tabsH + 8f, W - Pad * 2, searchH), _mode, TabsByMode, ModeNames);

        var area = new Rect(win.x + Pad, win.y + HeaderH + tabsH + searchH + 10f, W - Pad * 2, h - HeaderH - tabsH - searchH - FooterH - 18f);
        string tab = Tabs[Mathf.Clamp(_tab, 0, Tabs.Count - 1)];
        Scroll.TryGetValue(tab, out var scroll);
        float contentH = ContentH.TryGetValue(tab, out var ch) ? ch : 600f;
        Scroll[tab] = GUI.BeginScrollView(area, scroll, new Rect(0f, 0f, area.width - 16f, contentH));
        _y = 0f;
        _cw = area.width - 18f;
        DrawTabContent(_mode, tab);
        ContentH[tab] = _y + 6f;
        GUI.EndScrollView();

        if (MenuUx.NeedsIndex && Event.current.type == EventType.Layout)
        {
            // Search index: walk every tab once in a Layout event (nothing is drawn or clicked in Layout).
            float cw = _cw;
            MenuUx.BuildIndex(TabsByMode, (m, t) => { _y = 0f; _cw = cw; DrawTabContent(m, t); });
            _drawMode = _mode;
            _drawTab = tab;
        }

        DrawFooter(new Rect(win.x, win.yMax - FooterH, W, FooterH));
        MenuUx.EndFrame(area);
    }

    private static int _drawMode;
    private static string _drawTab = "";

    private static void DrawTabContent(int mode, string tab)
    {
        _drawMode = mode;
        _drawTab = tab;
        switch (tab)
        {
            case "QoL": DrawQol(); break;
            case "Lobby": DrawLobby(); break;
            case "Servers": DrawServers(); break;
        }
        foreach (var section in Features.Registry.SectionsFor(mode, tab))
        {
            try { section.Draw(); }
            catch (Exception e) { Small($"<color=#E5534B>{section.Owner} failed: {e.Message}</color>"); }
        }
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

    private const int TabsPerRow = 7;
    private static int TabRows => Math.Max(1, (Tabs.Count + TabsPerRow - 1) / TabsPerRow);

    private static void DrawTabs(Rect bar)
    {
        Theme.Fill(bar, Theme.Hex(0x0E1218), 8);
        int rows = TabRows;
        int perRow = (Tabs.Count + rows - 1) / rows;
        float tw = (bar.width - 8f) / perRow;
        for (int i = 0; i < Tabs.Count; i++)
        {
            int row = i / perRow, col = i % perRow;
            var r = new Rect(bar.x + 4f + col * tw, bar.y + 4f + row * TabsH, tw - 4f, TabsH - 8f);
            var prev = GUI.color;
            GUI.color = MenuUx.TabTint(_mode, Tabs[i], prev);
            if (GUI.Button(r, Tabs[i], i == _tab ? Theme.TabActive : Theme.Tab)) _tab = i;
            GUI.color = prev;
            MenuUx.TabBadge(r, _mode, Tabs[i]);
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

    internal static void SetStatus(string s)
    {
        if (string.IsNullOrEmpty(s)) return;
        _status = s;
        _statusAt = Time.unscaledTime;
    }

    // ---- tabs ----

    private static string _customSizeText;

    /// <summary>Drops text typed into menu fields so they re-read their settings (after settings change from outside).</summary>
    internal static void ForgetTypedText() => _customSizeText = null;

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
            Switch(ModConfig.MainMenuButton, "PLUS button on the main menu", "Takes effect next time the main menu opens", true);
        });
    }

    private static void DrawLobby()
    {
        Card("Lobby size", "Bigger lobbies in the game's Create Server screen. Everyone in a lobby above 4 players should run this mod.", () =>
        {
            Switch(ModConfig.ExpandLobbySize, "Up to 16 players", "Adds 2–16 to the Max Players option", true);
            GUI.Label(new Rect(Inset + 4f, _y, 190f, 28f), "Exact size (typed)", Theme.Label);
            _customSizeText ??= ModConfig.CustomMaxPlayers.Value >= 2 ? ModConfig.CustomMaxPlayers.Value.ToString() : "";
            _customSizeText = GUI.TextField(new Rect(Inset + 200f, _y, 80f, 28f), _customSizeText, 3, Theme.TextField);
            int typed = int.TryParse(_customSizeText, out int ts) ? ts : 0;
            int wanted = typed >= 2 ? Math.Min(typed, RoomNames.MaxCustomPlayers) : 0;
            if (wanted != ModConfig.CustomMaxPlayers.Value) ModConfig.CustomMaxPlayers.Value = wanted;
            GUI.Label(new Rect(Inset + 290f, _y, IW - 290f, 28f), wanted >= 2 ? $"hosting uses {wanted} (max {RoomNames.MaxCustomPlayers}{(wanted > 32 ? ", untested above 32" : "")})" : "empty = Create Server choice", Theme.Small);
            _y += 34f;
            float v = RawSlider("Preselected size", ModConfig.DefaultMaxPlayers.Value, 2f, LobbyRules.MaxPlayers,
                LobbyRules.OptionLabel(ModConfig.DefaultMaxPlayers.Value), ModConfig.ExpandLobbySize.Value);
            int n = Mathf.RoundToInt(v);
            if (n != ModConfig.DefaultMaxPlayers.Value) ModConfig.DefaultMaxPlayers.Value = n;
            KeyValue("Network capacity", ModConfig.MeasuredCapacity.Value > 0
                ? $"{ModConfig.MeasuredCapacity.Value} players (measured)"
                : "not measured yet — join or host any session once");
            Small("Above the network capacity, extra players get fallback spawn spots and placeholder names instead of crashing the lobby.");
        });
        Card("Server name", "Used for servers you host from now on. Leave empty for the game's default.", () =>
        {
            GUI.Label(new Rect(Inset + 4f, _y, 60f, 28f), "Name", Theme.Label);
            string name = GUI.TextField(new Rect(Inset + 64f, _y, IW - 64f - 98f, 28f), ModConfig.CustomRoomName.Value ?? "", 40, Theme.TextField);
            if (name != ModConfig.CustomRoomName.Value) ModConfig.CustomRoomName.Value = name;
            if (ChipButton(new Rect(Inset + IW - 92f, _y, 92f, 28f), "Clear", !string.IsNullOrEmpty(ModConfig.CustomRoomName.Value)))
                ModConfig.CustomRoomName.Value = "";
            _y += 36f;
            if (Features.Public.Filter.NameFilter.IsBlocked(RoomNames.Escape(ModConfig.CustomRoomName.Value), out string nameWhy))
                Small($"<color=#E5534B>Not used: {nameWhy}. Servers keep the game's default name until you change it.</color>");
            var styles = RoomNames.Styles.Select(st => (st == ModConfig.RoomNameStyle.Value ? $"[{st}]" : st, (Action)(() => ModConfig.RoomNameStyle.Value = st), true)).ToArray();
            Chips(5, styles);
            Switch(ModConfig.RoomNameBold, "Bold", null, true);
            string preview = RoomNames.Style(RoomNames.DisplayName("Player's room"));
            var row = new Rect(Inset - 6f, _y, IW + 12f, 34f);
            Theme.Fill(row, Theme.Hex(0x0E1218), 6);
            GUI.Label(new Rect(Inset + 4f, _y + 4f, IW - 8f, 26f), "Preview:  " + preview, Theme.Label);
            _y += 40f;
            Small("Colours show in everyone's server browser, including players without the mod.");
        });
        Card("Join message", "When you host: a pop-up for everyone each time a player joins. {name} = who joined, {host} = you.", () =>
        {
            Switch(ModConfig.JoinMessageEnabled, "Greet players who join", null, true);
            string msg = GUI.TextField(new Rect(Inset, _y, IW, 28f), ModConfig.JoinMessage.Value ?? "", 90, Theme.TextField);
            if (msg != ModConfig.JoinMessage.Value) ModConfig.JoinMessage.Value = msg;
            _y += 34f;
            if (Features.Public.Filter.NameFilter.IsBlocked(RoomNames.Escape(ModConfig.JoinMessage.Value), out string msgWhy))
                Small($"<color=#E5534B>Not sent: {msgWhy}.</color>");
            var colors = JoinMessage.Colors.Select(c => (c == ModConfig.JoinMessageColor.Value ? $"[{c}]" : c, (Action)(() => ModConfig.JoinMessageColor.Value = c), true)).ToArray();
            Chips(7, colors);
            KeyValue("Preview", JoinMessage.Preview());
        });
        Card("Stability", null, () =>
        {
            Switch(ModConfig.LobbyGuards, "Lobby crash guards", "Smart spawn points, safe join/leave, finish registering extra players", true);
            Switch(ModConfig.FallRescue, "Fall rescue", "Host/solo: cars under the ground or off the map are put back", true);
            KeyValue("Cars rescued", BigLobby.Rescued.ToString());
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
    }

    private static void DrawServers()
    {
        Card("Server browser", null, () =>
        {
            Switch(ModConfig.ColorRows, "Color-code rows", "Stripe and tint by server status", true);
            Switch(ModConfig.ShowAge, "Show server age", "How long ago the mod first saw each server", true);
            Switch(ModConfig.ThrottleSessionUpdates, "Smooth list refreshes", "Skip duplicate updates and merge bursts", true);
            SliderRow(ModConfig.SessionUpdateInterval, "Min time between rebuilds", 0.25f, 5f, "0.00", " s", ModConfig.ThrottleSessionUpdates.Value);
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
        });
    }

    // ---- widgets ----

    private static float IW => _cw - Inset * 2;

    // ---- layout access for feature files (Features/*) ----

    /// <summary>Current vertical position inside the tab's scroll area.</summary>
    internal static float Y { get => _y; set => _y = value; }
    /// <summary>Left edge for content inside a card.</summary>
    internal static float Left => Inset;
    /// <summary>Usable content width inside a card.</summary>
    internal static float Width => IW;

    /// <summary>Reserves a full-width row of the given height inside a card and returns its rect.</summary>
    internal static Rect NextRow(float height)
    {
        var r = new Rect(Inset, _y, IW, height);
        _y += height + 6f;
        return r;
    }

    /// <summary>A labelled single-line text field row.</summary>
    internal static string TextRow(string label, string value, int maxLength = 64)
    {
        if (MenuUx.Note(label)) MenuUx.Highlight(new Rect(Inset - 6f, _y - 2f, IW + 12f, 32f));
        GUI.Label(new Rect(Inset + 4f, _y, 150f, 28f), label, Theme.Label);
        string v = GUI.TextField(new Rect(Inset + 156f, _y, IW - 156f, 28f), value ?? "", maxLength, Theme.TextField);
        _y += 34f;
        return v;
    }

    internal static void Card(string title, string subtitle, Action body)
    {
        float top = _y;
        string key = MenuUx.CardKey(_drawMode, _drawTab, title);
        float h = CardH.TryGetValue(key, out var cached) ? cached : 80f;
        GUI.Box(new Rect(0f, top, _cw, h), GUIContent.none, Theme.CardBox);
        bool open = MenuUx.CardHeader(key, title, new Rect(0f, top, _cw, h), Inset, IW);
        _y += 32f;
        if (open)
        {
            var prev = GUI.color;
            GUI.color = MenuUx.CardTint(key, prev);
            try
            {
                if (!string.IsNullOrEmpty(subtitle)) Small(subtitle);
                body();
            }
            finally { GUI.color = prev; }
            _y += 8f;
        }
        else _y = top + 38f; // folded: title row only
        if (!MenuUx.Indexing) CardH[key] = _y - top;
        _y += 10f;
    }

    internal static void Banner(string text, Color color)
    {
        MenuUx.Note(text);
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

    internal static void Small(string text)
    {
        MenuUx.Note(text);
        float th = Theme.Muted_.CalcHeight(new GUIContent(text), IW);
        GUI.Label(new Rect(Inset, _y, IW, th), text, Theme.Muted_);
        _y += th + 6f;
    }

    internal static void Switch(ConfigEntry<bool> entry, string label, string hint, bool enabled)
    {
        float rh = hint == null ? 30f : 42f;
        var row = new Rect(Inset - 6f, _y, IW + 12f, rh);
        var prev = GUI.color;
        if (!enabled) GUI.color = new Color(1f, 1f, 1f, 0.45f);
        if (MenuUx.Note(label, hint, entry?.Description?.Description)) MenuUx.Highlight(row);
        else if (row.Contains(Event.current.mousePosition)) Theme.Fill(row, Theme.CardHover, 6);

        GUI.Label(new Rect(Inset + 4f, _y + (hint == null ? 4f : 2f), IW - 70f, 22f), label, Theme.Label);
        if (hint != null) GUI.Label(new Rect(Inset + 4f, _y + 21f, IW - 70f, 18f), hint, Theme.Small);

        var track = new Rect(Inset + IW - 44f, _y + (rh - 22f) / 2f, 42f, 22f);
        Theme.Fill(track, entry.Value ? Theme.Accent : Theme.Off, 11);
        Theme.Fill(new Rect(entry.Value ? track.xMax - 20f : track.x + 2f, track.y + 2f, 18f, 18f), Theme.Text, 9);

        if (GUI.Button(row, MenuUx.TipContent(entry, hint), Theme.Invisible)) entry.Value = !entry.Value;
        GUI.color = prev;
        _y += rh + 2f;
    }

    internal static void SliderRow(ConfigEntry<float> entry, string label, float min, float max, string fmt, string suffix, bool enabled, bool percent = false)
    {
        string shown = percent ? (entry.Value * 100f).ToString("0") + "%" : entry.Value.ToString(fmt) + suffix;
        float v = RawSlider(label, entry.Value, min, max, shown, enabled, MenuUx.TipFor(entry, null), entry.Description?.Description);
        if (Mathf.Abs(v - entry.Value) > 0.0001f) entry.Value = Mathf.Round(v * 100f) / 100f;
    }

    internal static float RawSlider(string label, float value, float min, float max, string shown, bool enabled, string tooltip = null, string searchText = null)
    {
        if (MenuUx.Note(label, searchText)) MenuUx.Highlight(new Rect(Inset - 6f, _y - 1f, IW + 12f, 30f));
        var prev = GUI.color;
        if (!enabled) GUI.color = new Color(1f, 1f, 1f, 0.45f);
        GUI.Label(new Rect(Inset + 4f, _y, 190f, 28f), label, Theme.Label);
        MenuUx.Tooltip(new Rect(Inset, _y, 196f, 28f), tooltip);
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

    internal static void KeyValue(string key, string value)
    {
        if (MenuUx.Note(key)) MenuUx.Highlight(new Rect(Inset - 6f, _y, IW + 12f, 22f));
        GUI.Label(new Rect(Inset + 4f, _y, 176f, 22f), key, Theme.Muted_);
        GUI.Label(new Rect(Inset + 180f, _y, IW - 184f, 22f), value, Theme.Label);
        _y += 24f;
    }

    internal static void Bar(string label, float fraction, Color color, string text)
    {
        MenuUx.Note(label);
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

    internal static void Chips(int columns, params (string label, Action action, bool enabled)[] items)
    {
        const float gap = 6f;
        float w = (IW - gap * (columns - 1)) / columns;
        for (int i = 0; i < items.Length; i++)
        {
            int col = i % columns;
            if (i > 0 && col == 0) _y += 36f;
            var r = new Rect(Inset + col * (w + gap), _y, w, 30f);
            bool hit = MenuUx.Note(items[i].label);
            if (ChipButton(r, items[i].label, items[i].enabled)) items[i].action();
            if (hit) Theme.Fill(new Rect(r.x + 6f, r.yMax - 3f, r.width - 12f, 2f), Theme.Accent, 1);
        }
        _y += 36f;
    }

    internal static bool ChipButton(Rect r, string label, bool enabled)
    {
        var prev = GUI.color;
        if (!enabled) GUI.color = new Color(1f, 1f, 1f, 0.35f);
        bool clicked = GUI.Button(r, label, Theme.Chip) && enabled;
        GUI.color = prev;
        return clicked;
    }
}
