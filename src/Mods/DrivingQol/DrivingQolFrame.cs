using System;

namespace OverTheHillPlus.DrivingQol;

/// <summary>
/// Driving QoL's per-frame work that runs before the features' ticks (as the old ModBehaviour ran it): the frame-rate
/// caps, field of view, compass range and auto headlights (<see cref="Qol.Tick"/>), plugged into core's hooks.
/// </summary>
internal static class DrivingQolFrame
{
    private static bool _loggedError;

    internal static void Install() => CoreBehaviour.BeforeFeatureTicks += BeforeFeatureTicks;

    private static void BeforeFeatureTicks()
    {
        // Caught here: a throw would otherwise skip the other mods' hooks this frame.
        try { Qol.Tick(); }
        catch (Exception e)
        {
            if (_loggedError) return;
            _loggedError = true;
            DrivingQolPlugin.Logger.LogError("QoL tick failed (further errors suppressed): " + e);
        }
    }
}
