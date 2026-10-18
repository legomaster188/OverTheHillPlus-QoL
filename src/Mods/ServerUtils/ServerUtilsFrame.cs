using System;

namespace OverTheHillPlus.ServerUtils;

/// <summary>
/// Server Utils' work outside the features' ticks, plugged into core's CoreBehaviour hooks: putting back the host's
/// graphics values and process priority when the game quits (the old ModBehaviour did it on quit).
/// </summary>
internal static class ServerUtilsFrame
{
    internal static void Install() => CoreBehaviour.OnQuit += OnQuit;

    private static void OnQuit()
    {
        // Caught here too: core calls each quit hook on its own, but a log line names what failed.
        try { Features.Public.HostGovernor.GovernorRuntime.Shutdown(); }
        catch (Exception e) { ServerUtilsPlugin.Logger.LogError("Host governor shutdown failed: " + e); }
    }
}
