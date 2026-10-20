using OverTheHillPlus.Features.Public;
using OverTheHillPlus.UI;
using UnityEngine.InputSystem;

namespace OverTheHillPlus.CameraPlus;

/// <summary>Camera+'s everyday settings, first on its MODS page (the rest is in its Advanced fold).</summary>
internal static class CameraSimple
{
    private static readonly float[] FovSteps = { 0f, 50f, 55f, 60f, 65f, 70f, 72f, 75f, 78f, 80f, 85f, 90f, 95f, 100f, 110f };

    internal static void Draw()
    {
        // Not bound when the mod failed to start: nothing to show then.
        if (CameraConfig.Mode == null) return;
        var key = CameraConfig.CycleKey.Value;
        SimpleMenu.Heading("Camera");
        SimpleMenu.Note((key == Key.None ? "No cycle key set." : $"{KeybindEditor.KeyName(key)} switches to the next camera (with Shift: the one before).") +
                        " Status: " + CameraRig.Status + ".");
        SimpleMenu.Choice("Camera", "Game = the game's own chase camera. Drone: move the mouse or the right stick to fly round your car.",
            (int)CameraConfig.Mode.Value, CameraMath.ModeNames, i => CameraRig.Set((CamMode)i));
        Fov("Field of view", CameraConfig.Mode.Value);

        SimpleMenu.Heading("In the cycle");
        SimpleMenu.Toggle(CameraConfig.ChaseFarInCycle, "Chase far", "The game's chase camera, farther back and a little higher.");
        SimpleMenu.Toggle(CameraConfig.HoodInCycle, "Hood", "On the hood, looking ahead.");
        SimpleMenu.Toggle(CameraConfig.BumperInCycle, "Bumper", "Low, at the front bumper.");
        SimpleMenu.Toggle(CameraConfig.DroneInCycle, "Drone", "A free orbit that follows your car.");
    }

    private static void Fov(string label, CamMode mode)
    {
        var entry = mode switch
        {
            CamMode.ChaseFar => CameraConfig.ChaseFarFov,
            CamMode.Hood => CameraConfig.HoodFov,
            CamMode.Bumper => CameraConfig.BumperFov,
            CamMode.Drone => CameraConfig.DroneFov,
            _ => null,
        };
        if (entry == null)
        {
            SimpleMenu.Note("The game camera keeps the game's field of view (Driving+ can change it).");
            return;
        }
        SimpleMenu.FloatChoice(label, "Field of view of the " + CameraMath.Name(mode).ToLowerInvariant() + " camera. Game = keep the game's.",
            entry.Value, FovSteps, v => v <= 0f ? "Game" : v.ToString("0") + "°", v => entry.Value = v);
    }
}
