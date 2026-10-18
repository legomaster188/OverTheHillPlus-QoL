using BepInEx.Configuration;

namespace OverTheHillPlus.Graphics;

/// <summary>
/// Graphics+'s own settings file, BepInEx/config/oth.graphics.cfg. Its features bind their entries on <see cref="File"/>
/// in their Register, with the same sections and keys as in the old OverTheHillPlus config ([Graphics.Lighting], [Graphics.Detail], [Graphics.Sky] ...),
/// so the settings migration can copy them over, the graphics profiles still find every "Graphics" section in this
/// file, and Server Utils' host governor still reads them through core by these names.
/// </summary>
internal static class GraphicsConfig
{
    public static ConfigFile File;

    public static void Bind(ConfigFile cfg) => File = cfg;
}
