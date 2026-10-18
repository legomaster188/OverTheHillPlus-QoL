using OverTheHillPlus.UI;
using UnityEngine.InputSystem;

namespace OverTheHillPlus.MapHud;

/// <summary>Map &amp; HUD's everyday settings, first on its MODS page (the rest is in its Advanced fold).</summary>
internal static class MapHudSimple
{
    internal static void Draw()
    {
        // Not bound when the mod failed to start: nothing to show then.
        if (MapHudConfig.ShowHud == null) return;
        SimpleMenu.Heading("HUD");
        SimpleMenu.Toggle(MapHudConfig.ShowHud, "Driving HUD", $"Speed, RPM and gear on screen. {KeyName(MapHudConfig.HudKey.Value)} shows or hides it.");
    }

    private static string KeyName(Key key) => key == Key.None ? "no key" : key.ToString();
}
