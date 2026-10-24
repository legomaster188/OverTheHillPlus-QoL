using OverTheHillPlus.Features.Public.Rejoin;
using OverTheHillPlus.Features.Public.ServerBrowserPlus;
using OverTheHillPlus.UI;
using UnityEngine.InputSystem;

namespace OverTheHillPlus.ServerBrowser;

/// <summary>Server Browser+'s everyday settings, first on its MODS page (the rest is in its Advanced fold).</summary>
internal static class ServerBrowserSimple
{
    private static readonly float[] RefreshSteps = { 0f, 10f, 15f, 30f, 60f };

    internal static void Draw()
    {
        SimpleMenu.Heading("Server browser");
        SimpleMenu.Toggle(BrowserPlus.PinFavourites, "Favourites at the top",
            BrowserPlus.FavouriteKey == null ? null : $"Star a server in the browser with {KeyName(BrowserPlus.FavouriteKey.Value)}.");
        SimpleMenu.Toggle(BrowserFriends.Highlight, "Highlight friends' servers", "Servers where a Steam friend plays stand out.");
        SimpleMenu.Toggle(BrowserPlus.PinModded, "Modded servers near the top", "Servers running these mods come right after favourites and friends.");
        if (BigLobbyListing.ShowBigLobbies != null)
            SimpleMenu.Toggle(BigLobbyListing.ShowBigLobbies, "Show big lobbies", "The game hides servers with more than 4 player slots; this lists them. Players without the mods still can't see them.");
        SimpleMenu.Toggle(BrowserPlus.OnlyFreeSlots, "Only servers with free slots", "Hides full and closed servers.");
        SimpleMenu.Toggle(BrowserPlus.HideIncompatible, "Hide other game versions", "Servers you can't join anyway.");
        if (BrowserRefresh.AutoRefresh != null && BrowserRefresh.AutoRefreshSeconds != null)
        {
            SimpleMenu.FloatChoice("Auto refresh", "Refreshes the server list by itself while it is open.",
                BrowserRefresh.AutoRefresh.Value ? BrowserRefresh.AutoRefreshSeconds.Value : 0f, RefreshSteps,
                v => v <= 0f ? "Off" : v.ToString("0") + " s",
                v =>
                {
                    BrowserRefresh.AutoRefresh.Value = v > 0f;
                    if (v > 0f) BrowserRefresh.AutoRefreshSeconds.Value = v;
                });
        }

        SimpleMenu.Heading("Rejoin");
        var last = LastServerStore.Current;
        if (RejoinCore.Active)
            SimpleMenu.Button("Cancel rejoin", RejoinCore.StatusText, "Stops the rejoin that is running.", RejoinCore.RequestCancel);
        else
            SimpleMenu.Button("Rejoin last server", last == null ? "None yet" : RejoinCore.Name(last),
                last == null ? "The next server you join as a guest is remembered here."
                : RejoinCore.InAnySession ? "Leave the current session first (works from the main menu or the server browser)."
                : "Joins the server you were last in.",
                RejoinCore.RequestManual, last != null && !RejoinCore.InAnySession);
        SimpleMenu.Toggle(RejoinFeature.AutoRejoin, "Auto-rejoin after a disconnect", "Counts down, then rejoins. Never after a kick, a ban or quitting.");
    }

    private static string KeyName(Key key) => key == Key.None ? "no key" : key.ToString();
}
