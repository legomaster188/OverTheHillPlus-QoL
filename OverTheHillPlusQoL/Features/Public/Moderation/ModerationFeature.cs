using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using Fusion;
using HarmonyLib;
using OverTheHillPlus.Features.Public.Photo;
using OverTheHillPlus.Features.Public.PlayerList;
using OverTheHillPlus.UI;
using UnityEngine;
using Net = OverTheHillPlus.Features.Public.ModNet.ModNet;
using SteamFriendsList = OverTheHillPlus.Features.Public.ServerBrowserPlus.SteamFriendsList;

namespace OverTheHillPlus.Features.Public.Moderation;

/// <summary>
/// Host moderation (PLUS > Moderation):
/// - AFK watch (<see cref="AfkWatch"/>): warn guests who haven't driven or touched the controls for a while, and
///   optionally remove them. Off by default; removal is a second opt-in.
/// - Join rules (<see cref="JoinRules"/>): a block list by name or Steam id, and a friends-only mode (the host's
///   Steam friends plus a manual allow list).
/// - A moderation log of everything the host's rules did this game.
/// Everything that affects other players runs only while hosting; removals always use the game's own kick.
/// Guests running this mod with the mod network on get the host's AFK notices on screen.
/// </summary>
internal sealed class ModerationFeature : IFeature
{
    internal const string Section = "Moderation";
    private const string Tab = "Moderation";

    internal static ConfigEntry<bool> AfkEnabled, AfkAnnounce, AfkRemove, AfkAllowRejoin, AfkOnlyWhenFull, ShowNotices;
    internal static ConfigEntry<int> AfkMinutes, AfkRespondSeconds;

    public void Register()
    {
        var f = ModConfig.File;
        AfkEnabled = f.Bind(Section, "AfkWatch", false,
            "Host: watch guests for being away (no driving, turning or controls) and warn them. Off by default.");
        AfkMinutes = f.Bind(Section, "AfkMinutes", 10,
            new ConfigDescription("AFK watch: minutes without activity before a guest is warned.", new AcceptableValueRange<int>(2, 60)));
        AfkAnnounce = f.Bind(Section, "AfkAnnounce", true,
            "AFK watch: show the warning (and a removal) as a pop-up to everyone in the session, so vanilla players see it too.");
        AfkRemove = f.Bind(Section, "AfkRemove", false,
            "AFK watch: remove a warned guest who is still away after the response time. Off by default.");
        AfkRespondSeconds = f.Bind(Section, "AfkRespondSeconds", 120,
            new ConfigDescription("AFK watch: seconds a warned guest has to come back before being removed.", new AcceptableValueRange<int>(30, 600)));
        AfkAllowRejoin = f.Bind(Section, "AfkAllowRejoin", true,
            "AFK watch: a guest removed for being away may join again (the game's kick would otherwise keep them out for the session).");
        AfkOnlyWhenFull = f.Bind(Section, "AfkOnlyWhenFull", false,
            "AFK watch: only count idle time while the server is full (free a slot for someone waiting).");
        ShowNotices = f.Bind(Section, "ShowHostNotices", true,
            "Guest: show the host's AFK notices on screen (modded hosts with the mod network on).");
        JoinRules.Bind(f, Section);

        AfkWatch.RegisterChannel();
        Registry.OnTick(Tick);
        Registry.OnGui(DrawOverlay);
        Registry.Section(Registry.PlusMode, Tab, DrawAfkCard, 10);
        Registry.Section(Registry.PlusMode, Tab, DrawJoinCard, 20);
        Registry.Section(Registry.PlusMode, Tab, DrawPlayersCard, 30);
        Registry.Section(Registry.PlusMode, Tab, DrawLogCard, 40);
        PatchJoinLeave();
    }

    private static void Tick()
    {
        try { ModerationCore.Tick(); } catch (Exception e) { ModerationCore.LogOnce("core", "[Moderation] " + e.Message); }
        try { JoinRules.Tick(); } catch (Exception e) { ModerationCore.LogOnce("join", "[Moderation] Join rules: " + e); }
        AfkWatch.Tick();
    }

    // ---------------------------------------------------------------- join / leave

