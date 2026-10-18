using BepInEx.Configuration;

namespace OverTheHillPlus.HostPowers;

/// <summary>
/// Host Powers' own settings in BepInEx/config/oth.hostpowers.cfg. Same sections and keys as before ([Host] from the
/// old OverTheHillPlus config, [NameFilter] AllowSwears from the old core config), so the settings migration can copy
/// them over. The features (events, players, teleport, track builder, server start) bind their own sections on
/// <see cref="File"/> in their Register.
/// </summary>
internal static class HostPowersConfig
{
    public static ConfigEntry<bool> LobbyGodMode;
    public static ConfigEntry<bool> LockWeather;
    public static ConfigEntry<int> LockedWeatherId;
    public static ConfigEntry<bool> AllowSwears;

    public static ConfigFile File;

    public static void Bind(ConfigFile cfg)
    {
        File = cfg;
        LobbyGodMode = cfg.Bind("Host", "LobbyGodMode", false, "Host: no car in the session takes damage.");
        LockWeather = cfg.Bind("Host", "LockWeather", false, "Host: keep the chosen weather from changing.");
        LockedWeatherId = cfg.Bind("Host", "LockedWeatherId", -1, "Host: weather profile id to keep (-1 = none).");
        AllowSwears = cfg.Bind("NameFilter", "AllowSwears", false,
            "Allow mild swear words in your server name and join message. Slurs are always blocked.");
        Features.Public.Filter.NameFilter.AllowSwears = () => AllowSwears.Value;
    }
}
