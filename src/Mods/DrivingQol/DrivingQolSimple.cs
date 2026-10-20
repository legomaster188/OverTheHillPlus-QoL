using OverTheHillPlus.Features.Public.GarageLift;
using OverTheHillPlus.Features.Public.Unstuck;
using OverTheHillPlus.UI;
using UnityEngine.InputSystem;
using UnstuckCore = OverTheHillPlus.Features.Public.Unstuck.Unstuck;

namespace OverTheHillPlus.DrivingQol;

/// <summary>Driving+'s everyday settings, first on its MODS page (the rest is in its Advanced fold).</summary>
internal static class DrivingQolSimple
{
    private static readonly int[] FpsSteps = { 0, 30, 60, 90, 120, 144, 165, 240, 360 };
    private static readonly float[] FovSteps = { 0f, 50f, 55f, 60f, 65f, 70f, 75f, 80f, 85f, 90f, 95f, 100f, 105f, 110f };

    internal static void Draw()
    {
        // Not bound when the mod failed to start: nothing to show then.
        if (DrivingQolConfig.FpsCap == null) return;
        SimpleMenu.Heading("Play");
        SimpleMenu.IntChoice("Frame-rate cap", "Goes higher than the game's own options. Setting a cap turns v-sync off.",
            DrivingQolConfig.FpsCap.Value, FpsSteps, v => v <= 0 ? "Game" : v + " fps", v => DrivingQolConfig.FpsCap.Value = v);
        SimpleMenu.FloatChoice("Field of view", "Driving camera field of view. Game = the game's own setting.",
            DrivingQolConfig.FovOverride.Value, FovSteps, v => v <= 0f ? "Game" : v.ToString("0"), v => DrivingQolConfig.FovOverride.Value = v);
        SimpleMenu.Toggle(DrivingQolConfig.AutoHeadlights, "Auto headlights", "Headlights on from 19:00 to 07:00, off in daylight.");
        SimpleMenu.Toggle(DrivingQolConfig.RunInBackground, "Keep running when alt-tabbed", "An alt-tabbed host otherwise freezes the session for everyone.");
        SimpleMenu.Toggle(DrivingQolConfig.SkipSplash, "Skip logo screens", "Straight to the main menu when the game starts.");
        SimpleMenu.Toggle(DrivingQolConfig.HideTutorialHints, "Hide tutorial hints", "No more reminder pop-ups.");
        if (GarageLiftFeature.Preview != null)
        {
            SimpleMenu.Heading("Garage");
            SimpleMenu.Toggle(GarageLiftFeature.Preview, "Lift preview", "The garage car rises or drops to the suspension stage you highlight, and goes back to the fitted one. Only the garage model moves.");
            SimpleMenu.Toggle(GarageLiftFeature.ShowText, "Ride height text", "A line such as \"Ride height: +8 cm\" under the stage's description.");
        }

        if (UnstuckFeature.UnstuckKey == null) return;
        float left = UnstuckCore.CooldownLeft;
        bool waiting = UnstuckCore.Waiting;
        var unstuckKey = UnstuckFeature.UnstuckKey.Value;
        string key = unstuckKey == Key.None ? null : unstuckKey.ToString();
        SimpleMenu.Button("Unstuck my car", waiting ? "Asking the host" : left > 0f ? $"{UnityEngine.Mathf.CeilToInt(left)} s" : "Ready",
            "Puts your car back on its wheels (as a guest, a host with this mod does it)." + (key != null ? $" Hotkey: {key}." : ""),
            () => Menu.SetStatus(UnstuckCore.Use()), left <= 0f && !waiting);
    }
}
