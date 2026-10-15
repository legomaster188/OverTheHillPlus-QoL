using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using Fusion;
using HarmonyLib;
using Il2CppInterop.Runtime;
using OverTheHillPlus.UI;
using UnityEngine;
using Object = UnityEngine.Object;

namespace OverTheHillPlus.Features.Public.PlayerTables;

// =====================================================================================================================
// Every fixed-size networked collection in the game's code (Assembly-CSharp), found by listing all NetworkArray /
// NetworkDictionary / NetworkLinkedList properties in the member dump and reading the size from each generated getter
// in the Cpp2IL output ("Move [rbx+8], 4" = array length, "Move r8, 7" = dictionary capacity). Fusion bakes these
// sizes into the networked state layout of every peer, so a mod cannot grow them; above the size we degrade
// gracefully instead (skip, overflow side table, stale-entry clean-up). No other assembly has networked collections.
//
// Table (owner.property)                         | Type                                   | Size | Keyed / indexed by         | Grows with players? | Stock behaviour when full (from the ISIL)                                                    | Guard
// -----------------------------------------------|----------------------------------------|------|----------------------------|---------------------|-----------------------------------------------------------------------------------------------|-----------------------------------------------
// OverlandSessionPlayers.OrderedPlayerRefs       | NetworkArray<PlayerRef>                |  4   | slot                       | yes                 | AddPlayer threw; map/compass/health UI/billboards only show slots 0-3                          | Multiplayer/BigLobby (overflow + promote)
// OverlandSessionPlayers.PlayerColors            | NetworkArray<PlayerRef>                |  4   | colour index               | yes                 | as above (UIManager.GetPlayerColor uses the index)                                            | Multiplayer/BigLobby
// OverlandSessionPlayers.PlayerRefToNameDictionary| NetworkDictionary<int,NetworkString<_32>>| 7  | PlayerId (RawEncoded-1)    | yes                 | Set throws "dictionary full" -> AddPlayer aborted                                              | BigLobby skips when full; HERE: stale purge before AddPlayer, clean-up on leave
// OverlandSessionPlayers.PlayerRefToSupporterEditionOwnership | NetworkDictionary<PlayerRef,NetworkBool> | 7 | player | yes                 | RPC_SetSupporterEditionOwnership throws for the 8th player                                    | BigLobby finalizer; HERE: stale purge first, clean-up on leave
// OverlandSessionPlayers.PlayerRefToDevTeamMembership | NetworkDictionary<PlayerRef,NetworkBool> | 7 | player       | yes                 | Set throws in AddPlayer                                                                       | BigLobby skips when full; HERE: stale purge, clean-up on leave
// OverlandSessionPlayers.BusyPlayerRefToBusyReason | NetworkDictionary<PlayerRef,int>     |  7   | player (only while busy)   | yes (concurrently busy) | RPC_SetBusyState throws for the 8th busy player                                           | HERE: stale purge, then skip cleanly (InvokeRpc flag reset)
// CampManager.SpawnedTents                       | NetworkArray<NetworkObject>            |  4   | first null slot            | yes (1 tent/player) | SpawnTent spawns the tent, finds no slot, keeps nothing -> 5th+ tent is never despawned (leak)  | HERE (TentGuard): overflow tents tracked, animated and despawned with the rest
// VehicleWinchSystem.AttachedWinchesOwners       | NetworkArray<PlayerRef>                |  4   | first empty slot           | yes (winches on 1 car) | AddAttachedWinchOwner silently drops the 5th owner -> not detached by DetachAllWinches (car despawn / winch points disabled), HasOtherWinchesAttached false | HERE (WinchOwnerGuard): overflow owners tracked, detached, promoted into freed slots
// MapNetworkManager.PlayerRefToPinpointPosition  | NetworkDictionary<PlayerRef,Vector3>   |  7   | player                     | yes                 | RPC_InstantiatePinpoint: Runner.Spawn first, then Insert throws -> pinpoint object leaked      | HERE: stale purge, then skip before spawning
// MapNetworkManager.PlayerRefToPinpointNetworkObject | NetworkDictionary<PlayerRef,NetworkObject> | 7 | player              | yes                 | Add throws after Runner.Spawn -> leaked pinpoint; RPC_MovePinpoint Set throws                  | HERE: stale purge, then skip
// MapNetworkManager.PlayerRefToPortableWinchAnchorNetworkObject | NetworkDictionary<PlayerRef,NetworkObject> | 7 | player | yes          | RPC_PlacePortableWinchAnchor: Add throws after Runner.Spawn -> leaked anchor                   | HERE: stale purge, then skip
// CarInventory.AvailableItems / EquippedItems    | NetworkArray<uint>                     |  4   | item slot of one car       | no (per car)        | -                                                                                             | none needed
// ItemRecoveryNetworkManager.Missions            | NetworkDictionary<uint,ItemRecoveryMissionStatus> | 17 | recovery point id   | no (per map)        | -                                                                                             | none needed
// NetworkedVehicleSnapshot.wheels / stdWheelStates / SuspensionFrames / dynamicGrips | NetworkArray<...> | 4 | wheel | no (per car)     | -                                                                                             | none needed
// PortalNetworkManager                           | (no networked collections: IsActivated + pendingPortalId only)                    | -                   | -                                                                                             | none needed
// NetworkProjectConfig.Simulation.PlayerCount    | Fusion session size                    | cfg  | -                          | yes                 | Photon refuses joins above it                                                                 | Multiplayer/PlayerLimitPatches
//
// Managed (not networked, unbounded) per-player maps, for completeness: MapNetworkManager.portablePlanks /
// trailPlacedTools, OverlandSessionPlayers.playerRefToDummy, CarHealthUI.* and OverlandNetworkRunner.spawnedCharacters.
//
// Notes on the guards:
// - Everything runs only where the table is written: the state authority (host / solo). Clients are untouched.
// - A skipped RPC body must clear Fusion's static NetworkBehaviourUtils.InvokeRpc flag, which the original would have
//   consumed first thing; otherwise the next local RPC call anywhere on the host would not be sent.
// - "Stale" = a key whose player is no longer in Runner.ActivePlayers and has no car: left behind when RemovePlayer
//   threw part-way for an extra player. Live players are never evicted.
// =====================================================================================================================

