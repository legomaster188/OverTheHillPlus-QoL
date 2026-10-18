using System;
using OverTheHillPlus.Features;
using OverTheHillPlus.Features.Public;
using UnityEngine;
using UnityEngine.InputSystem;
using static OverTheHillPlus.Menu;

namespace OverTheHillPlus.CameraPlus;

/// <summary>Camera+'s advanced cards: per-camera placement, field of view and the drone's handling.</summary>
internal sealed class CameraFeature : IFeature
{
    public void Register() => Registry.Section(Registry.PlusMode, "Cameras", DrawAdvanced);

    private static void DrawAdvanced()
    {
        Card("Keys", null, () =>
        {
            var key = CameraConfig.CycleKey.Value;
            KeyValue("Cycle key", key == Key.None ? "none" : KeybindEditor.KeyName(key));
            Chips(1, ("Change the cycle key…", () => KeybindEditor.StartCapture(CameraConfig.CycleKey), true));
            Switch(CameraConfig.ShowName, "Show the camera's name", "For a moment after switching", true);
            KeyValue("Status", CameraRig.Status);
        });
        Card("Chase far", null, () =>
        {
            Fov(CameraConfig.ChaseFarFov);
            SliderRow(CameraConfig.ChaseFarDistance, "Distance", 1f, 4f, "0.0", "×", true);
            SliderRow(CameraConfig.ChaseFarLift, "Extra height", -1f, 6f, "0.0", " m", true);
        });
        Card("Hood", null, () =>
        {
            Fov(CameraConfig.HoodFov);
            SliderRow(CameraConfig.HoodUp, "Up / down", -1f, 1.5f, "0.00", " m", true);
            SliderRow(CameraConfig.HoodForward, "Forward / back", -3f, 3f, "0.00", " m", true);
        });
        Card("Bumper", null, () =>
        {
            Fov(CameraConfig.BumperFov);
            SliderRow(CameraConfig.BumperUp, "Up / down", -1f, 1.5f, "0.00", " m", true);
            SliderRow(CameraConfig.BumperForward, "Forward / back", -3f, 3f, "0.00", " m", true);
        });
        Card("Drone", "Mouse or right stick: fly round the car. Mouse wheel: closer / farther.", () =>
        {
            Fov(CameraConfig.DroneFov);
            SliderRow(CameraConfig.DroneDistance, "Starting distance", CameraMath.MinDroneDistance, CameraMath.MaxDroneDistance, "0", " m", true);
            SliderRow(CameraConfig.DronePitch, "Starting height angle", CameraMath.MinPitch, CameraMath.MaxPitch, "0", "°", true);
            SliderRow(CameraConfig.DroneMouse, "Mouse speed", 0.1f, 5f, "0.0", "×", true);
            SliderRow(CameraConfig.DroneStick, "Stick speed", 0.1f, 5f, "0.0", "×", true);
            SliderRow(CameraConfig.DroneSmoothing, "Follow tightness", 0.5f, 30f, "0.0", "", true);
            Switch(CameraConfig.DroneFollowsHeading, "Orbit turns with the car", "Off: the drone keeps its compass direction", true);
        });
    }

    private static void Fov(BepInEx.Configuration.ConfigEntry<float> entry)
    {
        float v = RawSlider("Field of view", entry.Value, 0f, 120f, entry.Value < 10f ? "game's" : $"{entry.Value:0}°", true);
        float rounded = v < 10f ? 0f : Mathf.Round(v);
        if (Math.Abs(rounded - entry.Value) > 0.01f) entry.Value = rounded;
    }
}
