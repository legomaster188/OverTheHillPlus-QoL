using BepInEx.Configuration;

namespace OverTheHillPlus.Photo;

/// <summary>
/// Photo &amp; Spectate's own settings file, BepInEx/config/oth.photo.cfg. The photo and spectate features bind their
/// entries on <see cref="File"/> in their Register, with the same sections and keys as in the old OverTheHillPlus
/// config ([Photo], [Spectate]), so the settings migration can copy them over. (Named PhotoModConfig because the photo
/// feature's own entries are in Features.Public.Photo.PhotoConfig.)
/// </summary>
internal static class PhotoModConfig
{
    public static ConfigFile File;

    public static void Bind(ConfigFile cfg) => File = cfg;
}
