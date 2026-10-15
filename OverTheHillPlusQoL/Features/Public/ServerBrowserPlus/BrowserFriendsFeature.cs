using System;
using System.Collections.Generic;
using System.Linq;
using OverTheHillPlus.ServerBrowser;
using OverTheHillPlus.UI;
using UnityEngine;

namespace OverTheHillPlus.Features.Public.ServerBrowserPlus;

/// <summary>Server browser: Steam friends (highlight, filter, join), automatic list refresh and sort memory.</summary>
internal sealed class BrowserFriendsFeature : IFeature
{
    private const int MaxRows = 8;

    private static List<(OverlandSessionInfo info, BrowserFriends.Match match)> _rows = new();
    private static float _rowsAt = -10f;

    public void Register()
    {
        BrowserFriends.Bind(ModConfig.File);
        BrowserRefresh.Bind(ModConfig.File);
        Registry.Section(Registry.PlusMode, "Servers", DrawMenu, -8);
        Registry.OnTick(Tick);
    }

    private static void Tick()
    {
        BrowserFriends.Tick();
        BrowserRefresh.Tick();
    }

    // ---------------------------------------------------------------- menu

    private static void DrawMenu()
    {
        BrowserFriends.WantFriends();
        Menu.Card("Friends in servers", "Servers where your Steam friends are playing", () =>
        {
            var scope = BrowserFriends.Scope.Value;
            string state;
            Color color;
            if (!SteamFriendsList.Available)
            {
                state = SteamFriendsList.Problem ?? "Reading your Steam friends...";
                color = Theme.Warn;
            }
            else
            {
                var rows = Rows();
                int n = SteamFriendsList.CountIn(scope);
                string who = scope switch
                {
                    FriendScope.PlayingThisGame => $"{n} friend{(n == 1 ? "" : "s")} playing over the hill",
                    FriendScope.Online => $"{n} friend{(n == 1 ? "" : "s")} online",
                    _ => $"{n} friend{(n == 1 ? "" : "s")}",
                };
                state = rows.Count > 0
                    ? $"{who} · found in {rows.Count} server{(rows.Count == 1 ? "" : "s")}"
                    : BrowserPlus.AllSessions().Count == 0 ? $"{who} · open the game's server browser to look for them" : $"{who} · none found in the server list";
                if (SteamFriendsList.Problem != null) state += ". " + SteamFriendsList.Problem;
                color = rows.Count > 0 ? Theme.Good : Theme.Info;
            }
            Menu.Banner(state, color);

            if (SteamFriendsList.Available) DrawRows();

            Menu.Small("Look for");
            Menu.Chips(3,
                (Mark("Playing this game", scope == FriendScope.PlayingThisGame), (Action)(() => BrowserFriends.Scope.Value = FriendScope.PlayingThisGame), true),
                (Mark("Online", scope == FriendScope.Online), (Action)(() => BrowserFriends.Scope.Value = FriendScope.Online), true),
                (Mark("All friends", scope == FriendScope.All), (Action)(() => BrowserFriends.Scope.Value = FriendScope.All), true));

            Menu.Switch(BrowserFriends.Highlight, "Highlight their servers", "A coloured glow and edge on the row", true);
            Menu.Switch(BrowserFriends.ShowTag, "Friend tag", "FRIEND and the name next to the server name", true);
            Menu.Switch(BrowserFriends.PinFriends, "Pin their servers", "Right after favourites; quick join prefers them", true);
            Menu.Switch(BrowserFriends.OnlyFriends, "Only servers with friends", "Hide every other server (also a chip on the quick bar)", true);
            Menu.Switch(BrowserFriends.MatchHostName, "Match hosts by room name", "Finds friends hosting servers without this mod (\"Name's room\")", true);
            Menu.Chips(1, ("Re-read Steam friends now", (Action)(() =>
            {
                SteamFriendsList.RefreshIfDue(force: true);
                Menu.SetStatus(SteamFriendsList.Available ? $"Read {SteamFriendsList.Total} Steam friends" : "Steam friends aren't available");
            }), true));
            Menu.Small("Friends are found by name: in the player list a modded host shares (PLUS > Servers > Who's in a server), or in the " +
                       "room name of the host. A stranger with the exact name of a friend matches too. Your friend list stays on this PC.");
        });

        Menu.Card("List refresh and memory", "Keeps the open list current; remembers your sort", () =>
        {
            bool open = BrowserPlus.BrowserAlive;
            string refresh;
            if (!open) refresh = "Server browser closed.";
            else if (!BrowserRefresh.AutoRefresh.Value) refresh = "Auto refresh off.";
            else
            {
                float next = BrowserRefresh.SecondsToNext;
                refresh = next >= 0f ? $"Next refresh in {Mathf.CeilToInt(next)} s." : "Auto refresh on.";
            }
            if (BrowserRefresh.LastRefreshAt >= 0f)
                refresh += $" Last refresh {Mathf.Max(0, (int)(Time.unscaledTime - BrowserRefresh.LastRefreshAt))} s ago.";
            Menu.Banner(refresh, open ? Theme.Good : Theme.Info);

            Menu.Switch(BrowserRefresh.AutoRefresh, "Auto refresh the list", "Re-applies filters, sort, friends and ages while the browser is open", true);
            Menu.SliderRow(BrowserRefresh.AutoRefreshSeconds, "Every", 5f, 120f, "0", " s", BrowserRefresh.AutoRefresh.Value);
            Menu.Chips(1, ("Refresh now", (Action)(() => BrowserRefresh.Refresh(manual: true)), open));
            Menu.Small("Photon sends list changes on its own; a refresh never reconnects. It waits while you scroll, click or type.");

            Menu.Switch(BrowserRefresh.RememberSort, "Remember the sort", "Name, area or players, up or down: as you left it", true);
            Menu.KeyValue("Remembered sort", BrowserRefresh.SortText(BrowserRefresh.SavedSortCriteria.Value, BrowserRefresh.SavedSortDirection.Value));
            Menu.Switch(BrowserRefresh.RememberSearch, "Remember the search text", "Off: the search box starts empty", true);
            Menu.Small("The game keeps its own filters (regions, areas, hide full, hide private), and the PLUS filters above are saved too.");
        });
    }

