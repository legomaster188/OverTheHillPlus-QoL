using System;
using BepInEx.Configuration;
using OverTheHillPlus.Features.Cheats.Teleport;
using OverTheHillPlus.Features.Public;
using OverTheHillPlus.HostPowers;
using OverTheHillPlus.UI;
using UnityEngine;
using UnityEngine.InputSystem;
using Net = OverTheHillPlus.Features.Public.ModNet.ModNet;

namespace OverTheHillPlus.Features.Cheats.Players;

/// <summary>
/// MODS > Host Powers > Players: every player in the session with Go to / Bring / Send to my pin / Swap, "everyone to me" in a
/// formation, "summon all to pin", safe placement settings and teleport requests from modded guests (approve / deny
/// pop-up). Moving cars is host (or solo) only; as a guest the tab asks a modded host instead.
/// </summary>
internal sealed class PlayersFeature : IFeature
{
    private const string Section = "Players Teleport";
    private const string Tab = "Players";

    internal static ConfigEntry<float> Cooldown;
    internal static ConfigEntry<float> MaxSlope;
    internal static ConfigEntry<float> Separation;
    internal static ConfigEntry<float> SearchRadius;
    internal static ConfigEntry<bool> AvoidWater;
    internal static ConfigEntry<float> Columns;
    internal static ConfigEntry<float> Spacing;
    internal static ConfigEntry<float> RowGap;
    internal static ConfigEntry<bool> SummonIncludesMe;
    internal static ConfigEntry<bool> AcceptRequests;
    internal static ConfigEntry<bool> AutoApprove;
    internal static ConfigEntry<float> RequestTimeout;
    internal static ConfigEntry<float> RequestCooldown;
    internal static ConfigEntry<bool> ShowPopup;
    internal static ConfigEntry<Key> ApproveKey;
    internal static ConfigEntry<Key> DenyKey;

    private static string _toast = "";
    private static float _toastAt = -100f;
    private static GUIStyle _popupTitle, _popupText, _toastStyle, _popupButton;

    public void Register()
    {
        var cfg = HostPowersConfig.File;
        Cooldown = cfg.Bind(Section, "PerPlayerCooldown", 3f,
            new ConfigDescription("Seconds before the same player can be moved again (host moves).", new AcceptableValueRange<float>(0f, 60f)));
        MaxSlope = cfg.Bind(Section, "MaxSlope", 28f,
            new ConfigDescription("Steepest ground (degrees) a car is dropped on.", new AcceptableValueRange<float>(10f, 45f)));
        Separation = cfg.Bind(Section, "CarSpacing", 5.5f,
            new ConfigDescription("Minimum distance between dropped cars and other cars (m).", new AcceptableValueRange<float>(3.5f, 15f)));
        SearchRadius = cfg.Bind(Section, "SearchRadius", 32f,
            new ConfigDescription("How far from the wanted spot to look for safe ground (m).", new AcceptableValueRange<float>(0f, 80f)));
        AvoidWater = cfg.Bind(Section, "AvoidWater", true, "Never drop a car into a river or lake.");
        Columns = cfg.Bind(Section, "FormationColumns", 3f,
            new ConfigDescription("Cars per row in 'everyone to me' and 'summon'.", new AcceptableValueRange<float>(1f, 6f)));
        Spacing = cfg.Bind(Section, "FormationSpacing", 6.5f,
            new ConfigDescription("Side-to-side gap between cars in a row (m).", new AcceptableValueRange<float>(4f, 15f)));
        RowGap = cfg.Bind(Section, "FormationRowGap", 9f,
            new ConfigDescription("Front-to-back gap between rows (m).", new AcceptableValueRange<float>(6f, 20f)));
        SummonIncludesMe = cfg.Bind(Section, "SummonIncludesMe", true, "'Summon all to pin' also takes your own car to the front.");
        AcceptRequests = cfg.Bind(Section, "AcceptTeleportRequests", true,
            "Host: modded guests (this build, mod network on) can ask to be teleported; you approve or deny each request.");
        AutoApprove = cfg.Bind(Section, "AutoApproveRequests", false, "Host: approve every teleport request without asking.");
        RequestTimeout = cfg.Bind(Section, "RequestTimeout", 30f,
            new ConfigDescription("Seconds a request waits for your answer before it is denied.", new AcceptableValueRange<float>(10f, 120f)));
        RequestCooldown = cfg.Bind(Section, "RequestCooldown", 15f,
            new ConfigDescription("Seconds before the same guest can ask again after an answer.", new AcceptableValueRange<float>(0f, 120f)));
        ShowPopup = cfg.Bind(Section, "ShowRequestPopup", true, "Host: show incoming teleport requests on screen.");
        ApproveKey = cfg.Bind(Section, "ApproveRequestKey", Key.PageUp, "Host: approve the oldest teleport request (only while one is open). None = off.");
        DenyKey = cfg.Bind(Section, "DenyRequestKey", Key.PageDown, "Host: deny the oldest teleport request (only while one is open). None = off.");

        // Core's safe-placement search uses these limits (also for other mods' car placement while this one runs).
        SafeSpot.SettingsSource = () => new SafeSpot.Settings
        {
            MaxSlope = MaxSlope.Value,
            Separation = Separation.Value,
            SearchRadius = SearchRadius.Value,
            RingStep = 4f,
            AvoidWater = AvoidWater.Value,
            MaxWaterDepth = 0.35f,
        };

        TeleportRequests.RegisterChannel();
        Registry.Section(Registry.PlusMode, Tab, DrawTab, 0);
        Registry.OnTick(Tick);
        Registry.OnGui(DrawOverlay);
    }

