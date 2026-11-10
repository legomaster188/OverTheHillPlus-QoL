using OverTheHillPlus.Core.Shared;
using OverTheHillPlus.Features.Public.Rejoin;
using OverTheHillPlus.ServerBrowser;
using UnityEngine;

namespace OverTheHillPlus.Features.Public.ServerBrowserPlus;

/// <summary>
/// "Players with the mod" under the PLUS bar of the server browser, in the bar's own game look (QuickBarNative rows):
/// who has our mods running, in the browser's region or all regions, with a Join on every server its host shares.
/// The list comes from core's ModPlayers (the Player Market mod fetches it); Join runs the rejoin path
/// (<see cref="RejoinCore.JoinListed"/>: the region switch, the search by session or room code, the game's JoinSession).
/// </summary>
internal static class ModPlayersPanel
{
    public const int MaxRows = 6;
    private const float RowH = 30f, Gap = 4f, Top = 10f, JoinW = 90f;

    /// <summary>The panel is open (the bar's "Mod players" switch).</summary>
    public static bool Open;
    private static bool _allRegions;

    public static float Height => Open ? Top + 24f + Gap + (RowH + Gap) * (1 + Rows) : 0f;

    private static int Rows => Mathf.Clamp(ModPlayers.List.Count, 1, MaxRows);

    public static void DrawNative(float y, float w)
    {
        if (!Open) return;
        string region = _allRegions ? "" : LobbyRegion.Connected ?? "";
        ModPlayers.Ask(region);
        y += Top;
        string head = !ModPlayers.Available ? "Players with the mod: needs the Player Market mod"
            : ModPlayers.Problem ?? (ModPlayers.UpdatedAt < 0f ? "Players with the mod: looking..."
                : "Players with the mod: " + ModPlayers.Online + " online");
        QuickBarNative.SubLabel(new Rect(1f, y, w - 2f, 22f), head);
        y += 24f + Gap;
        string here = string.IsNullOrEmpty(LobbyRegion.Connected) ? "this region" : "region " + LobbyRegion.Connected.ToUpperInvariant();
        if (QuickBarNative.Toggle(new Rect(0f, y, w / 2f - Gap / 2f, RowH), "Only " + here, !_allRegions)) _allRegions = !_allRegions;
        if (QuickBarNative.Button(new Rect(w / 2f + Gap / 2f, y, w / 2f - Gap / 2f, RowH), "Refresh", ModPlayers.Available)) ModPlayers.Ask(region);
        y += RowH + Gap;
        var list = ModPlayers.List;
        if (list.Count == 0)
        {
            QuickBarNative.Button(new Rect(0f, y, w, RowH), ModPlayers.UpdatedAt < 0f ? "..." : "Nobody to show here right now", false);
            return;
        }
        for (int i = 0; i < list.Count && i < MaxRows; i++)
        {
            var p = list[i];
            string line = ModPlayers.Line(p) + (_allRegions && p.Region.Length > 0 ? "  (" + p.Region.ToUpperInvariant() + ")" : "");
            bool pressed = QuickBarNative.Button(new Rect(0f, y, w - JoinW - Gap, RowH), line, true) && p.Joinable;
            if (QuickBarNative.Button(new Rect(w - JoinW, y, JoinW, RowH), "Join", p.Joinable) || pressed)
            {
                string why = RejoinCore.JoinListed(p);
                if (why != null) Menu.SetStatus(why);
            }
            y += RowH + Gap;
        }
    }
}
