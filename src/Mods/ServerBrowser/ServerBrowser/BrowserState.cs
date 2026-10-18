using HarmonyLib;

namespace OverTheHillPlus.ServerBrowser;

/// <summary>Whether the game's server browser screen is open (list refreshes are only smoothed while it is).</summary>
internal static class BrowserState
{
    public static bool Open { get; private set; }

    internal static void SetOpen(bool open)
    {
        if (open == Open) return;
        Open = open;
    }
}

[HarmonyPatch]
internal static class BrowserOpenClosePatches
{
    [HarmonyPostfix]
    [HarmonyPatch(typeof(StateServerBrowser), nameof(StateServerBrowser.Open))]
    private static void Opened()
    {
        SessionThrottle.Reset();
        BrowserState.SetOpen(true);
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(StateServerBrowser), nameof(StateServerBrowser.Close))]
    private static void Closed()
    {
        BrowserState.SetOpen(false);
        FirstSeenTracker.SaveIfDue(force: true);
    }
}
