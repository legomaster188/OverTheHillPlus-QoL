using System;
using UnityEngine;
using UnityEngine.InputSystem;

namespace OverTheHillPlus.Minimap;

/// <summary>The minimap's per-frame work plugged into core: reading positions, its keys, drawing.</summary>
internal static class MinimapFrame
{
    private static float _drawRetryAt;
    private static bool _loggedDraw, _loggedTick;

    internal static void Install()
    {
        CoreBehaviour.BeforeFeatureTicks += Tick;
        CoreBehaviour.Hotkeys += Hotkeys;
        CoreBehaviour.AfterGuis += Draw;
    }

    private static void Tick()
    {
        if (!MinimapConfig.Show.Value) return;
        try { MinimapSource.Tick(); }
        catch (Exception e)
        {
            DebugLog.Error(MinimapPlugin.ModId, e, "tick"); // every distinct error, with repeat counts
            if (_loggedTick) return;
            _loggedTick = true;
            MinimapPlugin.Logger.LogError("Minimap tick (further errors not logged): " + e);
        }
    }

    private static void Hotkeys(Keyboard kb)
    {
        if (CoreBehaviour.Pressed(kb, MinimapConfig.ToggleKey.Value)) MinimapConfig.Show.Value = !MinimapConfig.Show.Value;
        if (CoreBehaviour.Pressed(kb, MinimapConfig.ZoomInKey.Value)) Zoom(0.8f);
        if (CoreBehaviour.Pressed(kb, MinimapConfig.ZoomOutKey.Value)) Zoom(1.25f);
    }

    internal static void Zoom(float factor) =>
        MinimapConfig.Meters.Value = Mathf.Round(MinimapMath.ClampMeters(MinimapConfig.Meters.Value * factor) / 10f) * 10f;

    private static void Draw()
    {
        if (!MinimapConfig.Show.Value || Time.unscaledTime < _drawRetryAt) return;
        try { MinimapDraw.Draw(); }
        catch (Exception e)
        {
            DebugLog.Error(MinimapPlugin.ModId, e, "draw");
            _drawRetryAt = Time.unscaledTime + 5f;
            if (!_loggedDraw)
            {
                _loggedDraw = true;
                MinimapPlugin.Logger.LogError("Minimap draw (retrying every 5 s, further errors not logged): " + e);
            }
        }
        finally
        {
            GUI.color = Color.white;
            HudLayout.End();
        }
    }
}
