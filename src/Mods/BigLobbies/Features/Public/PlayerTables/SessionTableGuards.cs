using System;
using Fusion;
using HarmonyLib;
using OverTheHillPlus.BigLobbies;

namespace OverTheHillPlus.Features.Public.PlayerTables;

/// <summary>
/// The 7-entry per-player dictionaries of OverlandSessionPlayers and MapNetworkManager (see the table in
/// PlayerTables.cs). On the host: clear entries of players who already left when a table is full, skip a write that
/// still can't fit (before anything is spawned), and make sure a leaving player's entries are removed even when the
/// game's RemovePlayer throws part-way.
/// </summary>
internal static class SessionTableGuards
{
    internal static void Install()
    {
        var osp = typeof(OverlandSessionPlayers);
        // Before BigLobby's AddPlayer replacement (Priority.First = 800), so it finds room for the new player.
        PlayerTables.Patch(osp, nameof(OverlandSessionPlayers.AddPlayer), null, typeof(SessionTableGuards), prefix: nameof(AddPlayerPrefix), priority: 900);
        PlayerTables.Patch(osp, nameof(OverlandSessionPlayers.RemovePlayer), null, typeof(SessionTableGuards), finalizer: nameof(RemovePlayerFinalizer));
        PlayerTables.Patch(osp, nameof(OverlandSessionPlayers.RPC_SetBusyState), null, typeof(SessionTableGuards), prefix: nameof(SetBusyPrefix));
        PlayerTables.Patch(osp, nameof(OverlandSessionPlayers.RPC_SetSupporterEditionOwnership), null, typeof(SessionTableGuards), prefix: nameof(SupporterPrefix));

        var map = typeof(MapNetworkManager);
        PlayerTables.Patch(map, nameof(MapNetworkManager.RPC_InstantiatePinpoint), null, typeof(SessionTableGuards), prefix: nameof(InstantiatePinpointPrefix));
        PlayerTables.Patch(map, nameof(MapNetworkManager.RPC_MovePinpoint), null, typeof(SessionTableGuards), prefix: nameof(MovePinpointPrefix));
        PlayerTables.Patch(map, nameof(MapNetworkManager.RPC_PlacePortableWinchAnchor), null, typeof(SessionTableGuards), prefix: nameof(PlaceAnchorPrefix));
    }

    private static bool Authority(Fusion.NetworkBehaviour b)
    {
        try { return b != null && b.HasStateAuthority; } catch (Exception ex) { DebugLog.Error("biglobbies", ex, "SessionTableGuards.Authority"); return false; }
    }

    // ---- OverlandSessionPlayers ----

    /// <summary>Clears ghost entries from every session table that is full, so the joining player fits.</summary>
    private static void PurgeSession(OverlandSessionPlayers sp, int keepEncoded, System.Collections.Generic.HashSet<int> live)
    {
        var names = sp.PlayerRefToNameDictionary;
        if (names.Count >= names.Capacity)
            PlayerTables.PurgeStale("PlayerRefToNameDictionary", live, keepEncoded, e => names.ContainsKey(e - 1), e => names.Remove(e - 1));
        var sup = sp.PlayerRefToSupporterEditionOwnership;
        if (sup.Count >= sup.Capacity)
            PlayerTables.PurgeStale("PlayerRefToSupporterEditionOwnership", live, keepEncoded, e => sup.ContainsKey(PlayerTables.Ref(e)), e => sup.Remove(PlayerTables.Ref(e)));
        var dev = sp.PlayerRefToDevTeamMembership;
        if (dev.Count >= dev.Capacity)
            PlayerTables.PurgeStale("PlayerRefToDevTeamMembership", live, keepEncoded, e => dev.ContainsKey(PlayerTables.Ref(e)), e => dev.Remove(PlayerTables.Ref(e)));
        var busy = sp.BusyPlayerRefToBusyReason;
        if (busy.Count >= busy.Capacity)
            PlayerTables.PurgeStale("BusyPlayerRefToBusyReason", live, keepEncoded, e => busy.ContainsKey(PlayerTables.Ref(e)), e => busy.Remove(PlayerTables.Ref(e)));
    }

    private static void AddPlayerPrefix(OverlandSessionPlayers __instance, PlayerRef playerRef)
    {
        if (!PlayerTables.On || !Authority(__instance)) return;
        try
        {
            var names = __instance.PlayerRefToNameDictionary;
            var sup = __instance.PlayerRefToSupporterEditionOwnership;
            var dev = __instance.PlayerRefToDevTeamMembership;
            var busy = __instance.BusyPlayerRefToBusyReason;
            if (names.Count < names.Capacity && sup.Count < sup.Capacity && dev.Count < dev.Capacity && busy.Count < busy.Capacity) return;
            PurgeSession(__instance, playerRef.RawEncoded, PlayerTables.LivePlayers(__instance.Runner));
        }
        catch (Exception e) { DebugLog.Error("biglobbies", e, "SessionTableGuards.AddPlayerPrefix"); PlayerTables.LogOnce("addplayer", "AddPlayer purge: " + e.Message); }
    }

