using OverTheHillPlus.Features;
using OverTheHillPlus.Features.Public;
using UnityEngine.InputSystem;
using static OverTheHillPlus.Menu;

namespace OverTheHillPlus.Cockpit;

/// <summary>The Cockpit's advanced cards: keys and the wheel, the look, the seat and the cab.</summary>
internal sealed class CockpitFeature : IFeature
{
    public void Register() => Registry.Section(Registry.PlusMode, "Cockpit", DrawAdvanced);

    private static void DrawAdvanced()
    {
        Card("Getting in", null, () =>
        {
            var key = CockpitConfig.ToggleKey.Value;
            KeyValue("Key", key == Key.None ? "none" : KeybindEditor.KeyName(key));
            Chips(2, ("Change the key…", () => KeybindEditor.StartCapture(CockpitConfig.ToggleKey), true), (CockpitRig.On ? "Get out" : "Get in", CockpitRig.Toggle, true));
            Switch(CockpitConfig.ZoomToEnter, "Mouse wheel in gets you in", "Once more at the closest chase view", true);
            SliderRow(CockpitConfig.ZoomDistance, "Closest view counts within", 2f, 20f, "0", " m", true);
            Switch(CockpitConfig.ZoomToLeave, "Mouse wheel out gets you out", null, true);
            Switch(CockpitConfig.InCycle, "In Camera+'s cycle", "After its drone camera", true);
            KeyValue("Status", CockpitRig.Status);
        });
        Card("View", "Mouse or right stick: look around. Let go and your head comes back.", () =>
        {
            SliderRow(CockpitConfig.Fov, "Field of view", 40f, 120f, "0", "°", true);
            SliderRow(CockpitConfig.NearPlane, "Near plane", 0.01f, 0.3f, "0.00", " m", true);
            SliderRow(CockpitConfig.LookMouse, "Mouse speed", 0.1f, 5f, "0.0", "×", true);
            SliderRow(CockpitConfig.LookStick, "Stick speed", 0.1f, 5f, "0.0", "×", true);
            Switch(CockpitConfig.SpringBack, "Head comes back", null, true);
            SliderRow(CockpitConfig.ReturnDelay, "Wait before it comes back", 0f, 3f, "0.0", " s", true);
            SliderRow(CockpitConfig.ReturnSpeed, "How quickly it comes back", 1f, 20f, "0", "", true);
        });
        Card("Head", null, () =>
        {
            Switch(CockpitConfig.Sway, "Head sway", "Bumps, braking, acceleration, corners", true);
            SliderRow(CockpitConfig.SwayAmount, "Amount", 0.2f, 3f, "0.0", "×", true);
        });
        Card("Seat", "Moves your eyes inside the cab (the cab is fitted to the car's size).", () =>
        {
            Switch(CockpitConfig.LeftHandDrive, "Left-hand drive", null, true);
            SliderRow(CockpitConfig.SeatUp, "Up / down", -0.4f, 0.4f, "0.00", " m", true);
            SliderRow(CockpitConfig.SeatForward, "Forward / back", -0.6f, 0.6f, "0.00", " m", true);
            SliderRow(CockpitConfig.SeatSide, "Left / right", -0.5f, 0.5f, "0.00", " m", true);
        });
        Card("Cab", "Colours and parts change next time you get in.", () =>
        {
            Switch(CockpitConfig.ShowCockpit, "Dashboard, wheel and gauges", null, true);
            Switch(CockpitConfig.HideBody, "Hide the car's parts that block the view", "Steering wheel, seats, trim round your eyes", true);
            Switch(CockpitConfig.Mph, "Speed in mph", null, true);
            SliderRow(CockpitConfig.WheelTurn, "Wheel turn at full steering", 30f, 900f, "0", "°", true);
            SliderRow(CockpitConfig.Brightness, "Brightness", 0.5f, 2f, "0.00", "×", true);
        });
    }
}
