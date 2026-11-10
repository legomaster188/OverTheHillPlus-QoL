using System;
using UnityEngine;

namespace OverTheHillPlus.Core.Shared;

/// <summary>
/// Where a player's semi-trailer carries its load, for the mods that put one on it (Haulage's lumber, pipe and tower steel
/// loads), so they don't reference Trailers. Trailers sets <see cref="DeckOf"/> while it runs: the deck node of the
/// player's semi-trailer (the deck top's centre, trailer axes: +z towards the kingpin), or null when they have none.
/// Main thread only.
/// </summary>
internal static class TrailerDecks
{
    /// <summary>Player (PlayerRef.RawEncoded) to their semi-trailer's deck node, or null. Set by Trailers (null otherwise).</summary>
    public static Func<int, Transform> DeckOf { get; set; }

    /// <summary>The deck node of the player's semi-trailer, or null (no Trailers, no semi-trailer, or it threw).</summary>
    public static Transform Find(int player)
    {
        var f = DeckOf;
        if (f == null) return null;
        try { return f(player); }
        catch (Exception e) { DebugLog.Error("core", e, "TrailerDecks.Find"); return null; }
    }
}
