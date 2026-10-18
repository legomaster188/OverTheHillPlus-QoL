using BepInEx.Configuration;
using UnityEngine.InputSystem;

namespace OverTheHillPlus.Bookmarks;

/// <summary>Teleport Bookmarks' settings in BepInEx/config/oth.bookmarks.cfg (the bookmarks are in oth.bookmarks.json).</summary>
internal static class BookmarksConfig
{
    private const string Section = "Bookmarks";

    public static ConfigEntry<float> Clearance;
    public static ConfigEntry<bool> StopCar;
    public static ConfigEntry<Key> SaveKey;
    public static ConfigEntry<Key> BackKey;

    public static ConfigFile File;

    public static void Bind(ConfigFile cfg)
    {
        File = cfg;
        Clearance = cfg.Bind(Section, "DropHeight", 1.0f, new ConfigDescription(
            "How far above the ground the car is put down after a jump (m).", new AcceptableValueRange<float>(0.3f, 5f)));
        StopCar = cfg.Bind(Section, "StopCarOnArrival", true, "Zero the car's speed and spin after a jump.");
        SaveKey = cfg.Bind(Section, "SaveKey", Key.None, "Saves the spot your car is on as a new bookmark (None = no key).");
        BackKey = cfg.Bind(Section, "BackKey", Key.None, "Goes back to where you were before the last jump (None = no key).");
    }
}
