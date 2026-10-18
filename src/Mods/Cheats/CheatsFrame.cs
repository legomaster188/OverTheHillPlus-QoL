using System;
using OverTheHillPlus.Diagnostics;
using UnityEngine;
using UnityEngine.InputSystem;

namespace OverTheHillPlus.CheatsPlus;

/// <summary>
/// Cheats+'s per-frame work, plugged into core's CoreBehaviour hooks: the long-winch range (once a second, before the
/// features' ticks), the flip / fly / nitro / jump hotkeys, the server browser profiler's frame timing, and the
/// driving HUD's last line (wheel slip and surface, from the assists).
/// </summary>
internal static class CheatsFrame
{
    private static float _nextSlowTick;
    private static bool _loggedTick, _loggedKeys;

    internal static void Install()
    {
        CoreBehaviour.BeforeFeatureTicks += BeforeFeatureTicks;
        CoreBehaviour.Hotkeys += Hotkeys;
        DrivingHud.ExtraLine = SlipLine;
    }

    private static void BeforeFeatureTicks()
    {
        // Caught here: a throw would otherwise skip the other mods' hooks this frame.
        try
        {
            BrowserProfiler.Tick();
            // Once a second, as when it ran in the old QoL tick.
            if (Time.unscaledTime < _nextSlowTick) return;
            _nextSlowTick = Time.unscaledTime + 1f;
            Vehicles.TickWinch();
        }
        catch (Exception e)
        {
            if (_loggedTick) return;
            _loggedTick = true;
            CheatsPlugin.Logger.LogError("Cheats+ tick failed (further errors suppressed): " + e);
        }
    }

    private static void Hotkeys(Keyboard kb)
    {
        try
        {
            if (CoreBehaviour.Pressed(kb, CheatsConfig.FlipKey.Value)) Assists.FlipUpright();
            Movement.Tick(kb);
        }
        catch (Exception e)
        {
            if (_loggedKeys) return;
            _loggedKeys = true;
            CheatsPlugin.Logger.LogError("Cheats+ hotkeys failed (further errors suppressed): " + e);
        }
    }

    // The driving HUD's last line (Map & HUD draws it): wheel slip and surface, rebuilt only when the shown values change.
    private static long _slipKey = long.MinValue;
    private static string _slipSurface;
    private static string _slipText = "";

    private static string SlipLine()
    {
        float slip = Assists.MaxNormalizedSlip;
        string surface = Assists.Surface;
        // Key includes which side of 1.0 the slip is on, so the colour flips exactly when the value crosses it.
        long key = Mathf.RoundToInt(slip * 100f) * 2L + (slip > 1f ? 1 : 0);
        if (key != _slipKey || !ReferenceEquals(surface, _slipSurface))
        {
            _slipKey = key;
            _slipSurface = surface;
            string slipColor = slip > 1f ? "#E5534B" : "#4CC38A";
            _slipText = $"slip <color={slipColor}>{slip:0.00}</color>  ·  {surface}";
        }
        return _slipText;
    }
}
