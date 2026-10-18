using BepInEx.Configuration;
using UnityEngine.InputSystem;

namespace OverTheHillPlus.Features.Public.Photo;

/// <summary>Settings for the Photo tab (screenshots, clean view, cinematic camera, photo mode limits).</summary>
internal static class PhotoConfig
{
    private const string Section = "Photo";

    // Screenshots
    public static ConfigEntry<Key> ScreenshotKey;
    public static ConfigEntry<int> SuperSize;
    public static ConfigEntry<bool> HideGameUiInShots;
    public static ConfigEntry<bool> HideNameTagsInShots;
    public static ConfigEntry<bool> ShowSavedToast;
    public static ConfigEntry<bool> ReRenderCamera;

    // Clean view
    public static ConfigEntry<Key> CleanViewKey;
    public static ConfigEntry<bool> CleanView;
    public static ConfigEntry<bool> ThirdsGrid;

    // Cinematic camera
    public static ConfigEntry<Key> CinematicKey;
    public static ConfigEntry<bool> Cinematic;
    public static ConfigEntry<string> CinematicPreset;
    public static ConfigEntry<float> CamFov;
    public static ConfigEntry<float> CamDistance;
    public static ConfigEntry<float> CamHeight;
    public static ConfigEntry<float> CamTargetHeight;
    public static ConfigEntry<float> CamLookAhead;
    public static ConfigEntry<float> CamDutch;
    public static ConfigEntry<float> CamZoomOutRange;

    // Photo mode limits
    public static ConfigEntry<bool> ExtendPhotoMode;
    public static ConfigEntry<float> PhotoDistance;
    public static ConfigEntry<float> PhotoFovMin;
    public static ConfigEntry<float> PhotoFovMax;
    public static ConfigEntry<bool> PhotoFreePitch;
    public static ConfigEntry<bool> ExtendEffectRanges;
    public static ConfigEntry<float> EffectRangeFactor;

    public static void Bind(ConfigFile cfg)
    {
        ScreenshotKey = cfg.Bind(Section, "ScreenshotKey", Key.F11, "Takes a high-resolution screenshot (UI hidden). Key.None disables the hotkey.");
        SuperSize = cfg.Bind(Section, "SuperSize", 2, new ConfigDescription("Screenshot resolution multiplier (1 = screen resolution, 2 = double width and height ...). Capped to about 8K output.", new AcceptableValueRange<int>(1, 4)));
        HideGameUiInShots = cfg.Bind(Section, "HideGameUiInShots", true, "Hide the game's HUD and menus while a screenshot is taken.");
        HideNameTagsInShots = cfg.Bind(Section, "HideNameTagsInShots", false, "Also hide world-space UI (player name tags) in screenshots.");
        ShowSavedToast = cfg.Bind(Section, "ShowSavedToast", true, "Show a small 'saved' notice after a screenshot.");
        ReRenderCamera = cfg.Bind(Section, "ReRenderCamera", false,
            "Experimental: render the camera into a big texture instead of Unity's supersized screenshot (use if shots come out at screen size).");

        CleanViewKey = cfg.Bind(Section, "CleanViewKey", Key.F10, "Toggles clean view (game HUD and the mod's driving HUD hidden). Key.None disables the hotkey.");
        CleanView = cfg.Bind(Section, "CleanView", false, "Hide the game HUD and the mod's driving HUD.");
        ThirdsGrid = cfg.Bind(Section, "ThirdsGrid", false, "Rule-of-thirds composition grid (never captured in screenshots).");

        CinematicKey = cfg.Bind(Section, "CinematicKey", Key.None, "Toggles the cinematic camera. Key.None disables the hotkey.");
        Cinematic = cfg.Bind(Section, "Cinematic", false, "Apply the cinematic chase-camera settings below.");
        CinematicPreset = cfg.Bind(Section, "CinematicPreset", "Custom", "Last chosen camera preset (informational).");
        CamFov = cfg.Bind(Section, "CamFov", 0f, "Chase camera field of view in degrees (0 = game setting).");
        CamDistance = cfg.Bind(Section, "CamDistance", 1f, "Chase camera distance multiplier.");
        CamHeight = cfg.Bind(Section, "CamHeight", 1f, "Chase camera height multiplier.");
        CamTargetHeight = cfg.Bind(Section, "CamTargetHeight", 0f, "Raises (or lowers) the point the camera follows, in metres.");
        CamLookAhead = cfg.Bind(Section, "CamLookAhead", 0f, "Moves the followed point ahead of the car, in metres.");
        CamDutch = cfg.Bind(Section, "CamDutch", 0f, "Camera roll (dutch angle) in degrees.");
        CamZoomOutRange = cfg.Bind(Section, "CamZoomOutRange", 1f, "Multiplies how far the mouse wheel / zoom can pull the chase camera out.");

        ExtendPhotoMode = cfg.Bind(Section, "ExtendPhotoMode", false, "Widen the game's photo mode camera limits.");
        PhotoDistance = cfg.Bind(Section, "PhotoDistance", 4f, "Photo mode: how far the camera may roam from the car, as a multiple of the game limit (20 = no limit).");
        PhotoFovMin = cfg.Bind(Section, "PhotoFovMin", 5f, "Photo mode: narrowest field of view (telephoto).");
        PhotoFovMax = cfg.Bind(Section, "PhotoFovMax", 120f, "Photo mode: widest field of view.");
        PhotoFreePitch = cfg.Bind(Section, "PhotoFreePitch", true, "Photo mode: look straight up and down.");
        ExtendEffectRanges = cfg.Bind(Section, "ExtendEffectRanges", false, "Photo mode: widen the effect slider ranges (exposure, contrast, bloom, focus ...).");
        EffectRangeFactor = cfg.Bind(Section, "EffectRangeFactor", 2f, "Photo mode: how much wider the effect sliders go.");
    }
}
