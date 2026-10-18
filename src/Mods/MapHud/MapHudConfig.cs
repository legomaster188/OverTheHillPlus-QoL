using BepInEx.Configuration;
using UnityEngine.InputSystem;

namespace OverTheHillPlus.MapHud;

/// <summary>
/// Map &amp; HUD's own settings in BepInEx/config/oth.maphud.cfg. Same sections and keys as in the old OverTheHillPlus
/// config (the driving HUD's [General] HudKey / ShowHud), so the settings migration can copy them over. The map,
/// compass and player list features bind their own entries on <see cref="File"/> in their Register.
/// </summary>
internal static class MapHudConfig
{
    public static ConfigEntry<Key> HudKey;
    public static ConfigEntry<bool> ShowHud;

    public static ConfigFile File;

    public static void Bind(ConfigFile cfg)
    {
        File = cfg;
        HudKey = cfg.Bind("General", "HudKey", Key.F8, "Shows/hides the driving HUD.");
        ShowHud = cfg.Bind("General", "ShowHud", false, "Driving HUD: speed, RPM, gear, power, torque, slip.");
        // Other mods read these through core (they place their panels under the HUD, or show its switch).
        DrivingHud.ShowSetting = ShowHud;
        DrivingHud.KeySetting = HudKey;
    }
}
