using System;
using UnityEngine;
using UnityEngine.InputSystem;

namespace OverTheHillPlus.GearboxPlus;

/// <summary>Gearbox+'s per-frame work plugged into core: the settings, the shift inputs, reading the car, drawing.</summary>
internal static class GearboxFrame
{
    private static float _drawRetryAt, _nextAdvantage;
    private static bool _loggedTick, _loggedDraw;

    internal static void Install()
    {
        CoreBehaviour.BeforeFeatureTicks += Tick;
        CoreBehaviour.Hotkeys += Hotkeys;
        CoreBehaviour.AfterGuis += Draw;
    }

    private static void Tick()
    {
        // Caught here: a throw would otherwise skip the other mods' hooks this frame.
        try
        {
            CarGears.Refresh();
            CarDiffs.Tick();
            Shifter.Tick();
            Shifter.Pad();
            GearboxHud.Tick();
            MarkAdvantage();
        }
        catch (Exception e)
        {
            DebugLog.Error(GearboxPlugin.ModId, e, "GearboxFrame.Tick");
            if (_loggedTick) return;
            _loggedTick = true;
            GearboxPlugin.Logger.LogError("Gearbox+ tick failed (further errors not logged): " + e);
        }
    }

    // Different gear ratios are changed physics (AdvantageFlag): with the extra gears under your car, achievements stay locked.
    private static void MarkAdvantage()
    {
        if (Time.unscaledTime < _nextAdvantage || AdvantageFlag.UsedThisSession) return;
        _nextAdvantage = Time.unscaledTime + 1f;
        if (CarGears.IsExtended(Shifter.LocalInput)) AdvantageFlag.MarkUsed();
    }

    private static void Hotkeys(Keyboard kb) => Shifter.Keys(kb);

    private static void Draw()
    {
        if (Time.unscaledTime < _drawRetryAt) return;
        try { GearboxHud.Draw(); }
        catch (Exception e)
        {
            _drawRetryAt = Time.unscaledTime + 5f;
            if (!_loggedDraw)
            {
                _loggedDraw = true;
                GearboxPlugin.Logger.LogError("Gearbox+ draw (retrying every 5 s, further errors not logged): " + e);
            }
        }
        finally { HudLayout.End(); }
    }
}