    internal static void Report(string status)
    {
        if (string.IsNullOrEmpty(status)) return;
        try { Menu.SetStatus(status); } catch (Exception ex) { DebugLog.Error("hostpowers", ex, "PlayersFeature.Report"); }
        _toast = status.Length > 160 ? status.Substring(0, 159) + "…" : status;
        _toastAt = Time.unscaledTime;
        HostPowersPlugin.Logger.LogInfo("[Players] " + status);
    }

    // ---------------------------------------------------------------- per frame

    private static void Tick()
    {
        PlayerMoves.Tick();
        TeleportRequests.Tick();
        if (TeleportRequests.Queue.Count == 0) return;
        var kb = Keyboard.current;
        if (kb == null) return;
        if (MenuUx.HotkeysCaptured || MenuUx.Typing || KeybindEditor.Capturing) return;
        if (GUIUtility.keyboardControl != 0) return; // typing in a text field
        if (Pressed(kb, ApproveKey)) Report(TeleportRequests.DecideFirst(true));
        else if (Pressed(kb, DenyKey)) Report(TeleportRequests.DecideFirst(false));
    }

    private static bool Pressed(Keyboard kb, ConfigEntry<Key> entry)
    {
        var k = entry.Value;
        if (k == Key.None) return false;
        try { return kb[k].wasPressedThisFrame; } catch (Exception ex) { DebugLog.Error("hostpowers", ex, "PlayersFeature.Pressed"); return false; }
    }

    private static string KeyName(ConfigEntry<Key> k) => k.Value == Key.None ? "no key" : k.Value.ToString();

    // ---------------------------------------------------------------- menu

    private static void DrawTab()
    {
        string blocked = PlayerMoves.NotAllowedReason;
        bool host = blocked == null;
        bool guest = Net.IsGuest;
        if (!host && !guest)
            Menu.Banner("Load into a world first. Moving players works solo or when hosting.", Theme.Info);
        else if (!host)
            Menu.Banner("You're a guest: the host's game simulates every car, so only the host can move players. You can ask a modded host to teleport you (below).", Theme.Info);

        if (host) DrawRequests();
        if (host) DrawGroup();
        DrawRoster(host);
        if (!host && guest) DrawAsk();
        DrawSettings(host);
    }

