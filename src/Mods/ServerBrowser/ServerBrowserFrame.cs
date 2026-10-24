using System;

namespace OverTheHillPlus.ServerBrowser;

/// <summary>
/// Server Browser+'s per-frame work that must run before the features' ticks (as the old ModBehaviour ran it): the
/// session list throttle and the first-seen saves, plugged into core's CoreBehaviour hooks.
/// </summary>
internal static class ServerBrowserFrame
{
    private static bool _loggedError;

    internal static void Install()
    {
        CoreBehaviour.BeforeFeatureTicks += BeforeFeatureTicks;
        CoreBehaviour.OnQuit += () => FirstSeenTracker.SaveIfDue(force: true);
    }

    private static void BeforeFeatureTicks()
    {
        // Caught here: a throw would otherwise skip every mod's feature ticks this frame.
        try
        {
            SessionThrottle.Tick();
            FirstSeenTracker.SaveIfDue();
        }
        catch (Exception e)
        {
            if (_loggedError) return;
            _loggedError = true;
            ServerBrowserPlugin.Logger.LogError("Server browser tick failed (further errors suppressed): " + e);
        }
    }
}
