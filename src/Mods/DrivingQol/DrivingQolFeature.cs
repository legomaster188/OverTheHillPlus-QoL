using System;
using OverTheHillPlus.Features;
using UnityEngine;
using static OverTheHillPlus.Menu;

namespace OverTheHillPlus.DrivingQol;

/// <summary>
/// The QoL cards on the mod's page (the old F7 menu's QoL tab drew these itself): frame-rate caps, field of view,
/// compass range, headlights, logo screens, fades and tutorial hints.
/// </summary>
internal sealed class DrivingQolFeature : IFeature
{
    // Before the feature cards of the same tab (Unstuck), as the old menu drew its own cards first.
    private const int CardOrder = int.MinValue / 2;

    public void Register() => Registry.Section(Registry.PlusMode, "QoL", DrawQol, CardOrder);

    private static void DrawQol()
    {
        Card("Performance", null, () =>
        {
            float cap = RawSlider("Frame-rate cap", DrivingQolConfig.FpsCap.Value, 0f, 360f,
                DrivingQolConfig.FpsCap.Value <= 0 ? "game setting" : $"{DrivingQolConfig.FpsCap.Value} fps", true);
            int capInt = cap < 15f ? 0 : Mathf.RoundToInt(cap / 5f) * 5;
            if (capInt != DrivingQolConfig.FpsCap.Value) DrivingQolConfig.FpsCap.Value = capInt;
            Small("Above the game's own options (it tops out lower). Setting a cap turns VSync off.");
            Switch(DrivingQolConfig.RunInBackground, "Keep running when alt-tabbed", "Stops the session freezing for everyone when the host alt-tabs", true);
            float bg = RawSlider("Background frame cap", DrivingQolConfig.BackgroundFpsCap.Value, 0f, 120f,
                DrivingQolConfig.BackgroundFpsCap.Value <= 0 ? "off" : $"{DrivingQolConfig.BackgroundFpsCap.Value} fps", true);
            int bgInt = bg < 10f ? 0 : Mathf.RoundToInt(bg / 5f) * 5;
            if (bgInt != DrivingQolConfig.BackgroundFpsCap.Value) DrivingQolConfig.BackgroundFpsCap.Value = bgInt;
        });
        Card("Camera", null, () =>
        {
            float fov = RawSlider("Field of view", DrivingQolConfig.FovOverride.Value, 0f, 110f,
                DrivingQolConfig.FovOverride.Value <= 0f ? "game setting" : $"{DrivingQolConfig.FovOverride.Value:0}°", true);
            float fovRounded = fov < 30f ? 0f : Mathf.Round(fov);
            if (Math.Abs(fovRounded - DrivingQolConfig.FovOverride.Value) > 0.01f) DrivingQolConfig.FovOverride.Value = fovRounded;
        });
        Card("Navigation", null, () =>
        {
            SliderRow(DrivingQolConfig.CompassRange, "Compass range", 1f, 4f, "0.0", "×", true);
            Switch(DrivingQolConfig.AutoHeadlights, "Auto headlights", "On from 19:00 to 07:00, off in daylight", true);
        });
        Card("Menus & startup", null, () =>
        {
            Switch(DrivingQolConfig.SkipSplash, "Skip logo screens", "Straight to the main menu at startup", true);
            SliderRow(DrivingQolConfig.FadeSpeed, "Screen fade length", 0.1f, 1f, "0.00", "×", true);
            Switch(DrivingQolConfig.HideTutorialHints, "Hide tutorial hints", "No more reminder pop-ups", true);
        });
    }
}