/// <summary>Guards for the game's fixed-size per-player networked tables (see the table above).</summary>
internal sealed class PlayerTablesFeature : IFeature
{
    public void Register()
    {
        PlayerTables.Enabled = ModConfig.File.Bind("PlayerTables", "Enabled", true,
            "Keep the game's fixed-size per-player tables (tents, winches, map pins, busy flags...) safe in lobbies above 4 players.");

        PlayerTables.Install();
        Registry.OnTick(PlayerTables.Tick);
        Registry.Section(Registry.PlusMode, "Lobby", PlayerTables.DrawCard, 50);
    }
}

internal static class PlayerTables
{
    public static ConfigEntry<bool> Enabled;
    public static bool On => Enabled == null || Enabled.Value;

    /// <summary>
    /// Highest PlayerRef encoding worth probing for stale keys. Fusion player ids grow as people join and leave (they
    /// are not reused right away), so this follows the highest id seen this game run plus a margin, never below 96.
    /// </summary>
    internal static int ScanMax => Math.Min(1024, Math.Max(96, _highestSeen + 32));
    private static int _highestSeen;

    /// <summary>Remembers the highest player id seen, so the stale-key scans reach it.</summary>
    internal static void NoteSeen(int encoded)
    {
        if (encoded > _highestSeen && encoded < 100_000) _highestSeen = encoded;
    }

    private static Harmony _harmony;
    private static readonly HashSet<string> Logged = new();
    internal static readonly Dictionary<string, int> Skipped = new();
    internal static int StaleRemoved, LeaveCleanups;

    // ---- install ----

    internal static void Install()
    {
        _harmony = new Harmony(Plugin.Guid + ".playertables");
        SessionTableGuards.Install();
        WinchOwnerGuard.Install();
        TentGuard.Install();
    }