    /// <summary>Patched by hand, each on its own: if a callback was renamed, the periodic scans still cover joins.</summary>
    private static void PatchJoinLeave()
    {
        var h = new Harmony(Plugin.Guid + ".moderation");
        void Try(string method, string patch)
        {
            try
            {
                var target = AccessTools.Method(typeof(OverlandNetworkRunner), method);
                if (target == null) { Plugin.Logger.LogWarning($"[Moderation] {method} not found; relying on the periodic scan"); return; }
                h.Patch(target, postfix: new HarmonyMethod(AccessTools.Method(typeof(ModerationFeature), patch)));
            }
            catch (Exception e) { Plugin.Logger.LogWarning($"[Moderation] Could not patch {method}: {e.Message}"); }
        }
        Try("Fusion_INetworkRunnerCallbacks_OnPlayerJoined", nameof(JoinedPostfix));
        Try("Fusion_INetworkRunnerCallbacks_OnPlayerLeft", nameof(LeftPostfix));
    }

    private static void JoinedPostfix(PlayerRef player)
    {
        try
        {
            Tokens.Remove(player.RawEncoded);
            AfkWatch.PlayerJoinedOrLeft(player);
            JoinRules.PlayerJoined(player);
        }
        catch (Exception e) { ModerationCore.LogOnce("joined", "[Moderation] join hook: " + e.Message); }
    }

    private static void LeftPostfix(PlayerRef player)
    {
        try
        {
            Tokens.Remove(player.RawEncoded);
            AfkWatch.PlayerJoinedOrLeft(player);
            JoinRules.PlayerLeft(player);
        }
        catch (Exception e) { ModerationCore.LogOnce("left", "[Moderation] leave hook: " + e.Message); }
    }

    // ---------------------------------------------------------------- menu

    private static string _entryText = "";
    private static int _armedRaw;
    private static float _armedUntil;

    private static void IntSlider(ConfigEntry<int> e, string label, int min, int max, Func<int, string> shown, bool enabled)
    {
        int v = Mathf.RoundToInt(Menu.RawSlider(label, e.Value, min, max, shown(e.Value), enabled));
        if (v != e.Value) e.Value = Mathf.Clamp(v, min, max);
    }

    private static string RoleLine()
    {
        if (ModerationCore.Hosting) return null;
        return ModerationCore.InMultiplayer ? "You are a guest here: these rules apply only to sessions you host." : "Applies while you host a multiplayer session.";
    }

    private static void DrawAfkCard()
    {
        Menu.Card("AFK watch (host)", "Warn guests who haven't driven, turned or touched the controls for a while; optionally remove them. Off by default.", () =>
        {
            bool on = AfkEnabled.Value;
            Menu.Switch(AfkEnabled, "AFK watch", on ? "On while you host" : "Off: nobody is warned or removed", true);
            IntSlider(AfkMinutes, "Away after", 2, 60, v => v + " min", on);
            Menu.Switch(AfkAnnounce, "Pop-up for everyone", "The warning shows like the join message, also for players without the mod", on);
            Menu.Switch(AfkRemove, "Remove if still away", AfkRemove.Value ? "After the response time below (at most 6 removals a minute)" : "Only warns", on);
            IntSlider(AfkRespondSeconds, "Time to respond", 30, 600, v => v + " s", on && AfkRemove.Value);
            Menu.Switch(AfkAllowRejoin, "Let them rejoin", "A guest removed for being away may join again", on && AfkRemove.Value);
            Menu.Switch(AfkOnlyWhenFull, "Only when the server is full", "Idle time only counts while every slot is taken", on);
            string role = RoleLine();
            if (role != null) Menu.Small(role);
            else if (on) Menu.KeyValue("Status", AfkWatch.HostState);
            if (AfkWatch.Warned + AfkWatch.Removed > 0) Menu.KeyValue("This game", $"{AfkWatch.Warned} warned · {AfkWatch.Removed} removed");
            Menu.Small("Moving 2 m, turning, or any steering, throttle, brake or button counts as activity. Time only counts while the guest has a car out, " +
                       "and a stall on your PC never adds minutes. Guests with this mod and the mod network on see the warning on their screen, " +
                       "and count as active while they spectate, chat or use the keyboard or mouse.");
            if (on && !Net.Enabled.Value) Menu.Small("Turn on the mod network (Network tab) to warn modded guests on their screen too.");

            if (!ModerationCore.Hosting && AfkWatch.HostRuleKnown)
                Menu.KeyValue("This host's AFK rule", !AfkWatch.HostRuleOn ? "off"
                    : $"warns after {AfkWatch.HostRuleMinutes} min" + (AfkWatch.HostRuleKicks ? $", removes {AfkWatch.HostRuleRespond} s later" : ""));
            Menu.Switch(ShowNotices, "Show host notices", "Guest: the host's away warnings on your screen", true);
        });
    }

