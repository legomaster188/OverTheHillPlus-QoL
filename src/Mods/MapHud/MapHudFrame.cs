using System;
using OverTheHillPlus.UI;
using UnityEngine;
using UnityEngine.InputSystem;

namespace OverTheHillPlus.MapHud;

/// <summary>
/// Map &amp; HUD's work outside the features' ticks, plugged into core's CoreBehaviour hooks: the driving HUD's hotkey
/// and drawing it after the features' overlays (the old ModBehaviour did both).
/// </summary>
internal static class MapHudFrame
{
    internal static void Install()
    {
        CoreBehaviour.Hotkeys += Hotkeys;
        CoreBehaviour.AfterGuis += DrawHud;
    }

    private static void Hotkeys(Keyboard kb)
    {
        if (CoreBehaviour.Pressed(kb, MapHudConfig.HudKey.Value)) MapHudConfig.ShowHud.Value = !MapHudConfig.ShowHud.Value;
    }

    private static float _hudRetryAt;
    private static bool _hudErrorLogged;

    private static void DrawHud()
    {
        // Hidden in the photo mode's clean view (core's clean view checks).
        if (!MapHudConfig.ShowHud.Value || CoreBehaviour.CleanView || Time.unscaledTime < _hudRetryAt) return;
        try { Hud.Draw(); }
        catch (Exception e)
        {
            // Usually transient (car despawning mid-draw): pause the HUD briefly instead of switching the saved
            // setting off, and log only the first failure.
            _hudRetryAt = Time.unscaledTime + 5f;
            if (!_hudErrorLogged) { _hudErrorLogged = true; MapHudPlugin.Logger.LogError("Hud.Draw (retrying every 5 s, further errors not logged): " + e); }
        }
    }
}
