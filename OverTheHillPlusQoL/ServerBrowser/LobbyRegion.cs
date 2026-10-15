using System;
using System.Collections.Generic;
using UnityEngine;

namespace OverTheHillPlus.ServerBrowser;

/// <summary>
/// The Photon region the server browser's lobby is connected to, and switching it the way the game's region
/// dropdown does (ContextUIServerList.ServerRegionOnChanged calls OverlandNetworkRunner.ChangeLobbyRegion with the
/// picked entry of AvailableServerRegions).
///
/// How a switch works (decompiled): ChangeLobbyRegion destroys the lobby NetworkRunner and awaits JoinLobby(region),
/// which clears SessionList, starts a new runner, joins that region's session lobby, saves the region as the
/// player's SERVER_REGION setting and raises ServerRegionUpdated. Photon lobbies are per region: the session list
/// only ever holds the connected region's rooms. A switch takes a few seconds; asking for another one while it is
/// still running would race two runners, so callers check <see cref="Switching"/> first.
/// </summary>
internal static class LobbyRegion
{
    /// <summary>A switch that hasn't reached its region by then is treated as failed.</summary>
    public const float SwitchTimeout = 25f;

    /// <summary>The region last asked for through <see cref="Switch"/> (null when none is in flight).</summary>
    public static string Target { get; private set; }
    public static float RequestedAt { get; private set; } = -100f;
    private static bool _loggedSwitchError;

    public static bool InLobby
    {
        get
        {
            try
            {
                var o = OverlandNetworkRunner.Instance;
                return o != null && o.NetworkRunner != null && o.IsConnectedToLobby;
            }
            catch { return false; }
        }
    }

    /// <summary>The connected lobby region (e.g. "eu"), or null when unknown.</summary>
    public static string Connected
    {
        get
        {
            try
            {
                var o = OverlandNetworkRunner.Instance;
                if (o == null || o.NetworkRunner == null) return null;
                string r = o.ConnectedRegion;
                return string.IsNullOrEmpty(r) ? null : r;
            }
            catch { return null; }
        }
    }

    public static bool Same(string a, string b) =>
        !string.IsNullOrEmpty(a) && !string.IsNullOrEmpty(b) && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>A switch asked for through <see cref="Switch"/> hasn't reached its region yet (and hasn't timed out).</summary>
    public static bool Switching
    {
        get
        {
            if (Target == null) return false;
            if (Time.unscaledTime - RequestedAt >= SwitchTimeout) { Target = null; return false; }
            if (InLobby && Same(Connected, Target)) { Target = null; return false; }
            return true;
        }
    }

    /// <summary>The game reported that it could not connect to a region's lobby: a switch to it is over.</summary>
    public static void NoteFailed(string region)
    {
        if (Target != null && (string.IsNullOrEmpty(region) || Same(region, Target))) Target = null;
    }

    /// <summary>The region codes the game's region dropdown offers, in its order (empty when not known yet).</summary>
    public static List<string> Available()
    {
        var result = new List<string>();
        try
        {
            var o = OverlandNetworkRunner.Instance;
            var list = o == null ? null : o.AvailableServerRegions;
            if (list == null) return result;
            for (int i = 0; i < list.Count; i++)
            {
                string code = list[i].RegionCode;
                if (!string.IsNullOrEmpty(code) && !result.Exists(c => Same(c, code))) result.Add(code);
            }
        }
        catch { }
        return result;
    }

    /// <summary>
    /// Asks the game to move the lobby to <paramref name="region"/> (what picking it in the dropdown does). The next
    /// session list from Photon is let straight through the update throttle. <paramref name="logTag"/> null = quiet.
    /// </summary>
    public static bool Switch(string region, string logTag)
    {
        if (string.IsNullOrEmpty(region)) return false;
        try
        {
            var o = OverlandNetworkRunner.Instance;
            if (o == null || o.NetworkRunner == null) return false;
            if (logTag != null)
                Plugin.Logger.LogInfo($"{logTag} switching the server list from region {Connected ?? "?"} to {region}");
            SessionThrottle.DropPending(); // a held-back list of the old region must not land after the switch
            SessionThrottle.Reset();
            Target = region;
            RequestedAt = Time.unscaledTime;
            o.ChangeLobbyRegion(region);
            return true;
        }
        catch (Exception e)
        {
            Target = null;
            if (!_loggedSwitchError)
            {
                _loggedSwitchError = true;
                Plugin.Logger.LogWarning("[Servers] region switch failed (further errors suppressed): " + e.Message);
            }
            return false;
        }
    }

    /// <summary>
    /// True when the lobby is in <paramref name="region"/> (or either region is unknown). Otherwise asks for a switch,
    /// at most once per <paramref name="retrySeconds"/> and never while another switch is still running, and
    /// returns false.
    /// </summary>
    public static bool Ensure(string region, ref float nextSwitch, float retrySeconds, string logTag)
    {
        string connected = Connected;
        if (string.IsNullOrEmpty(region) || string.IsNullOrEmpty(connected) || Same(region, connected)) return true;
        if (Switching) return false;
        float now = Time.unscaledTime;
        if (now >= nextSwitch)
        {
            nextSwitch = now + retrySeconds;
            Switch(region, logTag);
        }
        return false;
    }
}
