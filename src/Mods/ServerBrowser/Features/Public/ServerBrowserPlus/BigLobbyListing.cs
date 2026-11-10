using System;
using OverTheHillPlus.Config;
using HarmonyLib;
using OverTheHillPlus.ServerBrowser;

namespace OverTheHillPlus.Features.Public.ServerBrowserPlus;

/// <summary>
/// The game's server list hides every server with more than 4 player slots: OverlandSessionInfo's constructor sets
/// <c>VisibleInList</c> to (SessionInfo.MaxPlayers &lt;= 4) and SessionInfoListAdapter.ApplyFilters skips every entry without it
/// (join by code never looks at it). So big lobbies, ours (Lobby+) and other hosts', never
/// show in the browser. This sets the flag for them again, so they are listed like any other server (the game's own
/// hide-full / hide-private / area / search filters still apply). Only this game's list changes; players without the
/// mod still can't see servers above 4 slots.
/// </summary>
internal static class BigLobbyListing
{
    internal static ConfigEntry<bool> ShowBigLobbies;
    internal static int Unhidden { get; private set; }

    internal static void Bind(ConfigFile cfg)
    {
        ShowBigLobbies = cfg.Bind("ServerBrowserPlus", "ShowBigLobbies", true,
            "List servers with more than 4 player slots (the game hides them from its server list; join by code still works without this).");
    }

    private static int _lastLoggedCount = -1;

    /// <summary>Marks every big lobby in the runner's current session list as listable.</summary>
    internal static void FixRunnerList()
    {
        try
        {
            var list = OverlandNetworkRunner.Instance?.SessionList;
            if (list == null) return;
            int before = Unhidden;
            for (int i = 0; i < list.Count; i++)
            {
                var info = list[i];
                if (info == null) continue;
                AfterCreated(info, info.FusionSessionInfo);
            }
            int now = Unhidden - before;
            if (now > 0 && now != _lastLoggedCount)
            {
                _lastLoggedCount = now;
                ServerBrowserPlugin.Logger.LogInfo($"[Browser+] big lobbies: {now} server(s) above 4 slots made listable this refresh");
            }
        }
        catch (Exception e) { DebugLog.Error(ServerBrowserPlugin.ModId, e, "BigLobbyListing.FixRunnerList"); }
    }

    /// <summary>Lists one big lobby the game marked as hidden.</summary>
    internal static void AfterCreated(OverlandSessionInfo info, Fusion.SessionInfo sessionInfo)
    {
        if (ShowBigLobbies == null || !ShowBigLobbies.Value || info == null || sessionInfo == null) return;
        int max;
        try { max = sessionInfo.MaxPlayers; } catch { return; }
        if (max <= 4) return;
        try
        {
            if (!OverlandSessionInfo.IsSessionValid(sessionInfo)) return; // missing properties / other version: stays hidden
            if (info.VisibleInList) return;
            info.VisibleInList = true;
            Unhidden++;
        }
        catch (Exception e) { DebugLog.Error(ServerBrowserPlugin.ModId, e, "BigLobbyListing.AfterCreated"); }
    }
}

/// <summary>Right before the list view filters (SessionInfoListAdapter.UpdateSessionList runs ApplyFilters): marks the big
/// lobbies in the runner's session list (OverlandNetworkRunner.SessionList, built in OnSessionListUpdated) as listable.
/// (A postfix on OverlandSessionInfo's constructor never ran in game, so the list is fixed up here instead.)</summary>
[HarmonyPatch(typeof(SessionInfoListAdapter), nameof(SessionInfoListAdapter.UpdateSessionList))]
internal static class BigLobbyListingPatch
{
    private static void Prefix() => BigLobbyListing.FixRunnerList();
}
