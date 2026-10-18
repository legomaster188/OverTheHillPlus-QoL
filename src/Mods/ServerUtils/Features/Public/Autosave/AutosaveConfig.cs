using BepInEx.Configuration;
using UnityEngine.InputSystem;

namespace OverTheHillPlus.Features.Public.Autosave;

/// <summary>Settings for the autosave card (QoL tab).</summary>
internal static class AutosaveConfig
{
    private const string Section = "Autosave";

    public static ConfigEntry<bool> Enabled;
    public static ConfigEntry<float> IntervalMinutes;
    public static ConfigEntry<bool> SaveBeforeQuit;
    public static ConfigEntry<bool> ShowToast;
    public static ConfigEntry<bool> ToastGameSaves;
    public static ConfigEntry<bool> GuestSaves;
    public static ConfigEntry<Key> SaveKey;

    public static void Bind(ConfigFile cfg)
    {
        Enabled = cfg.Bind(Section, "Enabled", true,
            "Ask the game to save every few minutes while you play solo or host (the game's own save, same as when you reach a point of interest).");
        IntervalMinutes = cfg.Bind(Section, "IntervalMinutes", 10f, new ConfigDescription(
            "Minutes between autosaves. Any save by the game itself restarts the countdown.", new AcceptableValueRange<float>(2f, 60f)));
        SaveBeforeQuit = cfg.Bind(Section, "SaveBeforeQuit", true,
            "When you quit to the menu or the desktop, make sure a save happens even if the game would skip its own exit save.");
        ShowToast = cfg.Bind(Section, "ShowToast", true, "Show a small notice when the mod's autosave or 'Save now' has saved.");
        ToastGameSaves = cfg.Bind(Section, "ToastGameSaves", false, "Also show the notice when the game saves on its own (points of interest, shop, ...).");
        GuestSaves = cfg.Bind(Section, "GuestSaves", false,
            "Also autosave (and allow 'Save now') while you are a guest in someone else's session. This saves your own profile only; the host's game saves the world. Off by default: the game normally saves a guest's profile when they leave.");
        SaveKey = cfg.Bind(Section, "SaveKey", Key.None, "Hotkey for 'Save now'. Key.None disables the hotkey.");
    }
}