    /// <summary>
    /// Runs after RemovePlayer even when it threw (a finalizer; it doesn't change the exception, LobbyPatches decides).
    /// Removes whatever the game left behind for this player, including map pins and anchors.
    /// </summary>
    private static void RemovePlayerFinalizer(OverlandSessionPlayers __instance, PlayerRef playerRef)
    {
        if (!PlayerTables.On || !Authority(__instance)) return;
        bool any = false;
        try
        {
            int key = playerRef.RawEncoded - 1;
            var names = __instance.PlayerRefToNameDictionary;
            if (names.ContainsKey(key)) { names.Remove(key); any = true; }
            var sup = __instance.PlayerRefToSupporterEditionOwnership;
            if (sup.ContainsKey(playerRef)) { sup.Remove(playerRef); any = true; }
            var dev = __instance.PlayerRefToDevTeamMembership;
            if (dev.ContainsKey(playerRef)) { dev.Remove(playerRef); any = true; }
            var busy = __instance.BusyPlayerRefToBusyReason;
            if (busy.ContainsKey(playerRef)) { busy.Remove(playerRef); any = true; }
        }
        catch (Exception e) { DebugLog.Error("biglobbies", e, "SessionTableGuards.RemovePlayerFinalizer"); PlayerTables.LogOnce("remove:session", "RemovePlayer clean-up (session tables): " + e.Message); }
        try
        {
            var map = MapNetworkManager.Instance;
            if (map != null && Authority(map) && HasMapObjects(map, playerRef))
            {
                map.RPC_DestroyPlayerOwnedObjects(playerRef); // host: runs locally, same call the game makes in RemovePlayer
                any = true;
                RemoveMapKeys(map, playerRef); // in case the object was already gone and the RPC threw
            }
        }
        catch (Exception e) { DebugLog.Error("biglobbies", e, "SessionTableGuards.RemovePlayerFinalizer"); PlayerTables.LogOnce("remove:map", "RemovePlayer clean-up (map tables): " + e.Message); }
        if (any)
        {
            PlayerTables.LeaveCleanups++;
            BigLobbiesPlugin.Logger.LogInfo($"[Tables] Cleared leftover table entries of player {playerRef.PlayerId} after they left");
        }
    }

    private static bool SetBusyPrefix(OverlandSessionPlayers __instance, PlayerRef playerRef, bool isBusy)
    {
        if (!PlayerTables.On || !isBusy || !Authority(__instance)) return true;
        try
        {
            var busy = __instance.BusyPlayerRefToBusyReason;
            if (busy.ContainsKey(playerRef) || busy.Count < busy.Capacity) return true;
            var live = PlayerTables.LivePlayers(__instance.Runner);
            PlayerTables.PurgeStale("BusyPlayerRefToBusyReason", live, playerRef.RawEncoded,
                e => busy.ContainsKey(PlayerTables.Ref(e)), e => busy.Remove(PlayerTables.Ref(e)));
            if (busy.Count < busy.Capacity) return true;
            PlayerTables.CountSkip("BusyPlayerRefToBusyReason",
                $"More than {busy.Capacity} players busy at once: player {playerRef.PlayerId}'s busy flag is not shared (camping may not wait for them)");
            PlayerTables.ClearInvokeRpcFlag();
            return false;
        }
        catch (Exception e) { DebugLog.Error("biglobbies", e, "SessionTableGuards.SetBusyPrefix"); PlayerTables.LogOnce("busy", "RPC_SetBusyState guard: " + e.Message); return true; }
    }

    /// <summary>The supporter flag key is the RPC's sender (RpcInfo); only clear ghosts here, BigLobby's finalizer absorbs the rest.</summary>
    private static void SupporterPrefix(OverlandSessionPlayers __instance)
    {
        if (!PlayerTables.On || !Authority(__instance)) return;
        try
        {
            var sup = __instance.PlayerRefToSupporterEditionOwnership;
            if (sup.Count < sup.Capacity) return;
            PlayerTables.PurgeStale("PlayerRefToSupporterEditionOwnership", PlayerTables.LivePlayers(__instance.Runner), 0,
                e => sup.ContainsKey(PlayerTables.Ref(e)), e => sup.Remove(PlayerTables.Ref(e)));
            if (sup.Count >= sup.Capacity)
                PlayerTables.CountSkip("PlayerRefToSupporterEditionOwnership",
                    $"Supporter flags table full ({sup.Capacity}): players past that show as non-supporters to others");
        }
        catch (Exception e) { DebugLog.Error("biglobbies", e, "SessionTableGuards.SupporterPrefix"); PlayerTables.LogOnce("supporter", "supporter guard: " + e.Message); }
    }

    // ---- MapNetworkManager ----

