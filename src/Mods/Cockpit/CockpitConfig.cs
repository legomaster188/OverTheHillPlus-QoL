using BepInEx.Configuration;
using UnityEngine.InputSystem;

namespace OverTheHillPlus.Cockpit;

/// <summary>The Cockpit's settings in BepInEx/config/oth.cockpit.cfg.</summary>
internal static class CockpitConfig
{
    public static ConfigEntry<Key> ToggleKey;
    public static ConfigEntry<bool> ZoomToEnter, ZoomToLeave, InCycle;
    public static ConfigEntry<float> ZoomDistance;
    public static ConfigEntry<bool> LeftHandDrive;

    public static ConfigEntry<float> Fov, NearPlane;
    public static ConfigEntry<float> LookMouse, LookStick, ReturnDelay, ReturnSpeed;
    public static ConfigEntry<bool> SpringBack;
    public static ConfigEntry<bool> Sway;
    public static ConfigEntry<float> SwayAmount;
    public static ConfigEntry<float> SeatUp, SeatForward, SeatSide;

    public static ConfigEntry<bool> ShowCockpit, HideBody, Mph;
    public static ConfigEntry<float> WheelTurn, Brightness;

    public static ConfigFile File;

    public static void Bind(ConfigFile cfg)
    {
        File = cfg;
        ToggleKey = cfg.Bind("General", "ToggleKey", Key.F3, "Sits you in the driver's seat and back (None = no hotkey).");
        ZoomToEnter = cfg.Bind("General", "ZoomToEnter", true, "Turning the mouse wheel in once more at the game's closest chase view takes you into the cab.");
        ZoomToLeave = cfg.Bind("General", "ZoomToLeave", true, "Turning the mouse wheel out in the cab takes you back to the game's camera.");
        ZoomDistance = cfg.Bind("General", "ZoomDistance", 8f, "How close the chase camera must be (metres) for the wheel to take you into the cab.");
        InCycle = cfg.Bind("General", "InCameraPlusCycle", true, "Camera+'s cycle key goes through the Cockpit view too (after the drone), when Camera+ is on.");
        LeftHandDrive = cfg.Bind("General", "LeftHandDrive", true, "The driver sits on the left (off: the right).");

        Fov = cfg.Bind("View", "Fov", 75f, "Field of view in the cab (vertical, degrees).");
        NearPlane = cfg.Bind("View", "NearPlane", 0.03f, "How near the camera starts to draw (metres); small enough that the dashboard is never cut off.");
        LookMouse = cfg.Bind("View", "LookMouse", 1f, "Mouse look speed.");
        LookStick = cfg.Bind("View", "LookStick", 1f, "Gamepad right-stick look speed.");
        SpringBack = cfg.Bind("View", "SpringBack", true, "Your head comes back to looking ahead when you let go.");
        ReturnDelay = cfg.Bind("View", "ReturnDelay", 0.5f, "Seconds after the last movement before the head starts coming back.");
        ReturnSpeed = cfg.Bind("View", "ReturnSpeed", 7f, "How quickly it comes back (higher = quicker).");

        Sway = cfg.Bind("Head", "Sway", true, "Your head moves with bumps, braking, acceleration and corners.");
        SwayAmount = cfg.Bind("Head", "SwayAmount", 1f, "How much (0.2 to 3).");
        SeatUp = cfg.Bind("Seat", "Up", 0f, "Moves your eyes up (metres; negative = down).");
        SeatForward = cfg.Bind("Seat", "Forward", 0f, "Moves your eyes forward (metres; negative = back).");
        SeatSide = cfg.Bind("Seat", "Side", 0f, "Moves your eyes to the right (metres; negative = left).");

        ShowCockpit = cfg.Bind("Cockpit", "ShowCockpit", true, "Draws the dashboard, wheel, gauges and pillars (off: just the car's own body).");
        HideBody = cfg.Bind("Cockpit", "HideBlockingParts", true, "Hides the car's own parts that surround your eyes (the steering wheel, seats and trim) in the cab.");
        Mph = cfg.Bind("Cockpit", "Mph", false, "The speedometer shows mph.");
        WheelTurn = cfg.Bind("Cockpit", "WheelTurn", 360f, "How far the wheel turns at full steering (degrees each way).");
        Brightness = cfg.Bind("Cockpit", "Brightness", 1f, "How bright the cab's colours are (0.5 to 2).");
    }
}