    private static void DrawGroup()
    {
        bool hasPin = TeleportCore.TryGetPinCached(out _);
        bool busy = PlayerMoves.Busy;
        string sub = busy ? $"Moving players: {PlayerMoves.BusyLabel} ({PlayerMoves.BusyLeft} left)..." : "Tidy rows behind the leader, spaced out, on safe ground.";
        Menu.Card("Everyone", sub, () =>
        {
            Menu.Chips(2,
                ("Everyone to me", () => Report(PlayerMoves.EveryoneToMe()), !busy),
                (SummonIncludesMe.Value ? "Summon all (and me) to pin" : "Summon all to pin", () => Report(PlayerMoves.SummonAllToPin(SummonIncludesMe.Value)), !busy && hasPin));
            if (!hasPin) Menu.Small("Place a pin on the map to summon everyone there.");
        });
    }

    private static void DrawRoster(bool host)
    {
        var players = PlayerMoves.Players();
        var me = PlayerMoves.Me();
        bool hasHere = me != null && me.HasCar;
        bool hasPin = TeleportCore.TryGetPinCached(out _);
        bool busy = PlayerMoves.Busy;
        bool canAsk = !host && TeleportRequests.CanAsk(out _) && !TeleportRequests.Waiting;
        Menu.Card("Players", players.Count == 0 ? "Nobody listed yet." : $"{players.Count} in the session", () =>
        {
            if (players.Count == 0) { Menu.Small("No players found (load into a world)."); return; }
            foreach (var t in players)
            {
                string tag = t.IsMe ? "  (you)" : t.IsHost ? "  (host)" : "";
                string info;
                float cd = PlayerMoves.CooldownLeft(t);
                if (!t.HasCar) info = "loading";
                else if (cd > 0f) info = $"<color=#F2B33D>wait {Mathf.CeilToInt(cd)} s</color>";
                else if (t.IsMe) info = "";
                else info = hasHere ? TeleportCore.Distance(Vector3.Distance(me.Position, t.Position)) : "";
                string label = Trim(t.Name, 26) + tag + (info.Length > 0 ? "  ·  " + info : "");

                bool other = !t.IsMe && t.HasCar;
                var target = t;
                if (host)
                {
                    bool ok = other && !busy && cd <= 0f;
                    SimpleMenu.ActionsFor("player:" + t.Name, label,
                        ("Go to", () => Report(PlayerMoves.GoTo(target)), other && !busy && hasHere),
                        ("Bring", () => Report(PlayerMoves.Bring(target)), ok && hasHere),
                        ("To pin", () => Report(PlayerMoves.SendToMyPin(target)), ok && hasPin),
                        ("Swap", () => Report(PlayerMoves.Swap(target)), ok && hasHere));
                }
                else SimpleMenu.ActionsFor("player:" + t.Name, label, ("Ask", () => Report(TeleportRequests.Ask(target)), other && canAsk));
            }
            if (host)
                Menu.Small("Go to: you drive up behind them. Bring: they land behind you. To pin: they go to your map pin. Swap: you trade places. Spots are checked for water, steep slopes and obstacles.");
            else
                Menu.Small("Ask: the host gets a pop-up to let you teleport next to that player.");
        });
    }

