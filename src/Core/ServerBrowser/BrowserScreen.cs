using HarmonyLib;

namespace OverTheHillPlus.ServerBrowser;

/// <summary>
/// The game's server browser screen while it is open (null when closed), for every mod that adds to it: Server
/// Browser+ (filters, favourites, quick bar) and the players panel of the server listing both read it.
/// </summary>
internal static class BrowserScreen
{
    /// <summary>The open server browser state; null when the browser is closed.</summary>
    internal static StateServerBrowser Current { get; set; }

    public static bool Open => Current != null;

    /// <summary>The server selected in the open browser, or null.</summary>
    public static OverlandSessionInfo SelectedSession()
    {
        var b = Current;
        if (b == null) return null;
        try
        {
            if (b.currentlySelectedSession != null) return b.currentlySelectedSession;
        }
        catch (System.Exception ex) { DebugLog.Error("core", ex, "BrowserScreen.SelectedSession"); }
        try { return b.ui?.ContextUIServerList?.SessionInfoListAdapter?.currentlySelectedSessionInfo; }
        catch (System.Exception ex) { DebugLog.Error("core", ex, "BrowserScreen.SelectedSession"); return null; }
    }
}

[HarmonyPatch]
internal static class BrowserScreenPatches
{
    [HarmonyPostfix]
    [HarmonyPatch(typeof(StateServerBrowser), nameof(StateServerBrowser.Open))]
    private static void Opened(StateServerBrowser __instance) => BrowserScreen.Current = __instance;

    [HarmonyPostfix]
    [HarmonyPatch(typeof(StateServerBrowser), nameof(StateServerBrowser.Close))]
    private static void Closed() => BrowserScreen.Current = null;
}