    /// <summary>Patches one method; a missing or failing target only disables that guard.</summary>
    internal static void Patch(Type type, string method, Type[] args, Type patches,
        string prefix = null, string postfix = null, string finalizer = null, int priority = -1)
    {
        try
        {
            var target = args == null ? AccessTools.Method(type, method) : AccessTools.Method(type, method, args);
            if (target == null) { Plugin.Logger.LogWarning($"[Tables] {type.Name}.{method} not found, guard off"); return; }
            HarmonyMethod Hm(string name)
            {
                if (name == null) return null;
                var hm = new HarmonyMethod(AccessTools.Method(patches, name));
                if (priority >= 0) hm.priority = priority;
                return hm;
            }
            _harmony.Patch(target, prefix: Hm(prefix), postfix: Hm(postfix), finalizer: Hm(finalizer));
        }
        catch (Exception e) { Plugin.Logger.LogWarning($"[Tables] could not patch {type.Name}.{method}: {e.Message}"); }
    }

    // ---- shared helpers ----

    internal static void LogOnce(string key, string message)
    {
        if (Logged.Add(key)) Plugin.Logger.LogWarning("[Tables] " + message + " (repeats are counted, not logged)");
    }

    internal static void CountSkip(string table, string message)
    {
        Skipped.TryGetValue(table, out int n);
        Skipped[table] = n + 1;
        LogOnce("skip:" + table, message);
    }

    /// <summary>
    /// Call when a prefix skips an RPC method body: the original would have consumed Fusion's static "this call is an
    /// incoming RPC" flag in CheckInvokeRpc. Left set, the host's next local RPC call would run locally only.
    /// </summary>
    internal static void ClearInvokeRpcFlag()
    {
        try { NetworkBehaviourUtils.InvokeRpc = false; } catch (Exception e) { LogOnce("invokerpc", "InvokeRpc reset failed: " + e.Message); }
    }

    /// <summary>RawEncoded values of players still in the session (active players + anyone with a car), or null if unknown.</summary>
    internal static HashSet<int> LivePlayers(NetworkRunner runner)
    {
        if (runner == null) return null;
        var live = new HashSet<int>();
        bool gotActive = false;
        try
        {
            var en = runner.ActivePlayers.GetEnumerator();
            var plain = en.Cast<Il2CppSystem.Collections.IEnumerator>();
            while (plain.MoveNext()) { int raw = en.Current.RawEncoded; live.Add(raw); NoteSeen(raw); gotActive = true; }
        }
        catch (Exception e) { LogOnce("active", "ActivePlayers unreadable: " + e.Message); }
        try { live.Add(runner.LocalPlayer.RawEncoded); } catch { }
        try
        {
            foreach (var o in GameState.FusionPlayers)
            {
                var fp = o.TryCast<FusionPlayer>();
                if (fp?.Object == null) continue;
                try { live.Add(fp.Object.InputAuthority.RawEncoded); } catch { }
            }
        }
        catch { }
        // Without the active player list we can't tell a joining player from a ghost: purge nothing.
        return gotActive ? live : null;
    }

    /// <summary>
    /// Removes keys of players who are no longer in the session. <paramref name="contains"/>/<paramref name="remove"/>
    /// take a RawEncoded player id. Returns how many were removed.
    /// </summary>
    internal static int PurgeStale(string table, HashSet<int> live, int keepEncoded, Func<int, bool> contains, Action<int> remove)
    {
        if (live == null) return 0;
        int removed = 0;
        int scanMax = ScanMax;
        for (int enc = 1; enc <= scanMax; enc++)
        {
            if (enc == keepEncoded || live.Contains(enc)) continue;
            try
            {
                if (!contains(enc)) continue;
                remove(enc);
                removed++;
            }
            catch (Exception e) { LogOnce("purge:" + table, $"{table}: stale purge failed: {e.Message}"); return removed; }
        }
        if (removed > 0)
        {
            StaleRemoved += removed;
            Plugin.Logger.LogInfo($"[Tables] {table}: removed {removed} entr{(removed == 1 ? "y" : "ies")} of players who already left");
        }
        return removed;
    }

