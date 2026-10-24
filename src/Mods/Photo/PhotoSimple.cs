using OverTheHillPlus.Features.Public.Photo;
using OverTheHillPlus.Features.Public.Spectate;
using OverTheHillPlus.UI;
using UnityEngine.InputSystem;

namespace OverTheHillPlus.Photo;

/// <summary>Photo &amp; Spectate's everyday settings, first on its MODS page (the rest is in its Advanced fold).</summary>
internal static class PhotoSimple
{
    internal static void Draw()
    {
        // Not bound when the mod failed to start: nothing to show then.
        if (PhotoConfig.ScreenshotKey != null)
        {
            SimpleMenu.Heading("Photo");
            SimpleMenu.Note($"{KeyName(PhotoConfig.ScreenshotKey.Value)} takes a high-resolution screenshot, " +
                            $"{KeyName(PhotoConfig.CleanViewKey.Value)} hides the HUD.");
            SimpleMenu.Toggle(PhotoConfig.CleanView, "Hide HUD", "The game's HUD and the driving HUD, for a clean view.");
            bool before = PhotoConfig.ExtendPhotoMode.Value;
            SimpleMenu.Toggle(PhotoConfig.ExtendPhotoMode, "Wider photo mode", "The game's photo mode camera goes farther and zooms more.");
            if (before != PhotoConfig.ExtendPhotoMode.Value) PhotoModeLimits.Refresh(); // right away if photo mode is open
        }
        if (SpectateFeature.ToggleKey != null)
        {
            SimpleMenu.Heading("Spectate");
            SimpleMenu.Note($"{KeyName(SpectateFeature.ToggleKey.Value)} watches another player's car, " +
                            $"{KeyName(SpectateFeature.PrevKey.Value)} / {KeyName(SpectateFeature.NextKey.Value)} switch players.");
            SimpleMenu.Toggle(SpectateFeature.Handbrake, "Handbrake while watching", "Your car waits with the handbrake on.");
        }
    }

    private static string KeyName(Key key) => key == Key.None ? "no key" : key.ToString();
}
