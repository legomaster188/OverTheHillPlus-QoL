using System;
using System.Linq;
using OverTheHillPlus.Features;
using OverTheHillPlus.Features.Public.ServerNameStyles;
using OverTheHillPlus.Multiplayer;
using OverTheHillPlus.UI;
using UnityEngine;
using static OverTheHillPlus.Menu;

namespace OverTheHillPlus.ServerCreator;

/// <summary>
/// The server name (text, colour and styles) and join message cards on the mod's page (the old F7 menu's Lobby tab
/// drew these cards itself), and the name filter checks for both texts.
/// </summary>
internal sealed class ServerCreatorFeature : IFeature
{
    private const string LobbyTab = "Lobby";
    // Before the feature cards of the same tab, as the old menu drew its own cards first.
    private const int CardOrder = -100;

    public void Register()
    {
        Registry.Section(Registry.PlusMode, LobbyTab, DrawLobby, CardOrder);
        Features.Public.Filter.NameFilterFeature.AddCheck("Server name",
            () => RoomNames.Escape(ServerCreatorConfig.CustomRoomName.Value), "empty (game default)");
        Features.Public.Filter.NameFilterFeature.AddCheck("Join message",
            () => RoomNames.Escape(ServerCreatorConfig.JoinMessage.Value), "empty");
    }

    private static void DrawLobby()
    {
        Card("Server name", "Used for servers you host from now on. Leave empty for the game's default.", () =>
        {
            string name = SimpleMenu.Text("Name", ServerCreatorConfig.CustomRoomName.Value ?? "", 40, "Empty: the game's default name.");
            if (name != ServerCreatorConfig.CustomRoomName.Value) ServerCreatorConfig.CustomRoomName.Value = name;
            if (!string.IsNullOrEmpty(ServerCreatorConfig.CustomRoomName.Value))
                SimpleMenu.Button("Clear the name", null, "Back to the game's default name.", () => ServerCreatorConfig.CustomRoomName.Value = "");
            if (Features.Public.Filter.NameFilter.IsBlocked(RoomNames.Escape(ServerCreatorConfig.CustomRoomName.Value), out string nameWhy))
                Small($"<color=#E5534B>Not used: {nameWhy}. Servers keep the game's default name until you change it.</color>");
            SimpleMenu.Choice("Text colour", "The colour (or colour run) of the name in everyone's server browser.",
                Array.IndexOf(RoomNames.Styles, ServerCreatorConfig.RoomNameStyle.Value), RoomNames.Styles,
                i => ServerCreatorConfig.RoomNameStyle.Value = RoomNames.Styles[i], true, ServerCreatorConfig.RoomNameStyle.Value);
            ServerNameStyleMenu.Draw();
            Small("Colours and styles show in everyone's server browser, including players without the mod.");
        });
        Card("Join message", "When you host: a pop-up for everyone each time a player joins. {name} = who joined, {host} = you.", () =>
        {
            Switch(ServerCreatorConfig.JoinMessageEnabled, "Greet players who join", null, true);
            string msg = SimpleMenu.Text("Message", ServerCreatorConfig.JoinMessage.Value ?? "", 90, "{name} = who joined, {host} = you.");
            if (msg != ServerCreatorConfig.JoinMessage.Value) ServerCreatorConfig.JoinMessage.Value = msg;
            if (Features.Public.Filter.NameFilter.IsBlocked(RoomNames.Escape(ServerCreatorConfig.JoinMessage.Value), out string msgWhy))
                Small($"<color=#E5534B>Not sent: {msgWhy}.</color>");
            SimpleMenu.Choice("Message colour", null, Array.IndexOf(HostNotice.Colors, ServerCreatorConfig.JoinMessageColor.Value), HostNotice.Colors,
                i => ServerCreatorConfig.JoinMessageColor.Value = HostNotice.Colors[i], true, ServerCreatorConfig.JoinMessageColor.Value);
            KeyValue("Preview", JoinMessage.Preview());
            if (DevDetails.On) Chips(1, ("Send it now (test)", () => SetStatus(JoinMessage.SendTest()), GameState.IsAuthority && GameState.Runner != null));
        });
    }
}
