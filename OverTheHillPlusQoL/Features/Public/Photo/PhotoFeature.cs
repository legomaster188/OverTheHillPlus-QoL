using System;
using System.IO;
using UnityEngine;
using OverTheHillPlus.UI;
using UnityEngine.InputSystem;

namespace OverTheHillPlus.Features.Public.Photo;

/// <summary>
/// PLUS tab "Photo": high-resolution screenshots, clean view (HUD hidden), cinematic chase-camera presets and
/// wider photo mode limits. Everything here only changes your own view and files, so it works in every session.
/// </summary>
internal sealed class PhotoFeature : IFeature
{
    public void Register()
    {
        PhotoConfig.Bind(ModConfig.File);
        // A hidden HUD at the next launch would look like a bug: clean view always starts off.
        PhotoConfig.CleanView.Value = false;
        PhotoModeLimits.Install();

        Registry.OnTick(Tick);
        Registry.OnGui(PhotoCapture.DrawOverlays);
        Registry.Section(Registry.PlusMode, "Photo", DrawScreenshots, 0);
        Registry.Section(Registry.PlusMode, "Photo", DrawCleanView, 10);
        Registry.Section(Registry.PlusMode, "Photo", DrawCinematic, 20);
        Registry.Section(Registry.PlusMode, "Photo", DrawPhotoMode, 30);
    }

    private static bool _loggedKeyError;

    private static void Tick()
    {
        HandleKeys();
        PhotoCapture.Tick();
        CinematicCamera.Tick();
        PhotoModeLimits.Tick();
    }

    private static void HandleKeys()
    {
        var kb = Keyboard.current;
        if (kb == null) return;
        try
        {
            if (Pressed(kb, PhotoConfig.ScreenshotKey.Value)) PhotoCapture.Request();
            if (Pressed(kb, PhotoConfig.CleanViewKey.Value)) PhotoConfig.CleanView.Value = !PhotoConfig.CleanView.Value;
            if (Pressed(kb, PhotoConfig.CinematicKey.Value)) PhotoConfig.Cinematic.Value = !PhotoConfig.Cinematic.Value;
        }
        catch (Exception e)
        {
            if (!_loggedKeyError) { _loggedKeyError = true; Plugin.Logger.LogWarning("Photo hotkeys: " + e.Message); }
        }
    }

    private static bool Pressed(Keyboard kb, Key key) => key != Key.None && kb[key].wasPressedThisFrame;

    private static string KeyName(Key key) => key == Key.None ? "no hotkey" : key.ToString();

    // ---- menu ----

    private static void DrawScreenshots()
    {
        Menu.Card("Screenshots", "Saved as PNG in the game's screenshot folder. The mod's menu and overlays are never in the picture.", () =>
        {
            int n = PhotoCapture.EffectiveFactor;
            string label = PhotoCapture.Busy ? "Taking screenshot…" : $"Take screenshot ({KeyName(PhotoConfig.ScreenshotKey.Value)})";
            if (Menu.ChipButton(Menu.NextRow(32f), label, !PhotoCapture.Busy)) PhotoCapture.Request();

            float size = Menu.RawSlider("Resolution", Mathf.Clamp(PhotoConfig.SuperSize.Value, 1, PhotoCapture.MaxFactor), 1f, PhotoCapture.MaxFactor,
                $"{Mathf.Clamp(PhotoConfig.SuperSize.Value, 1, PhotoCapture.MaxFactor)}x", true, keyStep: 1f);
            int sizeInt = Mathf.Clamp(Mathf.RoundToInt(size), 1, PhotoCapture.MaxFactor);
            if (sizeInt != PhotoConfig.SuperSize.Value) PhotoConfig.SuperSize.Value = sizeInt;
            string res = $"Output {PhotoCapture.ResolutionText(n)}";
            if (n < Mathf.Clamp(PhotoConfig.SuperSize.Value, 1, PhotoCapture.MaxFactor)) res += $" (capped at {n}x to stay within video memory)";
            if (n >= 3) res += ". Big sizes take a moment and lots of memory; the game can hitch for a second.";
            Menu.Small(res);

            Menu.Switch(PhotoConfig.HideGameUiInShots, "Hide the game's UI in shots", "HUD, menus, photo mode panels", true);
            Menu.Switch(PhotoConfig.HideNameTagsInShots, "Also hide name tags", "World-space labels above players' cars", PhotoConfig.HideGameUiInShots.Value);
            Menu.Switch(PhotoConfig.ShowSavedToast, "Show a notice after saving", null, true);
            Menu.Switch(PhotoConfig.ReRenderCamera, "Re-render camera (experimental)",
                "Use if big shots come out at screen size; 3D view only", true);

            Menu.Chips(2,
                ("Open screenshot folder", PhotoCapture.OpenFolder, true),
                ("Copy folder path", () =>
                {
                    GUIUtility.systemCopyBuffer = PhotoCapture.Folder;
                    Menu.SetStatus("Copied " + PhotoCapture.Folder);
                }, true));
            if (!string.IsNullOrEmpty(PhotoCapture.LastFile))
                Menu.KeyValue("Last shot", Path.GetFileName(PhotoCapture.LastFile));
            Menu.Small("Tip: in the game's photo mode, hide its panels first and press the screenshot key for a supersized photo with its effects.");
        });
    }

