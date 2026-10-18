using OverTheHillPlus.Features;
using OverTheHillPlus.Features.Public;
using UnityEngine;
using UnityEngine.InputSystem;
using static OverTheHillPlus.Menu;

namespace OverTheHillPlus.HudPlus;

/// <summary>HUD+'s advanced cards: the dial's range, the RPM read-out, opacity and the show / hide key.</summary>
internal sealed class HudPlusFeature : IFeature
{
    public void Register() => Registry.Section(Registry.PlusMode, "HUD", DrawAdvanced);

    private static void DrawAdvanced()
    {
        Card("Speedometer", null, () =>
        {
            float max = RawSlider("Analog dial top", HudPlusConfig.DialMax.Value, 0f, 400f,
                HudPlusConfig.DialMax.Value <= 0 ? "automatic" : HudPlusConfig.DialMax.Value.ToString(), true);
            int rounded = max < 60f ? 0 : Mathf.RoundToInt(max / 20f) * 20;
            if (rounded != HudPlusConfig.DialMax.Value) HudPlusConfig.DialMax.Value = rounded;
            Switch(HudPlusConfig.ShowRpm, "RPM and gear", "On the analog and digital skins", true);
        });
        Card("Look", null, () =>
        {
            SliderRow(HudPlusConfig.Opacity, "Background opacity", 0.2f, 1f, "0.00", "", true);
            var key = HudPlusConfig.ToggleKey.Value;
            KeyValue("Show / hide hotkey", key == Key.None ? "none" : KeybindEditor.KeyName(key));
            Chips(1, ("Change the hotkey…", () => KeybindEditor.StartCapture(HudPlusConfig.ToggleKey), true));
            Small("Hidden in photo mode, in Photo & Spectate's clean view and while a screenshot is taken.");
        });
    }
}
