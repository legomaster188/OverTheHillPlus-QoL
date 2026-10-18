using System;
using OverTheHillPlus.Multiplayer;

namespace OverTheHillPlus.ServerCreator;

/// <summary>
/// Server Creator+'s per-frame work that runs after the features' ticks (as the old ModBehaviour ran it): sending the
/// join messages that are due, plugged into core's CoreBehaviour hooks.
/// </summary>
internal static class ServerCreatorFrame
{
    private static bool _loggedError;

    internal static void Install() => CoreBehaviour.AfterFeatureTicks += AfterFeatureTicks;

    private static void AfterFeatureTicks()
    {
        // Caught here: a throw would otherwise skip the other mods' hooks this frame.
        try { JoinMessage.Tick(); }
        catch (Exception e)
        {
            if (_loggedError) return;
            _loggedError = true;
            ServerCreatorPlugin.Logger.LogError("Join message tick failed (further errors suppressed): " + e);
        }
    }
}