    private static readonly string[] ModeNames = { "Everyone", "Steam friends + allow list", "Allow list only" };

    private static void DrawJoinCard()
    {
        Menu.Card("Join rules (host)", "Who may join your sessions. Checked as each player joins; players already in are left alone.", () =>
        {
            var mode = JoinRules.CurrentMode;
            int m = (int)mode;
            Menu.Chips(3,
                ((m == 0 ? "> " : "") + ModeNames[0], () => SetMode(0), true),
                ((m == 1 ? "> " : "") + "Friends only", () => SetMode(1), true),
                ((m == 2 ? "> " : "") + ModeNames[2], () => SetMode(2), true));
            Menu.KeyValue("Who may join", ModeNames[m]);
            if (mode == JoinRules.FriendsMode.SteamFriendsAndAllowList)
            {
                if (SteamFriendsList.AvailableNow) Menu.KeyValue("Steam friends", SteamFriendsList.Total.ToString());
                else Menu.Banner((SteamFriendsList.Problem ?? "Steam friends list unavailable") + ". Until it can be read, the friends rule lets everyone in; the block list still applies.", Theme.Warn);
            }
            if (mode == JoinRules.FriendsMode.AllowListOnly && JoinRules.AllowList.Count == 0)
                Menu.Banner("The allow list is empty: every player who joins will be removed.", Theme.Bad);
            Menu.Switch(JoinRules.BlockEnabled, "Block list", $"{JoinRules.BlockList.Count} player(s); removed as they join", true);

            _entryText = Menu.TextRow("Name or Steam id", _entryText, 40);
            bool typed = !string.IsNullOrWhiteSpace(_entryText);
            Menu.Chips(2,
                ("Add to block list", () => { Menu.SetStatus(JoinRules.Add(true, _entryText)); _entryText = ""; }, typed),
                ("Add to allow list", () => { Menu.SetStatus(JoinRules.Add(false, _entryText)); _entryText = ""; }, typed));

            DrawList("Blocked", JoinRules.BlockList, true);
            DrawList("Allowed", JoinRules.AllowList, false);

            string role = RoleLine();
            if (role != null) Menu.Small(role);
            else if (JoinRules.WaitingCount > 0) Menu.KeyValue("Checking", $"{JoinRules.WaitingCount} joining player(s)");
            Menu.Small("Names are matched without case. A removed player is also kept out by the game for the rest of the session. " +
                       "Steam ids come from the player's game (the same id the game uses for its own badges); a modified game could send another one, " +
                       "so friends only keeps strangers out of casual sessions, it is not a password.");
        });
    }

    private static void SetMode(int m)
    {
        JoinRules.Mode.Value = m;
        if (m == 1) SteamFriendsList.RefreshSoon();
        Menu.SetStatus("Who may join: " + ModeNames[m] + (m == 0 ? "" : " (new joins only)"));
        ModerationCore.Add("Join rule set to: " + ModeNames[m]);
    }

    private static void DrawList(string title, IReadOnlyList<JoinRules.Entry> list, bool blockList)
    {
        if (list.Count == 0) return;
        Menu.KeyValue(title, list.Count + (list.Count == 1 ? " entry" : " entries") + " (click to take off)");
        var chips = new List<(string, Action, bool)>();
        for (int i = 0; i < list.Count && i < 30; i++)
        {
            var e = list[i];
            chips.Add(("x " + ModerationCore.Clip(e.Shown, 22), () => Menu.SetStatus(JoinRules.Remove(blockList, e)), true));
        }
        Menu.Chips(2, chips.ToArray());
        if (list.Count > 30) Menu.Small($"+{list.Count - 30} more (edit the config file to see them all).");
    }

    private static void DrawPlayersCard()
    {
        if (!ModerationCore.Hosting) return;
        Menu.Card("Players (host)", "Everyone in your session, how long they've been idle, and a block button (click twice).", () =>
        {
            var rows = AfkWatch.Players;
            if (rows.Count == 0) { Menu.Small("No guests yet."); return; }
            bool friendsShown = JoinRules.CurrentMode == JoinRules.FriendsMode.SteamFriendsAndAllowList && SteamFriendsList.AvailableNow;
            int shown = 0;
            foreach (var e in rows)
            {
                if (shown++ >= 24) { Menu.Small($"+{rows.Count - 24} more"); break; }
                string detail = e.State;
                if (string.IsNullOrEmpty(detail)) detail = e.HasCar ? (e.Idle >= 5f ? "idle " + ModerationCore.Minutes(e.Idle) : "active") : "no car";
                if (e.Modded) detail += " · mod";
                if (friendsShown)
                {
                    ulong id = ModerationCore.SteamIdFromToken(TokenCache(e.Ref));
                    if (id != 0 && SteamFriendsList.IsFriend(id) == true) detail += " · friend";
                }
                Menu.KeyValue(ModerationCore.Clip(e.Name, 22), detail);
                bool armed = _armedRaw == e.Raw && Time.unscaledTime < _armedUntil;
                var ev = e;
                Menu.Chips(1, (armed ? $"Click again to block and remove {ModerationCore.Clip(e.Name, 18)}" : $"Block {ModerationCore.Clip(e.Name, 18)}",
                    () => BlockClicked(ev), true));
            }
        });
    }

