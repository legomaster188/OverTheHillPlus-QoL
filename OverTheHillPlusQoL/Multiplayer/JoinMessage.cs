using System;
using System.Collections.Generic;
using Fusion;
using HarmonyLib;
using UnityEngine;

namespace OverTheHillPlus.Multiplayer;

/// <summary>
/// Host: greets each player who joins with a custom message, shown to everyone in the session.
/// The game has no chat, so it rides on the networked "player left" pop-up, which takes a free-text name and a
/// colour. The text after the message (the game's "left" wording) is hidden with a transparent rich-text tag, and
/// clients running this mod strip it cleanly via the marker.
/// </summary>
internal static class JoinMessage
{
    private const string Marker = "​⁠"; // zero-width: invisible in vanilla, recognised by modded clients
    private const string HideRest = "<alpha=#00>";
    private const float DelaySeconds = 8f;

    private static readonly List<(float at, PlayerRef player)> Queue = new();

    public static void OnPlayerJoined(PlayerRef player)
    {
        if (!ModConfig.JoinMessageEnabled.Value || string.IsNullOrWhiteSpace(ModConfig.JoinMessage.Value)) return;
        if (!GameState.IsAuthority || GameState.Runner == null) return;
        try { if (GameState.Runner.LocalPlayer.PlayerId == player.PlayerId) return; } catch { }
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
            if (!result.StartsWith("Sent", StringComparison.Ordinal)) Plugin.Logger.LogInfo("[Lobby] Join message: " + result);
        }
    }

    public static string Preview(string joinerName = "NewPlayer")
    {
        if (TemplateBlocked(out string why)) return "Blocked: " + why;
        return Format(joinerName);
    }

    /// <summary>Swear/slur filter on the message the host typed: such a message is refused, not censored.</summary>
    private static bool TemplateBlocked(out string why) =>
        Features.Public.Filter.NameFilter.IsBlocked(RoomNames.Escape(ModConfig.JoinMessage.Value), out why);

    /// <summary>The message for this joiner, or "" when it must not be sent (blocked words).</summary>
    private static string Format(string joiner)
    {
        if (TemplateBlocked(out _)) return "";
        string host = "host";
        try { host = OverlandSessionPlayers.Instance?.GetPlayerName(GameState.Runner.LocalPlayer) ?? host; } catch { }
        // Player names are not ours to refuse; a name that fails the filter is left out of the greeting instead.
        // Typed markup is neutralised first (see RoomNames.Escape); only the mod's own tags are added when sending.
        joiner = RoomNames.Escape(joiner);
        host = RoomNames.Escape(host);
        if (Features.Public.Filter.NameFilter.IsBlocked(joiner, out _)) joiner = "a new player";
        if (Features.Public.Filter.NameFilter.IsBlocked(host, out _)) host = "the host";
        string text = RoomNames.Escape(ModConfig.JoinMessage.Value).Replace("{name}", joiner).Replace("{host}", host).Trim();
        return Features.Public.Filter.NameFilter.IsBlocked(text, out _) ? "" : text;
    }

    public static string Send(PlayerRef player)
    {
        if (TemplateBlocked(out string why)) return "Blocked: join message not sent, " + why + ".";
        var sp = OverlandSessionPlayers.Instance;
        if (sp == null) return "Failed: not in a session.";
        string joiner = $"Player {player.PlayerId}";
        try { joiner = sp.GetPlayerName(player); } catch { }
        string text = Format(joiner);
        if (text.Length == 0) return "Failed: empty message, or it would contain a blocked word.";
        try
        {
            sp.RPC_ShowPlayerLeftNotification(ColorFor(ModConfig.JoinMessageColor.Value), Marker + text + HideRest);
            return "Sent: " + text;
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning("[Lobby] Join message failed: " + e.Message);
            return "Failed: " + e.Message;
        }
    }

    public static Color ColorFor(string name) => name switch
    {
        "Cyan" => new Color(0.31f, 0.85f, 0.97f),
        "Pink" => new Color(1f, 0.44f, 0.71f),
        "Green" => new Color(0.36f, 0.89f, 0.54f),
        "Red" => new Color(1f, 0.35f, 0.31f),
        "Purple" => new Color(0.71f, 0.55f, 1f),
        "White" => Color.white,
        _ => new Color(0.95f, 0.70f, 0.24f), // Gold
    };

    public static readonly string[] Colors = { "Gold", "Cyan", "Pink", "Green", "Red", "Purple", "White" };

    /// <summary>On clients with this mod: show only the message, without the game's hidden "left" wording.</summary>
    internal static void CleanUp(ref string text)
    {
        if (text == null) return;
        int start = text.IndexOf(Marker, StringComparison.Ordinal);
        if (start < 0) return;
        start += Marker.Length;
        int end = text.IndexOf(HideRest, start, StringComparison.Ordinal);
        text = end > start ? text.Substring(start, end - start) : text.Substring(start);
    }
}

[HarmonyPatch(typeof(OverlandNetworkRunner), nameof(OverlandNetworkRunner.Fusion_INetworkRunnerCallbacks_OnPlayerJoined))]
internal static class JoinMessagePlayerJoinedPatch
{
    private static void Postfix(PlayerRef player) => JoinMessage.OnPlayerJoined(player);
}

[HarmonyPatch(typeof(HUDManager), nameof(HUDManager.ShowNotification),
    new[] { typeof(Color), typeof(string), typeof(string), typeof(float), typeof(float), typeof(float) })]
internal static class JoinMessageDisplayPatch
{
    private static void Prefix(ref string text) => JoinMessage.CleanUp(ref text);
}
