using System;
using System.Collections.Generic;
using Fusion;
using HarmonyLib;
using OverTheHillPlus.ServerCreator;
using UnityEngine;

namespace OverTheHillPlus.Multiplayer;

/// <summary>
/// Host: greets each player who joins with a custom message, shown to everyone in the session, on core's host notice
/// pop-up (<see cref="HostNotice"/>: the game's networked "player left" pop-up, its own wording hidden).
/// </summary>
internal static class JoinMessage
{
    private const float DelaySeconds = 8f;

    private static readonly List<(float at, PlayerRef player)> Queue = new();

    public static void OnPlayerJoined(PlayerRef player)
    {
        if (!ServerCreatorConfig.JoinMessageEnabled.Value || string.IsNullOrWhiteSpace(ServerCreatorConfig.JoinMessage.Value)) return;
        if (!GameState.IsAuthority || GameState.Runner == null) return;
        try { if (GameState.Runner.LocalPlayer.PlayerId == player.PlayerId) return; } catch (Exception ex) { DebugLog.Error("servercreator", ex, "JoinMessage.OnPlayerJoined"); }
        Queue.Add((Time.unscaledTime + DelaySeconds, player));
    }

    /// <summary>Called every frame; sends greetings whose delay has passed.</summary>
    public static void Tick()
    {
        for (int i = Queue.Count - 1; i >= 0; i--)
        {
            if (Time.unscaledTime < Queue[i].at) continue;
            var player = Queue[i].player;
            Queue.RemoveAt(i);
            string result = Send(player);
            if (!result.StartsWith("Sent", StringComparison.Ordinal)) ServerCreatorPlugin.Logger.LogInfo("[Lobby] Join message: " + result);
        }
    }

    public static string Preview(string joinerName = "NewPlayer")
    {
        if (TemplateBlocked(out string why)) return "Blocked: " + why;
        return Format(joinerName);
    }

    /// <summary>Swear/slur filter on the message the host typed: such a message is refused, not censored.</summary>
    private static bool TemplateBlocked(out string why) =>
        Features.Public.Filter.NameFilter.IsBlocked(RoomNames.Escape(ServerCreatorConfig.JoinMessage.Value), out why);

    /// <summary>The message for this joiner, or "" when it must not be sent (blocked words).</summary>
    private static string Format(string joiner)
    {
        if (TemplateBlocked(out _)) return "";
        string host = "host";
        try { host = OverlandSessionPlayers.Instance?.GetPlayerName(GameState.Runner.LocalPlayer) ?? host; } catch (Exception ex) { DebugLog.Error("servercreator", ex, "JoinMessage.Format"); }
        // Player names are not ours to refuse; a name that fails the filter is left out of the greeting instead.
        // Typed markup is neutralised first (see RoomNames.Escape); only the mod's own tags are added when sending.
        joiner = RoomNames.Escape(joiner);
        host = RoomNames.Escape(host);
        if (Features.Public.Filter.NameFilter.IsBlockedStrict(joiner, out _)) joiner = "a new player";
        if (Features.Public.Filter.NameFilter.IsBlockedStrict(host, out _)) host = "the host";
        string text = RoomNames.Escape(ServerCreatorConfig.JoinMessage.Value).Replace("{name}", joiner).Replace("{host}", host).Trim();
        return Features.Public.Filter.NameFilter.IsBlocked(text, out _) ? "" : text;
    }

    public static string Send(PlayerRef player)
    {
        if (TemplateBlocked(out string why)) return "Blocked: join message not sent, " + why + ".";
        var sp = OverlandSessionPlayers.Instance;
        if (sp == null) return "Failed: not in a session.";
        string joiner = $"Player {player.PlayerId}";
        try { joiner = sp.GetPlayerName(player); } catch (Exception ex) { DebugLog.Error("servercreator", ex, "JoinMessage.Send"); }
        string text = Format(joiner);
        if (text.Length == 0) return "Failed: empty message, or it would contain a blocked word.";
        try
        {
            HostNotice.Send(HostNotice.ColorFor(ServerCreatorConfig.JoinMessageColor.Value), text);
            if (DevDetails.On) ServerCreatorPlugin.Logger.LogInfo($"[Lobby] Join message for {joiner}: {text}");
            return "Sent: " + text;
        }
        catch (Exception e)
        {
            DebugLog.Error("servercreator", e, "JoinMessage.Send");
            ServerCreatorPlugin.Logger.LogWarning("[Lobby] Join message failed: " + e.Message);
            return "Failed: " + e.Message;
        }
    }

    /// <summary>Host test button (shown with <see cref="DevDetails"/> on): sends the message now, as if someone joined.</summary>
    public static string SendTest()
    {
        if (!GameState.IsAuthority || GameState.Runner == null) return "Blocked: host a session first.";
        try { return Send(GameState.Runner.LocalPlayer); }
        catch (Exception e) { DebugLog.Error("servercreator", e, "JoinMessage.SendTest"); return "Failed: " + e.Message; }
    }
}

[HarmonyPatch(typeof(OverlandNetworkRunner), nameof(OverlandNetworkRunner.Fusion_INetworkRunnerCallbacks_OnPlayerJoined))]
internal static class JoinMessagePlayerJoinedPatch
{
    private static void Postfix(PlayerRef player) => JoinMessage.OnPlayerJoined(player);
}
