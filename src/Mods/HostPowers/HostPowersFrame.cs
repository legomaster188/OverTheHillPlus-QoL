using System;

namespace OverTheHillPlus.HostPowers;

/// <summary>
/// Host Powers' per-frame work that runs after the features' ticks (as the old ModBehaviour ran it): the weather lock
/// (<see cref="HostWorld.Tick"/>), plugged into core's hooks.
/// </summary>
internal static class HostPowersFrame
{
    private static bool _loggedError;

    internal static void Install() => CoreBehaviour.AfterFeatureTicks += AfterFeatureTicks;

    private static void AfterFeatureTicks()
    {
        // Caught here: a throw would otherwise skip the other mods' hooks this frame.
        try { HostWorld.Tick(); }
        catch (Exception e)
        {
            if (_loggedError) return;
            _loggedError = true;
            HostPowersPlugin.Logger.LogError("Host Powers tick failed (further errors suppressed): " + e);
        }
    }
}
