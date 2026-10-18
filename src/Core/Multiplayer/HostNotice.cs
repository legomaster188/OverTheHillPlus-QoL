using System;
using HarmonyLib;
using OverTheHillPlus.Features.Public.Filter;
using UnityEngine;

namespace OverTheHillPlus.Multiplayer;

/// <summary>
/// Host notices for everyone in the session (Server Creator+'s join message, moderation notices). The game has no
/// chat, so they ride on the networked "player left" pop-up, which takes a free-text name and a colour. The text after
/// the notice (the game's "left" wording) is hidden with a transparent rich-text tag, and clients running these mods
/// strip it cleanly via the marker (<see cref="HostNoticeDisplayPatch"/>, in core so every modded client does it).
/// </summary>
internal static class HostNotice
{
    private const string Marker = "​⁠"; // zero-width: invisible in vanilla, recognised by modded clients
    private const string HideRest = "<alpha=#00>";

    /// <summary>
    /// Neutralises markup the user typed: '&lt;' and '&gt;' become look-alike angle quotes, so "&lt;size=0&gt;" or
    /// "&lt;alpha=#00&gt;" can neither hide letters from the word filter nor restyle what everyone sees. Only the
    /// mods' own tags are added afterwards.
    /// </summary>
    public static string Escape(string text) =>
        string.IsNullOrEmpty(text) ? (text ?? "") : text.Replace('<', '‹').Replace('>', '›').Replace('\\', '⧵'); // ⧵: no "\\u003C" escapes either

    /// <summary>
    /// Host: shows a short notice to everyone in the session (vanilla players see it too). Build <paramref name="text"/>
    /// from fixed wording plus names passed through <see cref="SafeName"/>; the whole text is escaped and filtered
    /// again here and not sent if it fails the filter. False = nothing sent.
    /// </summary>
    internal static bool Announce(string text, Color color)
    {
        if (string.IsNullOrWhiteSpace(text) || !GameState.IsAuthority || GameState.Runner == null) return false;
        try { if (GameState.Runner.IsSinglePlayer || !GameState.Runner.IsServer) return false; } catch (Exception ex) { DebugLog.Error("core", ex, "HostNotice.Announce"); return false; }
        if (OverlandSessionPlayers.Instance == null) return false;
        text = Escape(text).Trim();
        if (NameFilter.IsBlocked(text, out _)) return false;
        try
        {
            Send(color, text);
            return true;
        }
        catch (Exception e)
        {
            DebugLog.Error("core", e, "HostNotice.Announce");
            Core.CorePlugin.Logger?.LogWarning("[Lobby] Notice failed: " + e.Message);
            return false;
        }
    }

    /// <summary>
    /// Sends <paramref name="text"/> as it is (already escaped and filtered by the caller) on the pop-up. Throws when
    /// the game's call fails; does nothing outside a session.
    /// </summary>
    internal static void Send(Color color, string text)
    {
        var sp = OverlandSessionPlayers.Instance;
        if (sp == null) return;
        sp.RPC_ShowPlayerLeftNotification(color, Marker + text + HideRest);
    }

    /// <summary>A player name made safe to show to everyone: markup neutralised, and replaced when it fails the filter.</summary>
    internal static string SafeName(string name, string fallback = "a player")
    {
        name = Escape(name ?? "").Trim();
        if (name.Length == 0 || NameFilter.IsBlockedStrict(name, out _)) return fallback;
        return name;
    }

    /// <summary>The pop-up colours, by name (unknown names are gold).</summary>
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

    /// <summary>On clients with these mods: show only the notice, without the game's hidden "left" wording.</summary>
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

[HarmonyPatch(typeof(HUDManager), nameof(HUDManager.ShowNotification),
    new[] { typeof(Color), typeof(string), typeof(string), typeof(float), typeof(float), typeof(float) })]
internal static class HostNoticeDisplayPatch
{
    private static void Prefix(ref string text) => HostNotice.CleanUp(ref text);
}
