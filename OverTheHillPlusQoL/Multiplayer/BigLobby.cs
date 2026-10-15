using System;
using System.Collections.Generic;
using Fusion;
using HarmonyLib;
using Il2CppInterop.Runtime;
using UnityEngine;
using VehiclePhysics;
using Object = UnityEngine.Object;

namespace OverTheHillPlus.Multiplayer;

/// <summary>
/// Lobbies above the game's 4-slot player tables (decoded from the game's code with Cpp2IL):
/// - AddPlayer puts each player in the first free slot of a 4-entry array and throws when none is free, which also
///   aborts the caller (SpawnPlayerInLobby): no colour, no join pop-up, no car. It's replaced by the same steps,
///   minus the throw; extra players are "overflow" and get promoted into a slot when someone leaves.
/// - The lobby and the map portals only have spawn points for 4; more are cloned next to the existing ones.
/// - Fall rescue as a safety net for anything that still ends up under or off the map.
/// </summary>
internal static class BigLobby
{
    public const int TargetSpawnPoints = 16;
    public static int Overflowed, Promoted, Rescued;
    private static float _nextRescueCheck;
    private static readonly Dictionary<IntPtr, float> LastRescue = new();

    // ---- AddPlayer replacement ----

    /// <summary>Same steps as the game's AddPlayer, in the same order, but never throws for extra players.</summary>
    public static void AddPlayer(OverlandSessionPlayers sp, PlayerRef player, string name)
    {
        int key = player.RawEncoded - 1; // the game keys names by RawEncoded-1 (== PlayerId)

        // 1. name (the dictionary holds 7; past that, players show as "Player N")
        var names = sp.PlayerRefToNameDictionary;
        if (names.ContainsKey(key) || names.Count < names.Capacity)
            names.Set(key, (NetworkString<_32>)(name ?? ""));

        // 2-3. dev-team flag from the connection token, as the game does
        bool dev = false;
        try
        {
            var runner = sp.Runner;
            var token = runner != null && runner.LocalPlayer.RawEncoded == player.RawEncoded
                ? OverlandNetworkUtils.GetOverlandConnectionToken()
                : runner?.GetPlayerConnectionToken(player);
            dev = sp.devTeamMembers != null && token != null && sp.devTeamMembers.ContainsConnectionToken(token);
        }
        catch { }
        var devs = sp.PlayerRefToDevTeamMembership;
        if (devs.ContainsKey(player) || devs.Count < devs.Capacity) devs.Set(player, dev);

        // 4-5. slot + colour (only if a slot exists; otherwise the player is overflow for now)
        if (TryTakeSlot(sp, player)) { }
        else
        {
            Overflowed++;
            Plugin.Logger.LogInfo($"[Lobby] '{name}' (id {player.PlayerId}) joined beyond the game's 4 player slots");
        }

        // 6. join pop-up + connected event, which the original skipped for extra players
        sp.RPC_ShowPlayerJoinedNotification(player);
        sp.RPC_ConnectedPlayer(player);
    }

    /// <summary>Puts the player in the first free OrderedPlayerRefs slot and PlayerColors entry. False if full.</summary>
    private static bool TryTakeSlot(OverlandSessionPlayers sp, PlayerRef player)
    {
        var order = sp.OrderedPlayerRefs;
        int free = -1;
        for (int i = 0; i < order.Length; i++)
        {
            var p = order.Get(i);
            if (p.RawEncoded == player.RawEncoded) return true; // already in
            if (free < 0 && p.RawEncoded == 0) free = i;
        }
        if (free < 0) return false;
        order.Set(free, player);

        var colors = sp.PlayerColors;
        for (int i = 0; i < colors.Length; i++)
        {
            if (colors.Get(i).RawEncoded != 0) continue;
            colors.Set(i, player);
            break;
        }
        return true;
    }