    internal static PlayerRef Ref(int encoded) => PlayerRef.FromEncoded(encoded);

    // ---- per-frame ----

    private static float _uiWantedUntil, _nextSnapshot;

    internal static void Tick()
    {
        if (!On) return;
        TentGuard.Tick();
        WinchOwnerGuard.Tick();
        if (Time.unscaledTime < _uiWantedUntil && Time.unscaledTime >= _nextSnapshot)
        {
            _nextSnapshot = Time.unscaledTime + 1f;
            TakeSnapshot();
        }
    }

    // ---- menu card ----

    private struct Usage { public string Label; public int Count, Capacity; }
    private static readonly List<Usage> Snapshot = new();
    private static bool _snapshotInSession;

    private static void Add(string label, Func<int> count, Func<int> capacity)
    {
        try { Snapshot.Add(new Usage { Label = label, Count = count(), Capacity = capacity() }); } catch { }
    }

    private static void TakeSnapshot()
    {
        Snapshot.Clear();
        _snapshotInSession = GameState.Runner != null;
        if (!_snapshotInSession) return;
        try
        {
            var sp = OverlandSessionPlayers.Instance;
            if (sp != null)
            {
                Add("Player names", () => sp.PlayerRefToNameDictionary.Count, () => sp.PlayerRefToNameDictionary.Capacity);
                Add("Supporter flags", () => sp.PlayerRefToSupporterEditionOwnership.Count, () => sp.PlayerRefToSupporterEditionOwnership.Capacity);
                Add("Busy players", () => sp.BusyPlayerRefToBusyReason.Count, () => sp.BusyPlayerRefToBusyReason.Capacity);
            }
        }
        catch { }
        try
        {
            var map = MapNetworkManager.Instance;
            if (map != null)
            {
                Add("Map pinpoints", () => map.PlayerRefToPinpointNetworkObject.Count, () => map.PlayerRefToPinpointNetworkObject.Capacity);
                Add("Portable winch anchors", () => map.PlayerRefToPortableWinchAnchorNetworkObject.Count, () => map.PlayerRefToPortableWinchAnchorNetworkObject.Capacity);
            }
        }
        catch { }
        try
        {
            var camp = CampManager.Instance;
            if (camp != null)
            {
                Add("Camp tents", () =>
                {
                    var t = camp.SpawnedTents;
                    int n = 0;
                    for (int i = 0; i < t.Length; i++) if (t.Get(i) != null) n++;
                    return n + TentGuard.OverflowCount;
                }, () => camp.SpawnedTents.Length);
            }
        }
        catch { }
        int most = WinchOwnerGuard.MostOnOneCar(out int capacity);
        if (capacity > 0) Snapshot.Add(new Usage { Label = "Winches on one car (most)", Count = most, Capacity = capacity });
    }

    internal static void DrawCard()
    {
        if (!Features.Public.MenuUx.Indexing) _uiWantedUntil = Time.unscaledTime + 2f; // not for the search index pass
        Menu.Card("Player tables", "The game keeps some per-player data in fixed-size networked tables. These guards keep 5 to 16 players from overflowing them.", () =>
        {
            Menu.Switch(Enabled, "Player table guards", "Host/solo: tents, winches, map pins and busy flags past their stock size", true);
            if (!_snapshotInSession) Menu.Small("Live usage shows here while you are in a session.");
            foreach (var u in Snapshot)
            {
                float f = u.Capacity > 0 ? Mathf.Clamp01(u.Count / (float)u.Capacity) : 0f;
                var color = u.Count > u.Capacity ? Theme.Warn : u.Count == u.Capacity ? Theme.Bad : f >= 0.75f ? Theme.Warn : Theme.Good;
                string text = u.Count > u.Capacity ? $"{u.Count} / {u.Capacity} (+{u.Count - u.Capacity} handled by the mod)" : $"{u.Count} / {u.Capacity}";
                Menu.Bar(u.Label, f, color, text);
            }
        });
    }
}
