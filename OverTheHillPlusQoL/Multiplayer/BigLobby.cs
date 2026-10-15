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
/// Makes lobbies above the game's 4-slot player tables behave:
/// - when the game's AddPlayer fails for an extra player, the host finishes registration (name + join pop-up);
/// - fall rescue: any car that ends up under the ground or off the map is put back (host: everyone; solo: you).
/// </summary>
internal static class BigLobby
{
    public static int Recovered, Rescued;
    private static float _nextRescueCheck;
    private static readonly Dictionary<IntPtr, float> LastRescue = new();

    /// <summary>Called after the game's AddPlayer threw for this player (host side).</summary>
    public static void RecoverAddPlayer(OverlandSessionPlayers sp, PlayerRef player, string name)
    {
        if (!ModConfig.LobbyGuards.Value || sp == null) return;
        try
        {
            var names = sp.PlayerRefToNameDictionary;
            if (!string.IsNullOrEmpty(name) && !names.ContainsKey(player.PlayerId) && names.Count < names.Capacity)
                names.Set(player.PlayerId, name);
        }
        catch (Exception e) { Plugin.Logger.LogDebug("[Lobby] name recovery: " + e.Message); }
        try { sp.RPC_ShowPlayerJoinedNotification(player); }
        catch (Exception e) { Plugin.Logger.LogDebug("[Lobby] join pop-up recovery: " + e.Message); }
        Recovered++;
        Plugin.Logger.LogInfo($"[Lobby] Recovered extra player '{name}' (id {player.PlayerId}): name + join pop-up");
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
                if (car == null) continue;
                CheckCar(car);
            }
        }
        catch (Exception e) { Plugin.Logger.LogDebug("[Lobby] fall rescue: " + e.Message); }
    }

    private static void CheckCar(VehicleBase car)
    {
        var pos = car.transform.position;
        bool overMap = TryGroundHeight(pos, out float ground);
        bool underGround = overMap && pos.y < ground - 6f;
        bool offMap = !overMap || pos.y < -500f;
        if (!underGround && !offMap) return;

        // Don't fight the game while it's still placing a freshly spawned car, and don't spam repositions.
        if (LastRescue.TryGetValue(car.Pointer, out var last) && Time.unscaledTime - last < 5f) return;
        LastRescue[car.Pointer] = Time.unscaledTime;

        Vector3 target;
        Quaternion rot = Quaternion.LookRotation(Vector3.ProjectOnPlane(car.transform.forward, Vector3.up).normalized + Vector3.forward * 0.001f, Vector3.up);
        if (underGround) target = new Vector3(pos.x, ground + 2f, pos.z);
        else
        {
            // Off the map: next to the host (or the first car that's on the map).
            var anchor = GameState.LocalCar != null && !GameState.IsLocalCar(car) ? GameState.LocalCar.transform : null;
            if (anchor == null) return;
            target = anchor.position - anchor.forward * 10f + anchor.right * 5f + Vector3.up * 2f;
            if (TryGroundHeight(target, out float g)) target.y = g + 2f;
        }
        car.Reposition(target, rot);
        Rescued++;
        Plugin.Logger.LogInfo($"[Lobby] Fall rescue: car at {pos} ({(underGround ? "under ground" : "off map")}) -> {target}");
    }

    /// <summary>Height of the terrain under a point, if the point is over any terrain.</summary>
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

