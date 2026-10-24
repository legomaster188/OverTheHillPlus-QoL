using OverTheHillPlus.Features.Public;
using OverTheHillPlus.UI;
using UnityEngine.InputSystem;

namespace OverTheHillPlus.Cockpit;

/// <summary>The Cockpit's everyday settings, first on its MODS page (the rest is in its Advanced fold).</summary>
internal static class CockpitSimple
{
    private static readonly float[] Fovs = { 60f, 65f, 70f, 75f, 80f, 85f, 90f, 100f };
    private static readonly float[] Amounts = { 0.2f, 0.5f, 1f, 1.5f, 2f, 3f };
    private static readonly float[] Brightnesses = { 0.5f, 0.75f, 1f, 1.25f, 1.5f, 2f };

    internal static void Draw()
    {
        // Not bound when the mod failed to start: nothing to show then.
        if (CockpitConfig.ToggleKey == null) return;
        var key = CockpitConfig.ToggleKey.Value;
        SimpleMenu.Heading("Cockpit");
        SimpleMenu.Note((key == Key.None ? "No key set." : $"{KeybindEditor.KeyName(key)} sits you in the driver's seat and back.") +
                        " Turning the mouse wheel in at the closest chase view does too (the wheel out leaves). Status: " + CockpitRig.Status + ".");
        SimpleMenu.FloatChoice("Field of view", "Vertical field of view in the cab.", CockpitConfig.Fov.Value, Fovs, v => v.ToString("0") + "°", v => CockpitConfig.Fov.Value = v);
        SimpleMenu.Toggle(CockpitConfig.ShowCockpit, "Dashboard, wheel and gauges", "Off: just the car's own body, from the driver's seat.");
        SimpleMenu.Toggle(CockpitConfig.LeftHandDrive, "Left-hand drive", "The driver sits on the left (off: the right).");
        SimpleMenu.Toggle(CockpitConfig.Mph, "Speed in mph", "On the speedometer.");

        SimpleMenu.Heading("Head");
        SimpleMenu.Toggle(CockpitConfig.Sway, "Head sway", "Your head moves with bumps, braking, acceleration and corners.");
        SimpleMenu.FloatChoice("Sway amount", null, CockpitConfig.SwayAmount.Value, Amounts, v => v.ToString("0.0#") + "×", v => CockpitConfig.SwayAmount.Value = v, CockpitConfig.Sway.Value);
        SimpleMenu.Toggle(CockpitConfig.SpringBack, "Look comes back", "Mouse or right stick turns your head; it returns to looking ahead when you let go.");
        SimpleMenu.FloatChoice("Brightness", "How bright the cab's colours are. Takes effect next time you get in.", CockpitConfig.Brightness.Value, Brightnesses,
            v => v.ToString("0.0#") + "×", v => CockpitConfig.Brightness.Value = v);

        SimpleMenu.Heading("With Camera+");
        SimpleMenu.Toggle(CockpitConfig.InCycle, "In Camera+'s cycle", "Its cycle key goes through the Cockpit after the drone camera (when Camera+ is on).");
    }
}
