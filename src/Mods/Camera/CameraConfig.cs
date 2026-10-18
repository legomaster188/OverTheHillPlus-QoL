using BepInEx.Configuration;
using UnityEngine.InputSystem;

namespace OverTheHillPlus.CameraPlus;

/// <summary>Camera+'s settings in BepInEx/config/oth.camera.cfg.</summary>
internal static class CameraConfig
{
    public static ConfigEntry<CamMode> Mode;
    public static ConfigEntry<Key> CycleKey;
    public static ConfigEntry<bool> ShowName;
    public static ConfigEntry<bool> ChaseFarInCycle, HoodInCycle, BumperInCycle, DroneInCycle;

    public static ConfigEntry<float> ChaseFarFov, ChaseFarDistance, ChaseFarLift;
    public static ConfigEntry<float> HoodFov, HoodUp, HoodForward;
    public static ConfigEntry<float> BumperFov, BumperUp, BumperForward;
    public static ConfigEntry<float> DroneFov, DroneDistance, DronePitch, DroneMouse, DroneStick, DroneSmoothing;
    public static ConfigEntry<bool> DroneFollowsHeading;

    public static ConfigFile File;

    public static void Bind(ConfigFile cfg)
    {
        File = cfg;
        Mode = cfg.Bind("General", "Camera", CamMode.Game, "The camera in use: Game (the game's own chase camera), ChaseFar, Hood, Bumper or Drone.");
        CycleKey = cfg.Bind("General", "CycleKey", Key.F2, "Switches to the next camera (None = no hotkey).");
        ShowName = cfg.Bind("General", "ShowName", true, "Shows the camera's name for a moment when it changes.");
        ChaseFarInCycle = cfg.Bind("Cycle", "ChaseFar", true, "The cycle key goes through the far chase camera.");
        HoodInCycle = cfg.Bind("Cycle", "Hood", true, "The cycle key goes through the hood camera.");
        BumperInCycle = cfg.Bind("Cycle", "Bumper", true, "The cycle key goes through the bumper camera.");
        DroneInCycle = cfg.Bind("Cycle", "Drone", true, "The cycle key goes through the drone camera.");

        ChaseFarFov = cfg.Bind("Chase far", "Fov", 0f, "Field of view (0 = keep the game's).");
        ChaseFarDistance = cfg.Bind("Chase far", "Distance", 1.8f, "How much farther than the game's chase camera (1 to 4 times).");
        ChaseFarLift = cfg.Bind("Chase far", "ExtraHeight", 0.6f, "Extra height in metres.");

        HoodFov = cfg.Bind("Hood", "Fov", 72f, "Field of view (0 = keep the game's).");
        HoodUp = cfg.Bind("Hood", "Up", 0f, "Moves the camera up (metres; negative = down).");
        HoodForward = cfg.Bind("Hood", "Forward", 0f, "Moves the camera forward (metres; negative = back).");

        BumperFov = cfg.Bind("Bumper", "Fov", 78f, "Field of view (0 = keep the game's).");
        BumperUp = cfg.Bind("Bumper", "Up", 0f, "Moves the camera up (metres; negative = down).");
        BumperForward = cfg.Bind("Bumper", "Forward", 0f, "Moves the camera forward (metres; negative = back).");

        DroneFov = cfg.Bind("Drone", "Fov", 60f, "Field of view (0 = keep the game's).");
        DroneDistance = cfg.Bind("Drone", "Distance", 12f, "Starting distance from the car in metres (the mouse wheel changes it).");
        DronePitch = cfg.Bind("Drone", "Pitch", 25f, "Starting angle above the horizon in degrees.");
        DroneMouse = cfg.Bind("Drone", "MouseSpeed", 1f, "Mouse orbit speed.");
        DroneStick = cfg.Bind("Drone", "StickSpeed", 1f, "Gamepad right-stick orbit speed.");
        DroneSmoothing = cfg.Bind("Drone", "Smoothing", 6f, "How quickly the drone catches up with the car (higher = tighter).");
        DroneFollowsHeading = cfg.Bind("Drone", "FollowsHeading", false, "The orbit turns with the car (off: it stays fixed to the compass).");
    }

    public static bool InCycle(CamMode m) => m switch
    {
        CamMode.ChaseFar => ChaseFarInCycle.Value,
        CamMode.Hood => HoodInCycle.Value,
        CamMode.Bumper => BumperInCycle.Value,
        CamMode.Drone => DroneInCycle.Value,
        _ => true,
    };

    public static float FovFor(CamMode m) => m switch
    {
        CamMode.ChaseFar => ChaseFarFov.Value,
        CamMode.Hood => HoodFov.Value,
        CamMode.Bumper => BumperFov.Value,
        CamMode.Drone => DroneFov.Value,
        _ => 0f,
    };
}
