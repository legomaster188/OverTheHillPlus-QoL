using System;
using System.Collections.Generic;
using UnityEngine;

namespace OverTheHillPlus.Shared;

/// <summary>
/// Short-lived pings mods raise on the maps (Flares+): the owner replaces its whole list whenever it changes and
/// the Minimap draws them as pins kept at the panel's edge. The game's map screen gets the same spots through
/// <see cref="MapMarkers"/>. Main thread only.
/// </summary>
internal static class Pings
{
    public struct Ping
    {
        public Vector3 Pos;
        public Color Color;
    }

    private const int MaxPings = 32;
    private static readonly Dictionary<string, Ping[]> ByOwner = new(StringComparer.Ordinal);
    private static List<Ping> _all;

    /// <summary>Replaces the pings <paramref name="owner"/> shows (empty = none).</summary>
    public static void Set(string owner, IReadOnlyList<Ping> pings)
    {
        if (string.IsNullOrEmpty(owner)) return;
        if (pings == null || pings.Count == 0) ByOwner.Remove(owner);
        else
        {
            var copy = new Ping[Math.Min(pings.Count, MaxPings)];
            for (int i = 0; i < copy.Length; i++) copy[i] = pings[i];
            ByOwner[owner] = copy;
        }
        _all = null;
    }

    /// <summary>Every owner's pings (cached until someone calls <see cref="Set"/>).</summary>
    public static IReadOnlyList<Ping> All()
    {
        if (_all != null) return _all;
        var list = new List<Ping>();
        foreach (var kv in ByOwner) list.AddRange(kv.Value);
        return _all = list;
    }
}
