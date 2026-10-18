using BepInEx.Configuration;

namespace OverTheHillPlus.ServerUtils;

/// <summary>
/// Server Utils' own settings file, BepInEx/config/oth.serverutils.cfg. Its features bind their entries on
/// <see cref="File"/> in their Register, with the same sections and keys as in the old OverTheHillPlus config
/// ([Host Governor], [Ping Monitor], [Moderation], [Autosave], [JoinSmoothing], [Network Performance], [SettingsShare]), so the
/// settings migration can copy them over.
/// </summary>
internal static class ServerUtilsConfig
{
    public static ConfigFile File;

    public static void Bind(ConfigFile cfg) => File = cfg;
}
