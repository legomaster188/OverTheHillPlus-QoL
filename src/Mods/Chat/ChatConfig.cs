using BepInEx.Configuration;

namespace OverTheHillPlus.Chat;

/// <summary>
/// Chat's own settings file, BepInEx/config/oth.chat.cfg. The chat feature binds its entries on <see cref="File"/>
/// in its Register, with the same section and keys as in the old OverTheHillPlus config ([Chat]), so the settings
/// migration can copy them over.
/// </summary>
internal static class ChatConfig
{
    public static ConfigFile File;

    public static void Bind(ConfigFile cfg) => File = cfg;
}
