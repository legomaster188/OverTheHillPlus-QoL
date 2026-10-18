using OverTheHillPlus.UI;

namespace OverTheHillPlus.Discord;

/// <summary>Discord Presence's everyday settings, first on its MODS page.</summary>
internal static class DiscordSimple
{
    internal static void Draw()
    {
        if (DiscordConfig.ShowPresence == null) return;
        SimpleMenu.Heading("Discord");
        SimpleMenu.Toggle(DiscordConfig.ShowPresence, "Show my game in Discord", "Needs the Discord desktop app running on this PC.");
        SimpleMenu.Button("Status", DiscordClient.Status, "Press to try connecting to Discord again now.", DiscordClient.RetryNow);
        SimpleMenu.Heading("What to show");
        SimpleMenu.Toggle(DiscordConfig.ShowMap, "Map", "The map you are on.");
        SimpleMenu.Toggle(DiscordConfig.ShowCar, "Car", "The car you are driving.");
        SimpleMenu.Toggle(DiscordConfig.ShowPlayerCount, "Player count", "How many players are in the session, e.g. (3/8).");
        SimpleMenu.Toggle(DiscordConfig.ShowServerName, "Server name",
            "Off by default: everyone who can see your Discord profile would see the server's name.");
        SimpleMenu.Toggle(DiscordConfig.ShowElapsed, "Time played", "How long you have been on this map or in this session.");
        SimpleMenu.Toggle(DiscordConfig.ShowHosting, "Hosting", "Say \"Hosting a server\" when you are the host.");
        SimpleMenu.Toggle(DiscordConfig.ShowRoomCode, "Lobby code",
            "Off by default: anyone who can see your Discord profile could join your session with the code.");
        SimpleMenu.Toggle(DiscordConfig.ShowModCount, "Mod count", "How many mods you run, when someone hovers the logo.");
    }
}