    private static void DrawRequests()
    {
        var q = TeleportRequests.Queue;
        string sub;
        if (Net.Enabled == null || !Net.Enabled.Value) sub = "Turn on the mod network (PLUS > Network) so modded guests can ask.";
        else if (!Net.IsHost) sub = "Guests running this build can ask you for a teleport while you host.";
        else if (!Net.HostAdvertised) sub = "This server started with the mod network off: host a new one to take requests.";
        else if (!AcceptRequests.Value) sub = "Not taking requests.";
        else sub = q.Count == 0 ? "No open requests." : $"{q.Count} waiting for you.";
        Menu.Card("Teleport requests", sub, () =>
        {
            Menu.Switch(AcceptRequests, "Take teleport requests", "Modded guests can ask to go to a player or their map pin", true);
            Menu.Switch(AutoApprove, "Approve automatically", "Skip the pop-up and teleport everyone who asks", AcceptRequests.Value);
            Menu.Switch(ShowPopup, "Pop-up on screen", $"Approve with {KeyName(ApproveKey)}, deny with {KeyName(DenyKey)} (config, section {Section})", AcceptRequests.Value);
            Menu.SliderRow(RequestTimeout, "Answer within", 10f, 120f, "0", " s", AcceptRequests.Value);
            Menu.SliderRow(RequestCooldown, "Ask again after", 0f, 120f, "0", " s", AcceptRequests.Value);
            if (TeleportRequests.Approved + TeleportRequests.Denied > 0)
                Menu.KeyValue("This session", $"{TeleportRequests.Approved} approved · {TeleportRequests.Denied} denied");

            foreach (var r in q.ToArray())
            {
                // The seconds left are a separate line: a label that changes every second would drop a press on the row.
                var req = r;
                SimpleMenu.Actions($"{Trim(TeleportRequests.NameOf(r.From), 22)}: {TeleportRequests.Describe(r)}",
                    ("Approve", () => Report(TeleportRequests.Decide(req, true)), true),
                    ("Deny", () => Report(TeleportRequests.Decide(req, false)), true));
                Menu.Small($"{Mathf.CeilToInt(TeleportRequests.SecondsLeft(r))} s left to answer");
            }
        });
    }

    private static void DrawAsk()
    {
        bool can = TeleportRequests.CanAsk(out string why);
        bool hasPin = TeleportCore.TryGetPinCached(out _);
        string sub = !can ? "Not available: " + why + "."
            : TeleportRequests.Waiting ? $"Waiting for the host ({TeleportRequests.WaitingFor})" + (TeleportRequests.HostAcknowledged ? ", the host has it on screen." : "...")
            : "The host decides. Use Ask next to a player, or go to your map pin.";
        Menu.Card("Ask the host", sub, () =>
        {
            float cd = TeleportRequests.GuestCooldownLeft;
            Menu.Chips(2,
                (cd > 0f ? $"To my map pin ({Mathf.CeilToInt(cd)} s)" : "Teleport me to my map pin", () => Report(TeleportRequests.Ask(null)), can && hasPin && !TeleportRequests.Waiting),
                ("Cancel request", () => Report(TeleportRequests.Cancel()), TeleportRequests.Waiting));
            if (!hasPin) Menu.Small("Place a pin on the map to ask for a teleport there.");
        });
    }

    private static void DrawSettings(bool host)
    {
        Menu.Card("Placement and formation", "Applies to every move made from this tab and to approved requests.", () =>
        {
            Menu.SliderRow(Cooldown, "Per-player cooldown", 0f, 60f, "0", " s", true);
            Menu.Switch(AvoidWater, "Avoid water", "Never drop a car into a river or lake", true);
            Menu.SliderRow(MaxSlope, "Steepest ground", 10f, 45f, "0", "°", true);
            Menu.SliderRow(SearchRadius, "Search radius", 0f, 80f, "0", " m", true);
            Menu.SliderRow(Separation, "Gap to other cars", 3.5f, 15f, "0.0", " m", true);
            float cols = Menu.RawSlider("Cars per row", Columns.Value, 1f, 6f, Mathf.RoundToInt(Columns.Value).ToString(), true);
            if (Mathf.RoundToInt(cols) != Mathf.RoundToInt(Columns.Value)) Columns.Value = Mathf.RoundToInt(cols);
            Menu.SliderRow(Spacing, "Side gap", 4f, 15f, "0.0", " m", true);
            Menu.SliderRow(RowGap, "Row gap", 6f, 20f, "0.0", " m", true);
            Menu.Switch(SummonIncludesMe, "Summon takes me too", "You go to the front of the formation at the pin", true);
            Menu.Small("Drop height and 'stop the car on arrival' are shared with the Teleport tab.");
        });
    }

    private static string Trim(string s, int max) => string.IsNullOrEmpty(s) || s.Length <= max ? s ?? "" : s.Substring(0, max - 1) + "…";

    // ---------------------------------------------------------------- overlay

