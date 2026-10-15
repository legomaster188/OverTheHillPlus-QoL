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
            Send(player);
        }
    }

    public static string Preview(string joinerName = "NewPlayer") => Format(joinerName);

    private static string Format(string joiner)
    {
        string host = "host";
        try { host = OverlandSessionPlayers.Instance?.GetPlayerName(GameState.Runner.LocalPlayer) ?? host; } catch { }
        return (ModConfig.JoinMessage.Value ?? "").Replace("{name}", joiner).Replace("{host}", host).Trim();
    }

    public static string Send(PlayerRef player)
    {
        var sp = OverlandSessionPlayers.Instance;
        if (sp == null) return "Failed: not in a session.";
        string joiner = $"Player {player.PlayerId}";
        try { joiner = sp.GetPlayerName(player); } catch { }
        string text = Format(joiner);
        if (text.Length == 0) return "Failed: empty message.";
        try
        {
            sp.RPC_ShowPlayerLeftNotification(ColorFor(ModConfig.JoinMessageColor.Value), Marker + text + HideRest);
            Plugin.Logger.LogInfo($"[Lobby] Join message for {joiner}: {text}");
            return "Sent: " + text;
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning("[Lobby] Join message failed: " + e.Message);
            return "Failed: " + e.Message;
        }
    }

    /// <summary>Host test button: sends the message now, as if someone joined.</summary>
    public static string SendTest()
    {
        if (!GameState.IsAuthority || GameState.Runner == null) return "Blocked: host a session first.";
        try { return Send(GameState.Runner.LocalPlayer); }
        catch (Exception e) { return "Failed: " + e.Message; }
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