    /// <summary>After someone leaves: move a waiting overflow player into the freed slot.</summary>
    public static void PromoteOverflow(OverlandSessionPlayers sp)
    {
        if (!ModConfig.LobbyGuards.Value || sp == null) return;
        try
        {
            var order = sp.OrderedPlayerRefs;
            var inSlots = new HashSet<int>();
            for (int i = 0; i < order.Length; i++) inSlots.Add(order.Get(i).RawEncoded);
            foreach (var p in PlayersWithCars())
            {
                if (inSlots.Contains(p.RawEncoded)) continue;
                if (!TryTakeSlot(sp, p)) break;
                Promoted++;
                Plugin.Logger.LogInfo($"[Lobby] Promoted player {p.PlayerId} into a free player slot");
            }
            OverlandSessionPlayers.InvokePlayerDataUpdated();
        }
        catch (Exception e) { Plugin.Logger.LogDebug("[Lobby] promote: " + e.Message); }
    }

    /// <summary>Every player with a car in the world, oldest first (doesn't depend on the 4-slot tables).</summary>
    private static List<PlayerRef> PlayersWithCars()
    {
        var list = new List<PlayerRef>();
        foreach (var o in Object.FindObjectsOfType(Il2CppType.Of<FusionPlayer>()))
        {
            var fp = o.TryCast<FusionPlayer>();
            if (fp?.Object == null) continue;
            try { var p = fp.Object.InputAuthority; if (p.IsRealPlayer) list.Add(p); } catch { }
        }
        list.Sort((a, b) => a.RawEncoded.CompareTo(b.RawEncoded));
        return list;
    }

    // ---- spawn points ----

    /// <summary>Clones spawn points next to the existing ones until there are enough for a full lobby.</summary>
    public static void EnsureSpawnPoints(Il2CppSystem.Collections.Generic.List<SpawnPoint> points, string where)
    {
        if (!ModConfig.ExpandLobbySize.Value || points == null || points.Count == 0 || points.Count >= TargetSpawnPoints) return;
        int original = points.Count;
        try
        {
            for (int i = original; i < TargetSpawnPoints; i++)
            {
                var src = points[i % original];
                if (src == null) continue;
                var t = src.transform;
                int row = i / original; // 1, 2, 3: rows further back
                var pos = t.position - t.forward * (9f * row) + t.right * ((i % 2 == 0 ? 1f : -1f) * 2f);
                if (Physics.Raycast(pos + Vector3.up * 30f, Vector3.down, out var hit, 80f)) pos = hit.point;
                var clone = Object.Instantiate(src.gameObject, pos, t.rotation, t.parent);
                clone.name = $"{src.gameObject.name}_OTHPlus{i}";
                points.Add(clone.GetComponent<SpawnPoint>());
            }
            Plugin.Logger.LogInfo($"[Lobby] {where}: spawn points {original} -> {points.Count}");
        }
        catch (Exception e) { Plugin.Logger.LogWarning($"[Lobby] {where} spawn points: {e.Message}"); }
    }

    // ---- fall rescue ----

    public static void Tick()
    {
        if (!ModConfig.FallRescue.Value || Time.unscaledTime < _nextRescueCheck) return;
        _nextRescueCheck = Time.unscaledTime + 1f;
        if (!GameState.IsAuthority) return; // only the game that simulates the cars can move them
        try
        {
            foreach (var o in Object.FindObjectsOfType(Il2CppType.Of<FusionPlayer>()))
            {
                var car = o.TryCast<FusionPlayer>()?.vehicleBase;
                if (car != null) CheckCar(car);
            }
        }
        catch (Exception e) { Plugin.Logger.LogDebug("[Lobby] fall rescue: " + e.Message); }
    }

