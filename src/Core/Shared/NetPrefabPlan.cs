using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace OverTheHillPlus.Core.Shared;

/// <summary>What a joining player's connection token says about the host's runtime network prefabs.</summary>
internal enum NetPrefabCheck
{
    /// <summary>Same entries in the same order on the same game build: the player can load every one.</summary>
    Ok,
    /// <summary>No marker: a game without them (vanilla, or the mods switched off).</summary>
    Missing,
    /// <summary>Another set, order, version or game build: an index would mean another prefab there.</summary>
    Mismatch,
}

/// <summary>
/// The fixed order of the runtime network prefabs mods add to Fusion's prefab table, and the hash peers compare.
/// Fusion replicates a spawned object's prefab <i>index</i>, so every peer must append the same entries in the same
/// order: core registers them in <see cref="Order"/>'s order, whatever order the plugins load in, and the join marker
/// carries a hash of the game's stock count and every registered entry (index, key, guid, layout), so a peer that differs
/// in any of them is refused. Pure code (unit-tested).
/// </summary>
internal static class NetPrefabPlan
{
    /// <summary>
    /// Every key a mod may register, in registration order. <b>Append only</b>: a key's position decides its prefab
    /// index, so inserting or reordering changes the indices (and the hash) for everyone. "vera" is the Vera Crossover;
    /// the four "trailer.&lt;name&gt;" entries are the Trailers mod's; "tercel" is the Terrier 4WD Wagon and "aro" the
    /// Carpati 24, "hilo" the Hilo 89, "bastion" the Bastion 200, "hilo22" the Hilo 22, "drifter" the Drifter, "nomad" the Nomad, "mule" the Mule, "pixie" the Pixie, "bento" the Bento, "longhorn" the Longhorn, "ibex" the Ibex, "marmot" the Marmot, "drover" the Drover, "limber" the Limber, "badger" the Badger and "gemse" the Gemse 6x6 (core's car framework, CarDefinition.RegistryKey). Appending a key changes nothing for games that don't
    /// register it: only registered entries are in the join hash.
    /// </summary>
    public static readonly string[] Order = { "vera", "trailer.utility", "trailer.camper", "trailer.hauler", "trailer.boat", "tercel", "aro", "hilo", "bastion", "hilo22", "drifter", "nomad", "mule", "pixie", "bento", "longhorn", "ibex", "marmot", "drover", "limber", "badger", "gemse" };

    /// <summary>The mod id each key of <see cref="Order"/> belongs to (same positions): only that mod may declare it.</summary>
    public static readonly string[] Owners = { "vera", "trailers", "trailers", "trailers", "trailers", "tercel", "aro", "hilo", "bastion", "hilo22", "drifter", "nomad", "mule", "pixie", "bento", "longhorn", "ibex", "marmot", "drover", "limber", "badger", "gemse" };

    /// <summary>The mod that owns <paramref name="key"/>, null when the key isn't listed.</summary>
    public static string OwnerOf(string key)
    {
        int i = OrderOf(key);
        return i < 0 || i >= Owners.Length ? null : Owners[i];
    }

    /// <summary>
    /// The mods that own keys, each once, in the order they first appear in <see cref="Owners"/> (append only, like the
    /// keys): bit <c>i</c> of a prefab mask (<see cref="MaskOf"/>, the host's othp.req advert) is mod <c>i</c> here.
    /// </summary>
    public static readonly IReadOnlyList<string> OwnerMods = DistinctOwners();

    private static string[] DistinctOwners()
    {
        var list = new List<string>();
        foreach (var o in Owners)
            if (!list.Contains(o)) list.Add(o);
        return list.ToArray();
    }

    /// <summary>The mod owns at least one key: guests without it (or with it while the host lacks it) are refused.</summary>
    public static bool IsOwnerMod(string id) => OwnerIndex(id) >= 0;

