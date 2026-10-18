using System;

namespace OverTheHillPlus.Core.Manager;

/// <summary>
/// Which of our session properties a hosted StartGame may add (pure logic, unit-tested). Photon allows 10 custom
/// session properties; the game uses three. Each of our keys has a fixed rank (<see cref="Ours"/>), and whether it
/// may be added depends only on how many keys that are not ours are already there plus its rank, never on which of
/// our StartGame prefixes (core's mod network and server mods, Server Creator+'s player names) Harmony runs first.
/// So under pressure the key that is dropped is always the lowest-ranked one.
/// <para>The mod network and the server mods' list keep two slots free for the game and other mods; the player names
/// may take up to the last slot (as they always did).</para>
/// </summary>
internal static class SessionSlots
{
    public const int Max = 10;
    public const int KeepFree = 2;

    /// <summary>Our keys, highest rank first: ModNet.SessionKey, RequiredMods.Key, PlayerNamesProperty.Key.</summary>
    public static readonly string[] Ours = { "othp.net", RequiredMods.Key, "othp.pl" };

    /// <summary>The game's own session properties (OverlandSessionInfo needs all three to list a session).</summary>
    public static readonly string[] GameKeys = { "AreaInfoName", "GameVersion", "RoomProperties" };

    /// <summary>Every session property key the game and these mods use, for reading them one by one (never enumerate
    /// Fusion's property dictionary through IL2CPP: its enumerator is a value type and that crashed the runtime).</summary>
    public static readonly string[] KnownKeys = { "AreaInfoName", "GameVersion", "RoomProperties", "othp.net", RequiredMods.Key, "othp.pl" };

    /// <summary>Rank of one of our keys, -1 for any other key.</summary>
    public static int RankOf(string key) => key == null ? -1 : Array.IndexOf(Ours, key);

    /// <summary>
    /// True when <paramref name="key"/> may be added to start arguments holding <paramref name="count"/> properties, of
    /// which <paramref name="present"/> says which keys are there (only ours are asked). A key already there may always
    /// be set again; a key that isn't ours is not decided here (false).
    /// </summary>
    public static bool MayAdd(string key, int count, Func<string, bool> present)
    {
        int rank = RankOf(key);
        if (rank < 0) return false;
        bool Has(string k) { try { return present != null && present(k); } catch (Exception ex) { DebugLog.Error("core", ex, "SessionSlots.MayAdd"); return false; } }
        if (Has(key)) return true;
        int ours = 0;
        foreach (var k in Ours) if (Has(k)) ours++;
        int others = Math.Max(0, count - ours);
        int limit = key == Ours[Ours.Length - 1] ? Max : Max - KeepFree;
        return others + rank + 1 <= limit;
    }
}
