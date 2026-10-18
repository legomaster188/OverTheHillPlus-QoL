using System;
using UnityEngine;
using UnityEngine.InputSystem;

namespace OverTheHillPlus.HudPlus;

/// <summary>HUD+'s per-frame work plugged into core: reading the car, the show / hide key, drawing.</summary>
internal static class HudPlusFrame
{
    /// <summary>Hidden with the hotkey (this session only; the settings stay as they are).</summary>
    internal static bool Hidden;

    private static float _drawRetryAt;
    private static bool _loggedDraw;

    internal static void Install()
    {
        CoreBehaviour.BeforeFeatureTicks += Tick;
        CoreBehaviour.Hotkeys += Hotkeys;
        CoreBehaviour.AfterGuis += Draw;
    }

    private static bool Wanted => !Hidden && (HudPlusConfig.ShowSpeedo.Value || HudPlusConfig.ShowStatus.Value);

    private static void Tick()
    {
        if (Wanted) CarReadout.Tick();
    }

    private static void Hotkeys(Keyboard kb)
    {
        if (CoreBehaviour.Pressed(kb, HudPlusConfig.ToggleKey.Value)) Hidden = !Hidden;
    }

    private static void Draw()
    {
        // Core draws nothing while a screenshot is taken (CoreBehaviour.GuiHidden); the draw also checks photo mode
        // and the clean view (CameraView.HudHidden).
        if (!Wanted || Time.unscaledTime < _drawRetryAt) return;
        try { HudPlusDraw.Draw(); }
        catch (Exception e)
        {
            _drawRetryAt = Time.unscaledTime + 5f;
            if (!_loggedDraw)
            {
                _loggedDraw = true;
                HudPlusPlugin.Logger.LogError("HUD+ draw (retrying every 5 s, further errors not logged): " + e);
            }
        }
        finally { HudLayout.End(); }
    }
}
