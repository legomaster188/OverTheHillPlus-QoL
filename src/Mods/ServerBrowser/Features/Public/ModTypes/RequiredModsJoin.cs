using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;
using OverTheHillPlus.Features.Public.Rejoin;
using OverTheHillPlus.Features.Public.ServerBrowserPlus;
using OverTheHillPlus.ServerBrowser;
using OverTheHillPlus.UI;
using UnityEngine;

namespace OverTheHillPlus.Features.Public.ModTypes;

// After the namespace line on purpose: the game has its own global ModManager (mod.io).
using OverTheHillPlus.Core.Manager;

/// <summary>
/// Servers that need mods: their row shows "Needs: Vera Crossover, Race Mode" (from the host's othp.req, read by
/// core's RequiredModsSession), and a join is held while this game lacks one of them, switched one off, or runs
/// another version. Core's MODS page then asks what to do (RequiredModsPrompt): install and rejoin after the restart,
/// switch on and restart, join anyway (not when another major version blocks it), or cancel. The rejoin after the
/// restart goes through Rejoin (LastServerStore + RejoinCore). Core never calls this mod; this mod calls core.
/// </summary>
internal sealed class RequiredModsJoin : IFeature
{
    private const string Section = "ServerMods";

    public static ConfigEntry<bool> AskBeforeJoining, ShowNeeds;

    private static OverlandSessionInfo _pendingJoin;
    private static bool _pendingByCode;
    private static float _pendingSince;
    private static string _bypassName;
    private static float _bypassUntil;
    private static bool _loggedError;

    public void Register()
    {
        var cfg = ServerBrowserConfig.File;
        AskBeforeJoining = cfg.Bind(Section, "AskBeforeJoining", true,
            "Before joining a server that runs mods you don't have (or have switched off, or at another version), ask what to do: install them and rejoin after a restart, switch them on, or join anyway.");
        ShowNeeds = cfg.Bind(Section, "ShowNeeds", true, "Show the mods a server needs on its row (\"Needs: ...\").");
        ShowNeeds.SettingChanged += OnShowNeedsChanged;
        Registry.Section(Registry.PlusMode, "Servers", DrawCard, -10);
        Registry.OnTick(Tick);
    }

    private static void OnShowNeedsChanged(object sender, EventArgs args) => BrowserPlus.RequestRefresh();

    private static void DrawCard()
    {
        Menu.Card("Servers that need mods", "Server mods must be installed by everyone in the session; their hosts list them", () =>
        {
            Menu.Switch(AskBeforeJoining, "Ask before joining", "Install & join, switch on & restart, or join anyway", true);
            Menu.Switch(ShowNeeds, "Show \"Needs: ...\" on rows", "The server mods each server runs", true);
        });
    }

    // ---------------------------------------------------------------- row tag

    private static readonly Dictionary<RequiredList, string> TagCache = new();
    private static bool _tagsWithList, _tagsPrefabsPending = true;

    /// <summary>"Needs: ..." for a row: green when everything is here, yellow when something is missing or off, red when
    /// another major version blocks the join. Null when the server lists no server mods.</summary>
    internal static string RowTag(Fusion.SessionInfo session)
    {
        if (ShowNeeds == null || !ShowNeeds.Value || session == null) return null;
        var list = RequiredModsSession.For(session);
        if (list == null) return null;
        if (_tagsWithList != AvailableTab.ListReady)
        {
            _tagsWithList = AvailableTab.ListReady; // the mod list brings the names of mods not installed here
            TagCache.Clear();
        }
        bool pending = Core.Shared.NetPrefabRegistry.AnyPending;
        if (pending != _tagsPrefabsPending)
        {
            _tagsPrefabsPending = pending; // this game's network prefabs finished registering: the prefab check is final
            TagCache.Clear();
        }
        if (TagCache.TryGetValue(list, out var cached)) return cached; // what is installed here doesn't change while the game runs
        if (TagCache.Count > 256) TagCache.Clear();
        string text = RequiredMods.NeedsText(list, RequiredModsSession.NameOf, 48);
        string tag = null;
        if (text != null)
        {
            var issues = RequiredModsSession.CheckJoin(list);
            string color = issues.Count == 0 ? "#4CC38A" : RequiredMods.Blocks(issues) ? "#E5534B" : "#F2B33D";
            tag = $"<color={color}>{Escape(text)}</color>  ";
        }
        TagCache[list] = tag;
        return tag;
    }

    private static string Escape(string s) => s.Replace('<', '‹').Replace('>', '›');

    // ---------------------------------------------------------------- the join gate