    /// <summary>Position of <paramref name="id"/> in <see cref="OwnerMods"/> (case-insensitive), -1 when it owns no key.</summary>
    public static int OwnerIndex(string id)
    {
        if (string.IsNullOrEmpty(id)) return -1;
        for (int i = 0; i < OwnerMods.Count; i++)
            if (string.Equals(OwnerMods[i], id, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    /// <summary>The bit mask of these mods over <see cref="OwnerMods"/> (mods that own no key are ignored).</summary>
    public static int MaskOf(IEnumerable<string> mods)
    {
        int mask = 0;
        if (mods != null)
            foreach (var m in mods)
            {
                int i = OwnerIndex(m);
                if (i >= 0 && i < 31) mask |= 1 << i;
            }
        return mask;
    }

    /// <summary>The mods of a mask, in <see cref="OwnerMods"/> order; bits this build doesn't know are left out.</summary>
    public static List<string> ModsOf(int mask)
    {
        var list = new List<string>();
        for (int i = 0; i < OwnerMods.Count && i < 31; i++)
            if ((mask & (1 << i)) != 0) list.Add(OwnerMods[i]);
        return list;
    }

    /// <summary>A mod may take back its declarations (its Load failed after declaring) only before registration
    /// began, for the same reason as <see cref="MayDeclare"/>: the indices of later entries depend on them.</summary>
    public static bool MayWithdraw(bool registrationBegun) => !registrationBegun;

    /// <summary>Marker name in the connection token (see <see cref="ConnectionToken"/>).</summary>
    public const string MarkerName = "np1";

    /// <summary>Position of <paramref name="key"/> in <see cref="Order"/>, -1 when it isn't listed (it can't register).</summary>
    public static int OrderOf(string key)
    {
        if (string.IsNullOrEmpty(key)) return -1;
        return Array.IndexOf(Order, key);
    }

    /// <summary>The listed items sorted into <see cref="Order"/>; unlisted keys and repeats of a key are dropped.</summary>
    public static List<T> Sorted<T>(IEnumerable<T> items, Func<T, string> key)
    {
        var byPos = new SortedDictionary<int, T>();
        if (items != null)
            foreach (var it in items)
            {
                int pos = OrderOf(key(it));
                if (pos >= 0 && !byPos.ContainsKey(pos)) byPos[pos] = it;
            }
        return new List<T>(byPos.Values);
    }

    /// <summary>
    /// The marker value for a game with these registered entries (in registration order, each with its prefab index) on
    /// a game build with
    /// <paramref name="stockCount"/> stock prefabs: 16 hex digits of a 64-bit FNV-1a hash. Null when nothing is
    /// registered (then no marker is sent and nobody is refused).
    /// </summary>
    public static string Hash(int stockCount, IReadOnlyList<(string key, string guid, string layout, int index)> registered)
    {
        if (registered == null || registered.Count == 0) return null;
        var sb = new StringBuilder("np1|").Append(stockCount.ToString(CultureInfo.InvariantCulture));
        foreach (var (key, guid, layout, index) in registered)
            sb.Append('|').Append(index.ToString(CultureInfo.InvariantCulture)).Append('=').Append(key).Append('=')
              .Append((guid ?? "").ToLowerInvariant()).Append('=').Append(layout ?? "");
        ulong h = 14695981039346656037UL;
        foreach (byte b in Encoding.UTF8.GetBytes(sb.ToString()))
        {
            h ^= b;
            h *= 1099511628211UL;
        }
        return h.ToString("x16", CultureInfo.InvariantCulture);
    }

    /// <summary>Declarations are only taken before the first registration attempt: one arriving later could take an
    /// index that another peer (where it arrived in time) gave to a later entry.</summary>
    public static bool MayDeclare(bool registrationBegun) => !registrationBegun;

    /// <summary>A session holds the set fixed from its StartGame until its runner is gone or shut down (also while it is
    /// still starting, when it isn't "running" yet but its advert is already built).</summary>
    public static bool SessionHolds(bool runnerExists, bool runnerShutDown) => runnerExists && !runnerShutDown;

    /// <summary>The joiner's marker against the host's (<paramref name="host"/> null: the host registered nothing).</summary>
    public static NetPrefabCheck Check(string joiner, string host)
    {
        if (host == null) return NetPrefabCheck.Ok;
        if (string.IsNullOrEmpty(joiner)) return NetPrefabCheck.Missing;
        return string.Equals(joiner, host, StringComparison.Ordinal) ? NetPrefabCheck.Ok : NetPrefabCheck.Mismatch;
    }
}
