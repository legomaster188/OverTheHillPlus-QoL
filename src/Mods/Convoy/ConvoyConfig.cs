using BepInEx.Configuration;

namespace OverTheHillPlus.Convoy;

/// <summary>
/// Convoy AI's own config file, BepInEx/config/oth.convoy.cfg. The convoy feature binds its [Convoy AI] section on
/// <see cref="File"/> in its Register, with the same keys as in the old OverTheHillPlus config.
/// </summary>
internal static class ConvoyConfig
{
    public static ConfigFile File;

    public static void Bind(ConfigFile cfg) => File = cfg;
}
