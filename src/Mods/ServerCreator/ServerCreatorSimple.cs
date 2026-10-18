using System;
using OverTheHillPlus.Multiplayer;
using OverTheHillPlus.UI;

namespace OverTheHillPlus.ServerCreator;

// After the namespace line on purpose: the game has its own global ModManager (mod.io).
using OverTheHillPlus.Core.Manager;

/// <summary>Server Creator+'s everyday settings, first on its MODS page (the rest is in its Advanced fold).</summary>
internal static class ServerCreatorSimple
{
    private const string BigLobbiesId = "biglobbies";

    internal static void Draw()
    {
        SimpleMenu.Heading("Your server");
        string name = SimpleMenu.Text("Server name", ServerCreatorConfig.CustomRoomName.Value, 40, "Used for servers you host. Empty = the game's default name.");
        if (name != ServerCreatorConfig.CustomRoomName.Value) ServerCreatorConfig.CustomRoomName.Value = name;
        if (Features.Public.Filter.NameFilter.IsBlocked(RoomNames.Escape(ServerCreatorConfig.CustomRoomName.Value), out string why))
            SimpleMenu.Note($"<color=#E5534B>Name not used: {why}.</color>");

        var styles = RoomNames.Styles;
        SimpleMenu.Choice("Name colour", "How your server's name shows in everyone's server browser.",
            Array.IndexOf(styles, ServerCreatorConfig.RoomNameStyle.Value), styles, i => ServerCreatorConfig.RoomNameStyle.Value = styles[i]);

        SimpleMenu.Toggle(ServerCreatorConfig.JoinMessageEnabled, "Greet players who join",
            "A pop-up for everyone when a player joins. Edit the text under Advanced > Lobby.");

        // The lobby size is Big Lobbies' setting (the game's own Max players row on the Create Server screen): this row
        // leads to its page, where it can also be switched on.
        bool installed = ModManager.Find(BigLobbiesId) != null;
        bool running = ModManager.IsLoaded(BigLobbiesId);
        SimpleMenu.Button("Lobby size", !installed ? "Not installed" : running ? "Big Lobbies" : "Switched off",
            installed ? "Max players for the servers you create, up to 16: on Big Lobbies' page."
                : "The game allows up to 4 players. The Big Lobbies mod allows up to 16.",
            () => ModsPage.Open(BigLobbiesId), installed);
    }
}
