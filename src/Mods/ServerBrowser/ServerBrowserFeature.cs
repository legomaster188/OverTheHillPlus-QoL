using OverTheHillPlus.Features;
using OverTheHillPlus.UI;
using UnityEngine;
using static OverTheHillPlus.Menu;

namespace OverTheHillPlus.ServerBrowser;

/// <summary>
/// The server list settings (row colours, age, smoother refreshes), the colour legend and the stats on the mod's page
/// (the old F7 menu's Servers tab drew these cards itself).
/// </summary>
internal sealed class ServerBrowserFeature : IFeature
{
    private const string ServersTab = "Servers";
    // Before the feature cards of the same tab, as the old menu drew its own cards first.
    private const int CardOrder = -100;

    public void Register() => Registry.Section(Registry.PlusMode, ServersTab, DrawServers, CardOrder);

    private static void DrawServers()
    {
        Card("Server browser", null, () =>
        {
            Switch(ServerBrowserConfig.ColorRows, "Color-code rows", "Stripe and tint by server status", true);
            Switch(ServerBrowserConfig.ShowAge, "Show server age", "How long ago the mod first saw each server", true);
            Switch(ServerBrowserConfig.ThrottleSessionUpdates, "Smooth list refreshes", "Skip duplicate updates and merge bursts", true);
            SliderRow(ServerBrowserConfig.SessionUpdateInterval, "Min time between rebuilds", 0.25f, 5f, "0.00", " s", ServerBrowserConfig.ThrottleSessionUpdates.Value);
        });
        Card("Legend", null, () =>
        {
            Legend(ServerStatus.Public, "Public", "joinable");
            Legend(ServerStatus.Private, "Private", "locked / room code");
            Legend(ServerStatus.Full, "Full", "or closed to new players");
            Legend(ServerStatus.Incompatible, "Other version", "can't join");
            Small("Age is exact if the mod saw the server appear; \"≥\" means it already existed.");
        });
        Card("Stats", null, () =>
        {
            KeyValue("Servers remembered", FirstSeenTracker.Count.ToString());
            if (DevDetails.On)
            {
                KeyValue("List updates applied", SessionThrottle.Applied.ToString());
                KeyValue("Duplicates skipped", SessionThrottle.SkippedIdentical.ToString());
                KeyValue("Bursts merged", SessionThrottle.Deferred.ToString());
            }
        });
    }

    private static void Legend(ServerStatus status, string name, string meaning)
    {
        SwatchRow(RowStyler.ColorFor(status), name, meaning);
    }
}
