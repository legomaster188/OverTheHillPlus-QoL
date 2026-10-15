using System;
using BepInEx.Configuration;
using OverTheHillPlus.Features.Public.ServerBrowserPlus;
using OverTheHillPlus.UI;
using UnityEngine;

namespace OverTheHillPlus.Features.Public.ServerPlayers;

/// <summary>
/// See who is in a server from the server browser. Modded hosts publish their (filtered, plain-text) player names
/// as a session property; modded clients show them for the selected server and in a hover tooltip. Servers whose
/// host doesn't publish them say so. Vanilla players never see the property.
/// </summary>
internal sealed class ServerPlayersFeature : IFeature
{
    public static ConfigEntry<bool> Publish, PublishPrivate, ShowPanel, ShowTooltip;

    public void Register()
    {
        const string s = "ServerPlayers";
        var cfg = ModConfig.File;
        // Opt-in: it shares your guests' names (who never chose this) with anyone browsing with the mod.
        Publish = cfg.Bind(s, "PublishNames", false,
            "When hosting: list the names of the players in your server (including your guests) in the server browser, for players with this mod. " +
            "Names are plain text, shortened and checked by the name filter. Off by default. Takes effect for servers started after enabling.");
        PublishPrivate = cfg.Bind(s, "PublishInPrivateServers", false,
            "When hosting a private (room code) server: also list the names. Off = the browser shows that names are private.");
        ShowPanel = cfg.Bind(s, "ShowPanel", true, "Server browser: show who is in the selected server under the area picture.");
        ShowTooltip = cfg.Bind(s, "ShowTooltip", true, "Server browser: show who is in a server when the pointer rests on its row.");

        Registry.Section(Registry.PlusMode, "Servers", DrawMenu, -5);
        Registry.OnTick(PlayerNamesPublisher.Tick);
        Registry.OnGui(BrowserPlayersPanel.Draw);
    }

    private static void DrawMenu()
    {
        Menu.Card("Who's in a server", "Player names in the server browser · shared by hosts with this mod", () =>
        {
            Menu.Switch(ShowPanel, "Names of the selected server", "A small list under the area picture in the browser", true);
            Menu.Switch(ShowTooltip, "Names on hover", "Rest the pointer on a server row", true);
            Menu.Switch(Publish, "Share names when hosting", "Off by default. Players with this mod browsing servers see who is in yours, guests included (applies to new servers)", true);
            Menu.Switch(PublishPrivate, "Also in private servers", "Off: your private servers show the names as private", Publish.Value);

            string state;
            Color color;
            if (PlayerNamesPublisher.Active)
            {
                var sent = PlayerNamesProperty.Decode(PlayerNamesPublisher.LastSent);
                if (sent == null) state = "Hosting: names will be shared within a few seconds";
                else if (sent.Hidden) state = "Hosting: names are kept private";
                else state = $"Hosting: sharing {sent.List.Count} name{(sent.List.Count == 1 ? "" : "s")}" +
                             (sent.More > 0 ? $" (+{sent.More} did not fit)" : "") +
                             $" · updated {Mathf.Max(0, (int)(Time.unscaledTime - PlayerNamesPublisher.LastSentAt))} s ago";
                color = Theme.Good;
            }
            else if (PlayerNamesPublisher.KeyMissing)
            {
                state = "Hosting: this server was started without name sharing. Start a new server to share names.";
                color = Theme.Warn;
            }
            else
            {
                state = "Not hosting. In the browser, servers whose host doesn't have this mod show \"names unavailable\".";
                color = Theme.Info;
            }
            Menu.Banner(state, color);

            var sel = BrowserPlus.BrowserAlive ? BrowserPlus.SelectedSession() : null;
            var s = sel?.FusionSessionInfo;
            if (s != null)
            {
                var names = PlayerNamesProperty.For(s, Time.unscaledTime, out bool present);
                string who = !present ? "names unavailable (host not modded)"
                    : names == null ? "names unavailable (unknown format)"
                    : names.Hidden ? "private"
                    : names.List.Count == 0 ? "no names yet"
                    : string.Join(", ", names.List) + (names.More > 0 ? $" +{names.More}" : "");
                Menu.KeyValue("Selected server", who.Length > 120 ? who.Substring(0, 119) + "…" : who);
            }
        });
    }
}
