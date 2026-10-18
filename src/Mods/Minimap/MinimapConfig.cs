using BepInEx.Configuration;
using UnityEngine.InputSystem;

namespace OverTheHillPlus.Minimap;

/// <summary>The minimap's settings in BepInEx/config/oth.minimap.cfg.</summary>
internal static class MinimapConfig
{
    public static ConfigEntry<bool> Show;
    public static ConfigEntry<HudCorner> Corner;
    public static ConfigEntry<float> Size;
    public static ConfigEntry<float> Meters;
    public static ConfigEntry<float> Opacity;
    public static ConfigEntry<bool> ShowNames, ShowPins, ShowRoute;
    public static ConfigEntry<Key> ToggleKey, ZoomInKey, ZoomOutKey;

    public static ConfigFile File;

    public static void Bind(ConfigFile cfg)
    {
        File = cfg;
        Show = cfg.Bind("Minimap", "Show", true, "Show the minimap while driving.");
        Corner = cfg.Bind("Minimap", "Corner", HudCorner.BottomLeft, "Screen corner of the minimap.");
        Size = cfg.Bind("Minimap", "Size", 240f, "Size of the minimap on a 1080p screen (140 to 440).");
        Meters = cfg.Bind("Minimap", "Zoom", 600f, "How many metres the minimap shows across (100 to 4000).");
        Opacity = cfg.Bind("Minimap", "Opacity", 0.9f, "Opacity of the minimap (0.3 to 1).");
        ShowNames = cfg.Bind("Minimap", "Names", true, "Show other players' names next to their marker.");
        ShowPins = cfg.Bind("Minimap", "Pins", true, "Show the players' map pins, and the pins other mods share (Waypoints+: your saved pins and the session's).");
        ShowRoute = cfg.Bind("Minimap", "Route", true, "Show a dotted line from your car to the pin you follow in Waypoints+, else to your own map pin.");
        ToggleKey = cfg.Bind("Keys", "ToggleKey", Key.None, "Shows / hides the minimap (None = no hotkey).");
        ZoomInKey = cfg.Bind("Keys", "ZoomInKey", Key.None, "Zooms the minimap in (None = no hotkey).");
        ZoomOutKey = cfg.Bind("Keys", "ZoomOutKey", Key.None, "Zooms the minimap out (None = no hotkey).");
    }
}
