using OverTheHillPlus.UI;

namespace OverTheHillPlus.HudPlus;

/// <summary>HUD+'s everyday settings, first on its MODS page (the rest is in its Advanced fold).</summary>
internal static class HudPlusSimple
{
    private static readonly string[] Skins = { "Analog", "Digital", "Minimal" };
    private static readonly float[] Scales = { 0.6f, 0.75f, 0.9f, 1f, 1.15f, 1.3f, 1.5f, 1.75f, 2f };

    internal static void Draw()
    {
        // Not bound when the mod failed to start: nothing to show then.
        if (HudPlusConfig.ShowSpeedo == null) return;
        SimpleMenu.Heading("Speedometer");
        SimpleMenu.Toggle(HudPlusConfig.ShowSpeedo, "Show the speedometer", null);
        SimpleMenu.Choice("Skin", "Analog: a dial. Digital: big numbers, gear and an RPM bar. Minimal: just the speed.",
            (int)HudPlusConfig.Skin.Value, Skins, i => HudPlusConfig.Skin.Value = (SpeedoSkin)i, HudPlusConfig.ShowSpeedo.Value);
        SimpleMenu.Choice("Corner", null, (int)HudPlusConfig.SpeedoCorner.Value, HudLayout.CornerNames,
            i => HudPlusConfig.SpeedoCorner.Value = HudLayout.Corner(i), HudPlusConfig.ShowSpeedo.Value);
        SimpleMenu.FloatChoice("Size", "Size of the speedometer and the status card.", HudPlusConfig.SpeedoScale.Value, Scales,
            v => v.ToString("0.##") + "×", v => HudPlusConfig.SpeedoScale.Value = v);
        SimpleMenu.Toggle(HudPlusConfig.Mph, "Miles per hour", "mph on the speedometer and miles on the odometer.");

        SimpleMenu.Heading("Car status");
        SimpleMenu.Toggle(HudPlusConfig.ShowStatus, "Show the status card", "Health, tyres, winch and odometer of your car.");
        bool on = HudPlusConfig.ShowStatus.Value;
        SimpleMenu.Choice("Corner", "Above the speedometer when both are in the same corner.", (int)HudPlusConfig.StatusCorner.Value,
            HudLayout.CornerNames, i => HudPlusConfig.StatusCorner.Value = HudLayout.Corner(i), on);
        SimpleMenu.Toggle(HudPlusConfig.ShowDamage, "Health", "How damaged the car is.", on);
        SimpleMenu.Toggle(HudPlusConfig.ShowTyres, "Tyres", "Green: grip, yellow: slipping, red: sliding, grey: in the air.", on);
        SimpleMenu.Toggle(HudPlusConfig.ShowWinch, "Winch", "Stowed, anchored or hooked, slack or taut.", on);
        SimpleMenu.Toggle(HudPlusConfig.ShowOdometer, "Odometer", "How far this car has driven.", on);
    }
}
