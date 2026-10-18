using BepInEx.Configuration;

namespace OverTheHillPlus.ServerCreator;

/// <summary>
/// Server Creator+'s own settings in BepInEx/config/oth.servercreator.cfg. Same sections and keys as in the old
/// OverTheHillPlus config ([Multiplayer] ...), so the settings migration can copy them over. The features of this
/// mod (Create Server rows, name styles, player names in the listing) bind their own entries on <see cref="File"/>
/// in their Register.
/// </summary>
internal static class ServerCreatorConfig
{
    public static ConfigEntry<string> CustomRoomName;
    public static ConfigEntry<bool> ColoursForEveryone;
    public static ConfigEntry<bool> JoinMessageEnabled;
    public static ConfigEntry<string> JoinMessage;
    public static ConfigEntry<string> JoinMessageColor;
    public static ConfigEntry<string> RoomNameStyle;
    public static ConfigEntry<bool> RoomNameBold;

    public static ConfigFile File;

    public static void Bind(ConfigFile cfg)
    {
        File = cfg;
        RoomNameStyle = cfg.Bind("Multiplayer", "RoomNameStyle", "None", "Colour of your server's name in everyone's server browser: None, Gold, Cyan, Pink, Green, Red, Purple, Rainbow, Sunset, Ocean.");
        RoomNameBold = cfg.Bind("Multiplayer", "RoomNameBold", false, "Bold server name.");
        JoinMessageEnabled = cfg.Bind("Multiplayer", "JoinMessageEnabled", false, "When hosting, greet each player who joins with a message everyone sees.");
        JoinMessage = cfg.Bind("Multiplayer", "JoinMessage", "Welcome to {host}'s server, {name}!", "Join message. {name} = the player who joined, {host} = you.");
        JoinMessageColor = cfg.Bind("Multiplayer", "JoinMessageColor", "Gold", "Colour of the pop-up's smiley: Gold, Cyan, Pink, Green, Red, Purple, White.");
        CustomRoomName = cfg.Bind("Multiplayer", "CustomRoomName", "", "Name for servers you host (empty = the game's default \"<name>'s room\").");
        ColoursForEveryone = cfg.Bind("Lobby", "ColoursForEveryone", true,
            "Publish the server name's colours in a form the game's list still shows to every player (it strips normal tags since its 2026-10-16 update). Used only after Server Browser+ confirmed on this PC that the list renders it.");
    }
}