    private static void DrawOverlay()
    {
        bool toast = Time.unscaledTime - _toastAt < 3f && _toast.Length > 0;
        bool popup = ShowPopup.Value && TeleportRequests.Queue.Count > 0 && Net.IsHost;
        if (!toast && !popup) return;
        var e = Event.current;
        if (e == null) return;
        EnsureStyles();
        GUI.matrix = Matrix4x4.identity;

        if (toast && e.type == EventType.Repaint)
        {
            var c = _toast.StartsWith("Failed") ? Theme.Bad : _toast.StartsWith("Blocked") ? Theme.Warn : Theme.Good;
            var size = _toastStyle.CalcSize(new GUIContent(_toast));
            float w = Mathf.Min(size.x + 28f, Screen.width - 40f);
            var r = new Rect((Screen.width - w) / 2f, 40f, w, 34f);
            Theme.Fill(r, new Color(0.07f, 0.09f, 0.11f, 0.88f), 8);
            Theme.Fill(new Rect(r.x, r.y, 4f, r.height), c, 2);
            GUI.Label(r, _toast, _toastStyle);
        }
        if (popup) DrawPopup(e);
    }

    private static void DrawPopup(Event e)
    {
        var q = TeleportRequests.Queue[0];
        int more = TeleportRequests.Queue.Count - 1;
        string title = "Teleport request" + (more > 0 ? $"  (+{more} more)" : "");
        string body = $"{Trim(TeleportRequests.NameOf(q.From), 28)} wants to {TeleportRequests.Describe(q)}";
        string keys = $"{KeyName(ApproveKey)}: approve   ·   {KeyName(DenyKey)}: deny   ·   {Mathf.CeilToInt(TeleportRequests.SecondsLeft(q))} s";
        // Buttons only while there is a mouse cursor (map or pause menu open) and the mod menu isn't (it has its own
        // Approve / Deny in MODS > Host Powers > Players, and a click there must never land on a button hidden under it).
        bool buttons = Cursor.visible && !MenuUx.MenuVisible;
        float w = 420f, h = buttons ? 118f : 92f;
        var r = new Rect(Screen.width - w - 24f, 120f, w, h);
        if (e.type == EventType.Repaint)
        {
            Theme.Fill(r, new Color(0.07f, 0.09f, 0.11f, 0.92f), 8);
            Theme.Fill(new Rect(r.x, r.y, 4f, r.height), Theme.Info, 2);
        }
        GUI.Label(new Rect(r.x + 14f, r.y + 8f, w - 28f, 22f), title, _popupTitle);
        GUI.Label(new Rect(r.x + 14f, r.y + 32f, w - 28f, 24f), body, _popupText);
        GUI.Label(new Rect(r.x + 14f, r.y + 58f, w - 28f, 22f), keys, Theme.Small);
        if (!buttons) return; // while driving the keys do it
        if (GUI.Button(new Rect(r.x + 14f, r.y + 84f, 120f, 26f), "Approve", _popupButton)) Report(TeleportRequests.Decide(q, true));
        else if (GUI.Button(new Rect(r.x + 142f, r.y + 84f, 120f, 26f), "Deny", _popupButton)) Report(TeleportRequests.Decide(q, false));
    }

    private static void EnsureStyles()
    {
        Theme.Ensure();
        if (_toastStyle != null) return;
        _toastStyle = new GUIStyle(Theme.Label) { alignment = TextAnchor.MiddleCenter, richText = false, wordWrap = false };
        _toastStyle.normal.textColor = Theme.Text;
        _popupTitle = new GUIStyle(Theme.Label) { alignment = TextAnchor.MiddleLeft, richText = false, fontStyle = FontStyle.Bold };
        _popupTitle.normal.textColor = Theme.Info;
        _popupText = new GUIStyle(Theme.Label) { alignment = TextAnchor.MiddleLeft, richText = false, wordWrap = false, clipping = TextClipping.Clip };
        _popupText.normal.textColor = Theme.Text;
        _popupButton = Theme.Chip ?? GUI.skin.button;
    }
}
