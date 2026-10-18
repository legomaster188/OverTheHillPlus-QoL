using System;
using System.Collections.Generic;
using System.Text;

namespace OverTheHillPlus.Unreleased;

/// <summary>
/// Unreleased Cars' decisions, kept free of game types so the tests can run them (UnreleasedTests).
/// </summary>
internal static class UnreleasedRules
{
    private const int MaxProbes = 4096;

    /// <summary>
    /// An item id for a car the game ships without one (Brigand, Cordillera): a fixed hash of its name, so every game
    /// with this mod gives it the same id (a guest's choice reaches the host as an id). Ids the game already uses are
    /// skipped by counting up from the hash; 0 is never returned unless every candidate is taken.
    /// </summary>
    public static uint AllocateItemId(string carName, Func<uint, bool> taken)
    {
        uint id = Fnv1a("oth.unreleased." + (carName ?? "").ToLowerInvariant());
        for (int i = 0; i < MaxProbes; i++, id++)
        {
            if (id == 0) continue;
            if (!taken(id)) return id;
        }
        return 0;
    }

    private static uint Fnv1a(string s)
    {
        uint h = 2166136261;
        foreach (byte b in Encoding.UTF8.GetBytes(s))
        {
            h ^= b;
            h *= 16777619;
        }
        return h;
    }

    /// <summary>
    /// Whether the garage lists one of our cars. Only cars the player added (owned) are listed, so the garage never
    /// offers them for sale and the game's own "add every car" paths (new profile, unlock all) never hand them out.
    /// A guest needs a host that runs this mod: the host looks the car up when it spawns it.
    /// <para>A car whose CarInfo exists only with this mod (Brigand, Cordillera: <paramref name="soloOnly"/>) breaks
    /// on a vanilla player's game when spawned there, and a vanilla player can join while one is out, so (ruling P15)
    /// it is offered only in solo. Outside solo it stays listed only while it is the car in use (the garage keeps its
    /// current car; it is spawned as its donor anyway, see <see cref="SessionCarSwap"/>).</para>
    /// </summary>
    public static bool ShowInGarage(bool enabled, bool owned, bool authority, bool hostHasMod, bool soloOnly, bool solo, bool inUse = false) =>
        enabled && owned && (authority || hostHasMod) && (!soloOnly || solo || inUse);

    /// <summary>
    /// Solo (or the menus) for the solo-only cars. The game's own runner (OverlandNetworkRunner and its live
    /// NetworkRunner) decides first, then core's cached runner (refreshed once a second, so it can still be empty
    /// right after hosting or joining); with neither readable, it is solo only when no Fusion runner is live at all.
    /// </summary>
    public static bool DecideSolo(bool? overlandSinglePlayer, bool? cachedSinglePlayer, bool anyLiveRunner)
    {
        if (overlandSinglePlayer.HasValue) return overlandSinglePlayer.Value;
        if (cachedSinglePlayer.HasValue) return cachedSinglePlayer.Value;
        return !anyLiveRunner;
    }

    /// <summary>
    /// The car id to spawn instead of <paramref name="carId"/> in a session, or 0 to keep it: a solo-only car becomes
    /// its donor outside solo (only in what is spawned or sent, never in the inventory or the save).
    /// </summary>
    public static uint SessionCarSwap(uint carId, bool solo, bool soloOnly, uint donorId) =>
        !solo && soloOnly && donorId != 0 && donorId != carId ? donorId : 0;

    /// <summary>
    /// Whether a stash entry may go back into the inventory after a load: only for cars set up in this game (a car
    /// the game now sells itself, or one that failed, stays in the stash), and only parts the catalog knows.
    /// </summary>
    public static bool ShouldRestore(StashItem item, Func<uint, bool> carRegistered, Func<uint, bool> partKnown)
    {
        if (item == null) return false;
        return item.Kind switch
        {
            StashKind.Vehicle => carRegistered(item.Id),
            StashKind.PartState => carRegistered(item.VehicleId) && partKnown(item.Id),
            _ => partKnown(item.Id),
        };
    }

    /// <summary>
    /// The stock car that stands in for one of ours (in the saved file, or when joining a host without this mod):
    /// the last stock car the player drove when they still own it, else the first stock car they own; 0 = none.
    /// </summary>
    public static uint ChooseFallback(IList<uint> ownedVehicles, ISet<uint> ours, uint lastStockInUse)
    {
        if (ownedVehicles == null) return 0;
        if (lastStockInUse != 0 && !ours.Contains(lastStockInUse) && ownedVehicles.Contains(lastStockInUse)) return lastStockInUse;
        foreach (uint id in ownedVehicles)
            if (id != 0 && !ours.Contains(id)) return id;
        return 0;
    }

    /// <summary>
    /// The stash to write at a save: what the inventory has now, plus earlier entries that this game could not have
    /// put back (<paramref name="couldRestore"/> false: their car isn't set up here, see <see cref="ShouldRestore"/>),
    /// out of use. An earlier entry that could have been put back but isn't in the inventory any more was removed by
    /// the player and is dropped. The current entries win; the fallback is the current one.
    /// </summary>
    public static ProfileStash Merge(ProfileStash previous, ProfileStash now, Func<StashItem, bool> couldRestore)
    {
        var merged = new ProfileStash { FallbackId = now?.FallbackId ?? 0 };
        var seen = new HashSet<(StashKind, uint, uint)>();
        if (now != null)
            foreach (var i in now.Items)
                if (seen.Add(Key(i))) merged.Items.Add(i);
        if (previous != null)
            foreach (var i in previous.Items)
            {
                if (!seen.Add(Key(i)) || couldRestore(i)) continue;
                if (i.Kind == StashKind.Vehicle) i.InUse = false;
                merged.Items.Add(i);
            }
        return merged;
    }

    private static (StashKind, uint, uint) Key(StashItem i) => (i.Kind, i.Id, i.Kind == StashKind.PartState ? i.VehicleId : 0);

    /// <summary>
    /// After a load: the car of ours to put back in use, or 0. Only when one of ours was in use at the last save, it
    /// is still switched on, and the save still has the stand-in in use (a player who drove without the mod and chose
    /// another car keeps that one).
    /// </summary>
    public static uint CarToPutInUse(ProfileStash stash, uint currentInUse, Func<uint, bool> enabled)
    {
        if (stash == null) return 0;
        foreach (var item in stash.Vehicles())
        {
            if (!item.InUse) continue;
            if (!enabled(item.Id)) return 0;
            return currentInUse == stash.FallbackId ? item.Id : 0;
        }
        return 0;
    }
}