    /// <summary>Before the game joins a session. False = held: the MODS page asks what to do.</summary>
    internal static bool BeforeJoin(OverlandSessionInfo info, bool joiningByCode)
    {
        try
        {
            if (info == null || AskBeforeJoining == null) return true;
            var session = info.FusionSessionInfo;
            string name = session?.Name;
            if (name == null) return true; // nothing to tell it by (or remember it by): the game decides
            // The player already chose to join it. Kept (not used up) while the join runs: a join to another region
            // is deferred by All regions, which calls JoinSession again once it found the room there.
            if (name == _bypassName && Time.unscaledTime < _bypassUntil) return true;
            if (!AskBeforeJoining.Value) return true;
            var list = RequiredModsSession.For(session);
            if (list == null) return true;
            // With the host's prefab check: a join it would refuse (other added cars / trailers) is held and named here.
            var issues = RequiredModsSession.CheckJoin(list);
            if (issues.Count == 0) return true;

            bool blocks = RequiredMods.Blocks(issues);
            string label = Favourites.PlainName(Favourites.DisplayNameOf(info));
            if (string.IsNullOrEmpty(label)) label = "that server";
            if (RejoinCore.State == RejoinCore.Phase.Joining)
            {
                // Rejoin's own join: the player already chose for this server. Only another major version stops it.
                if (!blocks) return true;
                string why = RequiredMods.PrefabSummary(issues, RequiredModsSession.NameOf);
                RejoinCore.Abort(why != null ? $"Rejoin stopped: {label}: {why}" : $"Rejoin stopped: {label} needs another version of your mods");
            }
            ServerBrowserPlugin.Logger.LogInfo($"[ServerMods] holding the join of '{label}': {issues.Count} mod issue(s)" + (blocks ? " (blocked)" : ""));
            var held = info;
            RequiredModsPrompt.Show(new RequiredModsPrompt.Request
            {
                ServerName = label,
                Issues = issues,
                More = list.More,
                Join = blocks ? null : () => QueueJoin(held, joiningByCode),
                PrepareRejoin = () => RememberForRestart(held),
            });
            return false;
        }
        catch (Exception e)
        {
            DebugLog.Error("serverbrowser", e, "RequiredModsJoin.BeforeJoin");
            if (!_loggedError)
            {
                _loggedError = true;
                ServerBrowserPlugin.Logger.LogWarning("[ServerMods] checking a server's mods failed (further errors suppressed): " + e.Message);
            }
            return true; // never stand in the way of a join because of our own error
        }
    }

    private static void QueueJoin(OverlandSessionInfo info, bool byCode)
    {
        _pendingJoin = info;
        _pendingByCode = byCode;
        _pendingSince = Time.unscaledTime;
    }

    /// <summary>The join the player chose on the prompt, from Update once the MODS page has closed.</summary>
    private static void Tick()
    {
        // The chosen join is done with once a session started (or All regions gave up: the time ran out).
        if (_bypassName != null && (RejoinCore.InAnySession || Time.unscaledTime >= _bypassUntil)) _bypassName = null;
        var info = _pendingJoin;
        if (info == null) return;
        if (ModsPage.IsOpen)
        {
            if (Time.unscaledTime - _pendingSince > 5f) _pendingJoin = null; // the page stayed open: the player moved on
            return;
        }
        _pendingJoin = null;
        var browser = BrowserPlus.Browser;
        if (browser == null || !BrowserPlus.BrowserAlive)
        {
            Menu.SetStatus("Blocked: open the server browser to join");
            return;
        }
        try
        {
            _bypassName = info.FusionSessionInfo?.Name;
            _bypassUntil = Time.unscaledTime + AllRegions.JoinTimeoutSeconds + 15f;
            browser.JoinSession(info, _pendingByCode);
        }
        catch (Exception e)
        {
            DebugLog.Error("serverbrowser", e, "RequiredModsJoin.Tick");
            _bypassName = null;
            ServerBrowserPlugin.Logger.LogWarning("[ServerMods] join failed: " + e.Message);
            Menu.SetStatus("Failed: the game refused the join");
        }
    }

    /// <summary>Saves the held server as the pending join, joined once the game has restarted (the last server
    /// Rejoin remembers stays the one you were really in).</summary>
    private static void RememberForRestart(OverlandSessionInfo info)
    {
        var s = info?.FusionSessionInfo;
        string name = s?.Name;
        if (string.IsNullOrEmpty(name)) return;
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var e = new LastServerStore.Entry { SessionName = name, JoinedAt = now, LastSeenAt = now };
        try { e.DisplayName = Favourites.DisplayNameOf(info); } catch (Exception ex) { DebugLog.Error("serverbrowser", ex, "RequiredModsJoin.RememberForRestart"); }
        if (string.IsNullOrWhiteSpace(e.DisplayName)) e.DisplayName = name;
        e.Region = AllRegions.RegionOf(name);
        if (string.IsNullOrEmpty(e.Region)) { try { e.Region = s.Region; } catch (Exception ex) { DebugLog.Error("serverbrowser", ex, "RequiredModsJoin.RememberForRestart"); } }
        if (string.IsNullOrEmpty(e.Region)) { try { e.Region = OverlandNetworkRunner.Instance?.ConnectedRegion; } catch (Exception ex) { DebugLog.Error("serverbrowser", ex, "RequiredModsJoin.RememberForRestart"); } }
        try { e.Private = info.RoomProperties != null && info.RoomProperties.PrivateServer; } catch (Exception ex) { DebugLog.Error("serverbrowser", ex, "RequiredModsJoin.RememberForRestart"); }
        PendingJoinStore.Save(e);
        ServerBrowserPlugin.Logger.LogInfo($"[ServerMods] '{Favourites.PlainName(e.DisplayName)}' is rejoined after the restart");
    }
}

/// <summary>Runs before the other JoinSession prefixes (All regions, Rejoin), which skip a held join.</summary>
[HarmonyPatch(typeof(StateServerBrowser), nameof(StateServerBrowser.JoinSession))]
internal static class RequiredModsJoinPatch
{
    [HarmonyPriority(Priority.First + 100)]
    private static bool Prefix(OverlandSessionInfo sessionToJoin, bool joiningByCode) =>
        RequiredModsJoin.BeforeJoin(sessionToJoin, joiningByCode);
}