    private static bool HasMapObjects(MapNetworkManager map, PlayerRef p)
    {
        try
        {
            return map.PlayerRefToPinpointNetworkObject.ContainsKey(p) || map.PlayerRefToPinpointPosition.ContainsKey(p)
                || map.PlayerRefToPortableWinchAnchorNetworkObject.ContainsKey(p);
        }
        catch (Exception ex) { DebugLog.Error("biglobbies", ex, "SessionTableGuards.HasMapObjects"); return false; }
    }

    private static void RemoveMapKeys(MapNetworkManager map, PlayerRef p)
    {
        try
        {
            var pins = map.PlayerRefToPinpointNetworkObject;
            if (pins.ContainsKey(p)) pins.Remove(p);
            var pos = map.PlayerRefToPinpointPosition;
            if (pos.ContainsKey(p)) pos.Remove(p);
            var anchors = map.PlayerRefToPortableWinchAnchorNetworkObject;
            if (anchors.ContainsKey(p)) anchors.Remove(p);
        }
        catch (Exception ex) { DebugLog.Error("biglobbies", ex, "SessionTableGuards.RemoveMapKeys"); }
    }

    /// <summary>Removes map objects/keys of players who already left. True if anything was removed.</summary>
    private static bool PurgeMap(MapNetworkManager map, PlayerRef keep)
    {
        var live = PlayerTables.LivePlayers(map.Runner);
        int removed = PlayerTables.PurgeStale("MapNetworkManager tables", live, keep.RawEncoded,
            e => HasMapObjects(map, PlayerTables.Ref(e)),
            e =>
            {
                var p = PlayerTables.Ref(e);
                try { map.RPC_DestroyPlayerOwnedObjects(p); } catch (Exception ex) { DebugLog.Error("biglobbies", ex, "SessionTableGuards.PurgeMap"); }
                RemoveMapKeys(map, p);
            });
        return removed > 0;
    }

    private static bool PinpointFits(MapNetworkManager map, PlayerRef p)
    {
        var pins = map.PlayerRefToPinpointNetworkObject;
        var pos = map.PlayerRefToPinpointPosition;
        return (pins.ContainsKey(p) || pins.Count < pins.Capacity) && (pos.ContainsKey(p) || pos.Count < pos.Capacity);
    }

    private static bool InstantiatePinpointPrefix(MapNetworkManager __instance, PlayerRef playerRef)
    {
        if (!PlayerTables.On || !Authority(__instance)) return true;
        try
        {
            if (PinpointFits(__instance, playerRef)) return true;
            if (PurgeMap(__instance, playerRef) && PinpointFits(__instance, playerRef)) return true;
            PlayerTables.CountSkip("PlayerRefToPinpointNetworkObject",
                $"Map pinpoints table full ({__instance.PlayerRefToPinpointNetworkObject.Capacity}): player {playerRef.PlayerId}'s pinpoint was not placed (instead of leaking a pinpoint object)");
            PlayerTables.ClearInvokeRpcFlag();
            return false;
        }
        catch (Exception e) { DebugLog.Error("biglobbies", e, "SessionTableGuards.InstantiatePinpointPrefix"); PlayerTables.LogOnce("pin", "RPC_InstantiatePinpoint guard: " + e.Message); return true; }
    }

    private static bool MovePinpointPrefix(MapNetworkManager __instance, PlayerRef playerRef)
    {
        if (!PlayerTables.On || !Authority(__instance)) return true;
        try
        {
            if (PinpointFits(__instance, playerRef)) return true;
            if (PurgeMap(__instance, playerRef) && PinpointFits(__instance, playerRef)) return true;
            PlayerTables.CountSkip("PlayerRefToPinpointPosition",
                $"Map pinpoint positions table full ({__instance.PlayerRefToPinpointPosition.Capacity}): a pinpoint move was ignored");
            PlayerTables.ClearInvokeRpcFlag();
            return false;
        }
        catch (Exception e) { DebugLog.Error("biglobbies", e, "SessionTableGuards.MovePinpointPrefix"); PlayerTables.LogOnce("movepin", "RPC_MovePinpoint guard: " + e.Message); return true; }
    }

    private static bool PlaceAnchorPrefix(MapNetworkManager __instance, PlayerRef playerRef)
    {
        if (!PlayerTables.On || !Authority(__instance)) return true;
        try
        {
            var anchors = __instance.PlayerRefToPortableWinchAnchorNetworkObject;
            if (anchors.ContainsKey(playerRef) || anchors.Count < anchors.Capacity) return true; // existing anchor is just moved
            if (PurgeMap(__instance, playerRef) && anchors.Count < anchors.Capacity) return true;
            PlayerTables.CountSkip("PlayerRefToPortableWinchAnchorNetworkObject",
                $"Portable winch anchors table full ({anchors.Capacity}): player {playerRef.PlayerId}'s anchor was not placed (instead of leaking an anchor object)");
            PlayerTables.ClearInvokeRpcFlag();
            return false;
        }
        catch (Exception e) { DebugLog.Error("biglobbies", e, "SessionTableGuards.PlaceAnchorPrefix"); PlayerTables.LogOnce("anchor", "RPC_PlacePortableWinchAnchor guard: " + e.Message); return true; }
    }
}