    // Tokens don't change during a connection: read each once for the menu, not every frame.
    private static readonly Dictionary<int, string> Tokens = new();

    private static string TokenCache(PlayerRef p)
    {
        if (Tokens.TryGetValue(p.RawEncoded, out var t)) return t;
        if (Tokens.Count > 128) Tokens.Clear();
        t = ModerationCore.TokenOf(p);
        Tokens[p.RawEncoded] = t;
        return t;
    }

    private static void BlockClicked(AfkPlayer e)
    {
        if (!(_armedRaw == e.Raw && Time.unscaledTime < _armedUntil))
        {
            _armedRaw = e.Raw;
            _armedUntil = Time.unscaledTime + 4f;
            return;
        }
        _armedRaw = 0;
        Tokens.Remove(e.Raw);
        ulong id = ModerationCore.SteamIdFromToken(ModerationCore.TokenOf(e.Ref));
        string name = ModerationCore.RealNameOf(e.Ref) ?? e.Name;
        string added = JoinRules.Add(true, name, id);
        string removed = ModerationCore.Kick(e.Ref, "blocked by the host", false);
        Menu.SetStatus(added + " " + removed);
    }

    private static void DrawLogCard()
    {
        Menu.Card("Moderation log", "What the AFK watch and join rules did this game (newest first). Also written to the BepInEx log.", () =>
        {
            var log = ModerationCore.Log;
            if (log.Count == 0) { Menu.Small("Nothing yet."); return; }
            int n = 0;
            for (int i = log.Count - 1; i >= 0 && n < 15; i--, n++)
                Menu.Small($"{log[i].At:HH:mm}  {(log[i].Bad ? "! " : "")}{log[i].Text}");
            if (log.Count > 15) Menu.Small($"{log.Count - 15} older line(s) in the BepInEx log.");
            Menu.Chips(1, ("Clear log", () => { ModerationCore.Log.Clear(); Menu.SetStatus("Moderation log cleared"); }, true));
        });
    }

    // ---------------------------------------------------------------- overlay (guest notices)

    private static GUIStyle _toastStyle;

    private static void DrawOverlay()
    {
        if (Event.current == null || Event.current.type != EventType.Repaint) return;
        if (!ShowNotices.Value) return;
        string msg = AfkWatch.LastNotice;
        float age = Time.unscaledTime - AfkWatch.LastNoticeAt;
        const float show = 10f;
        if (string.IsNullOrEmpty(msg) || age > show || age < 0f) return;
        if (PhotoCapture.CleanViewActive || PhotoCapture.Capturing) return;
        Theme.Ensure();
        if (_toastStyle == null)
        {
            _toastStyle = new GUIStyle(Theme.Label) { alignment = TextAnchor.MiddleCenter, richText = false, wordWrap = true };
            _toastStyle.normal.textColor = Theme.Text;
        }
        float alpha = Mathf.Clamp01(show - age);
        var prev = GUI.color;
        GUI.color = new Color(1f, 1f, 1f, alpha);
        float w = Mathf.Min(560f, Screen.width - 40f);
        float th = _toastStyle.CalcHeight(new GUIContent(msg), w - 28f);
        // Below the ping notice's spot, so both can show at once.
        var r = new Rect((Screen.width - w) / 2f, 150f, w, th + 16f);
        Theme.Fill(r, new Color(0.07f, 0.09f, 0.11f, 0.9f), 8);
        Theme.Fill(new Rect(r.x, r.y, 4f, r.height), AfkWatch.LastNoticeBad ? Theme.Warn : Theme.Good, 2);
        GUI.Label(new Rect(r.x + 14f, r.y + 8f, w - 28f, th), msg, _toastStyle);
        GUI.color = prev;
    }
}