    private static void DrawCleanView()
    {
        Menu.Card("Clean view", null, () =>
        {
            Menu.Switch(PhotoConfig.CleanView, $"Hide HUD ({KeyName(PhotoConfig.CleanViewKey.Value)})",
                "The game's HUD and the mod's driving HUD, for screenshots and recording", true);
            Menu.Switch(PhotoConfig.ThirdsGrid, "Rule-of-thirds grid", "Composition guide; never captured in screenshots", true);
        });
    }

    private static void DrawCinematic()
    {
        Menu.Card("Cinematic camera", "Your chase camera only; the mouse wheel zoom still works on top.", () =>
        {
            Menu.Switch(PhotoConfig.Cinematic, $"Cinematic camera ({KeyName(PhotoConfig.CinematicKey.Value)})",
                $"Preset: {PhotoConfig.CinematicPreset.Value}  ·  {CinematicCamera.Status}", true);

            var presets = CinematicCamera.Presets;
            var chips = new (string, Action, bool)[presets.Length];
            for (int i = 0; i < presets.Length; i++)
            {
                var p = presets[i];
                chips[i] = (p.Name, () => { CinematicCamera.ApplyPreset(p); Menu.SetStatus("Camera preset: " + p.Name); }, true);
            }
            Menu.Chips(3, chips);

            bool on = PhotoConfig.Cinematic.Value;
            float fov = Menu.RawSlider("Field of view", PhotoConfig.CamFov.Value, 0f, 120f,
                PhotoConfig.CamFov.Value < 5f ? "game" : $"{PhotoConfig.CamFov.Value:0}°", on);
            float fovR = fov < 5f ? 0f : Mathf.Round(fov);
            if (Math.Abs(fovR - PhotoConfig.CamFov.Value) > 0.01f) { PhotoConfig.CamFov.Value = fovR; Custom(); }

            Slider(PhotoConfig.CamDistance, "Distance", 0.4f, 4f, "0.00", "×", on);
            Slider(PhotoConfig.CamHeight, "Height", 0f, 4f, "0.00", "×", on);
            Slider(PhotoConfig.CamTargetHeight, "Aim height", -1.5f, 3f, "0.00", " m", on);
            Slider(PhotoConfig.CamLookAhead, "Look ahead", -3f, 6f, "0.00", " m", on);
            Slider(PhotoConfig.CamDutch, "Dutch angle", -30f, 30f, "0", "°", on);
            Slider(PhotoConfig.CamZoomOutRange, "Zoom-out range", 1f, 4f, "0.0", "×", on);
            Menu.Chips(2,
                ("Reset camera values", () => { CinematicCamera.ResetValues(); Menu.SetStatus("Camera values reset"); }, true),
                (on ? "Turn off (game camera)" : "Turn on", () => PhotoConfig.Cinematic.Value = !on, true));
            Menu.Small("The QoL tab's field of view still applies when this FOV is set to 'game'.");
        });
    }