    private static void CheckCar(VehicleBase car)
    {
        var pos = car.transform.position;
        bool overMap = TryGroundHeight(pos, out float ground);
        // Only judge cars on the map's terrain: the lobby/garage scene has no terrain and must be left alone.
        if (Terrain.activeTerrains.Length == 0) return;
        bool underGround = overMap && pos.y < ground - 6f;
        bool offMap = !overMap && pos.y < -50f;
        if (!underGround && !offMap) return;
        if (LastRescue.TryGetValue(car.Pointer, out var last) && Time.unscaledTime - last < 5f) return;
        LastRescue[car.Pointer] = Time.unscaledTime;

        var fwd = Vector3.ProjectOnPlane(car.transform.forward, Vector3.up);
        if (fwd.sqrMagnitude < 0.01f) fwd = Vector3.forward;
        var rot = Quaternion.LookRotation(fwd.normalized, Vector3.up);
        Vector3 target;
        if (underGround) target = new Vector3(pos.x, ground + 2f, pos.z);
        else
        {
            var anchor = GameState.LocalCar != null && !GameState.IsLocalCar(car) ? GameState.LocalCar.transform : null;
            if (anchor == null) return;
            target = anchor.position - anchor.forward * 10f + anchor.right * 5f + Vector3.up * 2f;
            if (TryGroundHeight(target, out float g)) target.y = g + 2f;
        }
        car.Reposition(target, rot);
        Rescued++;
        Plugin.Logger.LogInfo($"[Lobby] Fall rescue: car at {pos} ({(underGround ? "under ground" : "off map")}) -> {target}");
    }

    private static bool TryGroundHeight(Vector3 pos, out float height)
    {
        height = 0f;
        foreach (var t in Terrain.activeTerrains)
        {
            if (t == null || t.terrainData == null) continue;
            var origin = t.transform.position;
            var size = t.terrainData.size;
            if (pos.x < origin.x || pos.z < origin.z || pos.x > origin.x + size.x || pos.z > origin.z + size.z) continue;
            height = t.SampleHeight(pos) + origin.y;
            return true;
        }
        return false;
    }
}

[HarmonyPatch(typeof(OverlandSessionPlayers), nameof(OverlandSessionPlayers.AddPlayer))]
internal static class AddPlayerReplacement
{
    [HarmonyPriority(Priority.First)]
    private static bool Prefix(OverlandSessionPlayers __instance, PlayerRef playerRef, string runtimePlayerPlayerName)
    {
        if (!ModConfig.LobbyGuards.Value) return true;
        try
        {
            BigLobby.AddPlayer(__instance, playerRef, runtimePlayerPlayerName);
        }
        catch (Exception e) { Plugin.Logger.LogWarning($"[Lobby] AddPlayer replacement: {e.GetType().Name}: {e.Message}"); }
        return false; // never fall back to the original: it would repeat steps and throw for extra players
    }
}

[HarmonyPatch(typeof(OverlandSessionPlayers), nameof(OverlandSessionPlayers.RemovePlayer))]
internal static class RemovePlayerPromotePatch
{
    private static void Postfix(OverlandSessionPlayers __instance) => BigLobby.PromoteOverflow(__instance);
}

[HarmonyPatch(typeof(global::Lobby), nameof(global::Lobby.Awake))]
internal static class LobbySpawnPointsPatch
{
    private static void Postfix(global::Lobby __instance) => BigLobby.EnsureSpawnPoints(__instance.spawnPoints, "Lobby");
}

[HarmonyPatch(typeof(Portal), nameof(Portal.AssignPlayersToSpawnPoints))]
internal static class PortalSpawnPointsPatch
{
    private static void Prefix(Portal __instance) => BigLobby.EnsureSpawnPoints(__instance.spawnPoints, "Portal");
}

/// <summary>The supporter flag dictionary is also capped at 7: don't let it crash the session past that.</summary>
[HarmonyPatch(typeof(OverlandSessionPlayers), nameof(OverlandSessionPlayers.RPC_SetSupporterEditionOwnership))]
internal static class SupporterFlagGuard
{
    private static Exception Finalizer(Exception __exception) => LobbyRules.Guard("RPC_SetSupporterEditionOwnership", __exception);
}

[HarmonyPatch(typeof(OverlandSessionPlayers), nameof(OverlandSessionPlayers.RPC_SetBusyState))]
internal static class BusyStateRpcGuard
{
    private static Exception Finalizer(Exception __exception) => LobbyRules.Guard("RPC_SetBusyState", __exception);
}