    private static void DrawRows()
    {
        var rows = Rows();
        if (rows.Count == 0) return;
        bool open = BrowserPlus.BrowserAlive;
        foreach (var (info, match) in rows.Take(MaxRows))
        {
            var s = info.FusionSessionInfo;
            if (s == null) continue;
            var row = Menu.NextRow(24f);
            string names = string.Join(", ", match.Names.Take(3)) + (match.Names.Count > 3 ? $" +{match.Names.Count - 3}" : "");
            if (match.Host) names += " (host)";
            Theme.Fill(new Rect(row.x, row.y + 4f, 4f, 16f), BrowserFriends.RowColor, 2);
            Menu.Label(new Rect(row.x + 12f, row.y, 190f, 24f), BrowserFriends.Short(names, 28), Theme.Label);
            string server = BrowserFriends.Short(Favourites.PlainName(Favourites.DisplayNameOf(info)), 26) + $"  {s.PlayerCount}/{s.MaxPlayers}";
            Menu.Label(new Rect(row.x + 206f, row.y, row.width - 206f - 70f, 24f), server, Theme.Small);
            bool joinable = open && BrowserPlus.HasFreeSlot(s);
            if (Menu.ChipButton(new Rect(row.xMax - 64f, row.y, 64f, 24f), "Join", joinable))
                BrowserPlus.Join(info);
        }
        if (rows.Count > MaxRows) Menu.Small($"+{rows.Count - MaxRows} more servers with friends.");
        if (!open) Menu.Small("Open the server browser to join.");
    }

    /// <summary>Servers with friends, rebuilt at most once a second: most friends first, then the busiest.</summary>
    private static List<(OverlandSessionInfo info, BrowserFriends.Match match)> Rows()
    {
        if (Time.unscaledTime - _rowsAt < 1f && Time.unscaledTime >= _rowsAt) return _rows;
        _rowsAt = Time.unscaledTime;
        try
        {
            _rows = BrowserFriends.AllMatches()
                .OrderByDescending(r => r.match.Names.Count)
                .ThenByDescending(r => r.info.FusionSessionInfo?.PlayerCount ?? 0)
                .ToList();
        }
        catch { _rows = new(); }
        return _rows;
    }

    private static string Mark(string label, bool on) => on ? $"[{label}]" : label;
}