    private static void Slider(BepInEx.Configuration.ConfigEntry<float> entry, string label, float min, float max, string fmt, string suffix, bool enabled)
    {
        float before = entry.Value;
        Menu.SliderRow(entry, label, min, max, fmt, suffix, enabled);
        if (Math.Abs(entry.Value - before) > 0.0001f) Custom();
    }

    private static void Custom() => PhotoConfig.CinematicPreset.Value = "Custom";

    private static void DrawPhotoMode()
    {
        Menu.Card("Photo mode limits", "For the game's own photo mode. Changes apply the next time photo mode opens (or right away if it is open).", () =>
        {
            if (!PhotoModeLimits.PatchesOk)
                Menu.Banner("Some photo mode hooks could not be installed (see the log); slider ranges or live updates may not apply.", Theme.Warn);

            bool before = PhotoConfig.ExtendPhotoMode.Value;
            Menu.Switch(PhotoConfig.ExtendPhotoMode, "Extended camera limits", "Roam farther, wider zoom range, free pitch", true);
            bool on = PhotoConfig.ExtendPhotoMode.Value;
            bool changed = before != on;

            float d = PhotoConfig.PhotoDistance.Value;
            float dv = Menu.RawSlider("Roam distance", d, 1f, PhotoModeLimits.NoLimit,
                d >= PhotoModeLimits.NoLimit - 0.01f ? "no limit" : $"{d:0.#}×", on);
            float dr = dv >= PhotoModeLimits.NoLimit - 0.5f ? PhotoModeLimits.NoLimit : Mathf.Round(dv * 2f) / 2f;
            if (Math.Abs(dr - d) > 0.001f) { PhotoConfig.PhotoDistance.Value = dr; changed = true; }
            if (on && !PhotoModeLimits.SinglePlayer && dr > PhotoModeLimits.MultiplayerMaxRoam)
                Menu.Small($"In multiplayer the camera roams at most {PhotoModeLimits.MultiplayerMaxRoam:0}× the game's limit (fair play); more works in single-player.");

            changed |= SliderChanged(PhotoConfig.PhotoFovMin, "Narrowest FOV", 1f, 30f, "0", "°", on);
            changed |= SliderChanged(PhotoConfig.PhotoFovMax, "Widest FOV", 60f, 160f, "0", "°", on);
            bool pitchBefore = PhotoConfig.PhotoFreePitch.Value;
            Menu.Switch(PhotoConfig.PhotoFreePitch, "Free pitch", "Look straight up and straight down", on);
            changed |= pitchBefore != PhotoConfig.PhotoFreePitch.Value;

            Menu.Switch(PhotoConfig.ExtendEffectRanges, "Wider effect sliders", "Exposure, contrast, saturation, white balance, vignette, bloom, lens dirt, grain, focus, aperture", true);
            Menu.SliderRow(PhotoConfig.EffectRangeFactor, "Effect range", 1f, 4f, "0.0", "×", PhotoConfig.ExtendEffectRanges.Value);
            Menu.Small("Effect ranges update when the photo mode effects panel opens. The polaroid camera for animal photos is never changed.");

            if (changed) PhotoModeLimits.Refresh();
            Menu.KeyValue("Photo mode", PhotoModeLimits.PhotoModeOpen ? "open" : "closed");
        });
    }

    private static bool SliderChanged(BepInEx.Configuration.ConfigEntry<float> entry, string label, float min, float max, string fmt, string suffix, bool enabled)
    {
        float before = entry.Value;
        Menu.SliderRow(entry, label, min, max, fmt, suffix, enabled);
        return Math.Abs(entry.Value - before) > 0.0001f;
    }
}
