using BepInEx.Configuration;

namespace OverTheHillPlus.BigLobbies;

/// <summary>
/// Lobby+'s own settings in BepInEx/config/oth.biglobbies.cfg. Same sections and keys as in the old
/// OverTheHillPlus config ([Multiplayer] ...), so the settings migration can copy them over. The features of this
/// mod (SlotKeeper, overflow names, colours, player tables, janitor, lobby garage) bind their own entries on
/// <see cref="File"/> in their Register.
/// </summary>
internal static class BigLobbiesConfig
{
    public static ConfigEntry<bool> ExpandLobbySize;
    public static ConfigEntry<int> DefaultMaxPlayers;
    public static ConfigEntry<bool> LobbyGuards;
    public static ConfigEntry<bool> FallRescue;
    public static ConfigEntry<int> MeasuredCapacity;
    public static ConfigEntry<int> CustomMaxPlayers;

    public static ConfigFile File;

    public static void Bind(ConfigFile cfg)
    {
        File = cfg;
        ExpandLobbySize = cfg.Bind("Multiplayer", "ExpandLobbySize", true, "Offer 2-16 players in the Create Server screen.");
        DefaultMaxPlayers = cfg.Bind("Multiplayer", "DefaultMaxPlayers", 4, new ConfigDescription("Player limit preselected in Create Server.", new AcceptableValueRange<int>(2, 16)));
        FallRescue = cfg.Bind("Multiplayer", "FallRescue", true, "Solo/host: put cars that end up under the ground or off the map back onto it.");
        LobbyGuards = cfg.Bind("Multiplayer", "LobbyGuards", true, "Crash guards and smart spawn points for lobbies above the stock size.");
        MeasuredCapacity = cfg.Bind("Multiplayer", "MeasuredCapacity", 0, "Player slots in the game's networked player tables, measured in-game (0 = not measured yet).");
        CustomMaxPlayers = cfg.Bind("Multiplayer", "CustomMaxPlayers", 0, "Exact player limit for servers you host (0 = use the Create Server choice).");
    }
}
