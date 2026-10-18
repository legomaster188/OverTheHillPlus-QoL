using BepInEx.Configuration;

namespace OverTheHillPlus.Discord;

/// <summary>Discord Presence's settings in BepInEx/config/oth.discord.cfg.</summary>
internal static class DiscordConfig
{
    /// <summary>The "Over The Hill Mods" Discord application (public: an application id is not a secret).</summary>
    public const string DefaultApplicationId = "1560887473893150790";

    public static ConfigEntry<bool> ShowPresence;
    public static ConfigEntry<string> ApplicationId;
    public static ConfigEntry<bool> ShowMap;
    public static ConfigEntry<bool> ShowCar;
    public static ConfigEntry<bool> ShowPlayerCount;
    public static ConfigEntry<bool> ShowServerName;
    public static ConfigEntry<bool> ShowElapsed;
    public static ConfigEntry<bool> ShowRoomCode;
    public static ConfigEntry<bool> ShowHosting;
    public static ConfigEntry<bool> ShowModCount;
    public static ConfigEntry<string> LargeImageKey;
    public static ConfigEntry<string> LargeText;

    public static ConfigFile File;

    public static void Bind(ConfigFile cfg)
    {
        File = cfg;
        ShowPresence = cfg.Bind("Presence", "ShowPresence", true, "Show what you are doing in over the hill on your Discord profile (needs the Discord desktop app running).");
        ApplicationId = cfg.Bind("Presence", "ApplicationId", DefaultApplicationId, "Discord application id (17-20 digits) whose name Discord shows as \"Playing ...\".");
        ShowMap = cfg.Bind("Presence", "ShowMap", true, "Show the map you are on.");
        ShowCar = cfg.Bind("Presence", "ShowCar", true, "Show the car you are driving.");
        ShowPlayerCount = cfg.Bind("Presence", "ShowPlayerCount", true, "Show how many players are in the session, e.g. (3/8).");
        ShowServerName = cfg.Bind("Presence", "ShowServerName", false, "Show the server's name. Off by default: everyone who can see your Discord profile would see it.");
        ShowElapsed = cfg.Bind("Presence", "ShowElapsed", true, "Show how long you have been on this map or in this session.");
        ShowRoomCode = cfg.Bind("Presence", "ShowRoomCode", false, "Show the session's join code. Off by default: anyone who can see your Discord profile could join with it.");
        ShowHosting = cfg.Bind("Presence", "ShowHosting", true, "Say \"Hosting a server\" when you are the host.");
        ShowModCount = cfg.Bind("Presence", "ShowModCount", true, "Show how many mods you run when someone hovers the image.");
        LargeImageKey = cfg.Bind("Presence", "LargeImageKey", "", "Art asset key uploaded to the Discord application, or an https image URL (empty = the mods' logo, \"none\" = text only).");
        LargeText = cfg.Bind("Presence", "LargeText", "over the hill", "Text shown when hovering the large image.");
    }

    public static PresenceOptions Options() => new()
    {
        ShowMap = ShowMap.Value,
        ShowCar = ShowCar.Value,
        ShowPlayerCount = ShowPlayerCount.Value,
        ShowServerName = ShowServerName.Value,
        ShowElapsed = ShowElapsed.Value,
        ShowRoomCode = ShowRoomCode.Value,
        ShowHosting = ShowHosting.Value,
        ShowModCount = ShowModCount.Value,
        LargeImageKey = LargeImageKey.Value ?? "",
        LargeText = LargeText.Value ?? "",
    };
}
